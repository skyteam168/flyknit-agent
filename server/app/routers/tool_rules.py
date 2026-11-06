"""工具规则：IT 在后台写的「AI 调用工具前后」规则（同 Claude Code 的 hooks、DeepSeek Harness 的 tools/pre-execute、post-execute）。

每条规则按工具名（支持 * 通配，例如 mcp__erp__*）和参数内容（正则，可选）匹配：
- block：直接拦下，AI 看到规则说明；
- confirm：必须员工确认，「以后自动允许」和「本次对话都允许」都跳不过；
- note：照常执行，在结果前面加一句公司规定，提醒 AI（例如「改 ERP 前必须先查库存」）。
另外可以写 redact：工具结果里匹配的内容替换成 ***（例如身份证号、手机号），AI 看不到原文。

规则只能收紧，不能放行任何本来要确认或被拦的操作。下发在员工端配置里（/client/config 的 tool_rules），改完马上生效。
"""

import re

from fastapi import APIRouter, Depends
from pydantic import BaseModel, Field, field_validator
from sqlalchemy.ext.asyncio import AsyncSession

from ..db import get_session
from ..deps import admin_name, require_admin
from ..models import Device
from ..services import plugin_scope, settings_store

admin_router = APIRouter(prefix="/api/v1/admin", tags=["tool-rules"], dependencies=[Depends(require_admin)])

KEY = "tool_rules"
MAX_RULES = 100


def _regex(v: str, field: str) -> str:
    v = v.strip()
    if v:
        try:
            re.compile(v)
        except re.error as e:
            raise ValueError(f"{field}不是合法的正则表达式：{e}") from e
    return v


class ToolRule(BaseModel):
    name: str = Field(min_length=1, max_length=60)
    enabled: bool = True
    #: 工具名，多个用逗号分开，支持 * 通配；空或 * 表示所有工具
    tools: str = Field(default="*", max_length=500)
    #: 参数（JSON 原文）里要匹配的正则；空表示不看参数
    match: str = Field(default="", max_length=500)
    action: str = Field(default="note", pattern="^(block|confirm|note|none)$")
    #: block / confirm 时给员工和 AI 看的原因；note 时加在结果前面的话
    message: str = Field(default="", max_length=500)
    #: 结果里要打码的内容（正则），空表示不打码
    redact: str = Field(default="", max_length=500)

    @field_validator("match")
    @classmethod
    def _match(cls, v: str) -> str:
        return _regex(v, "参数匹配")

    @field_validator("redact")
    @classmethod
    def _redact(cls, v: str) -> str:
        return _regex(v, "结果打码")


class ToolRulesIn(BaseModel):
    items: list[ToolRule] = Field(default_factory=list, max_length=MAX_RULES)


async def current(session: AsyncSession, device: Device | None = None) -> list[dict]:
    """员工端配置里下发的规则（只带启用的）：后台写的 + 这台电脑范围内插件带来的。"""
    items = list((await settings_store.get_value(session, KEY, {"items": []})).get("items", []))
    for p in await plugin_scope.visible(session, device):
        for r in (p.manifest or {}).get("rules", []):
            items.append({**r, "name": f"[{p.name or p.id}] {r.get('name', '')}".strip()})
    return [r for r in items if r.get("enabled", True)]


@admin_router.get("/tool-rules")
async def admin_list(session: AsyncSession = Depends(get_session)):
    return {"items": (await settings_store.get_value(session, KEY, {"items": []})).get("items", [])}


@admin_router.put("/tool-rules")
async def admin_save(data: ToolRulesIn, who: str = Depends(admin_name), session: AsyncSession = Depends(get_session)):
    items = [r.model_dump() for r in data.items]
    await settings_store.set_value(session, KEY, {"items": items, "updated_by": who})
    return {"items": items}
