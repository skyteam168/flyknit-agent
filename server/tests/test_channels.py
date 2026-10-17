"""钉钉通道：认人、排队、长轮询、回结果、Stream 帧处理。"""

import asyncio
import json
from datetime import datetime, timedelta, timezone

import httpx
import pytest
import respx

from app import db
from app.models import ChannelMessage
from app.services import channel_hub, dingtalk
from app.services.dingtalk import BotMessage, DingTalkApi, handle_frame

ADMIN = {"Authorization": "Bearer test-admin"}


class FakeApi:
    """代替钉钉接口：记下每一条回复。"""

    instances: list["FakeApi"] = []

    def __init__(self, http, client_id, client_secret, robot_code):
        self.client_id = client_id
        self.secret = client_secret
        self.replies: list[tuple[str, str]] = []
        FakeApi.instances.append(self)

    async def reply(self, webhook, webhook_expires, staff_id, title, text):
        self.replies.append((staff_id, text))


async def _never(api, status, on_message):
    status.connected = True
    await asyncio.Event().wait()


@pytest.fixture
def fake_dingtalk(monkeypatch):
    FakeApi.instances.clear()
    # 中枢是进程里的单例：上一个用例留下的回复器和在线记录清掉
    channel_hub.hub.repliers.clear()
    channel_hub.hub._last_poll.clear()
    monkeypatch.setattr(channel_hub, "DingTalkApi", FakeApi)
    monkeypatch.setattr(channel_hub, "run_stream", _never)
    return FakeApi


async def _enable(client) -> FakeApi:
    r = await client.put("/api/v1/admin/channels/dingtalk", headers=ADMIN,
                         json={"enabled": True, "client_id": "dingabc", "client_secret": "secret-1234567890"})
    assert r.status_code == 200, r.text
    for _ in range(100):
        if FakeApi.instances and channel_hub.hub.repliers.get("dingtalk") is FakeApi.instances[-1]:
            return FakeApi.instances[-1]
        await asyncio.sleep(0.01)
    raise AssertionError("钉钉通道没有启动")


def _msg(text: str, staff="staff-01", nick="阮文安", kind="1", msg_id="") -> BotMessage:
    return BotMessage(
        msg_id=msg_id or f"m-{text}-{staff}", staff_id=staff, nick=nick, text=text, conversation_type=kind,
        session_webhook="https://oapi.dingtalk.com/robot/sendBySession?session=x",
        webhook_expires_ms=int((datetime.now(timezone.utc) + timedelta(hours=1)).timestamp() * 1000), robot_code="dingabc",
    )


async def _inbound(msg: BotMessage):
    async with db.get_sessionmaker()() as s:
        return await channel_hub.hub.handle_inbound(s, msg)


async def _bind(client, device_headers, staff="staff-01") -> None:
    code = (await client.post("/api/v1/client/channels/bind-code", headers=device_headers)).json()["code"]
    await _inbound(_msg(f"绑定 {code}", staff=staff))


# ---------------------------------------------------------------------
# 帧处理
# ---------------------------------------------------------------------

def test_ping_is_answered_with_the_same_data():
    reply, message, signal = handle_frame(json.dumps(
        {"type": "SYSTEM", "headers": {"topic": "ping", "messageId": "p1"}, "data": "{\"opaque\":\"abc\"}"}))
    assert reply["code"] == 200 and reply["headers"]["messageId"] == "p1" and reply["data"] == "{\"opaque\":\"abc\"}"
    assert message is None and signal == ""


def test_bot_message_is_acked_and_parsed():
    data = {"msgtype": "text", "text": {"content": " 把桌面上的日报发我 "}, "senderStaffId": "s1", "senderNick": "安",
            "conversationType": "1", "msgId": "m1", "sessionWebhook": "https://x", "sessionWebhookExpiredTime": 1}
    reply, message, _ = handle_frame(json.dumps(
        {"type": "CALLBACK", "headers": {"topic": dingtalk.BOT_TOPIC, "messageId": "c1"}, "data": json.dumps(data)}))
    assert reply["headers"]["messageId"] == "c1"
    assert message.text == "把桌面上的日报发我" and message.staff_id == "s1" and message.conversation_type == "1"


