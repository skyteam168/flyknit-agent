"""
专家：草稿和发布的快照、编辑时的可选项、让 AI 按一句话起草一个专家。

一个专家就是一份配置（工作方式、知识、名片、几项可选的限制），员工端用同一个智能体循环来跑。
技能、连接器、工具默认全部沿用，以后新加的自动可用；这里列出的只是「优先用」或「不让用」。
"""

from __future__ import annotations

import json
import logging
import re

import httpx
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from ..models import Expert, McpVendor, ModelConfig, SkillPackage
from ..schemas import EXPERT_ID_PATTERN, ExpertFields
from . import model_router

log = logging.getLogger("flyknit.experts")

#: 草稿和快照里的内容（不含 id、状态、版本这些管理信息）
FIELDS: tuple[str, ...] = tuple(ExpertFields.model_fields)

#: 员工端的内置工具。专家可以禁用其中一部分；连接器的工具按连接器限定
BUILTIN_TOOLS: dict[str, str] = {
    "read_file": "读取文件",
    "list_dir": "查看文件夹",
    "search_files": "搜索文件",
    "write_file": "写文件",
    "delete_path": "删除文件",
    "run_shell": "执行命令",
    "open_app": "打开软件",
    "update_plan": "制定计划",
    "memory_write": "记住信息",
    "memory_search": "查找记忆",
    "load_skill": "使用技能",
    "search_skills": "查找技能",
}

MAX_EXAMPLES = 8


def snapshot(e: Expert) -> dict:
    return {name: getattr(e, name) for name in FIELDS}


def normalize(data: dict) -> dict:
    """整理一份专家内容：去空白、去重、截断列表。"""
    out = dict(data)
    for key in ("name", "tagline", "category", "description", "instructions", "knowledge", "avatar"):
        out[key] = (out.get(key) or "").strip()
    out["examples"] = _unique(out.get("examples") or [])[:MAX_EXAMPLES]
    for key in ("preferred_skills", "mcp_servers", "disallowed_tools"):
        out[key] = _unique(out.get(key) or [])[:50]
    return out


def apply(e: Expert, data: ExpertFields) -> None:
    for name, value in normalize(data.model_dump()).items():
        if name in FIELDS:
            setattr(e, name, value)


def from_snapshot(snap: dict | None) -> ExpertFields:
    """快照 → 内容。老快照里没有的字段（以后新加的）按默认值补上，已经去掉的字段忽略。"""
    return ExpertFields.model_validate({k: v for k, v in (snap or {}).items() if k in FIELDS})


def is_dirty(e: Expert) -> bool:
    """草稿和已发布的不一样。还没发布过的不算（状态已经说明了）。"""
    if not e.version:
        return False
    published = e.published or {}
    return any(published.get(name) != getattr(e, name) for name in FIELDS)


def _unique(items: list) -> list[str]:
    seen: list[str] = []
    for item in items:
        text = str(item).strip()
        if text and text not in seen:
            seen.append(text)
    return seen


# ---------- 编辑时的可选项 ----------
async def options(session: AsyncSession) -> dict:
    skills = (await session.scalars(
        select(SkillPackage).where(SkillPackage.enabled.is_(True)).order_by(SkillPackage.name))).all()
    vendors = (await session.scalars(
        select(McpVendor).where(McpVendor.enabled.is_(True)).order_by(McpVendor.sort_order, McpVendor.name))).all()
    models = (await session.scalars(
        select(ModelConfig).where(ModelConfig.enabled.is_(True)).order_by(ModelConfig.id))).all()
    return {
        "tools": [{"value": k, "label": v} for k, v in BUILTIN_TOOLS.items()],
        "skills": [{"value": s.name, "label": s.name, "detail": s.description[:200]} for s in skills],
        "mcp": [{"value": v.id, "label": v.name, "detail": v.description} for v in vendors],
        "models": [{"value": str(m.id), "label": m.name, "detail": m.model} for m in models
                   if not model_router.NON_CHAT_MODEL.search(m.model)],
    }


# ---------- AI 起草 ----------
class DraftError(Exception):
    pass


DRAFT_PROMPT = """你是企业智能体平台的「专家设计师」。管理员用一句话描述想要的专家，你据此设计这个专家的完整配置。

专家运行在员工电脑上的 AI 助手里：它能读写文件、执行命令、使用公司技能库里的技能、调用已接入的外部系统（MCP 连接器）。
你设计的是它的名片、工作方式和限制。

公司技能库里现有的技能（只能从这里选，没有合适的就留空）：
{skills}

已接入的外部系统（MCP 连接器，只能从这里选；留空表示员工连上的全部都能用）：
{mcp}

内置工具（disallowed_tools 只能从这里选）：
{tools}

只输出一个 JSON 对象，不要任何解释，字段如下：
{{
  "id": "英文小写标识，字母开头，只含字母数字下划线，如 mes_analyst",
  "name": "中文名称，如 MES生产分析专家",
  "avatar": "一个合适的 emoji",
  "tagline": "一句话介绍（20 字以内，显示在卡片上）",
  "category": "分类，如 生产 / 供应链 / IT / 行政 / 财务",
  "description": "名片，固定四行：\\n负责：……\\n适合：……\\n不负责：……\\n需要的信息：……",
  "instructions": "工作方式（给这个专家的提示词）：它是谁、怎么理解任务、按什么思路分析、查数据时注意什么、结论怎么组织（结论先行，给出依据和数据来源，不确定的地方明确说明）、什么情况下停下来问用户。300～800 字，分条写",
  "knowledge": "",
  "examples": ["3～5 个员工会这样问的示例问题"],
  "preferred_skills": ["从上面技能列表里挑相关的，没有就空数组"],
  "mcp_servers": ["只在明确只该用某几个系统时才填，否则空数组"],
  "disallowed_tools": ["这个专家不该用的内置工具，如分析类专家不需要 delete_path"],
  "read_only": true 或 false（只做查询分析、不该改动任何文件的专家设为 true）,
  "notes": ["给管理员的提示：比如需要的系统还没有接入 MCP、技能库里缺什么技能、知识里建议补充哪些固定信息"]
}}

knowledge 留空字符串，除非管理员的描述里给出了具体的固定信息（产线对应关系、编码规则等），那就整理进去。"""


