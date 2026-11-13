"""
团队（docs/team-mode.md）：2～8 位专家组成，主助手当队长。

- 员工端：列出这台电脑能用的团队；建、改、解散、复制自己部门或个人的团队。
- 管理端：IT 建 / 改 / 下发团队；查看所有团队，解散、恢复。

范围在这里执行：
- 团队的可见范围（IT 下发 = 使用部门 / 电脑；部门团队 = 同部门；个人团队 = 只有建的人）；
- 成员专家也要在使用者的范围内：建、改时不在范围内的拒收，列出时标成不可用（开工时跳过）。
"""

import logging
import secrets
from datetime import datetime, timedelta, timezone

from fastapi import APIRouter, Depends, HTTPException, status
from sqlalchemy import func, select
from sqlalchemy.ext.asyncio import AsyncSession

from ..db import get_session
from ..deps import admin_name, require_admin, require_device
from ..models import Device, Expert, ModelConfig, Team, TeamNote, TeamRun
from ..schemas import (
    TeamAdminIn,
    TeamAdminOut,
    TeamIn,
    TeamMemberOut,
    TeamNoteIn,
    TeamNoteOut,
    TeamOut,
    TeamRatingIn,
    TeamRunAdminOut,
    TeamRunIn,
    TeamStatOut,
    TeamTemplateOut,
)
from ..services import config_events, department_store, device_security, item_scope, plugin_scope, team_service, team_templates

log = logging.getLogger("flyknit.teams")
admin_router = APIRouter(prefix="/api/v1/admin/teams", tags=["teams"], dependencies=[Depends(require_admin)])
client_router = APIRouter(prefix="/api/v1/client/teams", tags=["teams"])


def _new_id() -> str:
    return "tm_" + secrets.token_hex(8)


async def _setting(session: AsyncSession, device: Device, key: str):
    return (await device_security.effective_for(session, device))[key]["value"]


async def _require_on(session: AsyncSession, device: Device) -> None:
    if not await _setting(session, device, "teams"):
        raise HTTPException(status.HTTP_403_FORBIDDEN, {"code": "teams_disabled", "message": "管理员没有开启团队模式"})


async def _usable_experts(session: AsyncSession, device: Device | None) -> set[str]:
    """这台电脑现在能用的专家：已发布、在使用范围和插件范围内。device 为空（管理端）= 所有已发布的。"""
    rows = (await session.scalars(select(Expert).where(Expert.status == "published"))).all()
    if device is None:
        return {e.id for e in rows}
    visible = await plugin_scope.visible_ids(session, device)
    return {e.id for e in rows if item_scope.visible(e, device, visible)}


def _visible(team: Team, device: Device) -> bool:
    return team_service.visible(team, device)


def _members_out(team: Team, usable: set[str]) -> list[TeamMemberOut]:
    return [TeamMemberOut(expert_id=m.get("expert_id", ""), note=m.get("note", ""), available=m.get("expert_id") in usable)
            for m in (team.members or [])]


def _out(team: Team, usable: set[str], device: Device | None) -> TeamOut:
    editable = device is not None and team.source in ("department", "personal") and team.owner_device_id == device.id
    return TeamOut(
        id=team.id, name=team.name, description=team.description, icon=team.icon or "team", leader_note=team.leader_note,
        members=_members_out(team, usable), model_id=team.model_id, disabled_tools=list(team.disabled_tools or []),
        read_only=bool(team.read_only), source=team.source, owner_name=team.owner_name, department=team.department,
        editable=editable, runs=team.runs or 0, leader_expert_id=team.leader_expert_id or "",
        leader_available=bool(team.leader_expert_id) and team.leader_expert_id in usable, examples=list(team.examples or []),
        created_at=team.created_at, updated_at=team.updated_at,
    )


def _admin_out(team: Team, usable: set[str]) -> TeamAdminOut:
    base = _out(team, usable, None).model_dump()
    return TeamAdminOut(**base, scope_departments=list(team.scope_departments or []), scope_devices=list(team.scope_devices or []),
                        owner_device_id=team.owner_device_id, dissolved_at=team.dissolved_at, dissolved_by=team.dissolved_by)


