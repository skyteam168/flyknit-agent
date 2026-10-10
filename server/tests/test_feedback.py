"""意见反馈：客户端提交、后台查看和处理，日志按聊天内容的规矩管。"""

import io
import zipfile

PNG = b"\x89PNG\r\n\x1a\n" + b"\x00" * 32
JPG = b"\xff\xd8\xff\xe0" + b"\x00" * 32


def _logs_zip() -> bytes:
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as z:
        z.writestr("flyknit-20261009.log", "hello")
    return buf.getvalue()


async def _submit(client, device_headers, content="界面卡住了", images=(), logs=None):
    files = [("images", (f"shot{i}.png", data, "image/png")) for i, data in enumerate(images)]
    if logs is not None:
        files.append(("logs", ("logs.zip", logs, "application/zip")))
    return await client.post("/api/v1/client/feedback", headers=device_headers,
                             data={"content": content}, files=files or None)


async def test_submit_and_list(client, device_headers, admin_headers):
    r = await _submit(client, device_headers, images=[PNG, JPG], logs=_logs_zip())
    assert r.status_code == 200, r.text
    fid = r.json()["id"]

    page = (await client.get("/api/v1/admin/feedback", headers=admin_headers)).json()
    assert page["total"] == 1 and page["open"] == 1
    item = page["items"][0]
    assert item["id"] == fid
    assert item["content"] == "界面卡住了"
    assert item["machine_name"] == "PC-001"
    assert item["images"] == ["image-1.png", "image-2.jpg"]
    assert item["logs_size"] > 0

    img = await client.get(f"/api/v1/admin/feedback/{fid}/images/image-2.jpg", headers=admin_headers)
    assert img.status_code == 200 and img.content == JPG
    assert img.headers["content-type"] == "image/jpeg"
    # 只给表里记着的文件
    bad = await client.get(f"/api/v1/admin/feedback/{fid}/images/logs.zip", headers=admin_headers)
    assert bad.status_code == 404


async def test_text_only_and_validation(client, device_headers):
    assert (await _submit(client, device_headers)).status_code == 200
    assert (await _submit(client, device_headers, content="  ")).status_code == 400
    assert (await _submit(client, device_headers, content="x" * 10_001)).status_code == 400
    assert (await _submit(client, device_headers, images=[PNG] * 7)).status_code == 400
    assert (await _submit(client, device_headers, images=[b"<svg></svg>"])).status_code == 400
    assert (await _submit(client, device_headers, logs=b"not a zip")).status_code == 400
    # 只贴图不写字也行
    assert (await _submit(client, device_headers, content="", images=[PNG])).status_code == 200


async def test_requires_device(client):
    r = await client.post("/api/v1/client/feedback", data={"content": "x"})
    assert r.status_code == 401


async def test_daily_limit(client, device_headers):
    for _ in range(20):
        assert (await _submit(client, device_headers)).status_code == 200
    assert (await _submit(client, device_headers)).status_code == 429


async def test_logs_need_chat_reader_and_are_recorded(client, device_headers, admin_headers, chat_reader_headers):
    fid = (await _submit(client, device_headers, logs=_logs_zip())).json()["id"]
    # 共享令牌答不出是谁看的
    assert (await client.get(f"/api/v1/admin/feedback/{fid}/logs", headers=admin_headers)).status_code in (401, 403)
    r = await client.get(f"/api/v1/admin/feedback/{fid}/logs", headers=chat_reader_headers)
    assert r.status_code == 200
    assert zipfile.ZipFile(io.BytesIO(r.content)).namelist() == ["flyknit-20261009.log"]

    access = (await client.get("/api/v1/admin/chat-access", headers=chat_reader_headers)).json()
    assert any(a["action"] == "feedback_logs" and a["target"] == f"feedback:{fid}" for a in access)

    without = (await _submit(client, device_headers)).json()["id"]
    assert (await client.get(f"/api/v1/admin/feedback/{without}/logs", headers=chat_reader_headers)).status_code == 404


async def test_handle_filter_and_delete(client, device_headers, admin_headers, chat_reader_headers):
    a = (await _submit(client, device_headers, content="打印机连不上", images=[PNG])).json()["id"]
    b = (await _submit(client, device_headers, content="翻译太慢")).json()["id"]

    r = await client.patch(f"/api/v1/admin/feedback/{a}", headers=chat_reader_headers,
                           json={"status": "done", "note": "已重装驱动"})
    assert r.status_code == 200
    assert r.json()["handled_by"] == "it.reader" and r.json()["handled_at"]

    page = (await client.get("/api/v1/admin/feedback", headers=admin_headers, params={"status": "open"})).json()
    assert [i["id"] for i in page["items"]] == [b] and page["open"] == 1
    page = (await client.get("/api/v1/admin/feedback", headers=admin_headers, params={"q": "打印机"})).json()
    assert [i["id"] for i in page["items"]] == [a]

    # 重新打开清掉处理人
    r = await client.patch(f"/api/v1/admin/feedback/{a}", headers=admin_headers, json={"status": "open"})
    assert r.json()["handled_by"] == "" and r.json()["handled_at"] is None

    assert (await client.delete(f"/api/v1/admin/feedback/{a}", headers=admin_headers)).status_code == 204
    assert (await client.get(f"/api/v1/admin/feedback/{a}/images/image-1.png", headers=admin_headers)).status_code == 404
