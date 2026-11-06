"""插件包：把技能 + 专家 + 连接器 + 快捷指令 + 工具规则打成一包，IT 按部门一键下发。

目录结构沿用 Claude Code 的 plugins（社区插件可以直接导入）：

    <插件根目录>/
    ├── .claude-plugin/plugin.json   清单：name（必填）、version、description、author、homepage、keywords
    ├── commands/*.md                快捷指令：文件名是指令名，frontmatter 的 description 是标题，正文是模板
    ├── skills/<名字>/SKILL.md        技能（社区 SKILL.md 标准）
    ├── agents/*.md                  专家：frontmatter name / description / tools / disallowedTools，正文是工作方式
    ├── .mcp.json                    连接器：{"mcpServers": {...}}，和 Claude Code 一样
    └── rules.json                   工具规则（我们自己的声明式规则，可放在 hooks/rules.json）

也认 .flyknit-plugin/plugin.json 和根目录的 plugin.json。

和 Claude Code 不一样的地方（安全边界，不是没做完）：
- hooks/hooks.json 里的 command 类 hook 要在员工电脑上执行脚本，**一律拒收**；改写成 rules.json 的声明式规则。
- 连接器里的 ${VAR} 只认填写项（员工连接时填，或管理员预填），不读员工电脑的环境变量；
  用到 ${CLAUDE_PLUGIN_ROOT} 的本地进程（要在员工电脑上跑插件自带的脚本）不支持。
- 专家只能做减法：Claude Code 的 tools 白名单里没有写入类工具时，专家记成「只读」；
  disallowedTools 照搬成「不让用的工具」。
"""

from __future__ import annotations

import io
import json
import re
import zipfile
from dataclasses import dataclass, field
from pathlib import PurePosixPath

from . import mcp_catalog, skill_library

MAX_ZIP_BYTES = skill_library.MAX_TOTAL_BYTES
VALID_ID = re.compile(r"^[a-z][a-z0-9_-]{1,39}$")
MANIFESTS = (".claude-plugin/plugin.json", ".flyknit-plugin/plugin.json", "plugin.json")
#: Claude Code 的工具名里算「会改东西」的；白名单里一个都没有就是只读专家
WRITE_TOOLS = {"write", "edit", "multiedit", "bash", "notebookedit", "write_file", "edit_file", "delete_path", "run_shell"}
#: 快捷指令名同 commands.NAME_RE
COMMAND_NAME = re.compile(r"^[\w\-一-鿿]{1,20}$")


class PluginError(Exception):
    """插件包不合法。"""


@dataclass
class ParsedPlugin:
    id: str
    name: str
    version: str = ""
    description: str = ""
    author: str = ""
    homepage: str = ""
    keywords: list[str] = field(default_factory=list)
    skills: list[skill_library.ParsedSkill] = field(default_factory=list)
    #: [{name, title, description, template}]
    commands: list[dict] = field(default_factory=list)
    #: [{id, fields: ExpertFields 的 dict}]
    experts: list[dict] = field(default_factory=list)
    #: mcp_catalog.Draft 的 dict（id 已经加上插件前缀以外的检查）
    connectors: list[dict] = field(default_factory=list)
    #: tool_rules.ToolRule 的 dict（未校验，路由层校验）
    rules: list[dict] = field(default_factory=list)
    #: 不影响安装、但管理员该知道的事（例如忽略了哪个文件）
    warnings: list[str] = field(default_factory=list)

    def summary(self) -> dict:
        """存进数据库、给后台和员工端看的清单（不含技能文件本体）。"""
        return {
            "skills": [{"name": s.name, "description": s.description} for s in self.skills],
            "commands": self.commands,
            "experts": [{"id": e["id"], "name": e["fields"]["name"], "description": e["fields"]["description"][:300]}
                        for e in self.experts],
            "connectors": [{"id": c["id"], "name": c["name"], "transport": c["transport"]} for c in self.connectors],
            "rules": self.rules,
            "warnings": self.warnings,
        }


def _norm(name: str) -> str:
    return name.replace("\\", "/")


def plugin_roots(names: list[str]) -> list[str]:
    """zip 里所有插件根目录（清单文件所在位置往上推）。"""
    roots: set[str] = set()
    for n in names:
        low = n.lower()
        for m in MANIFESTS[:2]:
            if low == m or low.endswith("/" + m):
                roots.add(n[: len(n) - len(m)].rstrip("/"))
    if roots:
        return sorted(roots)
    # 只有裸 plugin.json 时取最浅的一层：技能目录里碰巧有个 plugin.json 不能当成插件
    bare = [n for n in names if n.lower() == "plugin.json" or n.lower().endswith("/plugin.json")]
    if not bare:
        return []
    depth = min(n.count("/") for n in bare)
    return sorted({n[: -len("plugin.json")].rstrip("/") for n in bare if n.count("/") == depth})