async def _check_members(session: AsyncSession, members: list, usable: set[str], max_members: int) -> list[dict]:
    """成员：不重复、2～上限位、都在使用者能用的专家里。返回要存的 [{expert_id, note}]。"""
    ids = [m.expert_id for m in members]
    if len(set(ids)) != len(ids):
        raise HTTPException(status.HTTP_400_BAD_REQUEST, "同一位专家不能加两次")
    if len(ids) < 2:
        raise HTTPException(status.HTTP_400_BAD_REQUEST, "团队至少要 2 位专家")
    if len(ids) > max_members:
        raise HTTPException(status.HTTP_400_BAD_REQUEST, f"团队最多 {max_members} 位专家")
    missing = [i for i in ids if i not in usable]
    if missing:
        names = {e.id: e.name for e in (await session.scalars(select(Expert).where(Expert.id.in_(missing)))).all()}
        raise HTTPException(status.HTTP_400_BAD_REQUEST,
                            f"这些专家不能加入团队（已下线或不在使用范围内）：{'、'.join(names.get(i, i) for i in missing)}")
    return [{"expert_id": m.expert_id, "note": m.note.strip()} for m in members]


def _check_leader(leader: str, members: list[dict], usable: set[str]) -> str:
    """队长专家：可以不设；设了要在使用者能用的专家里，而且不能同时是成员（队长不派自己）。"""
    leader = (leader or "").strip()
    if not leader:
        return ""
    if leader not in usable:
        raise HTTPException(status.HTTP_400_BAD_REQUEST, "这位专家不能当队长（已下线或不在使用范围内）")
    if leader in {m["expert_id"] for m in members}:
        raise HTTPException(status.HTTP_400_BAD_REQUEST, "队长不能同时是成员")
    return leader


def _clean_examples(examples: list[str]) -> list[str]:
    seen: list[str] = []
    for e in examples:
        e = " ".join(e.split())
        if e and e not in seen:
            seen.append(e)
    return seen[:5]


async def _check_model(session: AsyncSession, model_id: int | None) -> int | None:
    if model_id is None:
        return None
    m = await session.get(ModelConfig, model_id)
    if m is None or not m.enabled or not m.supports_tools:
        raise HTTPException(status.HTTP_400_BAD_REQUEST, "这个模型不能用于团队（不存在、已停用或不支持工具调用）")
    return model_id


async def _check_name(session: AsyncSession, name: str, source: str, department: str, owner: int | None, exclude: str | None) -> None:
    """同一部门的部门团队不能重名；IT 团队之间不能重名；个人团队只和自己的比。"""
    q = select(Team).where(Team.dissolved_at.is_(None), Team.name == name, Team.source == source)
    for t in (await session.scalars(q)).all():
        if t.id == exclude:
            continue
        if source == "department" and department_store.key(t.department) != department_store.key(department):
            continue
        if source == "personal" and t.owner_device_id != owner:
            continue
        raise HTTPException(status.HTTP_409_CONFLICT, f"已经有叫「{name}」的团队了，换一个名字")


# ---------- 员工端 ----------
@client_router.get("", response_model=list[TeamOut])
async def client_teams(device: Device = Depends(require_device), session: AsyncSession = Depends(get_session)):
    """这台电脑能用的团队。团队模式关着时返回空列表（员工端也看不到入口）。"""
    if not await _setting(session, device, "teams"):
        return []
    usable = await _usable_experts(session, device)
    rows = (await session.scalars(select(Team).order_by(Team.created_at))).all()
    order = {"it": 0, "department": 1, "personal": 2}
    teams = [t for t in rows if _visible(t, device)]
    teams.sort(key=lambda t: (order.get(t.source, 3), -(t.runs or 0)))
    return [_out(t, usable, device) for t in teams]


