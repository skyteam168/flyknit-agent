"""设置每台电脑每天的 token 上限，以及额度用完时显示的联系方式。

用法（在 server 目录执行，会自动读取 .env 里的管理员令牌）：

    python -m scripts.setup_quota --daily 200000           # 每台电脑每天 20 万 token
    python -m scripts.setup_quota --daily 0                # 0 表示不限制
    python -m scripts.setup_quota --email jamesyang@shenzhougroup.com --phone 7815
    python -m scripts.setup_quota                          # 查看当前设置和各电脑用量
"""

import argparse
import sys

import httpx

from app.config import get_settings


def main() -> int:
    parser = argparse.ArgumentParser(description="设置 FlyknitBuddy 的 token 配额")
    parser.add_argument("--server", default="http://localhost:8000", help="服务端地址，默认 http://localhost:8000")
    parser.add_argument("--daily", type=int, help="每台电脑每天的 token 上限，0 表示不限制")
    parser.add_argument("--name", help="额度联系人显示名")
    parser.add_argument("--email", help="额度联系人邮箱")
    parser.add_argument("--phone", help="额度联系人电话")
    parser.add_argument("--days", type=int, default=7, help="用量统计的天数，默认 7")
    args = parser.parse_args()

    api = args.server.rstrip("/") + "/api/v1/admin"
    client = httpx.Client(headers={"Authorization": f"Bearer {get_settings().admin_token}"}, timeout=60)

    try:
        client.get(f"{args.server.rstrip('/')}/healthz").raise_for_status()
    except httpx.HTTPError as exc:
        print(f"无法连接服务端 {args.server}：{exc}\n请先在另一个窗口启动：python -m uvicorn app.main:app --host 0.0.0.0 --port 8000")
        return 1

    def call(method: str, path: str, **kwargs):
        r = client.request(method, api + path, **kwargs)
        if r.status_code == 403:
            print("管理员令牌无效：服务端使用的令牌与本机 server/.env 不一致。修改 .env 后需要重启服务端。")
            sys.exit(1)
        r.raise_for_status()
        return r.json() if r.content else None

    body = {k: v for k, v in (
        ("daily_tokens", args.daily),
        ("contact_name", args.name),
        ("contact_email", args.email),
        ("contact_phone", args.phone),
    ) if v is not None}
    quota = call("PUT", "/quota", json=body) if body else call("GET", "/quota")

    limit = quota["daily_tokens"]
    print(f"每台电脑每天上限：{'不限制' if not limit else f'{limit:,} tokens'}")
    print(f"额度联系人：{quota['contact_name']} · {quota['contact_email']} · 电话 {quota['contact_phone']}")

    rows = call("GET", f"/usage?days={args.days}")
    if not rows:
        print(f"\n最近 {args.days} 天还没有用量记录。")
        return 0
    print(f"\n最近 {args.days} 天用量：")
    print(f"{'电脑':<20}{'用户':<20}{'今天':>12}{'合计':>14}{'次数':>8}")
    for r in rows:
        over = " ←已超额" if limit and r["today_tokens"] >= limit else ""
        print(f"{r['machine_name'] or '-':<20}{r['user_name'] or '-':<20}{r['today_tokens']:>12,}{r['tokens']:>14,}{r['requests']:>8}{over}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
