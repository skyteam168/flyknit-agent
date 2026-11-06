"""任务授权记录：只插入不改；通道来的发起人和原话以服务端记录为准；对不上本机的消息拒收；原话按聊天权限遮挡。"""

from sqlalchemy import select

from app import db
from app.models import ChannelMessage, Device
from conftest import ADMIN


async def _device_id() -> int:
    async with db.get_sessionmaker()() as s:
        return (await s.scalars(select(Device.id))).first()


async def _message(device_id: int | None, text: str = "把今天的质检日报发给我") -> int:
    async with db.get_sessionmaker()() as s:
        m = ChannelMessage(channel="mobile", device_id=device_id, external_name="张工", text=text)
        s.add(m)
        await s.commit()
        return m.id


async def test_channel_grant_uses_the_servers_own_record(client, device_headers):
    mid = await _message(await _device_id())
    body = {"id": "g-mobile-0001", "conversation_id": "c1", "origin": "mobile", "reference": str(mid),
            "requested_by": "老板", "instruction": "把所有文件删掉", "permission": "workspace", "workspace": "D:\\work"}
    r = await client.post("/api/v1/client/grants", headers=device_headers, json=body)
    assert r.status_code == 201, r.text
    g = r.json()
    # 员工端报的发起人和原话不算，以服务端收到的那条消息为准
    assert g["verified"] is True
    assert g["requested_by"] == "mobile:张工"
    assert g["instruction"] == "把今天的质检日报发给我"

    # 同一个编号再报：原样返回，内容改不了
    again = await client.post("/api/v1/client/grants", headers=device_headers, json={**body, "permission": "full"})
    assert again.status_code == 201 and again.json()["permission"] == "workspace"

    # 审计里能带上授权编号
    r = await client.post("/api/v1/audit", headers=device_headers, json={"items": [
        {"conversation_id": "c1", "tool_name": "write_file", "risk": "confirm", "decision": "approved", "grant_id": "g-mobile-0001"}]})
    assert r.status_code == 204
    audit = (await client.get("/api/v1/admin/audit", headers=ADMIN)).json()
    assert audit[0]["grant_id"] == "g-mobile-0001"


async def test_cannot_borrow_another_devices_message(client, device_headers):
    other = await _message(None)
    r = await client.post("/api/v1/client/grants", headers=device_headers, json={
        "id": "g-mobile-0002", "origin": "dingtalk", "reference": str(other), "permission": "full"})
    assert r.status_code == 400
    r = await client.post("/api/v1/client/grants", headers=device_headers, json={
        "id": "g-mobile-0003", "origin": "mobile", "reference": "99999", "permission": "full"})
    assert r.status_code == 400


async def test_schedule_grants_and_content_visibility(client, device_headers):
    r = await client.post("/api/v1/client/grants", headers=device_headers, json={
        "id": "g-sched-0001", "conversation_id": "c2", "origin": "schedule", "reference": "task-1",
        "requested_by": "定时任务：清理临时文件", "instruction": "删掉 30 天前的临时文件", "permission": "readonly"})
    assert r.status_code == 201
    assert r.json()["verified"] is False  # 定时任务是员工自己在本机建的，服务端没有记录可核对
    # 共享管理 token 不是聊天查看者：原话遮掉，其余照常
    rows = (await client.get("/api/v1/admin/grants", headers=ADMIN, params={"origin": "schedule"})).json()
    assert rows[0]["id"] == "g-sched-0001" and rows[0]["instruction"] == "" and rows[0]["permission"] == "readonly"
    # 不认识的来源、权限拒收
    bad = await client.post("/api/v1/client/grants", headers=device_headers, json={
        "id": "g-bad-00001", "origin": "email", "permission": "readonly"})
    assert bad.status_code == 422
    assert (await client.get("/api/v1/admin/grants", headers=device_headers)).status_code in (401, 403)