@client_router.post("", response_model=TeamOut, status_code=201)
async def create_team(data: TeamIn, device: Device = Depends(require_device), session: AsyncSession = Depends(get_session)):
    await _require_on(session, device)
    if not await _setting(session, device, "team_create"):
        raise HTTPException(status.HTTP_403_FORBIDDEN, {"code": "team_create_disabled", "message": "管理员没有开放员工自建团队"})
    source = data.share
    department = (device.department or "").strip()
    if source == "department" and not department:
        raise HTTPException(status.HTTP_400_BAD_REQUEST, "这台电脑还没有登记部门，只能建「只我自己」的团队")
    usable = await _usable_experts(session, device)
    members = await _check_members(session, data.members, usable, int(await _setting(session, device, "team_max_members")))
    name = data.name.strip()
    await _check_name(session, name, source, department, device.id, None)
    team = Team(
        id=_new_id(), name=name, description=data.description.strip(), icon=data.icon or "team", leader_note=data.leader_note.strip(),
        members=members, model_id=await _check_model(session, data.model_id), disabled_tools=list(dict.fromkeys(data.disabled_tools)),
        read_only=data.read_only, source=source, owner_device_id=device.id, owner_name=device.user_name or device.machine_name or "",
        department=department if source == "department" else "", scope_departments=[], scope_devices=[],
        leader_expert_id=_check_leader(data.leader_expert_id, members, usable), examples=_clean_examples(data.examples),
    )
    session.add(team)
    await session.commit()
    log.info("设备 %s 建了团队 %s（%s，%d 位成员）", device.id, team.id, source, len(members))
    config_events.bump()
    return _out(team, usable, device)


async def _own(session: AsyncSession, team_id: str, device: Device) -> Team:
    team = await session.get(Team, team_id)
    if team is None or not _visible(team, device):
        raise HTTPException(status.HTTP_404_NOT_FOUND, "团队不存在或已解散")
    if team.source not in ("department", "personal") or team.owner_device_id != device.id:
        raise HTTPException(status.HTTP_403_FORBIDDEN, "只有建这个团队的人能修改或解散它")
    return team


@client_router.put("/{team_id}", response_model=TeamOut)
async def update_team(team_id: str, data: TeamIn, device: Device = Depends(require_device),
                      session: AsyncSession = Depends(get_session)):
    await _require_on(session, device)
    team = await _own(session, team_id, device)
    if data.updated_at is not None and _aware(team.updated_at) != _aware(data.updated_at):
        raise HTTPException(status.HTTP_409_CONFLICT, "这个团队刚被改过，请刷新后再改")
    source = data.share
    department = (device.department or "").strip()
    if source == "department" and not department:
        raise HTTPException(status.HTTP_400_BAD_REQUEST, "这台电脑还没有登记部门，只能建「只我自己」的团队")
    usable = await _usable_experts(session, device)
    members = await _check_members(session, data.members, usable, int(await _setting(session, device, "team_max_members")))
    name = data.name.strip()
    await _check_name(session, name, source, department, device.id, team.id)
    team.name = name
    team.description = data.description.strip()
    team.icon = data.icon or "team"
    team.leader_note = data.leader_note.strip()
    team.members = members
    team.model_id = await _check_model(session, data.model_id)
    team.disabled_tools = list(dict.fromkeys(data.disabled_tools))
    team.read_only = data.read_only
    team.leader_expert_id = _check_leader(data.leader_expert_id, members, usable)
    team.examples = _clean_examples(data.examples)
    team.source = source
    team.department = department if source == "department" else ""
    await session.commit()
    config_events.bump()
    return _out(team, usable, device)


@client_router.post("/{team_id}/dissolve", status_code=204)
async def dissolve_team(team_id: str, device: Device = Depends(require_device), session: AsyncSession = Depends(get_session)):
    team = await _own(session, team_id, device)
    team.dissolved_at = datetime.now(timezone.utc)
    team.dissolved_by = f"device:{device.id}"
    await session.commit()
    log.info("设备 %s 解散了团队 %s", device.id, team.id)
    config_events.bump()