def slug(text: str) -> str:
    s = re.sub(r"[^a-z0-9_-]+", "-", (text or "").strip().lower()).strip("-_")
    if s and not s[0].isalpha():
        s = "p-" + s
    return s[:40]


def parse(data: bytes, subdir: str = "") -> ParsedPlugin:
    """解析插件 zip。一个 zip（例如一个 marketplace 仓库）里有多个插件时要用 subdir 指定哪一个。"""
    try:
        zf = zipfile.ZipFile(io.BytesIO(data))
    except zipfile.BadZipFile as exc:
        raise PluginError("不是合法的 zip 文件") from exc
    with zf:
        try:
            members = skill_library._safe_members(zf)  # 路径穿越、可执行文件、体积、数量，和技能库同一套检查
        except skill_library.SkillError as exc:
            raise PluginError(str(exc).replace("技能包", "插件包")) from exc
        names = [_norm(m.filename) for m in members]
        roots = plugin_roots(names)
        sub = subdir.strip("/")
        if sub:
            roots = [r for r in roots if r == sub or r.endswith("/" + sub)]
        if not roots:
            raise PluginError("没有找到插件清单（.claude-plugin/plugin.json）。这不是一个插件包" +
                              ("，或者链接里的子目录不对" if sub else ""))
        if len(roots) > 1:
            listed = "、".join(r.rsplit("/", 1)[-1] or "(根目录)" for r in roots[:10])
            raise PluginError(f"这个压缩包里有 {len(roots)} 个插件（{listed}），请用指向某一个插件目录的链接导入")
        root = roots[0]
        prefix = f"{root}/" if root else ""
        files = {n[len(prefix):]: m for n, m in zip(names, members) if n.startswith(prefix)}

        def text(rel: str) -> str:
            return zf.read(files[rel]).decode("utf-8", "replace")

        manifest_rel = next(r for r in (m for m in MANIFESTS) if r in files)
        try:
            manifest = json.loads(text(manifest_rel))
        except json.JSONDecodeError as exc:
            raise PluginError(f"{manifest_rel} 不是合法的 JSON：{exc.msg}") from exc
        if not isinstance(manifest, dict):
            raise PluginError(f"{manifest_rel} 应该是一个 JSON 对象")
        raw_name = str(manifest.get("name") or "").strip()
        pid = slug(raw_name)
        if not VALID_ID.match(pid):
            raise PluginError("plugin.json 里的 name 不合法：要以字母开头，字母、数字、- 和 _，2~40 个字符")
        author = manifest.get("author")
        plugin = ParsedPlugin(
            id=pid,
            name=str(manifest.get("displayName") or raw_name)[:100],
            version=str(manifest.get("version") or "")[:50],
            description=str(manifest.get("description") or "")[:1000],
            author=str(author.get("name", "") if isinstance(author, dict) else author or "")[:100],
            homepage=str(manifest.get("homepage") or manifest.get("repository") or "")[:500],
            keywords=[str(k)[:30] for k in (manifest.get("keywords") or []) if isinstance(k, str)][:10],
        )

        _skills(zf, data_files=files, plugin=plugin)
        _commands(files, text, plugin)
        _experts(files, text, plugin)
        _connectors(files, text, plugin)
        _rules(files, text, plugin)

        if not (plugin.skills or plugin.commands or plugin.experts or plugin.connectors or plugin.rules):
            raise PluginError("插件里没有可用的内容（技能、快捷指令、专家、连接器、工具规则都没有）")
        return plugin


def _skills(zf: zipfile.ZipFile, data_files: dict, plugin: ParsedPlugin) -> None:
    rels = [r for r in data_files if r.lower().startswith("skills/")]
    if not rels:
        return
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", zipfile.ZIP_DEFLATED) as out:
        for r in rels:
            out.writestr(r, zf.read(data_files[r]))
    try:
        plugin.skills = skill_library.parse_packages(buffer.getvalue())
    except skill_library.SkillError as exc:
        raise PluginError(f"插件里的技能有问题：{exc}") from exc


def _commands(files: dict, text, plugin: ParsedPlugin) -> None:
    for rel in sorted(r for r in files if r.lower().startswith("commands/") and r.lower().endswith(".md")):
        # commands/a/b.md 在 Claude Code 里是 /a:b；我们的指令名不能有冒号，用 - 连起来
        name = "-".join(PurePosixPath(rel[len("commands/"):]).with_suffix("").parts)
        if not COMMAND_NAME.match(name):
            plugin.warnings.append(f"忽略了快捷指令 {rel}：指令名只能用中文、字母、数字、- 和 _，不超过 20 个字")
            continue
        meta, body = skill_library.split_frontmatter(text(rel))
        template = to_template(body.strip())
        if not template:
            plugin.warnings.append(f"忽略了空的快捷指令 {rel}")
            continue
        if "!`" in body:
            # Claude Code 的 !`命令` 会先在本机执行再把输出塞进模板。我们不在员工电脑上跑下发的命令
            plugin.warnings.append(f"/{name} 里的 !`命令` 不会执行，原样作为文字发给 AI")
        description = (meta.get("description") or "").strip()
        plugin.commands.append({
            "name": name,
            "title": description[:60],
            "description": (meta.get("argument-hint") or "").strip()[:300],
            "template": template[:4000],
        })


