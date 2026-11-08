"""一台电脑最终生效的安全设置：全厂默认 → 所在部门 → 这台电脑，后面的覆盖前面的。"""

from __future__ import annotations

from typing import Any

from sqlalchemy.ext.asyncio import AsyncSession

from ..models import DepartmentPolicy, Device, DevicePolicy
from . import department_store, security_settings, settings_store


async def department_policy(session: AsyncSession, department: str | None) -> DepartmentPolicy | None:
    dept = await department_store.find(session, department)
    return await session.get(DepartmentPolicy, dept.id) if dept is not None else None


async def effective_for(session: AsyncSession, device: Device) -> dict[str, dict[str, Any]]:
    shared = await settings_store.get_security(session)
    dept = await department_policy(session, device.department)
    mine = await session.get(DevicePolicy, device.id)
    return security_settings.effective(
        global_values=shared.get("values"),
        department_values=dept.overrides if dept else None,
        device_values=mine.overrides if mine else None,
        global_locks=shared.get("locks"),
        department_locks=dept.locks if dept else None,
        device_locks=mine.locks if mine else None,
    )
