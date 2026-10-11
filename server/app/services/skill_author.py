"""让 AI 按管理员的描述写一个技能包（SKILL.md + 需要的脚本、参考资料）。管理员在编辑器里确认后再保存。"""

from __future__ import annotations

import re

import httpx
from sqlalchemy.ext.asyncio import AsyncSession

from . import skill_library
from .expert_registry import DraftError, ask_json

DRAFT_PROMPT = """你是企业 AI 助手平台的「技能设计师」。管理员描述一个想让员工电脑上的 AI 助手学会的能力，你把它写成一个技能包。

技能包的格式（社区开放标准，和 Claude / Codex 的 Agent Skills 一样）：
- 一个文件夹，里面必须有 SKILL.md。
- SKILL.md 开头是 YAML frontmatter，必须有 name（英文小写标识，只含小写字母、数字、横线，如 excel-weekly-report）和 description（一两句话：这个技能做什么、什么时候该用它。AI 助手只看 description 来决定要不要加载这个技能，所以要写清楚触发场景），可以有 version: 1.0.0。
- frontmatter 后面是正文：给 AI 助手看的操作说明，用 Markdown 分节写：适用场景、需要的输入、一步一步怎么做、输出的格式和放在哪里、注意事项和常见错误。写具体，不要空话。
- 正文里需要固定的脚本、模板、参考资料时，放到其他文件里（如 scripts/xxx.py、reference/xxx.md），正文里写清楚什么时候读、怎么用。不需要就不要加文件。

AI 助手运行在 Windows 办公电脑上，能读写文件、执行 PowerShell 和 Python、处理 Excel / Word / PPT。
脚本优先用 Python（标准库 + openpyxl / python-docx / pandas 这类常用库），写完整、能直接运行，不要留 TODO。
不要编造公司内部的系统地址、账号、表结构；管理员没给的信息，在正文里写成「先问用户 X」，并在 notes 里提醒管理员补充。

只输出一个 JSON 对象，不要任何解释：
{
  "name": "英文小写标识",
  "skill_md": "完整的 SKILL.md 内容（含 frontmatter）",
  "files": [{"path": "scripts/xxx.py", "content": "文件内容"}],
  "notes": ["给管理员的提示：建议补充的信息、使用前要确认的事"]
}"""

REVISE_HINT = """

下面是这个技能现在的内容，管理员想按描述修改它。保留 name 不变，只改需要改的地方，输出修改后的完整内容（所有文件都要给出，没有改的也要原样给出）。

当前 SKILL.md：
{skill_md}

当前其他文件：
{files}"""


def _slug(text: str) -> str:
    return re.sub(r"[^a-z0-9._-]+", "-", text.strip().lower()).strip("-.")[:64]


async def draft(session: AsyncSession, http: httpx.AsyncClient, text: str, current: dict | None = None) -> dict:
    """管理员一句话描述 → 技能包草稿。current 给出时是在已有技能上按描述修改。"""
    system = DRAFT_PROMPT
    if current:
        listing = "\n\n".join(f"--- {f['path']} ---\n{f['content']}" for f in current.get("files", []) if f.get("content") is not None)
        system += REVISE_HINT.format(skill_md=current.get("skill_md", ""), files=listing or "（没有）")
    data = await ask_json(session, http, system, text, max_tokens=8000)
    notes = [str(n).strip() for n in (data.get("notes") or []) if str(n).strip()][:8]
    skill_md = str(data.get("skill_md") or "").strip()
    if not skill_md:
        raise DraftError("AI 没有写出 SKILL.md，请把描述写具体一点再试一次")
    meta, _ = skill_library.split_frontmatter(skill_md)
    name = _slug(str(current["name"]) if current and current.get("name") else str(data.get("name") or meta.get("name") or ""))
    if not skill_md.startswith("---"):
        skill_md = f"---\nname: {name}\ndescription: \n---\n\n{skill_md}"
        notes.append("AI 没有写 frontmatter，已补上，请填写 description")
    # name 以标识为准，避免 SKILL.md 和标识对不上
    if name and meta.get("name") != name:
        skill_md = skill_library.set_meta(skill_md, "name", name)
    if not meta.get("version"):
        skill_md = skill_library.set_meta(skill_md, "version", "1.0.0")
    files: list[dict] = []
    for f in data.get("files") or []:
        if not isinstance(f, dict):
            continue
        path, content = str(f.get("path") or "").strip(), f.get("content")
        if not path or not isinstance(content, str) or path.lower() == "skill.md":
            continue
        try:
            path = skill_library.check_path(path)
        except skill_library.SkillError as exc:
            notes.append(f"跳过了一个文件：{exc}")
            continue
        files.append({"path": path, "content": content})
    return {"name": name, "skill_md": skill_md, "files": files[: skill_library.MAX_AUTHORED_FILES], "notes": notes}
