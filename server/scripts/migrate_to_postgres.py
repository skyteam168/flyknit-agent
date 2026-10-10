"""把 SQLite 里的数据整库搬到 PostgreSQL：表结构、所有数据、自增编号。

搬完后管理员账号、模型配置（含加密的 API Key）、安全策略、设备、用量、聊天和审计记录都还在，
员工端的登录令牌照样有效，不用重新登录。

用法（在 server 目录下）：

    1. 先停掉服务端（搬的过程中别再有新数据写进 SQLite）
    2. 在 server/.env 里填好 PostgreSQL（填了 POSTGRES_HOST 服务端就改用 PostgreSQL）：
         POSTGRES_HOST=10.0.0.5
         POSTGRES_PORT=5432
         POSTGRES_DB=flyknit
         POSTGRES_USER=flyknit
         POSTGRES_PASSWORD=...
    3. 先演练一遍，只读不写，看看要搬多少、有没有问题：
         python -m scripts.migrate_to_postgres --dry-run
    4. 正式搬：
         python -m scripts.migrate_to_postgres
    5. 启动服务端，登录后台核对

旧库默认是 FLYKNIT_DATABASE_URL 指的那个 SQLite 文件（默认 server/flyknit.db），也可以用 --source 指定。
目标库里已经有数据时会停下来，确认要覆盖就加 --replace（先清空目标库里本系统的表）。
SQLite 文件本身不会被改动——脚本先把它拷一份，在副本上读。

FLYKNIT_SECRET_KEY 必须和原来一样：模型 Key 是用它加密的，换了就解不开。
server/data 目录（安装包、软件包、技能包、反馈附件）不在数据库里，换服务器时要一起拷过去。
"""

from __future__ import annotations

import argparse
import asyncio
import shutil
import sqlite3
import sys
import tempfile
from datetime import datetime, timezone
from pathlib import Path

import sqlalchemy as sa
from sqlalchemy.engine import make_url
from sqlalchemy.ext.asyncio import AsyncEngine, create_async_engine

SERVER_DIR = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SERVER_DIR))

from app import models  # noqa: E402,F401  注册所有表
from app.config import get_settings  # noqa: E402
from app.db import Base, ensure_pg_schema, make_engine, strip_nul  # noqa: E402
from app.migrate import ensure_schema  # noqa: E402

BATCH = 500


def say(msg: str = "") -> None:
    print(msg, flush=True)


def resolve_sqlite(source: str | None) -> Path:
    """--source 可以是文件路径或 sqlite+aiosqlite:/// 连接串；相对路径先按当前目录找，找不到再按 server 目录找。"""
    raw = source or get_settings().sqlite_url or "sqlite+aiosqlite:///./flyknit.db"
    if raw.startswith("sqlite"):
        raw = make_url(raw).database or ""
    path = Path(raw)
    if not path.is_absolute():
        for base in (Path.cwd(), SERVER_DIR):
            if (base / path).is_file():
                return (base / path).resolve()
    if not path.is_file():
        raise SystemExit(f"找不到 SQLite 数据库：{raw}（用 --source 指定）")
    return path.resolve()


def snapshot(source: Path, workdir: Path) -> Path:
    """用 SQLite 的 backup 接口拷一份一致的副本（WAL 模式下直接拷文件可能拷到半截事务），原库不动。"""
    copy = workdir / "snapshot.db"
    src = sqlite3.connect(f"file:{source}?mode=ro", uri=True)
    dst = sqlite3.connect(copy)
    try:
        src.backup(dst)
    finally:
        dst.close()
        src.close()
    return copy


def masked(url: str) -> str:
    return make_url(url).render_as_string(hide_password=True)


class Report:
    def __init__(self) -> None:
        self.copied: dict[str, int] = {}
        self.truncated: dict[str, int] = {}
        self.nulled: dict[str, int] = {}
        self.dropped: dict[str, int] = {}
        self.nul: dict[str, int] = {}

    def bump(self, bucket: dict[str, int], key: str, n: int = 1) -> None:
        bucket[key] = bucket.get(key, 0) + n


