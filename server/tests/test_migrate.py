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
