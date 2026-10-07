"""轻量的表结构升级。

SQLAlchemy 的 `create_all` 只会建缺失的表，不会给已有的表加字段——
所以老版本升级上来时，新增的列（例如 audit_logs.scene）会缺失，写入时报
`table xxx has no column named yyy`。

这里在每次启动时对比模型定义和实际表结构，把缺失的列和索引补上。
只做「加列、加索引、加表」这类安全操作，不会改类型、不会删东西，SQLite 和 PostgreSQL 都适用。
"""

from __future__ import annotations

import logging

import sqlalchemy as sa
from sqlalchemy.engine import Connection

from .db import Base

log = logging.getLogger("flyknit.migrate")


def ensure_schema(connection: Connection) -> list[str]:
    """补齐缺失的表、列和索引，返回做过的改动（便于写日志和测试）。"""
    changes: list[str] = []
    Base.metadata.create_all(connection)  # 缺失的表

    inspector = sa.inspect(connection)
    existing_tables = set(inspector.get_table_names())
    preparer = connection.dialect.identifier_preparer

    for table in Base.metadata.sorted_tables:
        if table.name not in existing_tables:
            continue  # 刚刚由 create_all 建好，结构肯定是新的

        have = {c["name"] for c in inspector.get_columns(table.name)}
        for column in table.columns:
            if column.name in have:
                continue
            ddl = _add_column_ddl(connection, table, column, preparer)
            if ddl is None:
                log.warning("表 %s 缺少字段 %s，但无法自动添加，请手动处理", table.name, column.name)
                continue
            connection.exec_driver_sql(ddl)
            changes.append(f"{table.name}.{column.name}")
            log.info("已为表 %s 添加字段 %s", table.name, column.name)

        have_indexes = {i["name"] for i in inspector.get_indexes(table.name)}
        for index in table.indexes:
            if index.name in have_indexes:
                continue
            try:
                index.create(connection)
                changes.append(f"index {index.name}")
                log.info("已为表 %s 建立索引 %s", table.name, index.name)
            except sa.exc.SQLAlchemyError as exc:
                log.warning("建立索引 %s 失败：%s", index.name, exc)

    changes.extend(_ensure_one_owner(connection))
    return changes


def _ensure_one_owner(connection: Connection) -> list[str]:
    """
    超级管理员这一档是后加的，老库里一个都没有。

    一个都没有时，管理后台就只剩共享 admin_token 能建号——对已经在用的部署来说，
    等于某天早上账号页和安全页突然全是 403。所以把最早建的那个账号提上去：
    它是当初拿着 admin_token 建出来的第一个号，本来就是这套系统的主人。
    """
    users = sa.table("admin_users", sa.column("id"), sa.column("username"), sa.column("is_owner"))
    try:
        has_owner = connection.execute(
            sa.select(sa.func.count()).select_from(users).where(users.c.is_owner.is_(True))
        ).scalar()
        if has_owner:
            return []
        first = connection.execute(
            sa.select(users.c.id, users.c.username).order_by(users.c.id).limit(1)
        ).first()
    except sa.exc.SQLAlchemyError as exc:
        log.warning("检查超级管理员时出错：%s", exc)
        return []

    if first is None:
        return []  # 还没有任何账号。第一个建出来的会自动是超级管理员
    connection.execute(sa.update(users).where(users.c.id == first.id).values(is_owner=True))
    log.info("已把最早的管理员账号 %s 设为超级管理员", first.username)
    return [f"admin_users.is_owner={first.username}"]


def _add_column_ddl(connection: Connection, table: sa.Table, column: sa.Column, preparer) -> str | None:
    """拼 ALTER TABLE ADD COLUMN。非空列必须带默认值，否则 SQLite 会拒绝。"""
    try:
        type_sql = column.type.compile(connection.dialect)
    except sa.exc.CompileError:
        return None

    parts = [f"ALTER TABLE {preparer.format_table(table)} ADD COLUMN {preparer.format_column(column)} {type_sql}"]

    default_sql = _default_literal(column)
    if not column.nullable:
        if default_sql is None:
            # 不知道填什么，就先允许为空，至少不会卡住启动
            log.warning("表 %s 的新字段 %s 没有默认值，按可空添加", table.name, column.name)
        else:
            parts.append(f"NOT NULL DEFAULT {default_sql}")
    elif default_sql is not None:
        parts.append(f"DEFAULT {default_sql}")

    return " ".join(parts)


def _default_literal(column: sa.Column) -> str | None:
    """把列的默认值转成 SQL 字面量。只处理常量，函数默认值（如 utcnow）交给应用层。"""
    if column.server_default is not None and hasattr(column.server_default, "arg"):
        return str(column.server_default.arg)

    default = column.default
    value = default.arg if default is not None and not default.is_callable and not default.is_sequence else None
    if value is None:
        # 没有显式默认值时，按类型给一个安全的零值
        if isinstance(column.type, (sa.String, sa.Text)):
            return "''"
        if isinstance(column.type, (sa.Integer, sa.BigInteger, sa.SmallInteger, sa.Numeric, sa.Float)):
            return "0"
        if isinstance(column.type, sa.Boolean):
            return "0"
        if isinstance(column.type, (sa.DateTime, sa.Date)):
            return "CURRENT_TIMESTAMP"
        return None

    if isinstance(value, bool):
        return "1" if value else "0"
    if isinstance(value, (int, float)):
        return str(value)
    if isinstance(value, str):
        escaped = value.replace("'", "''")
        return f"'{escaped}'"
    return None