@client_router.post("/{team_id}/copy", response_model=TeamOut, status_code=201)
async def copy_team(team_id: str, device: Device = Depends(require_device), session: AsyncSession = Depends(get_session)):
    """复制为「我的团队」：只复制这台电脑能用的成员。"""
    await _require_on(session, device)
    if not await _setting(session, device, "team_create"):
        raise HTTPException(status.HTTP_403_FORBIDDEN, {"code": "team_create_disabled", "message": "管理员没有开放员工自建团队"})
    src = await session.get(Team, team_id)
    if src is None or not _visible(src, device):
        raise HTTPException(status.HTTP_404_NOT_FOUND, "团队不存在或已解散")
    usable = await _usable_experts(session, device)
    members = [m for m in (src.members or []) if m.get("expert_id") in usable]
    if len(members) < 2:
        raise HTTPException(status.HTTP_400_BAD_REQUEST, "这个团队里你能用的专家不到 2 位，没法复制")
    base = f"{src.name}（我的）"[:30]
    name, n = base, 2
    while True:
        try:
            await _check_name(session, name, "personal", "", device.id, None)
            break
        except HTTPException:
            name = f"{base[:27]}{n}"
            n += 1
    team = Team(
        id=_new_id(), name=name, description=src.description, icon=src.icon, leader_note=src.leader_note, members=members,
        model_id=src.model_id, disabled_tools=list(src.disabled_tools or []), read_only=bool(src.read_only), source="personal",
        owner_device_id=device.id, owner_name=device.user_name or device.machine_name or "", department="",
        scope_departments=[], scope_devices=[],
        leader_expert_id=src.leader_expert_id if src.leader_expert_id in usable else "", examples=list(src.examples or []),
    )
    session.add(team)
    await session.commit()
    config_events.bump()
    return _out(team, usable, device)


@client_router.post("/{team_id}/start", status_code=204)
async def start_team(team_id: str, device: Device = Depends(require_device), session: AsyncSession = Depends(get_session)):
    """开始一次团队任务时报一下，只记次数（团队卡片上的「用过几次」）。"""
    await _require_on(session, device)
    team = await session.get(Team, team_id)
    if team is None or not _visible(team, device):
        raise HTTPException(status.HTTP_404_NOT_FOUND, "团队不存在或已解散")
    team.runs = (team.runs or 0) + 1
    await session.commit()


async def _usable_team(session: AsyncSession, team_id: str, device: Device) -> Team:
    """团队模式开着、这台电脑看得到的团队；否则 403 / 404。"""
    await _require_on(session, device)
    team = await session.get(Team, team_id)
    if team is None or not _visible(team, device):
        raise HTTPException(status.HTTP_404_NOT_FOUND, "团队不存在或已解散")
    return team


@client_router.post("/{team_id}/runs", status_code=204)
async def report_run(team_id: str, data: TeamRunIn, device: Device = Depends(require_device),
                     session: AsyncSession = Depends(get_session)):
    """一次团队任务的结果数字（docs/team-mode.md 第 10 节）。同一轮再报一次按最后一次算；评价不会被覆盖。"""
    await _usable_team(session, team_id, device)
    run = await session.scalar(select(TeamRun).where(TeamRun.device_id == device.id, TeamRun.run_id == data.run_id))
    if run is None:
        run = TeamRun(run_id=data.run_id, device_id=device.id, team_id=team_id)
        session.add(run)
    elif run.team_id != team_id:
        raise HTTPException(status.HTTP_409_CONFLICT, "这次任务已经记在别的团队下")
    run.department = (device.department or "").strip()
    run.planned, run.completed, run.failed, run.stopped = data.planned, data.completed, data.failed, data.stopped
    run.reassigned, run.tokens, run.duration_seconds, run.status = data.reassigned, data.tokens, data.duration_seconds, data.status
    await session.commit()


@client_router.post("/{team_id}/runs/{run_id}/rating", status_code=204)
async def rate_run(team_id: str, run_id: str, data: TeamRatingIn, device: Device = Depends(require_device),
                   session: AsyncSession = Depends(get_session)):
    """员工对团队报告的 👍 / 👎 和一句话反馈。只能评自己电脑上的任务。"""
    await _usable_team(session, team_id, device)
    run = await session.scalar(select(TeamRun).where(TeamRun.device_id == device.id, TeamRun.run_id == run_id,
                                                     TeamRun.team_id == team_id))
    if run is None:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "没有这次团队任务的记录")
    run.rating = data.rating
    run.feedback = team_service.mask_personal(" ".join(data.feedback.split())) if data.rating else ""
    await session.commit()


