"""命令行管理后台账号（管理后台网页完成前使用）。

聊天内容进了库之后，「谁看了谁的对话」必须答得上来，一个全组共用的 admin_token
答不了。所以查看聊天正文要用具名账号，配置类接口才继续收共享令牌。

用法（在 server 目录下，服务端需已启动）。每条写成一行——PowerShell 的续行符是
反引号 ` 而不是 \\：

    # 建一个能看聊天内容的账号
    python -m scripts.setup_admin --add it.zhang --name "张工" --can-read-chats

    # 建一个只管配置、不能看聊天的账号
    python -m scripts.setup_admin --add it.li --name "李工"

    # 用自己的账号登录（初次登录会引导改密，之后的命令不用再输密码）
    python -m scripts.setup_admin --login it.zhang

    # 看最近的会话（只有元数据，没有正文）
    python -m scripts.setup_admin --chats

    # 看某次对话的正文。这一步会留痕
    python -m scripts.setup_admin --chat conv-9f2a

    # 看看谁看过谁的对话
    python -m scripts.setup_admin --access

    # 列出所有账号
    python -m scripts.setup_admin --list
"""

import argparse
import getpass
import json
import secrets
import string
import sys
from pathlib import Path

import httpx

from app.config import get_settings


#: 登录后的令牌存这里，省得每条命令都重输密码。放用户目录不放仓库里。
TOKEN_FILE = Path.home() / ".flyknit-admin.json"


def save_token(server: str, username: str, token: str) -> None:
    try:
        TOKEN_FILE.write_text(json.dumps({"server": server, "username": username, "token": token}), encoding="utf-8")
        # 只有自己能读。Windows 上这行无效，但那边本来就按用户隔离
        try:
            TOKEN_FILE.chmod(0o600)
        except OSError:
            pass
    except OSError as exc:
        print(f"（提示：登录状态没能保存到 {TOKEN_FILE}：{exc}）")


def load_token(server: str) -> str:
    try:
        saved = json.loads(TOKEN_FILE.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return ""
    return saved.get("token", "") if saved.get("server") == server else ""


def ask(prompt: str) -> str:
    """读密码时不回显。管道里跑（没有终端）就退回普通输入。"""
    try:
        return getpass.getpass(prompt)
    except (EOFError, OSError):
        return input(prompt)


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
    parser.add_argument("--login", metavar="USERNAME", help="用具名账号登录；初次登录会引导改密")
    parser.add_argument("--chats", action="store_true", help="列出最近的会话（只有元数据，没有正文）")
    parser.add_argument("--chat", metavar="CONVERSATION_ID", help="查看某次对话的正文。这一步会留痕")
    parser.add_argument("--days", type=int, default=7, help="--chats 看最近几天，默认 7")
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

    def as_user(method: str, path: str, **kwargs):
        """用具名账号的身份调接口，而不是共享的 admin_token。"""
        token = load_token(base)
        if not token:
            print("请先登录：python -m scripts.setup_admin --login 你的用户名")
            sys.exit(1)
        r = httpx.request(method, api + path, headers={"Authorization": f"Bearer {token}"}, timeout=30, **kwargs)
        if r.status_code == 401:
            print("登录已过期，请重新登录：python -m scripts.setup_admin --login 你的用户名")
            sys.exit(1)
        if r.status_code >= 400:
            try:
                detail = r.json().get("detail") or r.text
            except ValueError:
                detail = r.text
            print(f"\n服务端拒绝了：{str(detail)[:300]}")
            sys.exit(1)
        return r.json() if r.content else None

    if args.login:
        password = ask(f"{args.login} 的密码：")
        r = client.post(api + "/login", json={"username": args.login, "password": password},
                        headers={"Authorization": ""})
        if r.status_code != 200:
            print("用户名或密码不正确" if r.status_code == 401 else f"登录失败：{r.text[:200]}")
            return 1
        info = r.json()
        save_token(base, args.login, info["token"])
        print(f"\n已登录：{info['display_name']}")

        if info["must_change_password"]:
            print("这是初始密码，必须先改掉才能查看聊天内容。")
            while True:
                new = ask("设置新密码（至少 8 位）：")
                if len(new) < 8:
                    print("太短了，至少 8 位。")
                    continue
                if new == password:
                    print("不能和初始密码一样。")
                    continue
                if new != ask("再输一遍确认："):
                    print("两次输入不一致。")
                    continue
                break
            headers = {"Authorization": f"Bearer {info['token']}"}
            r = httpx.post(api + "/password", headers=headers,
                           json={"old_password": password, "new_password": new}, timeout=30)
            if r.status_code != 204:
                print(f"改密失败：{r.text[:200]}")
                return 1
            print("密码已修改。改密会让其他地方的登录状态失效，这里自动重新登录。")
            # 改密后旧令牌作废，重新换一个
            r = client.post(api + "/login", json={"username": args.login, "password": new},
                            headers={"Authorization": ""})
            save_token(base, args.login, r.json()["token"])

        print(f"看聊天内容：{'允许' if info['can_read_chats'] else '不允许（只能管配置）'}")
        print("\n接下来可以：")
        print("  python -m scripts.setup_admin --chats           # 看最近的会话")
        print("  python -m scripts.setup_admin --chat <会话 id>   # 看某次对话的正文")
        return 0

    if args.chats:
        rows = as_user("GET", f"/chats?days={args.days}")
        if not rows:
            print(f"最近 {args.days} 天没有聊天记录。")
            print("（客户端要更新到带 conversation_id 的版本，归档才会按会话归到一起）")
            return 0
        print(f"{'会话 id':<26}{'机器':<16}{'用户':<16}{'轮数':<6}{'最后一次'}")
        for r in rows:
            print(f"{r['conversation_id']:<26}{r['machine_name']:<16}{r['user_name']:<16}"
                  f"{r['turns']:<6}{r['last_at']}")
        print(f"\n共 {len(rows)} 次会话。看正文：--chat <会话 id>（会留痕）")
        return 0

    if args.chat:
        rows = as_user("GET", f"/chats/{args.chat}")
        print(f"会话 {args.chat}：{rows[0]['machine_name']} / {rows[0]['user_name']}，共 {len(rows)} 轮\n")
        for i, r in enumerate(rows, 1):
            print(f"── 第 {i} 轮 · {r['created_at']} · {r['model']}")
            print(f"  用户：{r['user_content']}")
            print(f"  回答：{r['assistant_content']}")
            if r["attachments"]:
                print(f"  （带了 {r['attachments']} 个附件，内容未存档）")
            print()
        print("这次查看已记入访问日志，用 --access 可以看到。")
        return 0

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
    print("\n把密码当面或通过私密渠道给本人。首次登录会强制改密——")
    print("否则建号的人一直知道他的密码，审计记录就失去意义了。")
    if args.can_read_chats:
        print("\n提醒：这个账号能看到员工的对话内容，每次查看都会留痕，用 --access 可以查。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