def test_disconnect_asks_to_reconnect_and_strangers_are_ignored():
    assert handle_frame(json.dumps({"type": "SYSTEM", "headers": {"topic": "disconnect"}}))[2] == "disconnect"
    # 外部联系人没有 staffId：照样 ACK，但不当成员工消息
    _, message, _ = handle_frame(json.dumps({"type": "CALLBACK", "headers": {"topic": dingtalk.BOT_TOPIC, "messageId": "c"},
                                             "data": json.dumps({"msgtype": "text", "text": {"content": "hi"}})}))
    assert message is None


async def test_reply_uses_session_webhook_then_falls_back_to_robot_api():
    async with httpx.AsyncClient() as http:
        api = DingTalkApi(http, "dingabc", "secret", "")
        future = datetime.now(timezone.utc) + timedelta(minutes=10)
        with respx.mock(assert_all_called=False) as mock:
            hook = mock.post("https://hook.example/send").respond(200, json={"errcode": 0})
            token = mock.post(f"{dingtalk.API}/v1.0/oauth2/accessToken").respond(200, json={"accessToken": "t1", "expireIn": 7200})
            oto = mock.post(f"{dingtalk.API}/v1.0/robot/oToMessages/batchSend").respond(200, json={})
            await api.reply("https://hook.example/send", future, "s1", "标题", "结果")
            assert hook.called and not oto.called
            # webhook 过期了：走单聊接口，带上 access token 和机器人编码
            await api.reply("https://hook.example/send", datetime.now(timezone.utc) - timedelta(minutes=1), "s1", "标题", "x" * 5000)
            assert token.called and oto.called
            sent = json.loads(oto.calls.last.request.content)
            assert sent["robotCode"] == "dingabc" and sent["userIds"] == ["s1"]
            assert oto.calls.last.request.headers["x-acs-dingtalk-access-token"] == "t1"
            assert "完整结果请在电脑上" in json.loads(sent["msgParam"])["text"]


# ---------------------------------------------------------------------
# 开关与配置
# ---------------------------------------------------------------------

async def test_off_by_default_and_secret_is_never_shown(client, device_headers, fake_dingtalk):
    r = await client.get("/api/v1/client/channels", headers=device_headers)
    assert r.json() == {"dingtalk": False, "wecom": False, "bindings": []}
    assert (await client.post("/api/v1/client/channels/bind-code", headers=device_headers)).status_code == 409

    await _enable(client)
    cfg = (await client.get("/api/v1/admin/channels/dingtalk", headers=ADMIN)).json()
    assert cfg["enabled"] and cfg["client_id"] == "dingabc"
    assert "secret-1234567890" not in json.dumps(cfg)
    assert fake_dingtalk.instances[-1].secret == "secret-1234567890"
    # 不填 secret 表示不改
    r = await client.put("/api/v1/admin/channels/dingtalk", headers=ADMIN, json={"enabled": True, "client_id": "dingabc"})
    assert r.status_code == 200
    assert (await client.get("/api/v1/client/channels", headers=device_headers)).json()["dingtalk"] is True


async def test_enabling_needs_credentials(client, fake_dingtalk):
    r = await client.put("/api/v1/admin/channels/dingtalk", headers=ADMIN, json={"enabled": True, "client_id": "dingabc"})
    assert r.status_code == 422


# ---------------------------------------------------------------------
# 认人
# ---------------------------------------------------------------------

async def test_binding_with_a_one_time_code(client, device_headers, fake_dingtalk):
    api = await _enable(client)
    await _bind(client, device_headers)
    assert "已绑定到电脑 **PC-001**" in api.replies[-1][1]
    bindings = (await client.get("/api/v1/client/channels", headers=device_headers)).json()["bindings"]
    assert [b["external_name"] for b in bindings] == ["阮文安"]

    # 码用过就作废；别人拿同一个码绑不上
    code = (await client.post("/api/v1/client/channels/bind-code", headers=device_headers)).json()["code"]
    await _inbound(_msg(f"绑定 {code}", staff="staff-02", nick="别人"))
    await _inbound(_msg(f"绑定 {code}", staff="staff-03", nick="又一个人", msg_id="again"))
    assert "绑定码不对或已过期" in api.replies[-1][1]