def clean_rows(table: sa.Table, rows: list[dict], known_ids: dict[str, set], report: Report) -> list[dict]:
    """
    SQLite 不检查的东西，PostgreSQL 会检查，提前处理掉，免得搬到一半报错：
    - 没有时区的时间：本系统存的都是 UTC，补上时区；
    - 超长字符串（SQLite 不管 VARCHAR 长度）：截断并记下来；
    - 指向已删除记录的外键（SQLite 默认不强制外键）：按外键规则，SET NULL 的置空，CASCADE 的这一行不搬。
    """
    fks = [(c, next(iter(c.foreign_keys))) for c in table.columns if c.foreign_keys]
    out = []
    for row in rows:
        keep = True
        for column, fk in fks:
            value = row.get(column.name)
            target = fk.column.table.name
            if value is None or target not in known_ids or value in known_ids[target]:
                continue
            # 按外键自己的规则：ON DELETE SET NULL 的置空；CASCADE 的（原记录删掉时它本该一起删）不搬
            if (fk.ondelete or "").upper() == "SET NULL" or (column.nullable and not fk.ondelete and not column.primary_key):
                row[column.name] = None
                report.bump(report.nulled, f"{table.name}.{column.name}")
            else:
                keep = False
                report.bump(report.dropped, table.name)
                break
        if not keep:
            continue
        for column in table.columns:
            value = row.get(column.name)
            # PostgreSQL 的文本存不了 \x00（命令输出、二进制文件片段里会带），去掉
            if isinstance(value, (str, dict, list)):
                cleaned = strip_nul(value)
                if cleaned != value:
                    row[column.name] = value = cleaned
                    report.bump(report.nul, f"{table.name}.{column.name}")
            if isinstance(value, datetime) and value.tzinfo is None and getattr(column.type, "timezone", False):
                row[column.name] = value.replace(tzinfo=timezone.utc)
            elif isinstance(value, str) and isinstance(column.type, sa.String) and column.type.length:
                if len(value) > column.type.length:
                    row[column.name] = value[: column.type.length]
                    report.bump(report.truncated, f"{table.name}.{column.name}")
        out.append(row)
    return out


def single_int_pk(table: sa.Table) -> sa.Column | None:
    pk = list(table.primary_key.columns)
    if len(pk) == 1 and isinstance(pk[0].type, sa.Integer):
        return pk[0]
    return None


def table_ownership(conn: sa.Connection, schema: str | None = None) -> tuple[list[str], list[str]]:
    """
    库里和本系统同名的表，分成「本系统的」和「别的程序的」。

    settings、models、routes、feedback 这种名字很常见，和别的系统共用一个库时可能撞名。
    本系统建的表，列都是模型里有的；有模型里没有的列、或者缺主键列的，就是别人的表——绝不能往里写，更不能清空或删掉。
    """
    inspector = sa.inspect(conn)
    present = set(inspector.get_table_names(schema=schema))
    ours, foreign = [], []
    for table in Base.metadata.sorted_tables:
        if table.name not in present:
            continue
        cols = {c["name"] for c in inspector.get_columns(table.name, schema=schema)}
        model = {c.name for c in table.columns}
        pk = {c.name for c in table.primary_key.columns}
        (ours if cols <= model and pk <= cols else foreign).append(table.name)
    return ours, foreign


async def target_rows(target: AsyncEngine) -> dict[str, int]:
    async with target.connect() as conn:
        existing = set(await conn.run_sync(lambda c: sa.inspect(c).get_table_names()))
        counts = {}
        for table in Base.metadata.sorted_tables:
            if table.name in existing:
                counts[table.name] = (await conn.execute(sa.select(sa.func.count()).select_from(table))).scalar_one()
        return counts


