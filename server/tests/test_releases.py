"""员工端版本发布与自动更新。

发布一个版本，全厂电脑会自己装上。这是这套系统里影响面最大的动作，所以：
上传和发布分两步、限超级管理员、客户端只认 sha256。
"""

import hashlib
import io
import zipfile

import pytest
from conftest import ADMIN

pytestmark = pytest.mark.asyncio

INIT = "init-pass-123"


def a_zip(marker: str = "hello") -> bytes:
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w") as z:
        z.writestr("FlyknitBuddy.exe", marker)
    return buffer.getvalue()


async def owner_headers(client, username="it.yang"):
    await client.post("/api/v1/admin/users", headers=ADMIN,
                      json={"username": username, "password": INIT, "is_owner": True})
    r = await client.post("/api/v1/admin/login", json={"username": username, "password": INIT})
    headers = {"Authorization": f"Bearer {r.json()['token']}"}
    await client.post("/api/v1/admin/password", headers=headers,
                      json={"old_password": INIT, "new_password": INIT + "-ok"})
    r = await client.post("/api/v1/admin/login", json={"username": username, "password": INIT + "-ok"})
    return {"Authorization": f"Bearer {r.json()['token']}"}


async def upload(client, headers, version="0.2.0", notes="修了几个问题", body=None):
    return await client.post(
        "/api/v1/admin/releases",
        headers=headers,
        files={"file": (f"FlyknitBuddy-{version}.zip", body or a_zip(version), "application/zip")},
        data={"version": version, "notes": notes},
    )


async def publish(client, headers, release_id):
    return await client.patch(f"/api/v1/admin/releases/{release_id}", headers=headers, json={"published": True})


# ---------- 上传与发布 ----------

async def test_uploading_does_not_publish(client, device_headers):
    """传完就下发的话，传错一个包等于几百台电脑一起装错。"""
    headers = await owner_headers(client)
    r = await upload(client, headers)
    assert r.status_code == 201, r.text
    assert r.json()["published"] is False

    # 没发布之前客户端看不到
    r = await client.get("/api/v1/client/update", headers=device_headers, params={"version": "0.1.0"})
    assert r.json()["available"] is False


async def test_publishing_makes_it_visible_to_clients(client, device_headers):
    headers = await owner_headers(client)
    release = (await upload(client, headers)).json()
    assert (await publish(client, headers, release["id"])).status_code == 200

    r = await client.get("/api/v1/client/update", headers=device_headers, params={"version": "0.1.0"})
    body = r.json()
    assert body["available"] is True
    assert body["version"] == "0.2.0"
    assert body["notes"] == "修了几个问题"
    assert body["sha256"] == hashlib.sha256(a_zip("0.2.0")).hexdigest()


async def test_a_client_already_on_the_latest_gets_nothing(client, device_headers):
    headers = await owner_headers(client)
    release = (await upload(client, headers)).json()
    await publish(client, headers, release["id"])

    for current in ("0.2.0", "0.3.0"):
        r = await client.get("/api/v1/client/update", headers=device_headers, params={"version": current})
        assert r.json()["available"] is False, current


async def test_the_newest_version_wins_not_the_newest_upload(client, device_headers):
    """补传一个旧版本不该把它变成「最新」——按版本号比，不按上传时间。"""
    headers = await owner_headers(client)
    new = (await upload(client, headers, version="0.10.0")).json()
    old = (await upload(client, headers, version="0.9.0")).json()
    await publish(client, headers, new["id"])
    await publish(client, headers, old["id"])

    r = await client.get("/api/v1/client/update", headers=device_headers, params={"version": "0.1.0"})
    assert r.json()["version"] == "0.10.0"


async def test_unpublishing_pulls_it_back(client, device_headers):
    headers = await owner_headers(client)
    release = (await upload(client, headers)).json()
    await publish(client, headers, release["id"])
    await client.patch(f"/api/v1/admin/releases/{release['id']}", headers=headers, json={"published": False})

    r = await client.get("/api/v1/client/update", headers=device_headers, params={"version": "0.1.0"})
    assert r.json()["available"] is False


# ---------- 校验 ----------

async def test_a_bad_version_number_is_refused(client):
    headers = await owner_headers(client)
    for bad in ("最新版", "v", "2026-10-07 的包", ""):
        r = await upload(client, headers, version=bad)
        assert r.status_code in (400, 422), bad


async def test_only_zip_is_accepted(client):
    headers = await owner_headers(client)
    r = await client.post("/api/v1/admin/releases", headers=headers,
                          files={"file": ("setup.exe", b"MZ...", "application/octet-stream")},
                          data={"version": "0.2.0"})
    assert r.status_code == 400


async def test_the_same_version_cannot_be_uploaded_twice(client):
    """同一个版本号传两次，员工电脑就说不清自己装的是哪一份。"""
    headers = await owner_headers(client)
    assert (await upload(client, headers, version="0.2.0")).status_code == 201
    r = await upload(client, headers, version="0.2.0", body=a_zip("不一样的内容"))
    assert r.status_code == 409


# ---------- 权限 ----------

async def test_only_an_owner_can_upload_or_publish(client):
    owner = await owner_headers(client)
    await client.post("/api/v1/admin/users", headers=ADMIN,
                      json={"username": "it.li", "password": INIT, "can_dispatch": True})
    r = await client.post("/api/v1/admin/login", json={"username": "it.li", "password": INIT})
    h = {"Authorization": f"Bearer {r.json()['token']}"}
    await client.post("/api/v1/admin/password", headers=h,
                      json={"old_password": INIT, "new_password": INIT + "-ok"})
    r = await client.post("/api/v1/admin/login", json={"username": "it.li", "password": INIT + "-ok"})
    dispatcher = {"Authorization": f"Bearer {r.json()['token']}"}

    # 能下发运维任务 ≠ 能换掉全厂电脑上的程序本身
    assert (await upload(client, dispatcher, version="9.9.9")).status_code == 403
    release = (await upload(client, owner)).json()
    assert (await publish(client, dispatcher, release["id"])).status_code == 403


async def test_a_device_token_cannot_reach_the_admin_side(client, device_headers):
    assert (await client.get("/api/v1/admin/releases", headers=device_headers)).status_code == 403


async def test_downloading_needs_a_device_token(client, device_headers):
    headers = await owner_headers(client)
    release = (await upload(client, headers)).json()
    await publish(client, headers, release["id"])

    assert (await client.get("/api/v1/client/update/download")).status_code == 401
    r = await client.get("/api/v1/client/update/download", headers=device_headers)
    assert r.status_code == 200
    assert hashlib.sha256(r.content).hexdigest() == release["sha256"]


# ---------- 文件丢了 ----------

async def test_a_release_whose_file_vanished_is_not_offered(client, device_headers, tmp_path):
    """
    让客户端去下一个下不到的包，只会反复失败。当作没有更新，并在日志里喊一声。
    """
    from pathlib import Path

    from app.config import get_settings

    headers = await owner_headers(client)
    release = (await upload(client, headers)).json()
    await publish(client, headers, release["id"])
    Path(get_settings().data_dir, "releases", f"{release['sha256']}.zip").unlink()

    r = await client.get("/api/v1/client/update", headers=device_headers, params={"version": "0.1.0"})
    assert r.json()["available"] is False


async def test_a_release_whose_file_vanished_cannot_be_published(client):
    from pathlib import Path

    from app.config import get_settings

    headers = await owner_headers(client)
    release = (await upload(client, headers)).json()
    Path(get_settings().data_dir, "releases", f"{release['sha256']}.zip").unlink()
    r = await publish(client, headers, release["id"])
    assert r.status_code == 400
