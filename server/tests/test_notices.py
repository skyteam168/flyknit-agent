"""消息通知（docs/notices.md）。

重点在服务端执行的几条：
- 正文按白名单清洗：脚本、事件属性、外部图片、javascript: 链接都留不下来；
- 员工端只看得到已发布、到点、没过期、在范围内的通知；范围外的详情、标已读一律 404；
- 媒体要带设备令牌、管理端令牌或没过期的签名才能取，视频支持 Range；
- 已发布的要先撤回才能删。
"""

import io
import struct
from datetime import datetime, timedelta, timezone

import pytest

from conftest import ADMIN
from test_departments import assign, department, register

from app.services import notice_service

pytestmark = pytest.mark.asyncio

PNG = b"\x89PNG\r\n\x1a\n" + b"\x00" * 64
MP4 = struct.pack(">I", 24) + b"ftypisom" + b"\x00" * 2000


def notice(title="系统升级通知", **extra) -> dict:
    return {"kind": "upgrade", "title": title, "summary": "", "body_html": "<p>今晚 22:00 升级。</p>", **extra}


async def create(client, **extra) -> dict:
    r = await client.post("/api/v1/admin/notices", headers=ADMIN, json=notice(**extra))
    assert r.status_code == 201, r.text
    return r.json()


async def publish(client, nid: str) -> dict:
    r = await client.post(f"/api/v1/admin/notices/{nid}/publish", headers=ADMIN)
    assert r.status_code == 200, r.text
    return r.json()


async def titles(client, headers) -> list[str]:
    r = await client.get("/api/v1/client/notices", headers=headers)
    assert r.status_code == 200, r.text
    return [n["title"] for n in r.json()["items"]]


async def upload(client, data: bytes, name="a.png"):
    return await client.post("/api/v1/admin/notices/media", headers=ADMIN, files={"file": (name, io.BytesIO(data), "application/octet-stream")})


# ---------- 正文清洗 ----------


async def test_sanitizer_drops_scripts_handlers_and_external_media():
    media = "0123456789abcdef0123456789abcdef"
    html, used = notice_service.sanitize_html(
        '<p onclick="steal()">你好<script>alert(1)</script><style>p{}</style></p>'
        '<a href="javascript:alert(1)">坏链接</a><a href="https://intra.example/doc">好链接</a>'
        '<img src="https://tracker.example/x.png"><img src="/api/v1/notices/media/' + media + '?exp=1&sig=x" alt="图" onerror="x()">'
        '<iframe src="https://evil"></iframe><video src="https://evil/v.mp4"><source src="x">备用</video>'
        '<p style="text-align:center; position:fixed; color:#c00">居中</p><unknown>留字</unknown>'
    )
    assert "script" not in html and "alert" not in html and "onclick" not in html and "onerror" not in html
    assert "<style" not in html and "iframe" not in html and "tracker.example" not in html and "evil" not in html
    assert "备用" not in html
    assert '<a>坏链接</a>' in html
    assert 'href="https://intra.example/doc" target="_blank" rel="noopener noreferrer"' in html
    assert f'<img src="/api/v1/notices/media/{media}" alt="图">' in html
    assert 'style="text-align: center; color: #c00"' in html and "position" not in html
    assert "留字" in html and "<unknown>" not in html
    assert used == {media}


async def test_sanitizer_closes_open_tags_and_escapes_text():
    html, _ = notice_service.sanitize_html("<p><b>粗体 &lt;x&gt;<ul><li>一")
    assert html == "<p><b>粗体 &lt;x&gt;<ul><li>一</li></ul></b></p>"


async def test_media_signature_expires():
    url = notice_service.sign("a" * 32, "s", ttl=10, now=1000)
    exp, sig = url.split("exp=")[1].split("&sig=")
    assert notice_service.verify("a" * 32, exp, sig, "s", now=1005)
    assert not notice_service.verify("a" * 32, exp, sig, "s", now=1011)
    assert not notice_service.verify("b" * 32, exp, sig, "s", now=1005)
    assert not notice_service.verify("a" * 32, exp, sig, "other", now=1005)


async def test_saved_body_is_sanitized(client):
    n = await create(client, body_html='<p>正文</p><script>x()</script><img src="http://x/y.png">')
    assert n["body_html"] == "<p>正文</p>"
    assert n["status"] == "draft"
    r = await client.post("/api/v1/admin/notices", headers=ADMIN, json=notice(action_label="去看看", action_url="javascript:x()"))
    assert r.status_code == 400


# ---------- 员工端可见性 ----------


