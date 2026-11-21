"""数据脱敏（docs/data-protection.md）：发给外部模型前换成占位符，回答回来时换回原值。

重点：
- 识别靠校验压低误判：身份证校验码、银行卡 Luhn；18 位订单号这类不换；
- 默认关，关着时请求原样转发；
- 开着时上游只见占位符；非流式、流式（占位符被拆在两段里）、工具调用参数都能还原；
- 不拦截：一律换成占位符发出，响应头带各类型数量，员工端据此提示，任务不停；
- 只记数量，日志里没有原值；管理端统计和试一试。
"""

import json
import logging
from urllib.parse import unquote

import httpx
import pytest
import respx
import sqlalchemy as sa

from app import db
from app.models import ChatRecord, DlpEvent
from app.services import dlp
from conftest import ADMIN, setup_models
from test_embeddings import setup_embedding

pytestmark = pytest.mark.asyncio

UPSTREAM = "http://primary.local/v1/chat/completions"
ID = "11010519491231002X"          # 校验码正确
ID_BAD = "110105194912310021"      # 校验码不对：不是身份证
CARD = "6222 0212 3456 7890 128"   # Luhn 正确
PHONE = "13812345678"
EMAIL = "zhang.san@corp.cn"
ORDER = "202611200000001234"       # 18 位订单号


async def dlp_on(client, **extra):
    values = {"dlp_enabled": True, **extra}
    r = await client.put("/api/v1/admin/security", headers=ADMIN, json={"values": values, "locks": {}})
    assert r.status_code == 200, r.text


def chat_body(text: str, stream: bool = False) -> dict:
    return {"model": "agent", "stream": stream, "conversation_id": "conv-1",
            "messages": [{"role": "system", "content": "你是助手。"}, {"role": "user", "content": text}]}


# ---------- 识别 ----------


async def test_detection_uses_checksums_and_keeps_tokens_stable():
    vault = dlp.Vault()
    policy = dlp.Policy(enabled=True)
    text = (f"张三 {ID}，复核 {ID}；卡 {CARD}；电话 {PHONE}；邮箱 {EMAIL}；订单 {ORDER}；错号 {ID_BAD}；"
            "密码是Abc12345；token: sk-abcdefghijklmnopqrstuv；登录密码需要先用公钥加密")
    masked = dlp.mask_text(text, vault, policy)
    assert ID not in masked and masked.count("[身份证_1]") == 2  # 同一个号同一个占位符
    assert "[银行卡_1]" in masked and "[手机号_1]" in masked and "[邮箱_1]" in masked
    assert ORDER in masked and ID_BAD in masked                    # 校验对不上的不换
    assert "Abc12345" not in masked and "sk-abcdefghijklmnop" not in masked and masked.count(dlp.SECRET_MASK) == 2
    assert "登录密码需要先用公钥加密" in masked                     # 讲做法的句子不动
    assert vault.counts["id_card"] == 2 and vault.counts["secret"] == 2
    # 换回来
    assert dlp.restore_text("请核对 [身份证_1] 和【手机号_1】，[身份证_9] 不认识", vault) == f"请核对 {ID} 和{PHONE}，[身份证_9] 不认识"


async def test_password_value_stops_at_chinese_text():
    vault = dlp.Vault()
    masked = dlp.mask_text("系统密码是Abc12345。订单号 X1，password=p@ss,word 下一句", vault, dlp.Policy(enabled=True))
    assert masked == "系统密码是〔已隐藏〕。订单号 X1，password=〔已隐藏〕,word 下一句"


async def test_contacts_can_be_left_alone():
    vault = dlp.Vault()
    masked = dlp.mask_text(f"{PHONE} {EMAIL} {ID}", vault, dlp.Policy(enabled=True, contacts=False))
    assert PHONE in masked and EMAIL in masked and ID not in masked


async def test_json_arguments_are_escaped_when_restored():
    vault = dlp.Vault()
    token = vault.token("email", 'a"b@corp.cn')
    restored = dlp.restore_text('{"to": "' + token + '"}', vault, dlp._json_escape)
    assert json.loads(restored) == {"to": 'a"b@corp.cn'}


# ---------- 网关 ----------


