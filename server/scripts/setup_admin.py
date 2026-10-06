"""命令行管理后台账号（管理后台网页完成前使用）。

聊天内容进了库之后，「谁看了谁的对话」必须答得上来，一个全组共用的 admin_token
答不了。所以查看聊天正文要用具名账号，配置类接口才继续收共享令牌。

用法（在 server 目录下，服务端需已启动）。每条写成一行——PowerShell 的续行符是
反引号 ` 而不是 \\：

    # 建一个能看聊天内容的账号
    python -m scripts.setup_admin --add it.zhang --name "张工" --can-read-chats

    # 建一个只管配置、不能看聊天的账号
    python -m scripts.setup_admin --add it.li --name "李工"

    # 看看谁看过谁的对话
    python -m scripts.setup_admin --access

    # 列出所有账号
    python -m scripts.setup_admin --list
"""

import argparse
import secrets
import string
import sys

import httpx

from app.config import get_settings


def strong_password(length: int = 14) -> str:
    """初始密码由脚本生成，不让人自己想——首次登录会强制改掉。"""
    alphabet = string.ascii_letters + string.digits + "!@#%-_"
    return "".join(secrets.choice(alphabet) for _ in range(length))


def main() -> int:
    parser = argparse.ArgumentParser(description="管理 Flyknit 后台账号")
    parser.add_argument("--server", default="http://localhost:8000", help="服务端地址，默认 http://localhost:8000")
    parser.add_argument("--add", metavar="USERNAME", help="新建账号的用户名")
    parser.add_argument("--name", default="", help="显示名，例如 张工")
    parser.add_argument("--can-read-chats", action="store_true",
                        help="允许查看聊天正文。不加这个就只能管配置，看不了对话内容")
    parser.add_argument("--password", default="", help="指定初始密码；不指定则自动生成")
    parser.add_argument("--list", action="store_true", help="列出所有账号")
    parser.add_argument("--access", action="store_true", help="列出谁看过谁的对话")
    args = parser.parse_args()

    base = args.server.rstrip("/")
    api = base + "/api/v1/admin"
    client = httpx.Client(headers={"Authorization": f"Bearer {get_settings().admin_token}"}, timeout=30)

    try:
        client.get(f"{base}/healthz").raise_for_status()
    except httpx.HTTPError as exc:
        print(f"无法连接服务端 {args.server}：{exc}\n"
              f"请先启动：python -m uvicorn app.main:app --host 0.0.0.0 --port 8000")
        return 1

    # 管理账号这组接口是新加的。服务端如果还跑着旧代码就没有这些路由，
    # 直接 404。先探一下，免得让人对着一句 Not Found 猜半天。
    probe = client.get(api + "/users")
    if probe.status_code == 404:
        print("服务端还在跑旧代码——它没有管理账号这组接口。")
        print("请到服务端那个窗口按 Ctrl+C 停掉，然后重新启动：")
        print("  python -m uvicorn app.main:app --host 0.0.0.0 --port 8000")
        print("（代码已经拉下来了，但正在运行的进程还是启动时加载的那份，必须重启才生效。）")
        return 1

    def call(method: str, path: str, **kwargs):
        r = client.request(method, api + path, **kwargs)
        if r.status_code == 403 and "令牌" in r.text:
            print("管理员令牌无效：服务端用的令牌和本机 server/.env 不一致。改完 .env 要重启服务端。")
            sys.exit(1)
        if r.status_code >= 400:
            try:
                detail = r.json().get("detail") or r.text
            except ValueError:
                detail = r.text
            print(f"\n服务端拒绝了 {method} {path}（HTTP {r.status_code}）：{str(detail)[:300]}")
            sys.exit(1)
        return r.json() if r.content else None

    if args.list:
        users = call("GET", "/users")
        if not users:
            print("还没有任何管理后台账号。")
            print('建一个：python -m scripts.setup_admin --add it.zhang --name "张工" --can-read-chats')
            return 0
        print(f"{'用户名':<16}{'显示名':<12}{'看聊天':<8}{'待改密':<8}{'最后登录'}")
        for u in users:
            print(f"{u['username']:<16}{u['display_name']:<12}"
                  f"{'是' if u['can_read_chats'] else '否':<8}"
                  f"{'是' if u['must_change_password'] else '否':<8}"
                  f"{u['last_login'] or '从未'}")
        return 0

    if args.access:
        rows = call("GET", "/chat-access")
        if not rows:
            print("还没有人查看过聊天正文。")
            return 0
        print(f"{'时间':<28}{'管理员':<14}{'会话':<24}说明")
        for r in rows:
            print(f"{r['created_at']:<28}{r['username']:<14}{r['target']:<24}{r['detail']}")
        return 0

    if not args.add:
        parser.error("需要 --add 用户名（或用 --list / --access 查看）")

    password = args.password or strong_password()
    user = call("POST", "/users", json={
        "username": args.add,
        "password": password,
        "display_name": args.name or args.add,
        "can_read_chats": args.can_read_chats,
    })

    print(f"\n已创建账号：{user['username']}（{user['display_name']}）")
    print(f"  初始密码：{password}")
    print(f"  查看聊天正文：{'允许' if user['can_read_chats'] else '不允许'}")
    print("\n把密码当面或通过私密渠道给本人，**首次登录会强制改密**——")
    print("否则建号的人一直知道他的密码，审计记录就失去意义了。")
    if args.can_read_chats:
        print("\n提醒：这个账号能看到员工的对话内容，每次查看都会留痕，用 --access 可以查。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
