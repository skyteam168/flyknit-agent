"""老版本数据库升级：补齐缺失的字段和索引，数据不丢。

对应线上出现过的错误：table audit_logs has no column named scene
（SQLAlchemy 的 create_all 只建表，不会给已有的表加字段）。
"""

import sqlalchemy as sa

from app.db import Base
from app.migrate import ensure_schema

# v0.3 时的 audit_logs：没有 scene 字段
OLD_AUDIT = """
CREATE TABLE audit_logs (
    id INTEGER NOT NULL PRIMARY KEY,
    device_id INTEGER,
    machine_name VARCHAR(200) NOT NULL,
    user_name VARCHAR(200) NOT NULL,
    conversation_id VARCHAR(64) NOT NULL,
    tool_name VARCHAR(100) NOT NULL,
    arguments TEXT NOT NULL,
    risk VARCHAR(20) NOT NULL,
    decision VARCHAR(20) NOT NULL,
    status VARCHAR(20) NOT NULL,
    summary TEXT NOT NULL,
    occurred_at DATETIME NOT NULL,
    created_at DATETIME NOT NULL
)
"""


def test_adds_missing_column_to_an_existing_table(tmp_path):
    engine = sa.create_engine(f"sqlite:///{tmp_path / 'old.db'}")
    with engine.begin() as conn:
        conn.exec_driver_sql(OLD_AUDIT)
        conn.exec_driver_sql(
            "INSERT INTO audit_logs (machine_name, user_name, conversation_id, tool_name, arguments,"
            " risk, decision, status, summary, occurred_at, created_at)"
            " VALUES ('PC-001', 'nguyen', 'c1', 'run_shell', '{}', 'blocked', 'blocked', 'skipped',"
            " 'rm -rf /', '2026-10-01 10:00:00', '2026-10-01 10:00:00')"
        )

    with engine.begin() as conn:
        changes = ensure_schema(conn)

    assert "audit_logs.scene" in changes

    with engine.begin() as conn:
        columns = {c["name"] for c in sa.inspect(conn).get_columns("audit_logs")}
        assert "scene" in columns
        # 老数据还在，新字段取到默认值
        row = conn.exec_driver_sql("SELECT machine_name, summary, scene FROM audit_logs").one()
        assert row == ("PC-001", "rm -rf /", "")
        # 补上字段后能正常写入
        conn.exec_driver_sql(
            "INSERT INTO audit_logs (machine_name, user_name, conversation_id, tool_name, arguments,"
            " scene, risk, decision, status, summary, occurred_at, created_at)"
            " VALUES ('PC-002', 'li', 'c2', 'run_shell', '{}', 'agent', 'blocked', 'blocked', 'skipped',"
            " 'format C:', '2026-10-02 10:00:00', '2026-10-02 10:00:00')"
        )
        assert conn.exec_driver_sql("SELECT COUNT(*) FROM audit_logs").scalar() == 2


def test_creates_tables_added_in_newer_versions(tmp_path):
    engine = sa.create_engine(f"sqlite:///{tmp_path / 'old.db'}")
    with engine.begin() as conn:
        conn.exec_driver_sql(OLD_AUDIT)
        ensure_schema(conn)

    with engine.begin() as conn:
        tables = set(sa.inspect(conn).get_table_names())
    # 后来新增的表由 create_all 建好
    assert {"usage_daily", "skill_packages", "devices", "providers"} <= tables


def test_is_idempotent_and_a_fresh_database_needs_no_change(tmp_path):
    engine = sa.create_engine(f"sqlite:///{tmp_path / 'new.db'}")
    with engine.begin() as conn:
        Base.metadata.create_all(conn)

    with engine.begin() as conn:
        assert ensure_schema(conn) == []
    with engine.begin() as conn:
        assert ensure_schema(conn) == []


def test_every_model_column_can_be_generated_as_ddl(tmp_path):
    """任何一张表少了任何一个字段，都应该能自动补上（防止以后加字段再踩同样的坑）。"""
    engine = sa.create_engine(f"sqlite:///{tmp_path / 'probe.db'}")
    with engine.begin() as conn:
        Base.metadata.create_all(conn)

    checked = 0
    for table in Base.metadata.sorted_tables:
        for column in table.columns:
            if column.primary_key:
                continue
            with engine.begin() as conn:
                if not _droppable(conn, table, column):
                    continue
                conn.exec_driver_sql(f'ALTER TABLE "{table.name}" DROP COLUMN "{column.name}"')
            with engine.begin() as conn:
                ensure_schema(conn)
                have = {c["name"] for c in sa.inspect(conn).get_columns(table.name)}
                assert column.name in have, f"{table.name}.{column.name} 没能自动补上"
                checked += 1
    assert checked > 20  # 确实覆盖到了大部分字段


def _droppable(conn, table: sa.Table, column: sa.Column) -> bool:
    """SQLite 不允许删除带索引、唯一约束或外键的列，这些列跳过。"""
    if column.foreign_keys:
        return False
    inspector = sa.inspect(conn)
    for index in inspector.get_indexes(table.name):
        if column.name in index["column_names"]:
            return False
    unique = inspector.get_unique_constraints(table.name)
    return all(column.name not in c["column_names"] for c in unique)


# 加超级管理员这一档之前的 admin_users：没有 is_owner
OLD_ADMIN_USERS = """
CREATE TABLE admin_users (
    id INTEGER NOT NULL PRIMARY KEY,
    username VARCHAR(64) NOT NULL UNIQUE,
    display_name VARCHAR(100) NOT NULL,
    password_hash VARCHAR(255) NOT NULL,
    must_change_password BOOLEAN NOT NULL,
    can_read_chats BOOLEAN NOT NULL,
    can_dispatch BOOLEAN NOT NULL,
    disabled BOOLEAN NOT NULL,
    created_at DATETIME NOT NULL,
    last_login DATETIME
)
"""


