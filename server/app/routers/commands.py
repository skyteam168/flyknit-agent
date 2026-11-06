"""快捷指令：输入框里打 / 选出来的提示词模板（同 Claude Code 的自定义 slash command）。

IT 在后台发布全公司通用的（例如「/周报 {日期}」），员工也可以在客户端里自己加（只存本机）。
模板里的 {参数} 在员工输入 /名字 参数1 参数2 时按顺序替换。只是一段文字，不带任何权限：
展开后照常作为员工的一句话发给 AI，执行哪一步该确认还是确认。
"""

import re

from fastapi import APIRouter, Depends, HTTPException
from pydantic import BaseModel, Field, field_validator
from sqlalchemy.ext.asyncio import AsyncSession

from ..db import get_session
from ..deps import admin_name, require_admin, require_device
from ..models import Device
from ..services import plugin_scope, settings_store

client_router = APIRouter(prefix="/api/v1/client", tags=["commands"])
admin_router = APIRouter(prefix="/api/v1/admin", tags=["commands"], dependencies=[Depends(require_admin)])

KEY = "slash_commands"
MAX_COMMANDS = 100
#: 指令名：中文、字母、数字、- _，不能有空格和斜杠（打字时「/名字 参数」要分得开）
NAME_RE = re.compile(r"^[\w\-一-鿿]{1,20}$")
#: 客户端自带的指令，不能被后台占用
RESERVED = {"btw"}


class CommandItem(BaseModel):
    name: str = Field(min_length=1, max_length=20)
    title: str = Field(default="", max_length=60)
    description: str = Field(default="", max_length=300)
    template: str = Field(min_length=1, max_length=4000)

    @field_validator("name")
    @classmethod
    def _name(cls, v: str) -> str:
        v = v.strip().lstrip("/")
        if not NAME_RE.match(v):
            raise ValueError("指令名只能用中文、字母、数字、- 和 _，不超过 20 个字，不能有空格")
        if v.lower() in RESERVED:
            raise ValueError(f"/{v} 是客户端自带的指令，换个名字")
        return v


class CommandsIn(BaseModel):
    items: list[CommandItem] = Field(default_factory=list, max_length=MAX_COMMANDS)


async def _items(session: AsyncSession) -> list[dict]:
    return (await settings_store.get_value(session, KEY, {"items": []})).get("items", [])


@admin_router.get("/commands")
async def admin_list(session: AsyncSession = Depends(get_session)):
    return {"items": await _items(session)}


@admin_router.put("/commands")
async def admin_save(data: CommandsIn, who: str = Depends(admin_name), session: AsyncSession = Depends(get_session)):
    names = [c.name.lower() for c in data.items]
    dup = {n for n in names if names.count(n) > 1}
    if dup:
        raise HTTPException(400, f"指令名重复：{'、'.join(sorted(dup))}")
    items = [c.model_dump() for c in data.items]
    # 存进设置表：改完在线的员工端马上来拉（config_events）
    await settings_store.set_value(session, KEY, {"items": items, "updated_by": who})
    return {"items": items}


@client_router.get("/commands")
async def client_list(device: Device = Depends(require_device), session: AsyncSession = Depends(get_session)):
    """后台发布的 + 这台电脑范围内插件带来的。同名时后台的优先（插件的不覆盖 IT 手写的）。"""
    items = [{**c, "source": ""} for c in await _items(session)]
    taken = {c["name"].lower() for c in items} | RESERVED
    for p in await plugin_scope.visible(session, device):
        for c in (p.manifest or {}).get("commands", []):
            if c.get("name", "").lower() in taken:
                continue
            taken.add(c["name"].lower())
            items.append({**c, "source": p.name or p.id})
    return {"items": items}
