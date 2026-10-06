"""增量提取、流式拼接、数据库备份与轮转。都是同步的纯逻辑。"""

import sqlalchemy as sa

from app.services import chat_archive, housekeeping


def test_takes_only_the_newest_user_message():
    """请求体带的是整段历史，归档只要这一轮新增的那条。"""
    messages = [
        {"role": "system", "content": "你是……"},
        {"role": "user", "content": "第一轮"},
        {"role": "assistant", "content": "好的"},
        {"role": "user", "content": "第二轮"},
        {"role": "assistant", "content": "", "tool_calls": [{"id": "t1"}]},
        {"role": "tool", "tool_call_id": "t1", "content": "工具输出"},
    ]
    text, attachments = chat_archive.last_user_message(messages)
    assert text == "第二轮"  # 不是「第一轮」，也不是工具输出
    assert attachments == 0


def test_counts_attachments_without_storing_them():
    content = [
        {"type": "text", "text": "看看这张图"},
        {"type": "image_url", "image_url": {"url": "data:image/png;base64,AAAA" + "B" * 50000}},
    ]
    text, attachments = chat_archive.text_of(content)
    assert text == "看看这张图"
    assert attachments == 1
    assert "base64" not in text  # 图片本身不入库


def test_handles_messages_with_no_user_turn():
    assert chat_archive.last_user_message([]) == ("", 0)
    assert chat_archive.last_user_message([{"role": "assistant", "content": "x"}]) == ("", 0)
    assert chat_archive.last_user_message([None, "junk"]) == ("", 0)


def test_stream_scanner_rebuilds_the_answer():
    scanner = chat_archive.StreamTextScanner()
    for part in (b'data: {"choices":[{"delta":{"content":"\\u597d"}}]}\n', b'data: {"choices":[{"delta":'):
        scanner.feed(part)
    scanner.feed(b'{"content":"\\u7684"}}]}\n')
    scanner.feed(b"data: [DONE]\n")
    scanner.finish()
    assert scanner.text == "好的"


def test_stream_scanner_survives_broken_json():
    scanner = chat_archive.StreamTextScanner()
    scanner.feed(b"data: {not json}\n: comment\n\n")
    scanner.finish()
    assert scanner.text == ""


def test_clip_marks_truncation():
    long = "x" * (chat_archive.MAX_CONTENT + 100)
    out = chat_archive.clip(long)
    assert len(out) < len(long)
    assert "已截断" in out


# ---------- 备份 ----------

def test_backup_makes_a_readable_copy(tmp_path):
    db = tmp_path / "flyknit.db"
    conn = sa.create_engine(f"sqlite:///{db}")
    with conn.begin() as c:
        c.exec_driver_sql("CREATE TABLE t (v TEXT)")
        c.exec_driver_sql("INSERT INTO t VALUES ('聊天内容')")

    out = housekeeping.backup(f"sqlite+aiosqlite:///{db}", tmp_path / "backups")

    assert out is not None and out.exists()
    copy = sa.create_engine(f"sqlite:///{out}")
    with copy.begin() as c:
        assert c.exec_driver_sql("SELECT v FROM t").scalar() == "聊天内容"


def test_backup_skips_non_sqlite(tmp_path):
    assert housekeeping.backup("postgresql+asyncpg://u:p@db/f", tmp_path) is None


def test_old_backups_are_pruned(tmp_path):
    for i in range(20):
        (tmp_path / f"flyknit-2026100{i:02d}-000000.db").write_text("x")
    housekeeping.prune(tmp_path, keep=5)
    assert len(list(tmp_path.glob("flyknit-*.db"))) == 5
