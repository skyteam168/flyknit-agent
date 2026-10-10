"""把本系统的表从某个 schema（默认 public）里删掉，别的程序的表一张不碰。

用在：迁移时没单独建库，本系统的表和别的项目的表混在了同一个库的 public 里。
先把数据迁到单独的库或 schema 里、确认服务端在新位置跑好了，再用这个把 public 里的旧表清掉。

用法（在 server 目录下）：

    # 先看：列出会删哪些表、哪些同名表是别人的（不会动）。.env 已经改成新库的话，用 --db 指回原来的库
    python -m scripts.cleanup_postgres --db postgres

    # 确认无误再删
    python -m scripts.cleanup_postgres --db postgres --yes

只删「列都在本系统模型里、主键也对得上」的表；同名但结构不一样的，认定是别的程序的，跳过并列出来。
连接用的是 .env 里的 POSTGRES_HOST / PORT / DB / USER / PASSWORD（也可以用 --target 指定连接串）。
"""

from __future__ import annotations

import argparse
import asyncio
import sys
from pathlib import Path

import sqlalchemy as sa
from sqlalchemy.engine import make_url
from sqlalchemy.ext.asyncio import create_async_engine

SERVER_DIR = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SERVER_DIR))

from app import models  # noqa: E402,F401  注册所有表
from app.config import get_settings  # noqa: E402
from app.db import Base, check_schema  # noqa: E402
from scripts.migrate_to_postgres import masked, say, table_ownership  # noqa: E402


async def run(args: argparse.Namespace) -> int:
    target_url = args.target or get_settings().database_url
    if not target_url.startswith("postgresql"):
        say("目标库不是 PostgreSQL。请在 server/.env 里填好 POSTGRES_HOST 等几项（或用 --target 指定连接串）。")
        return 2
    if args.db:
        # .env 已经改成新库了，旧表在原来那个库（比如 postgres）里：只换库名，其它连接信息不变
        target_url = make_url(target_url).set(database=args.db).render_as_string(hide_password=False)
    schema = check_schema(args.schema) or "public"
    say(f"库：{masked(target_url)}")
    say(f"清理位置：schema {schema}")

    engine = create_async_engine(target_url)
    try:
        async with engine.begin() as conn:
            ours, foreign = await conn.run_sync(lambda c: table_ownership(c, schema))
            if foreign:
                say("\n同名但结构不是本系统的表（别的程序的），不会动：")
                for name in foreign:
                    say(f"  {name}")
            if not ours:
                say("\n这里没有本系统的表，不用清理。")
                return 0
            counts = {}
            for name in ours:
                counts[name] = (await conn.execute(sa.text(f'SELECT COUNT(*) FROM "{schema}"."{name}"'))).scalar_one()
            say(f"\n本系统的表（{len(ours)} 张）：")
            for name in ours:
                say(f"  {name:<28}{counts[name]:>10} 行")
            if not args.yes:
                say("\n这是预览，什么都没删。确认上面都是本系统的表、数据已经迁到新位置，再加 --yes 运行。")
                return 0
            # 按外键依赖倒序删：先删引用别人的表，再删被引用的
            order = [t.name for t in reversed(Base.metadata.sorted_tables) if t.name in ours]
            for name in order:
                await conn.execute(sa.text(f'DROP TABLE "{schema}"."{name}"'))
            say(f"\n已删除 {len(order)} 张表。别的程序的表没有动。")
            return 0
    except sa.exc.DBAPIError as exc:
        detail = str(getattr(exc, "orig", exc)).splitlines()[0]
        say(f"\n清理失败，已回滚，一张表都没删：{detail}")
        return 1
    finally:
        await engine.dispose()


def main() -> None:
    parser = argparse.ArgumentParser(description="从 PostgreSQL 的某个 schema 里删掉本系统的表（别的程序的表不动）")
    parser.add_argument("--target", help="PostgreSQL 连接串（默认用 .env 里的 POSTGRES_* 拼出来）")
    parser.add_argument("--db", help="清理哪个库（默认 .env 里的 POSTGRES_DB）。.env 已改成新库时，用它指回原来的库，比如 --db postgres")
    parser.add_argument("--schema", default="public", help="从哪个 schema 里删（默认 public）")
    parser.add_argument("--yes", action="store_true", help="真的删除（不加只预览）")
    args = parser.parse_args()
    sys.exit(asyncio.run(run(args)))


if __name__ == "__main__":
    main()
