"""部门清单、部门安全设置、技能 / 专家 / 连接器的使用范围（管理端）。

隔离在服务端执行：员工端拉列表、下载技能包、派遣专家、拿连接器配置时按范围过滤（见 services/item_scope.py），
这里只负责让 IT 配置范围。改了部门、范围、部门安全设置都会 bump 配置版本，在线的员工端几秒内重新同步。
"""

from __future__ import annotations

import logging
from typing import Literal

from fastapi import APIRouter, Depends, HTTPException, status
from pydantic import BaseModel, Field
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from ..db import get_session
from ..deps import require_admin, require_owner
from ..models import AdminUser, Department, DepartmentPolicy, Device, DevicePolicy, Expert, McpVendor, SkillPackage
from ..services import config_events, department_store, device_security, expert_registry, item_scope, plugin_scope, security_settings, settings_store

log = logging.getLogger("flyknit.departments")

router = APIRouter(prefix="/api/v1/admin", tags=["departments"], dependencies=[Depends(require_admin)])

Kind = Literal["skill", "expert", "mcp"]
_MODELS = {kind: (model, pk, label) for kind, model, pk, label in department_store.SCOPED}


class DepartmentIn(BaseModel):
    name: str = Field(min_length=1, max_length=200)
    note: str = Field(default="", max_length=300)
    sort_order: int = 0


class DepartmentPatch(BaseModel):
    name: str | None = Field(default=None, min_length=1, max_length=200)
    note: str | None = Field(default=None, max_length=300)
    sort_order: int | None = None


class PolicyIn(BaseModel):
    """部门单独的安全设置。只传和全厂不一样的项。"""

    overrides: dict = Field(default_factory=dict)
    locks: dict = Field(default_factory=dict)
    note: str = Field(default="", max_length=300)


class ScopeIn(BaseModel):
    """使用范围：部门名 + 电脑 id。两个都空 = 全公司。"""

    departments: list[str] = Field(default_factory=list, max_length=200)
    devices: list[int] = Field(default_factory=list, max_length=2000)


async def _department(session: AsyncSession, department_id: int) -> Department:
    dept = await session.get(Department, department_id)
    if dept is None:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "部门不存在")
    return dept


async def _out(session: AsyncSession, dept: Department, counts: dict[str, int]) -> dict:
    policy = await session.get(DepartmentPolicy, dept.id)
    return {
        "id": dept.id,
        "name": dept.name,
        "note": dept.note,
        "sort_order": dept.sort_order,
        "devices": counts.get(department_store.key(dept.name), 0),
        # 部门单独设置了几项安全设置（值或锁和全厂不一样）
        "policy_items": len(set((policy.overrides or {}) | (policy.locks or {}))) if policy else 0,
    }


# ---------- 部门清单 ----------

@router.get("/departments")
async def list_departments(session: AsyncSession = Depends(get_session)):
    # 升级上来、或者旧接口写进来的部门名：自动补进清单，IT 看到的就是全集
    await department_store.ensure_from_devices(session)
    counts = await department_store.device_counts(session)
    return [await _out(session, d, counts) for d in await department_store.all_departments(session)]


@router.post("/departments", status_code=201)
async def create_department(data: DepartmentIn, session: AsyncSession = Depends(get_session)):
    name = department_store.clean(data.name)
    if not name:
        raise HTTPException(status.HTTP_400_BAD_REQUEST, "部门名称不能为空")
    if await department_store.find(session, name) is not None:
        raise HTTPException(status.HTTP_409_CONFLICT, f"已经有「{name}」这个部门了")
    dept = Department(name=name, note=data.note.strip(), sort_order=data.sort_order)
    session.add(dept)
    await session.commit()
    log.info("新建部门 %s", name)
    return await _out(session, dept, await department_store.device_counts(session))