async def test_strangers_groups_and_unbinding(client, device_headers, fake_dingtalk):
    api = await _enable(client)
    assert await _inbound(_msg("帮我整理桌面")) is None
    assert "还没有绑定电脑" in api.replies[-1][1]

    await _bind(client, device_headers)
    assert await _inbound(_msg("帮我整理桌面", kind="2", msg_id="group")) is None
    assert "请私聊我" in api.replies[-1][1]

    await _inbound(_msg("解绑"))
    assert "已解绑" in api.replies[-1][1]
    assert (await client.get("/api/v1/client/channels", headers=device_headers)).json()["bindings"] == []


# ---------------------------------------------------------------------
# 排队、长轮询、回结果
# ---------------------------------------------------------------------

async def test_message_reaches_a_waiting_computer_and_the_answer_goes_back(client, device_headers, fake_dingtalk):
    api = await _enable(client)
    await _bind(client, device_headers)

    poll = asyncio.create_task(client.get("/api/v1/client/channels/messages?wait=5", headers=device_headers))
    await asyncio.sleep(0.2)  # 员工端挂着等
    row = await _inbound(_msg("把桌面上的日报发我"))
    assert row is not None and "正在你的电脑上处理" in api.replies[-1][1]

    got = (await poll).json()["messages"]
    assert [(m["text"], m["sender"], m["conversation_id"]) for m in got] == [("把桌面上的日报发我", "阮文安", "")]

    r = await client.post(f"/api/v1/client/channels/messages/{got[0]['id']}/finish", headers=device_headers,
                          json={"status": "succeeded", "answer": "日报在 D:\\日报\\10-16.xlsx", "conversation_id": "conv-1"})
    assert r.json() == {"ok": True}
    assert api.replies[-1] == ("staff-01", "日报在 D:\\日报\\10-16.xlsx")
    # 同一条不能回两次
    r = await client.post(f"/api/v1/client/channels/messages/{got[0]['id']}/finish", headers=device_headers,
                          json={"status": "succeeded", "answer": "again"})
    assert r.json() == {"ok": False}

    # 下一条接着上次的对话
    await _inbound(_msg("再发一份给主管"))
    got = (await client.get("/api/v1/client/channels/messages", headers=device_headers)).json()["messages"]
    assert got[0]["conversation_id"] == "conv-1"


async def test_failures_and_other_computers(client, device_headers, fake_dingtalk):
    api = await _enable(client)
    await _bind(client, device_headers)
    client.headers.update({})
    other = (await client.post("/api/v1/devices/register",
                               json={"enrollment_key": "test-enroll", "machine_name": "PC-002", "user_name": "le"})).json()
    other_headers = {"Authorization": f"Bearer {other['token']}"}

    await client.get("/api/v1/client/channels/messages", headers=device_headers)  # 在线
    row = await _inbound(_msg("装一下打印机驱动"))
    # 别的电脑领不到、也回不了这条
    assert (await client.get("/api/v1/client/channels/messages", headers=other_headers)).json()["messages"] == []
    got = (await client.get("/api/v1/client/channels/messages", headers=device_headers)).json()["messages"]
    r = await client.post(f"/api/v1/client/channels/messages/{row.id}/finish", headers=other_headers,
                          json={"status": "failed", "error": "x"})
    assert r.status_code == 404
    await client.post(f"/api/v1/client/channels/messages/{got[0]['id']}/finish", headers=device_headers,
                      json={"status": "failed", "error": "需要在电脑上确认，已跳过"})
    assert api.replies[-1][1] == "没办成：需要在电脑上确认，已跳过"


async def test_offline_computer_queues_then_expires(client, device_headers, fake_dingtalk):
    api = await _enable(client)
    await _bind(client, device_headers)
    channel_hub.hub._last_poll.clear()

    row = await _inbound(_msg("帮我查一下昨天的产量"))
    assert "没有打开 FlyknitBuddy" in api.replies[-1][1]

    async with db.get_sessionmaker()() as s:
        m = await s.get(ChannelMessage, row.id)
        m.created_at = datetime.now(timezone.utc) - channel_hub.PENDING_TTL - timedelta(minutes=1)
        await s.commit()
        assert await channel_hub.hub.expire(s) == 1
        assert (await s.get(ChannelMessage, row.id)).status == "expired"
    assert "已取消" in api.replies[-1][1]
    assert (await client.get("/api/v1/client/channels/messages", headers=device_headers)).json()["messages"] == []


