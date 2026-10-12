"""产品页下载：默认关闭；超级管理员打开后，产品页能下到带专用凭证的员工端，凭证能吊销、能换。"""

import io
import json
import zipfile

import pytest
from conftest import ADMIN
from test_client_package import SERVER, nested_zip, register, released, split_setup, with_setup
from test_releases import owner_headers

pytestmark = pytest.mark.asyncio


async def test_closed_by_default(client):
    assert (await client.get("/api/v1/public/release")).json() == {
        "download_enabled": False, "version": "", "notes": "", "size": 0, "published_at": "", "formats": []}
    r = await client.get("/api/v1/public/download")
    assert r.status_code == 404 and "暂未开放" in r.json()["detail"]


async def test_open_download_and_install(client):
    headers = await owner_headers(client)
    await released(client, headers, version="0.3.0", body=with_setup("0.3.0"))

    # 地址不完整不让开
    r = await client.put("/api/v1/admin/landing", headers=headers, json={"enabled": True, "server_url": "10.0.0.5:8000"})
    assert r.status_code == 400
    r = await client.put("/api/v1/admin/landing", headers=headers, json={"enabled": True, "server_url": SERVER + "/"})
    assert r.status_code == 200, r.text
    conf = r.json()
    assert conf["enabled"] and conf["server_url"] == SERVER and conf["ticket_id"] and conf["version"] == "0.3.0"

    info = (await client.get("/api/v1/public/release")).json()
    assert info["download_enabled"] and info["version"] == "0.3.0" and info["formats"] == ["exe", "zip"]

    # 安装程序：外壳 + 带凭证的 zip；第二次直接用缓存，内容一样
    r = await client.get("/api/v1/public/download")
    assert r.status_code == 200 and "FlyknitBuddy-Setup-0.3.0.exe" in r.headers["content-disposition"]
    _, inner = split_setup(r.content)
    with zipfile.ZipFile(io.BytesIO(inner)) as z:
        provision = json.loads(z.read("FlyknitBuddy/flyknit.provision.json"))
    assert provision["server_url"] == SERVER and provision["label"] == "产品页下载"
    assert (await client.get("/api/v1/public/download")).content == r.content

    # 免安装版
    r = await client.get("/api/v1/public/download", params={"format": "zip"})
    with zipfile.ZipFile(io.BytesIO(r.content)) as z:
        assert json.loads(z.read("FlyknitBuddy/flyknit.provision.json"))["ticket"] == provision["ticket"]

    # 装上就能登录；后台看得到这张凭证用了几次
    assert (await register(client, provision["ticket"])).status_code == 200
    assert (await client.get("/api/v1/admin/landing", headers=headers)).json()["ticket_uses"] == 1

    # 外泄了：换一张，旧的注册不上，新下的包带新凭证
    r = await client.post("/api/v1/admin/landing/rotate", headers=headers)
    assert r.json()["ticket_id"] != conf["ticket_id"]
    assert (await register(client, provision["ticket"], machine_name="PC-QC-02")).status_code == 403
    _, inner = split_setup((await client.get("/api/v1/public/download")).content)
    with zipfile.ZipFile(io.BytesIO(inner)) as z:
        assert json.loads(z.read("FlyknitBuddy/flyknit.provision.json"))["ticket"] != provision["ticket"]

    # 关掉：产品页显示暂未开放
    await client.put("/api/v1/admin/landing", headers=headers, json={"enabled": False})
    assert not (await client.get("/api/v1/public/release")).json()["download_enabled"]
    assert (await client.get("/api/v1/public/download")).status_code == 404


async def test_revoked_ticket_is_replaced_when_reopened(client):
    headers = await owner_headers(client)
    await released(client, headers, body=nested_zip("0.3.0"))
    first = (await client.put("/api/v1/admin/landing", headers=headers, json={"enabled": True, "server_url": SERVER})).json()
    # 旧的打包脚本出来的没有安装程序外壳：只能下 zip
    assert (await client.get("/api/v1/public/release")).json()["formats"] == ["zip"]
    assert (await client.get("/api/v1/public/download")).status_code == 400

    # 在「安装凭证」里吊销了：产品页暂停下载，重新打开时换一张新的
    await client.post(f"/api/v1/admin/tickets/{first['ticket_id']}/revoke", headers=headers)
    assert not (await client.get("/api/v1/public/release")).json()["download_enabled"]
    again = (await client.put("/api/v1/admin/landing", headers=headers, json={"enabled": True, "server_url": ""})).json()
    assert again["ticket_id"] not in (None, first["ticket_id"]) and again["server_url"] == SERVER


async def test_only_owners_can_open_it(client):
    assert (await client.put("/api/v1/admin/landing", json={"enabled": True, "server_url": SERVER})).status_code in (401, 403)
    assert (await client.get("/api/v1/admin/landing", headers=ADMIN)).status_code == 200


async def test_landing_page_is_served_at_root(client):
    r = await client.get("/")
    assert r.status_code == 200 and "FlyknitBuddy" in r.text
    # 接口和后台不受影响
    assert (await client.get("/healthz")).status_code == 200
    assert (await client.get("/api/v1/public/release")).status_code == 200