def _listing(items: list[dict], empty: str) -> str:
    if not items:
        return empty
    lines = []
    for i in items[:80]:
        head = i["value"] if i["label"] == i["value"] else f"{i['value']}（{i['label']}）"
        lines.append(f"- {head}" + (f"：{i['detail']}" if i.get("detail") else ""))
    return "\n".join(lines)


def parse_json(text: str) -> dict:
    """模型的回答里取出 JSON 对象：可能包在 ```json 里，前后可能还有话。"""
    text = re.sub(r"<think>.*?</think>", "", text, flags=re.S)
    start = text.find("{")
    end = text.rfind("}")
    if start < 0 or end <= start:
        raise DraftError("AI 没有按格式返回，请再试一次")
    try:
        data = json.loads(text[start:end + 1])
    except json.JSONDecodeError as exc:
        raise DraftError("AI 返回的内容格式不对，请再试一次") from exc
    if not isinstance(data, dict):
        raise DraftError("AI 返回的内容格式不对，请再试一次")
    return data


def clean_draft(data: dict, opts: dict) -> dict:
    """AI 的草稿只留认得的东西：技能、连接器、工具必须是真实存在的。"""
    known = {key: {o["value"] for o in opts[key]} for key in ("skills", "mcp", "tools")}
    notes = [str(n).strip() for n in (data.get("notes") or []) if str(n).strip()][:8]

    def pick(key: str, kind: str) -> list[str]:
        raw = data.get(key) or []
        if not isinstance(raw, list):
            return []
        values = _unique(raw)
        dropped = [v for v in values if v not in known[kind]]
        if dropped and kind != "tools":
            notes.append(f"AI 建议的 {'、'.join(dropped)} 在{'技能库' if kind == 'skills' else '连接器'}里没有，已去掉")
        return [v for v in values if v in known[kind]]

    def text(key: str, limit: int) -> str:
        value = data.get(key)
        return str(value).strip()[:limit] if value is not None else ""

    slug = text("id", 40).lower()
    if not re.match(EXPERT_ID_PATTERN, slug):
        slug = ""
    examples = data.get("examples") if isinstance(data.get("examples"), list) else []
    out = {
        "id": slug,
        "name": text("name", 100) or "新专家",
        "avatar": text("avatar", 16),
        "tagline": text("tagline", 200),
        "category": text("category", 50),
        "description": text("description", 4000),
        "instructions": text("instructions", 20000),
        "knowledge": text("knowledge", 20000),
        "examples": _unique(examples)[:MAX_EXAMPLES],
        "preferred_skills": pick("preferred_skills", "skills"),
        "mcp_servers": pick("mcp_servers", "mcp"),
        "disallowed_tools": pick("disallowed_tools", "tools"),
        "read_only": bool(data.get("read_only")),
        "notes": notes,
    }
    return out


async def draft(session: AsyncSession, http: httpx.AsyncClient, text: str) -> dict:
    opts = await options(session)
    prompt = DRAFT_PROMPT.format(
        skills=_listing(opts["skills"], "（技能库是空的）"),
        mcp=_listing(opts["mcp"], "（还没有接入任何外部系统）"),
        tools=_listing(opts["tools"], ""),
    )
    try:
        targets = await model_router.resolve(session, "agent")
    except model_router.NoRouteError as exc:
        raise DraftError("还没有配置「办事」场景的模型，没法让 AI 起草。请先在模型路由里配置") from exc
    messages = [{"role": "system", "content": prompt}, {"role": "user", "content": text}]
    last_error = ""
    for target in targets:
        body = model_router.build_body({"messages": messages, "temperature": 0.3, "stream": False}, target)
        try:
            resp = await http.post(target.url, json=body, headers=model_router.headers_for(target), timeout=120)
        except httpx.HTTPError as exc:
            last_error = f"连不上模型服务：{exc}"
            continue
        if resp.status_code in model_router.RETRYABLE_STATUS:
            last_error = f"模型服务暂时不可用（{resp.status_code}）"
            continue
        if resp.status_code >= 400:
            raise DraftError(f"模型返回错误（{resp.status_code}）：{resp.text[:200]}")
        try:
            content = resp.json()["choices"][0]["message"]["content"] or ""
        except (ValueError, KeyError, IndexError, TypeError) as exc:
            raise DraftError("模型返回的内容看不懂，请再试一次") from exc
        return clean_draft(parse_json(content), opts)
    raise DraftError(last_error or "没有可用的模型")