async def test_duplicate_delivery_is_ignored(client, device_headers, fake_dingtalk):
    await _enable(client)
    await _bind(client, device_headers)
    assert await _inbound(_msg("查产量", msg_id="dup")) is not None
    assert await _inbound(_msg("查产量", msg_id="dup")) is None


async def test_admin_lists_bindings_and_messages(client, device_headers, fake_dingtalk):
    await _enable(client)
    await _bind(client, device_headers)
    await _inbound(_msg("查产量"))
    bindings = (await client.get("/api/v1/admin/channels/bindings", headers=ADMIN)).json()
    assert bindings[0]["machine_name"] == "PC-001" and bindings[0]["external_name"] == "阮文安"
    messages = (await client.get("/api/v1/admin/channels/messages", headers=ADMIN)).json()
    assert messages[0]["text"] == "查产量" and messages[0]["machine_name"] == "PC-001"
    assert (await client.delete(f"/api/v1/admin/channels/bindings/{bindings[0]['id']}", headers=ADMIN)).status_code == 200
    assert (await client.get("/api/v1/admin/channels/bindings", headers=ADMIN)).json() == []


async def test_switching_to_another_app_drops_old_bindings(client, device_headers, fake_dingtalk):
    """从测试组织换到公司组织：员工 ID 只在组织内唯一，旧绑定必须作废，不然会错把消息发到别人电脑上。"""
    await _enable(client)
    await _bind(client, device_headers)
    url = "/api/v1/admin/channels/dingtalk"
    # 只改开关、不换应用：绑定还在
    assert (await client.put(url, headers=ADMIN, json={"enabled": False, "client_id": "dingabc"})).status_code == 200
    assert len((await client.get("/api/v1/admin/channels/bindings", headers=ADMIN)).json()) == 1
    # 换成另一个应用：清空
    r = await client.put(url, headers=ADMIN, json={"enabled": True, "client_id": "ding-company", "client_secret": "s-0987654321"})
    assert r.status_code == 200 and r.json()["bindings"] == 0
    assert (await client.get("/api/v1/admin/channels/bindings", headers=ADMIN)).json() == []


async def test_stream_loop_against_a_local_websocket_server():
    """真的连一个 WebSocket：回 ping、ACK 机器人消息、把消息交出去。"""
    from websockets.asyncio.server import serve

    acks: list[dict] = []
    done = asyncio.Event()

    async def fake_dingtalk(ws):
        await ws.send(json.dumps({"type": "SYSTEM", "headers": {"topic": "ping", "messageId": "p1"}, "data": "{\"opaque\":1}"}))
        acks.append(json.loads(await ws.recv()))
        data = {"msgtype": "text", "text": {"content": "查产量"}, "senderStaffId": "s9", "conversationType": "1", "msgId": "m9"}
        await ws.send(json.dumps({"type": "CALLBACK", "headers": {"topic": dingtalk.BOT_TOPIC, "messageId": "c9"},
                                  "data": json.dumps(data)}))
        acks.append(json.loads(await ws.recv()))
        await done.wait()

    got: list[BotMessage] = []

    async def on_message(m: BotMessage):
        got.append(m)
        done.set()

    async with serve(fake_dingtalk, "127.0.0.1", 0) as server:
        port = server.sockets[0].getsockname()[1]
        async with httpx.AsyncClient() as http:
            api = DingTalkApi(http, "dingabc", "secret", "")
            with respx.mock(assert_all_called=False) as mock:
                mock.post(f"{dingtalk.API}/v1.0/gateway/connections/open").respond(
                    200, json={"endpoint": f"ws://127.0.0.1:{port}/", "ticket": "tk"})
                mock.route(host="127.0.0.1").pass_through()
                status = dingtalk.StreamStatus()
                task = asyncio.create_task(dingtalk.run_stream(api, status, on_message))
                await asyncio.wait_for(done.wait(), 5)
                assert status.connected and status.received == 1
                task.cancel()
                await asyncio.gather(task, return_exceptions=True)
    assert [a["headers"]["messageId"] for a in acks] == ["p1", "c9"]
    assert acks[0]["data"] == "{\"opaque\":1}"
    assert got[0].text == "查产量" and got[0].staff_id == "s9"