async def run(args: argparse.Namespace) -> int:
    settings = get_settings()
    target_url = args.target or settings.database_url
    if not target_url.startswith("postgresql"):
        say("目标库不是 PostgreSQL。请在 server/.env 里填好 POSTGRES_HOST 等几项（或用 --target 指定连接串）。")
        return 2
    source = resolve_sqlite(args.source)
    say(f"旧库（SQLite）：{source}")
    say(f"新库（PostgreSQL）：{masked(target_url)}")

    # Windows 上出错时 SQLite 副本可能还被占着，删不掉就留在临时目录，别让它盖住真正的错误
    schema = (args.schema if args.schema is not None else settings.postgres_schema).strip()
    say(f"新库 schema：{schema or 'public（默认）'}")
    with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as tmp:
        copy = snapshot(source, Path(tmp))
        src = create_async_engine(f"sqlite+aiosqlite:///{copy}")
        dst = make_engine(target_url, schema)
        try:
            # 老版本的库可能缺几列：在副本上补齐，读出来的结构就和现在的模型一致
            async with src.begin() as conn:
                await conn.run_sync(ensure_schema)

            try:
                if not args.dry_run:
                    await ensure_pg_schema(dst, schema)
                async with dst.connect() as conn:
                    _, foreign = await conn.run_sync(table_ownership)
                existing = await target_rows(dst) if not foreign else {}
            except Exception as exc:  # noqa: BLE001  连不上要把原因原样告诉 IT
                say(f"\n连不上 PostgreSQL：{exc}")
                say("检查 POSTGRES_HOST / PORT / USER / PASSWORD / DB，以及服务器防火墙是否放行 5432 端口。")
                return 2
            if foreign:
                # 不管加没加 --replace 都停下：这些表是别的程序的，写进去或清空都会毁掉别人的数据
                say("\n目标位置已经有别的程序的同名表，为了不碰到它们的数据，没有做任何操作：")
                for name in foreign:
                    say(f"  {name}")
                say("请给本系统单独建一个库（推荐，POSTGRES_DB=flyknit），或者在 .env 里设 POSTGRES_SCHEMA=flyknit 放进单独的 schema，再重新运行。")
                return 4
            occupied = {name: n for name, n in existing.items() if n}
            if occupied and not args.dry_run and not args.replace:
                say("\n新库里已经有数据，没有动它：")
                for name, n in occupied.items():
                    say(f"  {name}: {n} 行")
                say("确认要用旧库的数据覆盖，加 --replace 重新运行。")
                return 3

            report = Report()
            if args.dry_run:
                say("\n演练（只读，不写新库）：")
                async with src.connect() as conn:
                    for table in Base.metadata.sorted_tables:
                        n = (await conn.execute(sa.select(sa.func.count()).select_from(table))).scalar_one()
                        say(f"  {table.name:<28}{n:>10} 行")
                if occupied:
                    say("\n注意：新库里已经有数据，正式搬时要加 --replace 才会覆盖。")
                return 0

            current = ""
            try:
                async with dst.begin() as conn:
                    if args.replace and existing:
                        names = ", ".join(f'"{t.name}"' for t in Base.metadata.sorted_tables if t.name in existing)
                        await conn.execute(sa.text(f"TRUNCATE {names} RESTART IDENTITY CASCADE"))
                        say("已清空新库里本系统的表")
                    # 只建表，不跑 ensure_schema 里的一次性修复——那些记录会从旧库原样搬过来
                    await conn.run_sync(Base.metadata.create_all)

                    known_ids: dict[str, set] = {}
                    say("")
                    async with src.connect() as sconn:
                        for table in Base.metadata.sorted_tables:
                            current = table.name
                            result = await sconn.stream(sa.select(table))
                            total = 0
                            ids: set = set()
                            # 只有单列主键的表才记编号（外键都指向这种表的主键）
                            key = next(iter(table.primary_key.columns)).name if len(table.primary_key.columns) == 1 else None
                            async for chunk in result.mappings().partitions(BATCH):
                                rows = clean_rows(table, [dict(r) for r in chunk], known_ids, report)
                                if rows:
                                    await conn.execute(table.insert(), rows)
                                    total += len(rows)
                                    if key is not None:
                                        ids.update(r[key] for r in rows)
                            if key is not None:
                                known_ids[table.name] = ids
                            report.copied[table.name] = total
                            say(f"  {table.name:<28}{total:>10} 行")

                    # 自增编号接着旧库的最大值往后排，不然新记录会撞上搬过来的编号
                    for table in Base.metadata.sorted_tables:
                        pk = single_int_pk(table)
                        if pk is None:
                            continue
                        seq = (await conn.execute(sa.text("SELECT pg_get_serial_sequence(:t, :c)"),
                                                  {"t": table.name, "c": pk.name})).scalar()
                        if seq:
                            await conn.execute(sa.text(
                                f'SELECT setval(:s, COALESCE((SELECT MAX("{pk.name}") FROM "{table.name}"), 1), '
                                f'(SELECT MAX("{pk.name}") FROM "{table.name}") IS NOT NULL)'
                            ), {"s": seq})
            except sa.exc.DBAPIError as exc:
                # 整个迁移在一个事务里，出错就全部回滚，新库保持原样，修好后直接重跑
                detail = str(getattr(exc, "orig", exc)).splitlines()[0]
                say(f"\n迁移失败（表 {current or '建表'}）：{detail}")
                say("新库已回滚，没有写进任何数据。把上面这行发给开发排查，修好后直接重新运行即可。")
                return 1

            # 逐表核对行数（不搬的孤儿行要算进去）
            after = await target_rows(dst)
            bad = [t for t, n in report.copied.items() if after.get(t) != n]
            say("")
            if report.truncated:
                say("以下字段有超长内容，已按字段长度截断：")
                for key, n in report.truncated.items():
                    say(f"  {key}: {n} 行")
            if report.nul:
                say("以下字段含有空字符（\\x00，PostgreSQL 不能存），已去掉：")
                for key, n in report.nul.items():
                    say(f"  {key}: {n} 行")
            if report.nulled:
                say("以下字段指向已删除的记录，已置空：")
                for key, n in report.nulled.items():
                    say(f"  {key}: {n} 行")
            if report.dropped:
                say("以下表有指向已删除记录的残留行，没有搬（原来就看不到）：")
                for key, n in report.dropped.items():
                    say(f"  {key}: {n} 行")
            if bad:
                say(f"核对失败：{', '.join(bad)} 的行数和搬入的不一致")
                return 1
            say(f"完成：{len(report.copied)} 张表、{sum(report.copied.values())} 行已搬到 PostgreSQL，行数核对一致。")
            say("下一步：确认 server/.env 里 POSTGRES_* 已填好、FLYKNIT_SECRET_KEY 没变，然后启动服务端。")
            return 0
        finally:
            await src.dispose()
            await dst.dispose()


def main() -> None:
    parser = argparse.ArgumentParser(description="把 SQLite 数据整库迁移到 PostgreSQL")
    parser.add_argument("--source", help="SQLite 文件路径（默认 FLYKNIT_DATABASE_URL 指的那个，一般是 server/flyknit.db）")
    parser.add_argument("--target", help="PostgreSQL 连接串（默认用 .env 里的 POSTGRES_* 拼出来）")
    parser.add_argument("--dry-run", action="store_true", help="只看要搬多少，不写新库")
    parser.add_argument("--replace", action="store_true", help="新库里已有数据时，先清空本系统的表再搬")
    parser.add_argument("--schema", help="放进 PostgreSQL 的哪个 schema（默认用 .env 的 POSTGRES_SCHEMA，没有就是 public）")
    args = parser.parse_args()
    sys.exit(asyncio.run(run(args)))


if __name__ == "__main__":
    main()
