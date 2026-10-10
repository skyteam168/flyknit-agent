"""员工端安装包自带服务器地址和安装凭证：员工装完点「登录」就行，不用填服务器地址和注册密钥。"""

import io
import json
import zipfile

import pytest
from conftest import ADMIN
from test_releases import owner_headers, publish, upload

pytestmark = pytest.mark.asyncio

SERVER = "http://10.0.0.5:8000"


def nested_zip(version: str) -> bytes:
    """publish 出来的文件夹整个打包时，exe 往往套在一层文件夹里。"""
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w") as z:
        z.writestr(zipfile.ZipInfo("FlyknitBuddy/FlyknitBuddy.exe", date_time=(2026, 1, 1, 0, 0, 0)), version)
        z.writestr(zipfile.ZipInfo("FlyknitBuddy/FlyknitUpdater.exe", date_time=(2026, 1, 1, 0, 0, 0)), "u")
    return buffer.getvalue()


async def released(client, headers, version="0.3.0", body=None):
    r = await upload(client, headers, version=version, body=body)
    assert r.status_code == 201, r.text
    assert (await publish(client, headers, r.json()["id"])).status_code == 200


async def package(client, headers, **body) -> dict:
    r = await client.post("/api/v1/admin/client-package", headers=headers, json={"server_url": SERVER, **body})
    assert r.status_code == 200, r.text
    with zipfile.ZipFile(io.BytesIO(r.content)) as z:
        names = z.namelist()
        provision_name = next(n for n in names if n.endswith("flyknit.provision.json"))
        return {"names": names, "provision_name": provision_name, "provision": json.loads(z.read(provision_name))}


async def register(client, ticket, method="domain", **extra):
    versions = (await client.get("/api/v1/legal")).json()["versions"]
    return await client.post("/api/v1/devices/register", json={
        "ticket": ticket, "login_method": method, "machine_name": "PC-QC-01",
        "user_name": "CORP\\nguyen.van.a", "domain": "CORP", "agreed_legal": versions, **extra,
    })


async def test_the_package_carries_the_server_and_a_ticket_next_to_the_exe(client):
    headers = await owner_headers(client)
    await released(client, headers, body=nested_zip("0.3.0"))

    pkg = await package(client, headers, label="三车间")
    assert pkg["provision_name"] == "FlyknitBuddy/flyknit.provision.json"
    assert "FlyknitBuddy/FlyknitBuddy.exe" in pkg["names"]          # 原来的文件都在
    p = pkg["provision"]
    assert p["server_url"] == SERVER and p["version"] == "0.3.0" and p["label"] == "三车间"
    assert len(p["ticket"]) > 20

    # 凭证只存哈希，后台能看到是谁、什么时候下载的
    tickets = (await client.get("/api/v1/admin/tickets", headers=headers)).json()
    assert tickets[0]["label"] == "三车间" and tickets[0]["created_by"] == "it.yang" and tickets[0]["uses"] == 0
    assert "ticket" not in tickets[0]


async def test_logging_in_with_the_ticket_registers_the_device(client):
    headers = await owner_headers(client)
    await released(client, headers)
    ticket = (await package(client, headers))["provision"]["ticket"]

    r = await register(client, ticket)
    assert r.status_code == 200, r.text
    device_headers = {"Authorization": f"Bearer {r.json()['token']}"}
    assert (await client.get("/api/v1/client/config", headers=device_headers)).status_code == 200

    device = next(d for d in (await client.get("/api/v1/admin/devices", headers=ADMIN)).json() if d["id"] == r.json()["device_id"])
    assert device["user_name"] == "CORP\\nguyen.van.a"
    assert device["login_method"] == "domain"
    assert device["domain"] == "CORP"

    # 本机账号登录也行；不在域里的不记域名
    local = await register(client, ticket, method="local", user_name="PC-QC-01\\worker", domain="WORKGROUP")
    assert local.status_code == 200
    d = next(d for d in (await client.get("/api/v1/admin/devices", headers=ADMIN)).json() if d["id"] == local.json()["device_id"])
    assert (d["login_method"], d["domain"]) == ("local", "")

    assert (await client.get("/api/v1/admin/tickets", headers=headers)).json()[0]["uses"] == 2