def _note_out(note: TeamNote, deletable: bool) -> TeamNoteOut:
    return TeamNoteOut(id=note.id, team_id=note.team_id, text=note.content, kind=note.kind, source=note.source,
                       author=note.author, created_at=note.created_at, deletable=deletable)


def _can_delete_note(note: TeamNote, team: Team, device: Device) -> bool:
    return note.device_id == device.id or (team.source in ("department", "personal") and team.owner_device_id == device.id)


async def _add_note(session: AsyncSession, team: Team, text: str, kind: str, source: str, device: Device | None, author: str) -> TeamNote:
    text = " ".join(text.split())
    found = team_service.secrets_in(text)
    if found:
        raise HTTPException(status.HTTP_400_BAD_REQUEST,
                            {"code": "sensitive", "message": f"经验里有{'、'.join(dict.fromkeys(found))}，不能记"})
    count = await session.scalar(select(func.count()).select_from(TeamNote).where(TeamNote.team_id == team.id))
    if (count or 0) >= team_service.MAX_NOTES:
        raise HTTPException(status.HTTP_409_CONFLICT,
                            {"code": "notes_full", "message": f"这个团队的经验已经有 {team_service.MAX_NOTES} 条，请先清理过时的"})
    note = TeamNote(id="tn_" + secrets.token_hex(8), team_id=team.id, content=team_service.mask_personal(text)[:300], kind=kind,
                    source=source, device_id=device.id if device else None, author=author[:200])
    session.add(note)
    await session.commit()
    return note


@client_router.get("/{team_id}/notes", response_model=list[TeamNoteOut])
async def client_notes(team_id: str, device: Device = Depends(require_device), session: AsyncSession = Depends(get_session)):
    """团队经验（最近的在前）。看得到团队就看得到它的经验。"""
    team = await _usable_team(session, team_id, device)
    rows = (await session.scalars(select(TeamNote).where(TeamNote.team_id == team_id)
                                  .order_by(TeamNote.created_at.desc()).limit(team_service.MAX_NOTES))).all()
    return [_note_out(n, _can_delete_note(n, team, device)) for n in rows]


@client_router.post("/{team_id}/notes", response_model=TeamNoteOut, status_code=201)
async def client_add_note(team_id: str, data: TeamNoteIn, device: Device = Depends(require_device),
                          session: AsyncSession = Depends(get_session)):
    team = await _usable_team(session, team_id, device)
    note = await _add_note(session, team, data.text, data.kind, data.source, device, device.user_name or device.machine_name or "")
    log.info("设备 %s 给团队 %s 记了一条经验（%s）", device.id, team_id, data.source)
    return _note_out(note, True)


@client_router.delete("/{team_id}/notes/{note_id}", status_code=204)
async def client_delete_note(team_id: str, note_id: str, device: Device = Depends(require_device),
                             session: AsyncSession = Depends(get_session)):
    team = await _usable_team(session, team_id, device)
    note = await session.get(TeamNote, note_id)
    if note is None or note.team_id != team_id:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "这条经验不存在")
    if not _can_delete_note(note, team, device):
        raise HTTPException(status.HTTP_403_FORBIDDEN, "只有写这条经验的人、团队创建人或 IT 能删")
    await session.delete(note)
    await session.commit()


def _aware(dt: datetime | None) -> datetime | None:
    """SQLite 读回来的时间不带时区：统一当 UTC 比较。"""
    if dt is None:
        return None
    return dt if dt.tzinfo else dt.replace(tzinfo=timezone.utc)


# ---------- 管理端 ----------
@admin_router.get("/templates", response_model=list[TeamTemplateOut])
async def admin_templates():
    """建团队用的模板：每个位置要什么样的专家、队长要求、示例任务（专家由 IT 在本厂的专家里挑）。"""
    return [TeamTemplateOut(**t) for t in team_templates.TEMPLATES]


