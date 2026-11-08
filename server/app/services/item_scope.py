"""技能、专家、连接器的使用范围：这台电脑能不能用某一条。

规则（服务端执行，员工端拿不到范围外条目的任何内容）：
- 范围 = 部门 ∪ 电脑。两个都空 = 全公司（升级前的老数据就是这样，行为不变）；
- 插件带来的条目还要同时在插件的下发范围里（plugin_scope）。
"""

from __future__ import annotations

from typing import Any

from ..models import Device
from . import plugin_scope
from .department_store import key


def scope_of(row: Any) -> dict[str, list]:
    return {"departments": list(row.scope_departments or []), "devices": list(row.scope_devices or [])}


def in_scope(row: Any, device: Device | None) -> bool:
    """只看条目自己的范围（不看插件）。"""
    departments = [d for d in (row.scope_departments or []) if key(d)]
    devices = {int(d) for d in (row.scope_devices or []) if str(d).isdigit()}
    if not departments and not devices:
        return True
    if device is None:
        return False
    if device.id in devices:
        return True
    return key(device.department) in {key(d) for d in departments}


def visible(row: Any, device: Device | None, plugins_visible: set[str]) -> bool:
    """插件范围和条目范围都满足才看得到。"""
    return plugin_scope.allowed(getattr(row, "plugin", ""), plugins_visible) and in_scope(row, device)