@respx.mock
async def test_off_by_default_forwards_unchanged(client, device_headers):
    await setup_models(client)
    route = respx.post(UPSTREAM).mock(return_value=httpx.Response(200, json={"choices": [{"message": {"role": "assistant", "content": "好"}}]}))
    r = await client.post("/api/v1/chat/completions", headers=device_headers, json=chat_body(f"身份证 {ID}"))
    assert r.status_code == 200
    sent = json.loads(route.calls.last.request.content)
    assert ID in sent["messages"][1]["content"]
    assert "x-flyknit-masked" not in r.headers


@respx.mock
async def test_upstream_sees_placeholders_and_the_answer_comes_back_with_real_values(client, device_headers, caplog):
    await setup_models(client)
    await dlp_on(client)
    reply = {"choices": [{"message": {
        "role": "assistant",
        "content": "已核对：[身份证_1] 对应卡号 [银行卡_1]",
        "tool_calls": [{"id": "c1", "type": "function", "function": {"name": "write_file",
                         "arguments": json.dumps({"path": "a.csv", "content": "[身份证_1],[邮箱_1]"}, ensure_ascii=False)}}],
    }}], "usage": {"prompt_tokens": 100, "completion_tokens": 10}}
    route = respx.post(UPSTREAM).mock(return_value=httpx.Response(200, json=reply))
    caplog.set_level(logging.DEBUG, logger="flyknit")
    r = await client.post("/api/v1/chat/completions", headers=device_headers,
                          json=chat_body(f"员工 {ID}，工资卡 {CARD}，邮箱 {EMAIL}"))
    assert r.status_code == 200, r.text
    sent = json.loads(route.calls.last.request.content)
    raw = json.dumps(sent, ensure_ascii=False)
    assert ID not in raw and "6222" not in raw and EMAIL not in raw  # 上游一个原值都没见到
    assert "[身份证_1]" in sent["messages"][1]["content"]
    assert "【数据保护】" in sent["messages"][0]["content"]          # 告诉模型占位符怎么用
    msg = r.json()["choices"][0]["message"]
    assert msg["content"] == f"已核对：{ID} 对应卡号 {CARD}"
    args = json.loads(msg["tool_calls"][0]["function"]["arguments"])
    assert args["content"] == f"{ID},{EMAIL}"                        # AI 写进文件的是真实值
    assert r.headers["x-flyknit-masked"] == "3"
    ours = "\n".join(r.getMessage() for r in caplog.records if r.name.startswith("flyknit"))
    assert ID not in ours and EMAIL not in ours                      # 我们自己的日志里没有原值
    async with db.get_sessionmaker()() as s:
        event = (await s.scalars(sa.select(DlpEvent))).one()
        archived = (await s.scalars(sa.select(ChatRecord))).one()
    assert event.action == "masked" and event.counts == {"email": 1, "id_card": 1, "bank_card": 1}
    assert ID in archived.user_content                                 # 公司自己服务器上的归档照旧是原文


@respx.mock
async def test_streaming_restores_placeholders_split_across_chunks(client, device_headers):
    await setup_models(client)
    await dlp_on(client)
    events = [
        {"choices": [{"index": 0, "delta": {"content": "联系人 [手"}}]},
        {"choices": [{"index": 0, "delta": {"content": "机号_1] 收到"}}]},
        {"choices": [{"index": 0, "delta": {"tool_calls": [{"index": 0, "id": "c1", "function": {"name": "send", "arguments": "{\"to\":\"[邮"}}]}}]},
        {"choices": [{"index": 0, "delta": {"tool_calls": [{"index": 0, "function": {"arguments": "箱_1]\"}"}}]}}]},
        {"choices": [{"index": 0, "delta": {"content": "，结尾 ["}, "finish_reason": "stop"}]},
        {"choices": [], "usage": {"prompt_tokens": 50, "completion_tokens": 5}},
    ]
    sse = "".join(f"data: {json.dumps(e, ensure_ascii=False)}\n\n" for e in events) + "data: [DONE]\n\n"
    raw = sse.encode()
    # 故意从多字节字符中间切开，模拟网络分片
    chunks = [raw[i:i + 7] for i in range(0, len(raw), 7)]
    respx.post(UPSTREAM).mock(return_value=httpx.Response(200, content=b"".join(chunks), headers={"content-type": "text/event-stream"}))
    r = await client.post("/api/v1/chat/completions", headers=device_headers, json=chat_body(f"打给 {PHONE}，抄送 {EMAIL}", stream=True))
    assert r.status_code == 200
    text, args, done = "", "", False
    for line in r.text.split("\n"):
        if not line.startswith("data: "):
            continue
        if line == "data: [DONE]":
            done = True
            continue
        obj = json.loads(line[6:])
        for c in obj.get("choices") or []:
            text += c.get("delta", {}).get("content") or ""
            for call in c.get("delta", {}).get("tool_calls") or []:
                args += call.get("function", {}).get("arguments") or ""
    assert text == f"联系人 {PHONE} 收到，结尾 ["
    assert json.loads(args) == {"to": EMAIL}
    assert done