def test_charts_and_cards_become_plain_text_for_dingtalk():
    answer = (
        "查好了。\n```chart\n{\"type\":\"bar\"}\n```\n"
        "```ui\n{\"kind\":\"facts\",\"title\":\"昨日产量\",\"items\":[{\"title\":\"6#线\",\"value\":1200,\"unit\":\"件\"}]}\n```\n"
        "```ui\n{\"kind\":\"actions\",\"items\":[{\"title\":\"导出表格\",\"prompt\":\"把结果导出成 Excel\"}]}\n```\n"
        "```python\nprint(1)\n```"
    )
    out = dingtalk.for_dingtalk(answer)
    assert "```chart" not in out and "请在电脑上" in out
    assert "**昨日产量**" in out and "- 6#线：1200件" in out
    assert "接下来可以直接回复：" in out and "- 导出表格：把结果导出成 Excel" in out
    assert "```python" in out  # 普通代码块原样保留


async def test_guessing_bind_codes_is_limited(client, device_headers, fake_dingtalk):
    api = await _enable(client)
    for i in range(channel_hub.MAX_BIND_FAILURES):
        await _inbound(_msg(f"绑定 00000{i}", staff="guesser", msg_id=f"g{i}"))
    code = (await client.post("/api/v1/client/channels/bind-code", headers=device_headers)).json()["code"]
    await _inbound(_msg(f"绑定 {code}", staff="guesser", msg_id="real"))
    assert "输错次数太多" in api.replies[-1][1]
    assert (await client.get("/api/v1/client/channels", headers=device_headers)).json()["bindings"] == []


async def test_work_that_never_comes_back_is_reported(client, device_headers, fake_dingtalk):
    api = await _enable(client)
    await _bind(client, device_headers)
    await client.get("/api/v1/client/channels/messages", headers=device_headers)
    row = await _inbound(_msg("整理一下共享盘"))
    await client.get("/api/v1/client/channels/messages", headers=device_headers)  # 领走了，但再也没回音
    async with db.get_sessionmaker()() as s:
        m = await s.get(ChannelMessage, row.id)
        m.started_at = datetime.now(timezone.utc) - channel_hub.RUNNING_TTL - timedelta(minutes=1)
        await s.commit()
        await channel_hub.hub.expire(s)
        assert (await s.get(ChannelMessage, row.id)).status == "failed"
    assert "一直没有办完" in api.replies[-1][1]



# =====================================================================
# 企业微信
# =====================================================================

from app.services import wecom  # noqa: E402


class FakeWeCom:
    """代替企业微信长连接：记下回复。"""

    instances: list["FakeWeCom"] = []

    def __init__(self, bot_id, secret):
        self.bot_id = bot_id
        self.secret = secret
        self.replies: list[tuple[str, str, str]] = []
        FakeWeCom.instances.append(self)

    async def reply(self, webhook, webhook_expires, staff_id, title, text):
        self.replies.append((staff_id, webhook, text))

    async def run(self, status, on_message):
        status.connected = True
        await asyncio.Event().wait()


@pytest.fixture
def fake_wecom(monkeypatch, fake_dingtalk):
    FakeWeCom.instances.clear()
    monkeypatch.setattr(channel_hub, "WeComBot", FakeWeCom)
    return FakeWeCom


async def _enable_wecom(client) -> FakeWeCom:
    r = await client.put("/api/v1/admin/channels/wecom", headers=ADMIN,
                         json={"enabled": True, "bot_id": "aibbot-1", "secret": "wecom-secret-0123456789"})
    assert r.status_code == 200, r.text
    for _ in range(100):
        if FakeWeCom.instances and channel_hub.hub.repliers.get("wecom") is FakeWeCom.instances[-1]:
            return FakeWeCom.instances[-1]
        await asyncio.sleep(0.01)
    raise AssertionError("企业微信通道没有启动")