async def test_only_published_live_in_scope_notices_are_visible(client):
    await department(client, "生产部")
    await department(client, "质量部")
    a_id, a = await register(client, "PC-A")
    q_id, q = await register(client, "PC-Q")
    await assign(client, a_id, "生产部")
    await assign(client, q_id, "质量部")
    now = datetime.now(timezone.utc)

    draft = await create(client, title="草稿")
    everyone = await create(client, title="全公司")
    prod = await create(client, title="生产部才有", scope_departments=["生产部"])
    later = await create(client, title="明天才发", publish_at=(now + timedelta(days=1)).isoformat())
    old = await create(client, title="已过期", expire_at=(now + timedelta(seconds=1)).isoformat())
    for n in (everyone, prod, later, old):
        await publish(client, n["id"])
    # 让「已过期」过期
    from app import db
    from app.models import Notice

    async with db._sessionmaker() as s:
        row = await s.get(Notice, old["id"])
        row.expire_at = now - timedelta(minutes=1)
        await s.commit()

    assert sorted(await titles(client, a)) == ["全公司", "生产部才有"]
    assert await titles(client, q) == ["全公司"]
    # 范围外、草稿、未到点：详情和标已读都当不存在
    for nid in (prod["id"], draft["id"], later["id"], old["id"]):
        assert (await client.get(f"/api/v1/client/notices/{nid}", headers=q)).status_code == 404
        assert (await client.post(f"/api/v1/client/notices/{nid}/read", headers=q)).status_code == 404
    listed = (await client.get("/api/v1/admin/notices", headers=ADMIN)).json()
    by_title = {n["title"]: n for n in listed}
    assert by_title["明天才发"]["scheduled"] is True
    assert by_title["已过期"]["expired"] is True
    assert by_title["生产部才有"]["audience"] == 1


async def test_read_state_and_withdraw(client, device_headers):
    a = await create(client, title="一", important=True)
    b = await create(client, title="二")
    await publish(client, b["id"])
    await publish(client, a["id"])
    r = (await client.get("/api/v1/client/notices", headers=device_headers)).json()
    assert r["unread"] == 2
    assert r["items"][0]["title"] == "一"  # 重要的在前
    assert r["items"][0]["body_html"] == ""  # 列表不带正文
    assert r["items"][1]["summary"] == "今晚 22:00 升级。"  # 没写摘要就从正文取

    detail = (await client.get(f"/api/v1/client/notices/{b['id']}", headers=device_headers)).json()
    assert detail["body_html"] == "<p>今晚 22:00 升级。</p>" and detail["read"] is False
    assert (await client.post(f"/api/v1/client/notices/{b['id']}/read", headers=device_headers)).status_code == 204
    assert (await client.post(f"/api/v1/client/notices/{b['id']}/read", headers=device_headers)).status_code == 204
    assert (await client.get("/api/v1/client/notices", headers=device_headers)).json()["unread"] == 1
    assert (await client.post("/api/v1/client/notices/read-all", headers=device_headers)).status_code == 204
    assert (await client.get("/api/v1/client/notices", headers=device_headers)).json()["unread"] == 0

    stats = (await client.get(f"/api/v1/admin/notices/{b['id']}", headers=ADMIN)).json()
    assert stats["reads"] == 1
    readers = (await client.get(f"/api/v1/admin/notices/{b['id']}/reads", headers=ADMIN)).json()
    assert len(readers) == 1

    # 已发布的不能直接删；撤回后员工端看不到，再删
    assert (await client.delete(f"/api/v1/admin/notices/{b['id']}", headers=ADMIN)).status_code == 400
    assert (await client.post(f"/api/v1/admin/notices/{b['id']}/withdraw", headers=ADMIN)).status_code == 200
    assert await titles(client, device_headers) == ["一"]
    assert (await client.delete(f"/api/v1/admin/notices/{b['id']}", headers=ADMIN)).status_code == 204


async def test_device_cannot_use_admin_endpoints(client, device_headers):
    assert (await client.get("/api/v1/admin/notices", headers=device_headers)).status_code in (401, 403)
    assert (await client.post("/api/v1/admin/notices", headers=device_headers, json=notice())).status_code in (401, 403)


# ---------- 媒体 ----------


async def test_media_upload_checks_type_and_fetch_needs_auth(client, device_headers):
    bad = await upload(client, b"<html>not an image</html>", "x.png")
    assert bad.status_code == 400
    r = await upload(client, PNG)
    assert r.status_code == 201, r.text
    m = r.json()
    assert m["mime"] == "image/png" and m["src"] == f"/api/v1/notices/media/{m['id']}"

    assert (await client.get(m["src"])).status_code == 401
    assert (await client.get(m["src"], headers=device_headers)).status_code == 200
    assert (await client.get(m["src"], headers=ADMIN)).status_code == 200
    assert (await client.get(m["preview"])).status_code == 200  # 后台编辑器里的临时地址
    assert (await client.get(m["src"] + "?exp=1&sig=00")).status_code == 401
    assert (await client.get("/api/v1/notices/media/../../etc", headers=device_headers)).status_code == 404

    # 正文里引用：保存时去掉签名，后台读出来再带上
    n = await create(client, body_html=f'<p><img src="{m["preview"]}"></p>')
    assert "sig=" in n["body_html"]
    await publish(client, n["id"])
    detail = (await client.get(f"/api/v1/client/notices/{n['id']}", headers=device_headers)).json()
    assert detail["body_html"] == f'<p><img src="{m["src"]}"></p>'


async def test_video_supports_range(client, device_headers):
    m = (await upload(client, MP4, "v.mp4")).json()
    assert m["mime"] == "video/mp4"
    r = await client.get(m["src"], headers={**device_headers, "Range": "bytes=0-99"})
    assert r.status_code == 206
    assert len(r.content) == 100