async def test_a_revoked_or_made_up_ticket_cannot_register(client):
    headers = await owner_headers(client)
    await released(client, headers)
    ticket = (await package(client, headers))["provision"]["ticket"]
    first = await register(client, ticket)
    assert first.status_code == 200

    ticket_id = (await client.get("/api/v1/admin/tickets", headers=headers)).json()[0]["id"]
    r = await client.post(f"/api/v1/admin/tickets/{ticket_id}/revoke", headers=headers)
    assert r.json()["revoked"] is True
    blocked = await register(client, ticket)
    assert blocked.status_code == 403
    assert "停用" in blocked.json()["detail"]

    # 已经注册的电脑不受影响
    device_headers = {"Authorization": f"Bearer {first.json()['token']}"}
    assert (await client.get("/api/v1/client/config", headers=device_headers)).status_code == 200

    assert (await register(client, "made-up-ticket")).status_code == 403
    assert (await register(client, ticket, method="")).status_code == 403   # 吊销优先于其他检查


async def test_a_ticket_needs_a_login_method(client):
    headers = await owner_headers(client)
    await released(client, headers)
    ticket = (await package(client, headers))["provision"]["ticket"]
    assert (await register(client, ticket, method="")).status_code == 400


async def test_the_old_enrollment_key_still_works(client):
    r = await client.post("/api/v1/devices/register", json={"enrollment_key": "test-enroll", "machine_name": "PC-002"})
    assert r.status_code == 200
    assert (await client.post("/api/v1/devices/register", json={"enrollment_key": "wrong", "machine_name": "PC-003"})).status_code == 403
    assert (await client.post("/api/v1/devices/register", json={"machine_name": "PC-004"})).status_code == 403


async def test_only_owners_can_download_a_package(client):
    headers = await owner_headers(client)
    await released(client, headers)

    await client.post("/api/v1/admin/users", headers=ADMIN, json={"username": "clerk", "password": "init-pass-123"})
    r = await client.post("/api/v1/admin/login", json={"username": "clerk", "password": "init-pass-123"})
    clerk = {"Authorization": f"Bearer {r.json()['token']}"}
    await client.post("/api/v1/admin/password", headers=clerk,
                      json={"old_password": "init-pass-123", "new_password": "init-pass-123-ok"})
    r = await client.post("/api/v1/admin/login", json={"username": "clerk", "password": "init-pass-123-ok"})
    clerk = {"Authorization": f"Bearer {r.json()['token']}"}

    assert (await client.post("/api/v1/admin/client-package", headers=clerk, json={"server_url": SERVER})).status_code == 403
    assert (await client.get("/api/v1/admin/tickets", headers=clerk)).status_code == 403


async def test_a_package_needs_a_published_release_and_a_real_address(client):
    headers = await owner_headers(client)
    r = await client.post("/api/v1/admin/client-package", headers=headers, json={"server_url": SERVER})
    assert r.status_code == 404
    await released(client, headers)
    r = await client.post("/api/v1/admin/client-package", headers=headers, json={"server_url": "10.0.0.5:8000"})
    assert r.status_code == 400


STUB = b"MZ" + b"\x90" * 64  # 安装程序外壳（真的是个 exe，这里只要字节）


def with_setup(version: str) -> bytes:
    """新版打包脚本出来的：FlyknitBuddy 文件夹里还有安装程序外壳和运维代理。"""
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w") as z:
        for name, data in (("FlyknitBuddy.exe", version), ("FlyknitUpdater.exe", "u"),
                           ("FlyknitSetup.exe", STUB), ("agent/FlyknitAgent.exe", "agent")):
            z.writestr(zipfile.ZipInfo(f"FlyknitBuddy/{name}", date_time=(2026, 1, 1, 0, 0, 0)), data)
    return buffer.getvalue()