def _callback(text: str, userid="zhangsan", chattype="single", rid="r1", msgid="w1") -> dict:
    return {"cmd": "aibot_msg_callback", "headers": {"req_id": rid},
            "body": {"msgid": msgid, "aibotid": "aibbot-1", "chattype": chattype, "from": {"userid": userid},
                     "msgtype": "text", "text": {"content": text}}}


def test_wecom_callback_is_parsed_into_a_channel_message():
    m = wecom.parse_callback(_callback(" 查一下产量 "))
    assert (m.channel, m.staff_id, m.text, m.conversation_type, m.session_webhook) == ("wecom", "zhangsan", "查一下产量", "1", "r1")
    assert m.webhook_expires > datetime.now(timezone.utc)
    assert wecom.parse_callback(_callback("x", chattype="group")).conversation_type == "2"
    assert wecom.parse_callback({"cmd": "aibot_msg_callback", "headers": {}, "body": {"msgtype": "text"}}) is None


def test_wecom_replies_are_clipped_by_bytes():
    out = wecom.clip_bytes("字" * 3000)
    assert len(out.encode("utf-8")) <= wecom.MAX_REPLY_BYTES and "完整结果请在电脑上" in out


async def test_wecom_admin_config_and_client_flag(client, device_headers, fake_wecom):
    assert (await client.put("/api/v1/admin/channels/wecom", headers=ADMIN,
                             json={"enabled": True, "bot_id": "aibbot-1"})).status_code == 422
    await _enable_wecom(client)
    cfg = (await client.get("/api/v1/admin/channels/wecom", headers=ADMIN)).json()
    assert cfg["enabled"] and cfg["bot_id"] == "aibbot-1" and "wecom-secret-0123456789" not in json.dumps(cfg)
    assert fake_wecom.instances[-1].secret == "wecom-secret-0123456789"
    state = (await client.get("/api/v1/client/channels", headers=device_headers)).json()
    assert state["wecom"] is True and state["dingtalk"] is False
    # 只开企业微信也能生成绑定码
    assert (await client.post("/api/v1/client/channels/bind-code", headers=device_headers)).status_code == 200


async def test_wecom_end_to_end_and_bindings_are_per_channel(client, device_headers, fake_wecom):
    await _enable(client)  # 钉钉也开着：两边的绑定互不影响
    bot = await _enable_wecom(client)
    code = (await client.post("/api/v1/client/channels/bind-code", headers=device_headers)).json()["code"]
    await _inbound(wecom.parse_callback(_callback(f"绑定 {code}", rid="rb")))
    assert bot.replies[-1][1] == "rb" and "已绑定到电脑 **PC-001**" in bot.replies[-1][2]
    bindings = (await client.get("/api/v1/client/channels", headers=device_headers)).json()["bindings"]
    assert [(b["channel"], b["external_name"]) for b in bindings] == [("wecom", "zhangsan")]

    # 同一个人在钉钉里还没绑定：钉钉的消息不会跑到这台电脑上
    await _inbound(_msg("帮我整理桌面", staff="zhangsan"))
    assert "还没有绑定电脑" in fake_dingtalk_replies()[-1][1]

    await client.get("/api/v1/client/channels/messages", headers=device_headers)
    row = await _inbound(wecom.parse_callback(_callback("把日报发我", rid="r2", msgid="w2")))
    assert row.channel == "wecom" and "正在你的电脑上处理" in bot.replies[-1][2]
    got = (await client.get("/api/v1/client/channels/messages", headers=device_headers)).json()["messages"]
    assert [(m["channel"], m["text"], m["sender"]) for m in got] == [("wecom", "把日报发我", "zhangsan")]
    await client.post(f"/api/v1/client/channels/messages/{got[0]['id']}/finish", headers=device_headers,
                      json={"status": "succeeded", "answer": "已发，见 D:\\日报", "conversation_id": "c9"})
    assert bot.replies[-1] == ("zhangsan", "r2", "已发，见 D:\\日报")

    # 群里发：提示私聊，不排队
    assert await _inbound(wecom.parse_callback(_callback("查产量", chattype="group", rid="r3", msgid="w3"))) is None
    assert "请私聊我" in bot.replies[-1][2]