@admin_router.get("/stats", response_model=list[TeamStatOut])
async def admin_stats(days: int = 30, session: AsyncSession = Depends(get_session)):
    """每个团队最近 days 天的表现：完成率、补派率、平均 token 和耗时、👍 / 👎、经验条数。"""
    days = max(1, min(days, 365))
    since = datetime.now(timezone.utc) - timedelta(days=days)
    runs = (await session.scalars(select(TeamRun).where(TeamRun.created_at >= since))).all()
    notes = dict((await session.execute(select(TeamNote.team_id, func.count()).group_by(TeamNote.team_id))).all())
    by_team: dict[str, list[TeamRun]] = {}
    for r in runs:
        by_team.setdefault(r.team_id, []).append(r)
    out: list[TeamStatOut] = []
    for team in (await session.scalars(select(Team).order_by(Team.created_at))).all():
        rs = by_team.get(team.id, [])
        planned = sum(r.planned for r in rs)
        completed = sum(r.completed for r in rs)
        n = len(rs)
        out.append(TeamStatOut(
            team_id=team.id, name=team.name, source=team.source, department=team.department, dissolved=team.dissolved_at is not None,
            runs=n, planned=planned, completed=completed,
            completion_rate=round(completed / planned, 3) if planned else 0,
            reassign_rate=round(sum(1 for r in rs if r.reassigned) / n, 3) if n else 0,
            avg_tokens=round(sum(r.tokens for r in rs) / n) if n else 0,
            avg_seconds=round(sum(r.duration_seconds for r in rs) / n) if n else 0,
            up=sum(1 for r in rs if r.rating > 0), down=sum(1 for r in rs if r.rating < 0), notes=int(notes.get(team.id, 0)),
        ))
    out.sort(key=lambda s: (-s.runs, s.name))
    return out


@admin_router.get("/{team_id}/runs", response_model=list[TeamRunAdminOut])
async def admin_runs(team_id: str, session: AsyncSession = Depends(get_session)):
    """这个团队最近 100 次任务的数字和员工反馈（不含对话内容）。"""
    rows = (await session.scalars(select(TeamRun).where(TeamRun.team_id == team_id)
                                  .order_by(TeamRun.created_at.desc()).limit(100))).all()
    devices = {d.id: d for d in (await session.scalars(select(Device).where(Device.id.in_({r.device_id for r in rows})))).all()} if rows else {}
    return [TeamRunAdminOut(
        run_id=r.run_id, device_id=r.device_id,
        device_name=(devices[r.device_id].user_name or devices[r.device_id].machine_name) if r.device_id in devices else "",
        department=r.department, planned=r.planned, completed=r.completed, failed=r.failed, stopped=r.stopped,
        reassigned=r.reassigned, tokens=r.tokens, duration_seconds=r.duration_seconds, status=r.status, rating=r.rating,
        feedback=r.feedback, created_at=r.created_at,
    ) for r in rows]


@admin_router.get("/{team_id}/notes", response_model=list[TeamNoteOut])
async def admin_notes(team_id: str, session: AsyncSession = Depends(get_session)):
    rows = (await session.scalars(select(TeamNote).where(TeamNote.team_id == team_id).order_by(TeamNote.created_at.desc()))).all()
    return [_note_out(n, True) for n in rows]


@admin_router.post("/{team_id}/notes", response_model=TeamNoteOut, status_code=201)
async def admin_add_note(team_id: str, data: TeamNoteIn, who: str = Depends(admin_name), session: AsyncSession = Depends(get_session)):
    team = await session.get(Team, team_id)
    if team is None:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "团队不存在")
    note = await _add_note(session, team, data.text, data.kind, "it", None, who)
    return _note_out(note, True)


@admin_router.delete("/{team_id}/notes/{note_id}", status_code=204)
async def admin_delete_note(team_id: str, note_id: str, who: str = Depends(admin_name), session: AsyncSession = Depends(get_session)):
    note = await session.get(TeamNote, note_id)
    if note is None or note.team_id != team_id:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "这条经验不存在")
    await session.delete(note)
    await session.commit()
    log.info("%s 删了团队 %s 的一条经验", who, team_id)


