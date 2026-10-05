"""管理公司技能库（命令行版，管理后台界面做好之前先用这个）。

用法（在 server 目录执行，会自动读取 .env 里的管理员令牌）：

    # 从 GitHub 仓库导入（仓库里有多个技能时全部导入）
    python -m scripts.setup_skill --import https://github.com/owner/skills-repo

    # 只导入仓库里的某一个技能
    python -m scripts.setup_skill --import https://github.com/owner/skills-repo/tree/main/skills/excel-report

    # 从社区站的下载链接导入（ClawHub 等，直接粘贴技能包 zip 的下载地址）
    python -m scripts.setup_skill --import https://example.com/excel-report.zip

    # 离线环境：上传本地 zip
    python -m scripts.setup_skill --upload D:\\技能\\excel-report.zip

    # 标记为必装（所有电脑自动安装，用户不能停用）
    python -m scripts.setup_skill --require excel-report

    # 查看、停用、删除
    python -m scripts.setup_skill --list
    python -m scripts.setup_skill --disable excel-report
    python -m scripts.setup_skill --delete excel-report
"""

import argparse
import sys
from pathlib import Path

import httpx

from app.config import get_settings


def main() -> int:
    parser = argparse.ArgumentParser(description="管理 Flyknit 公司技能库")
    parser.add_argument("--server", default="http://localhost:8000", help="服务端地址，默认 http://localhost:8000")
    parser.add_argument("--import", dest="import_url", help="从 GitHub 页面链接或技能包 zip 下载链接导入")
    parser.add_argument("--upload", help="上传本地技能包 zip")
    parser.add_argument("--required", action="store_true", help="导入时直接标记为必装")
    parser.add_argument("--require", help="把已有技能标记为必装")
    parser.add_argument("--optional", help="取消必装")
    parser.add_argument("--enable", help="启用技能")
    parser.add_argument("--disable", help="停用技能（客户端不再看到）")
    parser.add_argument("--delete", help="从技能库删除")
    parser.add_argument("--list", action="store_true", help="显示技能库内容")
    args = parser.parse_args()

    api = args.server.rstrip("/") + "/api/v1/admin"
    client = httpx.Client(headers={"Authorization": f"Bearer {get_settings().admin_token}"}, timeout=180)

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
        if r.status_code >= 400:
            detail = r.json().get("detail", r.text) if r.headers.get("content-type", "").startswith("application/json") else r.text
            print(f"失败（HTTP {r.status_code}）：{detail}")
            sys.exit(1)
        return r.json() if r.content else None

    if args.import_url:
        result = call("POST", "/skills/import", json={"url": args.import_url, "required": args.required})
        print(f"已导入：{'、'.join(result['imported']) or '(无)'}")

    if args.upload:
        path = Path(args.upload)
        if not path.is_file():
            print(f"找不到文件：{path}")
            return 1
        with path.open("rb") as f:
            result = call(
                "POST",
                f"/skills/upload?required={'true' if args.required else 'false'}",
                files={"file": (path.name, f, "application/zip")},
            )
        print(f"已导入：{'、'.join(result['imported']) or '(无)'}")

    for name, body in (
        (args.require, {"required": True}),
        (args.optional, {"required": False}),
        (args.enable, {"enabled": True}),
        (args.disable, {"enabled": False}),
    ):
        if name:
            call("PATCH", f"/skills/{name}", json=body)
            print(f"已更新 {name}：{body}")

    if args.delete:
        call("DELETE", f"/skills/{args.delete}")
        print(f"已删除 {args.delete}")

    if args.list or not any([args.import_url, args.upload, args.require, args.optional, args.enable, args.disable, args.delete]):
        skills = call("GET", "/skills")
        if not skills:
            print("技能库是空的。用 --import 或 --upload 添加。")
            return 0
        print(f"{'技能':<28}{'版本':<12}{'状态':<10}{'大小':<10}来源")
        for s in skills:
            state = "必装" if s["required"] else ("启用" if s["enabled"] else "停用")
            size = f"{s['size'] / 1024:.0f} KB"
            print(f"{s['name']:<28}{s['version'] or '-':<12}{state:<10}{size:<10}{s['origin']}")
            print(f"{'':<28}{s['description'][:70]}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