def to_template(body: str) -> str:
    """Claude Code 的 $ARGUMENTS / $1…$9 → 我们的 {参数}（按顺序填，最后一个拿走剩下的文字）。"""
    out = re.sub(r"\$([1-9])", lambda m: "{arg" + m.group(1) + "}", body)
    return out.replace("$ARGUMENTS", "{arguments}")


def _tools(value: str) -> list[str]:
    return [t.strip() for t in re.split(r"[,\s]+", value or "") if t.strip()]


def _experts(files: dict, text, plugin: ParsedPlugin) -> None:
    for rel in sorted(r for r in files if r.lower().startswith("agents/") and r.lower().endswith(".md")):
        meta, body = skill_library.split_frontmatter(text(rel))
        base = (meta.get("name") or PurePosixPath(rel).stem).strip()
        eid = slug(f"{plugin.id}-{base}")
        if not VALID_ID.match(eid):
            plugin.warnings.append(f"忽略了专家 {rel}：名字不合法")
            continue
        instructions = body.strip()
        if not instructions:
            plugin.warnings.append(f"忽略了专家 {rel}：正文（工作方式）是空的")
            continue
        allowed = _tools(meta.get("tools", ""))
        read_only = str(meta.get("read-only") or meta.get("read_only") or "").lower() == "true" or (
            bool(allowed) and not any(t.lower() in WRITE_TOOLS for t in allowed))
        disallowed = _tools(meta.get("disallowedtools") or meta.get("disallowed-tools") or meta.get("disallowed_tools") or "")
        description = (meta.get("description") or "").strip()
        plugin.experts.append({
            "id": eid,
            "fields": {
                "name": base[:100],
                "tagline": description[:200],
                "category": plugin.name[:50],
                "author": plugin.author,
                "description": description[:4000],
                "instructions": instructions[:20000],
                "disallowed_tools": disallowed[:50],
                "read_only": read_only,
                "preferred_skills": [s.name for s in plugin.skills][:50],
            },
        })
        if "model" in meta:
            plugin.warnings.append(f"专家 {base} 的 model 设置不生效：模型由后台场景统一配置")


def _connectors(files: dict, text, plugin: ParsedPlugin) -> None:
    rel = next((r for r in (".mcp.json", "mcp.json") if r in files), None)
    if rel is None:
        return
    try:
        drafts = mcp_catalog.parse_import(text(rel))
    except mcp_catalog.McpConfigError as exc:
        raise PluginError(f"{rel}：{exc}") from exc
    for d in drafts:
        joined = json.dumps([d.command, d.args, d.env, d.url, d.headers], ensure_ascii=False)
        if "CLAUDE_PLUGIN_ROOT" in joined:
            raise PluginError(f"连接器 {d.name} 要在员工电脑上运行插件自带的程序（${{CLAUDE_PLUGIN_ROOT}}），不支持。"
                              "请改成在线服务（http），或由 IT 在「连接器」里单独配置")
        # 插件里的预填值（写死在配置里的密钥）不随包走：管理员装好后在「连接器」里填
        if d.preset:
            plugin.warnings.append(f"连接器 {d.name} 里写死的密钥没有导入，装好后请在「连接器」页面填写")
            d.preset = {}
        item = d.__dict__.copy()
        try:
            mcp_catalog.validate({**item, "fields": d.fields})
        except mcp_catalog.McpConfigError as exc:
            raise PluginError(f"连接器 {d.name}：{exc}") from exc
        plugin.connectors.append(item)


def _rules(files: dict, text, plugin: ParsedPlugin) -> None:
    hooks = next((r for r in ("hooks/hooks.json", "hooks.json") if r in files), None)
    if hooks is not None:
        try:
            data = json.loads(text(hooks))
        except json.JSONDecodeError:
            data = {}
        if "command" in json.dumps(data):
            raise PluginError("插件里的 hooks 要在员工电脑上执行脚本，出于安全考虑不支持。"
                              "请改写成 rules.json 里的工具规则（拦截 / 每次确认 / 附提醒 / 结果打码）")
    rel = next((r for r in ("rules.json", "hooks/rules.json") if r in files), None)
    if rel is None:
        return
    try:
        data = json.loads(text(rel))
    except json.JSONDecodeError as exc:
        raise PluginError(f"{rel} 不是合法的 JSON：{exc.msg}") from exc
    items = data.get("rules") if isinstance(data, dict) else data
    if not isinstance(items, list):
        raise PluginError(f"{rel} 应该是规则数组，或 {{\"rules\": [...]}}")
    plugin.rules = [r for r in items if isinstance(r, dict)]