@admin_router.get("", response_model=list[TeamAdminOut])
async def admin_teams(session: AsyncSession = Depends(get_session)):
    """所有团队，包括各部门自建的和已解散的（IT 可以查看、解散、恢复）。"""
    usable = await _usable_experts(session, None)
    rows = (await session.scalars(select(Team).order_by(Team.created_at))).all()
    return [_admin_out(t, usable) for t in rows]


@admin_router.post("", response_model=TeamAdminOut, status_code=201)
async def admin_create(data: TeamAdminIn, who: str = Depends(admin_name), session: AsyncSession = Depends(get_session)):
    usable = await _usable_experts(session, None)
    members = await _check_members(session, data.members, usable, 8)
    name = data.name.strip()
    await _check_name(session, name, "it", "", None, None)
    team = Team(
        id=_new_id(), name=name, description=data.description.strip(), icon=data.icon or "team", leader_note=data.leader_note.strip(),
        members=members, model_id=await _check_model(session, data.model_id), disabled_tools=list(dict.fromkeys(data.disabled_tools)),
        read_only=data.read_only, source="it", owner_device_id=None, owner_name="IT", department="",
        scope_departments=[d.strip() for d in data.scope_departments if d.strip()], scope_devices=list(dict.fromkeys(data.scope_devices)),
        leader_expert_id=_check_leader(data.leader_expert_id, members, usable), examples=_clean_examples(data.examples),
    )
    session.add(team)
    await session.commit()
    log.info("%s 建了团队 %s", who, team.id)
    config_events.bump()
    return _admin_out(team, usable)


@admin_router.put("/{team_id}", response_model=TeamAdminOut)
async def admin_update(team_id: str, data: TeamAdminIn, who: str = Depends(admin_name), session: AsyncSession = Depends(get_session)):
    team = await session.get(Team, team_id)
    if team is None:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "团队不存在")
    if team.source != "it":
        raise HTTPException(status.HTTP_400_BAD_REQUEST, "部门和个人建的团队由建的人修改；后台只能查看、解散或恢复")
    usable = await _usable_experts(session, None)
    members = await _check_members(session, data.members, usable, 8)
    name = data.name.strip()
    await _check_name(session, name, "it", "", None, team.id)
    team.name = name
    team.description = data.description.strip()
    team.icon = data.icon or "team"
    team.leader_note = data.leader_note.strip()
    team.members = members
    team.model_id = await _check_model(session, data.model_id)
    team.disabled_tools = list(dict.fromkeys(data.disabled_tools))
    team.read_only = data.read_only
    team.leader_expert_id = _check_leader(data.leader_expert_id, members, usable)
    team.examples = _clean_examples(data.examples)
    team.scope_departments = [d.strip() for d in data.scope_departments if d.strip()]
    team.scope_devices = list(dict.fromkeys(data.scope_devices))
    await session.commit()
    log.info("%s 改了团队 %s", who, team.id)
    config_events.bump()
    return _admin_out(team, usable)


@admin_router.post("/{team_id}/dissolve", response_model=TeamAdminOut)
async def admin_dissolve(team_id: str, who: str = Depends(admin_name), session: AsyncSession = Depends(get_session)):
    team = await session.get(Team, team_id)
    if team is None:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "团队不存在")
    if team.dissolved_at is None:
        team.dissolved_at = datetime.now(timezone.utc)
        team.dissolved_by = who
        await session.commit()
        log.info("%s 解散了团队 %s", who, team.id)
        config_events.bump()
    return _admin_out(team, await _usable_experts(session, None))


@admin_router.post("/{team_id}/restore", response_model=TeamAdminOut)
async def admin_restore(team_id: str, who: str = Depends(admin_name), session: AsyncSession = Depends(get_session)):
    team = await session.get(Team, team_id)
    if team is None:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "团队不存在")
    if team.dissolved_at is not None:
        await _check_name(session, team.name, team.source, team.department, team.owner_device_id, team.id)
        team.dissolved_at = None
        team.dissolved_by = ""
        await session.commit()
        log.info("%s 恢复了团队 %s", who, team.id)
        config_events.bump()
    return _admin_out(team, await _usable_experts(session, None))