def split_setup(exe: bytes) -> tuple[bytes, bytes]:
    """按结尾 32 字节的标记把安装程序拆回 外壳 + zip。"""
    trailer = exe[-32:]
    assert trailer[:16] == b"FLYKNIT-SETUP-01"
    offset = int.from_bytes(trailer[16:24], "little")
    length = int.from_bytes(trailer[24:32], "little")
    assert offset + length + 32 == len(exe)
    return exe[:offset], exe[offset:offset + length]


async def test_the_setup_exe_is_the_stub_plus_the_provisioned_package(client):
    headers = await owner_headers(client)
    await released(client, headers, body=with_setup("0.3.0"))

    r = await client.post("/api/v1/admin/client-package", headers=headers,
                          json={"server_url": SERVER, "label": "三车间", "format": "exe"})
    assert r.status_code == 200, r.text
    assert 'FlyknitBuddy-Setup-0.3.0.exe' in r.headers["content-disposition"]
    stub, payload = split_setup(r.content)
    assert stub == STUB
    with zipfile.ZipFile(io.BytesIO(payload)) as z:
        assert "FlyknitBuddy/agent/FlyknitAgent.exe" in z.namelist()
        provision = json.loads(z.read("FlyknitBuddy/flyknit.provision.json"))
    assert provision["server_url"] == SERVER and len(provision["ticket"]) > 20

    # 安装程序里的凭证和 zip 里的一样能登录
    assert (await register(client, provision["ticket"])).status_code == 200


async def test_an_old_package_without_the_setup_stub_cannot_be_an_exe(client):
    headers = await owner_headers(client)
    await released(client, headers, body=nested_zip("0.3.0"))
    r = await client.post("/api/v1/admin/client-package", headers=headers, json={"server_url": SERVER, "format": "exe"})
    assert r.status_code == 400 and "FlyknitSetup.exe" in r.json()["detail"]
    # zip 照常
    assert (await client.post("/api/v1/admin/client-package", headers=headers, json={"server_url": SERVER})).status_code == 200


async def test_the_agent_registers_with_the_setup_ticket_and_updates_the_client(client):
    headers = await owner_headers(client)
    await released(client, headers, body=with_setup("0.3.0"))
    ticket = (await package(client, headers))["provision"]["ticket"]

    r = await client.post("/api/v1/agent/register", json={"ticket": ticket, "machine_guid": "abcd-1234-efgh"})
    assert r.status_code == 200, r.text
    agent = {"Authorization": f"Bearer {r.json()['token']}"}

    # 代理替 Program Files 里的员工端查更新、下载
    upd = (await client.get("/api/v1/agent/client-update", headers=agent, params={"version": "0.2.0"})).json()
    assert upd["available"] and upd["version"] == "0.3.0" and len(upd["sha256"]) == 64
    assert not (await client.get("/api/v1/agent/client-update", headers=agent, params={"version": "0.3.0"})).json()["available"]
    dl = await client.get("/api/v1/agent/client-update/download", headers=agent, params={"version": "0.3.0"})
    assert dl.status_code == 200 and dl.content == with_setup("0.3.0")
    # 没有代理令牌不行
    assert (await client.get("/api/v1/agent/client-update")).status_code == 401

    # 吊销了凭证，用它的新代理注册不上
    tid = (await client.get("/api/v1/admin/tickets", headers=headers)).json()[0]["id"]
    await client.post(f"/api/v1/admin/tickets/{tid}/revoke", headers=headers)
    r = await client.post("/api/v1/agent/register", json={"ticket": ticket, "machine_guid": "zzzz-9999-yyyy"})
    assert r.status_code == 403
    # 什么凭据都没有也不行
    assert (await client.post("/api/v1/agent/register", json={"machine_guid": "zzzz-9999-yyyy"})).status_code == 403


async def test_a_failed_exe_request_issues_no_ticket(client):
    headers = await owner_headers(client)
    await released(client, headers, body=nested_zip("0.3.0"))
    await client.post("/api/v1/admin/client-package", headers=headers, json={"server_url": SERVER, "format": "exe"})
    assert (await client.get("/api/v1/admin/tickets", headers=headers)).json() == []