@router.patch("/departments/{department_id}")
async def update_department(department_id: int, data: DepartmentPatch, session: AsyncSession = Depends(get_session)):
    dept = await _department(session, department_id)
    if data.name is not None:
        name = department_store.clean(data.name)
        if not name:
            raise HTTPException(status.HTTP_400_BAD_REQUEST, "部门名称不能为空")
        other = await department_store.find(session, name)
        if other is not None and other.id != dept.id:
            raise HTTPException(status.HTTP_409_CONFLICT, f"已经有「{name}」这个部门了")
        if name != dept.name:
            old = dept.name
            # 设备、插件、各类条目范围里的旧名字一起换掉，不然改名就等于把这些电脑移出了部门
            await department_store.rename(session, dept, name)
            log.info("部门改名 %s → %s", old, name)
    if data.note is not None:
        dept.note = data.note.strip()
    if data.sort_order is not None:
        dept.sort_order = data.sort_order
    await session.commit()
    config_events.bump()
    return await _out(session, dept, await department_store.device_counts(session))


@router.delete("/departments/{department_id}", status_code=204)
async def delete_department(department_id: int, session: AsyncSession = Depends(get_session)):
    dept = await _department(session, department_id)
    used = await department_store.usage(session, dept)
    if any(used.values()):
        # 不能直接删：范围里删掉最后一个部门，范围就变空了，空 = 全公司，等于悄悄放大了权限
        parts = []
        if used["devices"]:
            parts.append(f"{len(used['devices'])} 台电脑")
        if used["plugins"]:
            parts.append(f"插件 {'、'.join(used['plugins'][:3])}")
        for kind, (_, _, label) in _MODELS.items():
            if used[kind]:
                parts.append(f"{label} {'、'.join(used[kind][:3])}")
        raise HTTPException(status.HTTP_409_CONFLICT,
                            f"「{dept.name}」还在用：{'；'.join(parts)}。先把这些改到别的部门或从范围里去掉再删")
    await session.delete(dept)
    await session.commit()
    config_events.bump()
    log.info("删除部门 %s", dept.name)


# ---------- 部门安全设置 ----------

def _policy_out(dept: Department, policy: DepartmentPolicy | None, shared: dict) -> dict:
    return {
        "department_id": dept.id,
        "department": dept.name,
        "overrides": policy.overrides if policy else {},
        "locks": policy.locks if policy else {},
        "note": policy.note if policy else "",
        # 这个部门的电脑（没有单独设置时）最终生效的样子
        "effective": security_settings.effective(
            global_values=shared.get("values"), global_locks=shared.get("locks"),
            department_values=policy.overrides if policy else None,
            department_locks=policy.locks if policy else None,
        ),
    }


@router.get("/departments/{department_id}/policy")
async def get_department_policy(department_id: int, session: AsyncSession = Depends(get_session)):
    dept = await _department(session, department_id)
    return _policy_out(dept, await session.get(DepartmentPolicy, dept.id), await settings_store.get_security(session))


@router.put("/departments/{department_id}/policy")
async def set_department_policy(department_id: int, data: PolicyIn,
                                _owner: AdminUser | None = Depends(require_owner),
                                session: AsyncSession = Depends(get_session)):
    """给整个部门放开或锁死几项。限超级管理员：和改全厂默认值、改单台电脑的安全设置一样。"""
    dept = await _department(session, department_id)
    row = await session.get(DepartmentPolicy, dept.id)
    if row is None:
        row = DepartmentPolicy(department_id=dept.id)
        session.add(row)
    row.overrides = security_settings.sanitize(data.overrides)
    row.locks = security_settings.sanitize_locks(data.locks)
    row.note = data.note[:300]
    await session.commit()
    config_events.bump()
    log.info("部门 %s 的安全设置已更新：值 %s，锁 %s", dept.name, row.overrides, row.locks)
    return _policy_out(dept, row, await settings_store.get_security(session))


@router.delete("/departments/{department_id}/policy", status_code=204)
async def clear_department_policy(department_id: int,
                                  _owner: AdminUser | None = Depends(require_owner),
                                  session: AsyncSession = Depends(get_session)):
    """取消部门的单独设置，回到全厂默认。"""
    row = await session.get(DepartmentPolicy, department_id)
    if row is not None:
        await session.delete(row)
        await session.commit()
        config_events.bump()