@respx.mock
async def test_never_blocks_and_tells_the_client_what_was_masked(client, device_headers):
    """以前的「发现就拦下」档会让员工看到「调用模型失败」、任务停住；现在一律换成占位符照常发出。
    数据库里留着旧的 dlp_block=true 也不起作用（未知键在读设置时丢掉）。"""
    await setup_models(client)
    r = await client.put("/api/v1/admin/security", headers=ADMIN,
                         json={"values": {"dlp_enabled": True, "dlp_block": True}, "locks": {}})
    assert r.status_code == 200, r.text
    route = respx.post(UPSTREAM).mock(return_value=httpx.Response(200, json={"choices": [{"message": {"role": "assistant", "content": "好"}}]}))
    emails = " ".join(f"u{i}@corp.cn" for i in range(28))
    r = await client.post("/api/v1/chat/completions", headers=device_headers, json=chat_body(f"身份证 {ID}，邮箱 {emails}"))
    assert r.status_code == 200, r.text
    sent = route.calls.last.request.content.decode()
    assert ID not in sent and "u0@corp.cn" not in sent and "[邮箱_28]" in json.loads(sent)["messages"][1]["content"]
    assert r.headers["x-flyknit-masked"] == "29"
    counts = json.loads(unquote(r.headers["x-flyknit-masked-counts"]))
    assert counts == {"id_card": 1, "email": 28}
    assert ID not in r.headers["x-flyknit-masked-counts"]              # 头里只有数量
    async with db.get_sessionmaker()() as s:
        assert (await s.scalars(sa.select(DlpEvent))).one().action == "masked"


@respx.mock
async def test_nothing_found_means_nothing_changes(client, device_headers):
    await setup_models(client)
    await dlp_on(client)
    route = respx.post(UPSTREAM).mock(return_value=httpx.Response(200, json={"choices": [{"message": {"role": "assistant", "content": "[身份证_1] 是我编的"}}]}))
    r = await client.post("/api/v1/chat/completions", headers=device_headers, json=chat_body(f"订单 {ORDER} 到哪了"))
    sent = json.loads(route.calls.last.request.content)
    assert "【数据保护】" not in sent["messages"][0]["content"]  # 没换东西就不加说明，提示词前缀不变
    assert r.json()["choices"][0]["message"]["content"] == "[身份证_1] 是我编的"


@respx.mock
async def test_embeddings_are_masked(client, device_headers):
    await setup_embedding(client)
    await dlp_on(client)
    route = respx.post("http://bailian.local/v1/embeddings").mock(return_value=httpx.Response(200, json={
        "data": [{"index": 0, "embedding": [0.1]}], "usage": {"prompt_tokens": 3}}))
    r = await client.post("/api/v1/embeddings", headers=device_headers, json={"input": [f"王五 {ID}"]})
    assert r.status_code == 200
    assert ID not in route.calls.last.request.content.decode()


# ---------- 管理端 ----------


@respx.mock
async def test_admin_summary_and_preview(client, device_headers):
    await setup_models(client)
    await dlp_on(client)
    respx.post(UPSTREAM).mock(return_value=httpx.Response(200, json={"choices": [{"message": {"role": "assistant", "content": "好"}}]}))
    await client.post("/api/v1/chat/completions", headers=device_headers, json=chat_body(f"{ID} {PHONE}"))
    s = (await client.get("/api/v1/admin/dlp/summary", headers=ADMIN)).json()
    assert s["totals"]["masked"] == {"id_card": 1, "phone": 1}
    assert s["devices"][0]["total"] == 2
    assert ID not in json.dumps(s, ensure_ascii=False)
    p = (await client.post("/api/v1/admin/dlp/preview", headers=ADMIN, json={"text": f"卡号 {CARD}"})).json()
    assert p["masked"] == "卡号 [银行卡_1]" and p["counts"] == {"bank_card": 1}
    assert (await client.get("/api/v1/admin/dlp/summary", headers=device_headers)).status_code in (401, 403)