def fake_dingtalk_replies():
    return FakeApi.instances[-1].replies if FakeApi.instances else []


async def test_wecom_protocol_against_a_local_websocket_server(monkeypatch):
    """真的连一个 WebSocket：认证、收消息、回复这条消息、过了时间主动发、错误码。"""
    from websockets.asyncio.server import serve

    seen: list[dict] = []
    got_reply = asyncio.Event()

    async def fake_wecom_server(ws):
        sub = json.loads(await ws.recv())
        seen.append(sub)
        await ws.send(json.dumps({"headers": {"req_id": sub["headers"]["req_id"]}, "errcode": 0, "errmsg": "ok"}))
        await ws.send(json.dumps(_callback("查产量", rid="cb-1")))
        async for raw in ws:
            frame = json.loads(raw)
            seen.append(frame)
            code = 0 if frame["cmd"] != "aibot_send_msg" or len(seen) < 4 else 40001
            await ws.send(json.dumps({"headers": {"req_id": frame["headers"]["req_id"]}, "errcode": code, "errmsg": "x"}))
            got_reply.set()

    messages: list = []
    arrived = asyncio.Event()

    async def on_message(m):
        messages.append(m)
        arrived.set()

    async with serve(fake_wecom_server, "127.0.0.1", 0) as server:
        port = server.sockets[0].getsockname()[1]
        bot = wecom.WeComBot("aibbot-1", "s3cret", url=f"ws://127.0.0.1:{port}")
        status = dingtalk.StreamStatus()
        task = asyncio.create_task(bot.run(status, on_message))
        await asyncio.wait_for(arrived.wait(), 5)
        assert status.connected and messages[0].text == "查产量"
        assert seen[0]["cmd"] == "aibot_subscribe" and seen[0]["body"] == {"bot_id": "aibbot-1", "secret": "s3cret"}

        # 刚收到：回复这条消息（同一个 req_id，流式一次结束）
        m = messages[0]
        await bot.reply(m.session_webhook, m.webhook_expires, m.staff_id, "t", "6#线 1200 件")
        respond = seen[-1]
        assert respond["cmd"] == "aibot_respond_msg" and respond["headers"]["req_id"] == "cb-1"
        assert respond["body"]["stream"]["finish"] is True and respond["body"]["stream"]["content"] == "6#线 1200 件"

        # 任务做完时窗口早过了：主动发给这个人
        await bot.reply(m.session_webhook, datetime.now(timezone.utc) - timedelta(seconds=1), m.staff_id, "t", "做完了")
        send = seen[-1]
        assert send["cmd"] == "aibot_send_msg" and send["body"]["chatid"] == "zhangsan"
        assert send["body"]["markdown"]["content"] == "做完了"

        # 企业微信返回错误码：抛出来，由中枢记成「没回复成功」
        with pytest.raises(RuntimeError, match="40001"):
            await bot.reply("", None, m.staff_id, "t", "再发一次")
        task.cancel()
        await asyncio.gather(task, return_exceptions=True)


async def test_wecom_bad_credentials_show_up_as_an_error():
    from websockets.asyncio.server import serve

    async def reject(ws):
        sub = json.loads(await ws.recv())
        await ws.send(json.dumps({"headers": {"req_id": sub["headers"]["req_id"]}, "errcode": 40014, "errmsg": "invalid secret"}))
        await ws.wait_closed()

    async with serve(reject, "127.0.0.1", 0) as server:
        port = server.sockets[0].getsockname()[1]
        bot = wecom.WeComBot("aibbot-1", "wrong", url=f"ws://127.0.0.1:{port}")
        status = dingtalk.StreamStatus()
        task = asyncio.create_task(bot.run(status, lambda m: asyncio.sleep(0)))
        for _ in range(100):
            if status.last_error:
                break
            await asyncio.sleep(0.05)
        task.cancel()
        await asyncio.gather(task, return_exceptions=True)
    assert "40014" in status.last_error and not status.connected