def _old_console(tmp_path, name):
    """造一个加超级管理员之前的库，里面已经有两个管理员账号。"""
    engine = sa.create_engine(f"sqlite:///{tmp_path / name}")
    with engine.begin() as conn:
        conn.exec_driver_sql(OLD_ADMIN_USERS)
        for i, username in enumerate(("it.yang", "it.li"), start=1):
            conn.exec_driver_sql(
                "INSERT INTO admin_users (id, username, display_name, password_hash,"
                " must_change_password, can_read_chats, can_dispatch, disabled, created_at)"
                " VALUES (?, ?, '', 'x', 0, 0, 0, 0, CURRENT_TIMESTAMP)",
                (i, username),
            )
    return engine


def test_the_earliest_account_becomes_the_owner(tmp_path):
    """
    老库里一个超级管理员都没有。不认领一个的话，账号页和安全页第二天早上会
    全变 403——只剩共享 admin_token 能用，而那个令牌多半没人记得。
    """
    engine = _old_console(tmp_path, "console.db")
    with engine.begin() as conn:
        changes = ensure_schema(conn)
    assert any("is_owner" in c for c in changes)

    with engine.connect() as conn:
        rows = dict(conn.exec_driver_sql("SELECT username, is_owner FROM admin_users").all())
    assert rows == {"it.yang": 1, "it.li": 0}, "最早建的那个号才是主人，不是全部提上去"


def test_an_existing_owner_is_left_alone(tmp_path):
    engine = _old_console(tmp_path, "console2.db")
    with engine.begin() as conn:
        ensure_schema(conn)
        conn.exec_driver_sql("UPDATE admin_users SET is_owner = 0 WHERE username = 'it.yang'")
        conn.exec_driver_sql("UPDATE admin_users SET is_owner = 1 WHERE username = 'it.li'")
    with engine.begin() as conn:
        assert not any("is_owner" in c for c in ensure_schema(conn))
    with engine.connect() as conn:
        rows = dict(conn.exec_driver_sql("SELECT username, is_owner FROM admin_users").all())
    assert rows == {"it.yang": 0, "it.li": 1}, "已经有主人了就别再认领一个"


# 按设备配额上线之前的 devices 表（没有 daily_tokens 和登录相关的列）
OLD_DEVICES = """
CREATE TABLE devices (
    id INTEGER NOT NULL PRIMARY KEY,
    token_hash VARCHAR(64) NOT NULL,
    machine_name VARCHAR(200) NOT NULL,
    user_name VARCHAR(200) NOT NULL,
    os_version VARCHAR(200) NOT NULL,
    client_version VARCHAR(50) NOT NULL,
    ui_language VARCHAR(10) NOT NULL,
    disabled BOOLEAN NOT NULL,
    created_at DATETIME NOT NULL,
    last_seen DATETIME
)
"""


def test_new_nullable_columns_start_empty_on_an_old_database(tmp_path):
    """
    可空的新列不能被补成「零值」：daily_tokens 补成 0 就是「不限制」，老电脑全都绕过了每日配额；
    可空的时间列补成 CURRENT_TIMESTAMP，SQLite 直接拒绝，升级失败。
    """
    engine = sa.create_engine(f"sqlite:///{tmp_path / 'old.db'}")
    with engine.begin() as conn:
        conn.exec_driver_sql(OLD_DEVICES)
        conn.exec_driver_sql(
            "INSERT INTO devices (token_hash, machine_name, user_name, os_version, client_version, ui_language, disabled, created_at)"
            " VALUES ('h', 'PC-001', 'nguyen', '', '', 'zh-CN', 0, '2026-10-01 10:00:00')"
        )
    with engine.begin() as conn:
        changes = ensure_schema(conn)
    assert "devices.daily_tokens" in changes and "devices.legal_agreed_at" in changes
    with engine.connect() as conn:
        row = conn.exec_driver_sql("SELECT daily_tokens, legal_agreed_at, login_method FROM devices").one()
    assert row == (None, None, "")


def test_devices_wrongly_set_to_unlimited_are_repaired_once(tmp_path):
    engine = sa.create_engine(f"sqlite:///{tmp_path / 'old.db'}")
    with engine.begin() as conn:
        Base.metadata.create_all(conn)
        conn.exec_driver_sql(
            "INSERT INTO devices (token_hash, machine_name, user_name, os_version, client_version, ui_language, disabled, created_at,"
            " domain, ip_addresses, observed_ip, mac_address, machine_guid, owner, department, note, daily_tokens, login_method, legal_agreed)"
            " VALUES ('h1', 'PC-001', 'a', '', '', 'zh-CN', 0, '2026-10-01', '', '', '', '', '', '', '', '', 0, '', ''),"
            "        ('h2', 'PC-002', 'b', '', '', 'zh-CN', 0, '2026-10-01', '', '', '', '', '', '', '', '', 500000, '', '')"
        )
    with engine.begin() as conn:
        assert "devices.daily_tokens reset=1" in ensure_schema(conn)
    with engine.connect() as conn:
        assert [r[0] for r in conn.exec_driver_sql("SELECT daily_tokens FROM devices ORDER BY id")] == [None, 500000]

    # 只修一次：之后管理员特意设的「不限制」（0）不再被动
    with engine.begin() as conn:
        conn.exec_driver_sql("UPDATE devices SET daily_tokens = 0 WHERE id = 1")
        assert not any("daily_tokens" in c for c in ensure_schema(conn))
    with engine.connect() as conn:
        assert conn.exec_driver_sql("SELECT daily_tokens FROM devices WHERE id = 1").scalar() == 0