# ---------- 使用范围 ----------

@router.get("/scopes")
async def list_scopes(session: AsyncSession = Depends(get_session)):
    """所有技能、专家、连接器的使用范围（列表页一次拉齐）。没有设置范围的不返回，界面上按「全公司」显示。"""
    out: dict[str, dict] = {}
    for kind, (model, pk, _) in _MODELS.items():
        out[kind] = {}
        for row in (await session.scalars(select(model))).all():
            scope = item_scope.scope_of(row)
            if scope["departments"] or scope["devices"]:
                out[kind][str(getattr(row, pk))] = scope
    return out


@router.put("/scopes/{kind}/{item_id}")
async def set_scope(kind: Kind, item_id: str, data: ScopeIn, session: AsyncSession = Depends(get_session)):
    model, pk, label = _MODELS[kind]
    row = await session.get(model, item_id)
    if row is None:
        raise HTTPException(status.HTTP_404_NOT_FOUND, f"{label}不存在")
    departments: list[str] = []
    for name in data.departments:
        dept = await department_store.find(session, name)
        if dept is None:
            raise HTTPException(status.HTTP_400_BAD_REQUEST, f"部门「{name}」不在部门清单里，先在「部门」里添加")
        if dept.name not in departments:
            departments.append(dept.name)
    devices: list[int] = []
    for device_id in data.devices:
        if await session.get(Device, device_id) is None:
            raise HTTPException(status.HTTP_400_BAD_REQUEST, f"电脑 #{device_id} 不存在")
        if device_id not in devices:
            devices.append(device_id)
    row.scope_departments = departments
    row.scope_devices = devices
    await session.commit()
    # 在线的员工端马上重新拉列表：范围外的技能删掉、连接器断开、专家收起
    config_events.bump()
    log.info("%s %s 的使用范围：部门 %s，电脑 %s", label, item_id, departments or "全部", devices or "—")
    return {"departments": departments, "devices": devices}


# ---------- 一台电脑能用什么 ----------

@router.get("/devices/{device_id}/access")
async def device_access(device_id: int, session: AsyncSession = Depends(get_session)):
    """这台电脑现在能用的技能、专家、连接器（和员工端拿到的一致），以及最终生效的安全设置和每项来自哪一层。"""
    device = await session.get(Device, device_id)
    if device is None:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "电脑不存在")
    visible = await plugin_scope.visible_ids(session, device)
    skills = (await session.scalars(select(SkillPackage).where(SkillPackage.enabled.is_(True)))).all()
    experts = (await session.scalars(select(Expert).where(Expert.status == "published"))).all()
    vendors = (await session.scalars(select(McpVendor).where(McpVendor.enabled.is_(True)))).all()

    shared = await settings_store.get_security(session)
    dept = await department_store.find(session, device.department)
    dept_policy = await session.get(DepartmentPolicy, dept.id) if dept else None
    mine = await session.get(DevicePolicy, device.id)
    effective = await device_security.effective_for(session, device)
    sources: dict[str, str] = {}
    for key in effective:
        sources[key] = ("device" if mine and (key in (mine.overrides or {}) or key in (mine.locks or {}))
                        else "department" if dept_policy and (key in (dept_policy.overrides or {}) or key in (dept_policy.locks or {}))
                        else "global" if key in (shared.get("values") or {}) or key in (shared.get("locks") or {})
                        else "default")
    return {
        "department": device.department,
        "department_known": dept is not None or not device.department,
        "skills": sorted(s.name for s in skills if item_scope.visible(s, device, visible)),
        "experts": [{"id": e.id, "name": expert_registry.from_snapshot(e.published).name}
                    for e in sorted(experts, key=lambda x: x.id) if item_scope.visible(e, device, visible)],
        "mcp": [{"id": v.id, "name": v.name}
                for v in sorted(vendors, key=lambda x: x.id) if item_scope.visible(v, device, visible)],
        "security": effective,
        "security_sources": sources,
    }
