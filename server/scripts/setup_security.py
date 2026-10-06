"""命令行配置安全中心（管理后台网页完成前使用）。

分两层：全厂默认，加上给某台机器的单独设置。单独设置只存和全厂不一样的那几项，
所以改全厂默认时，没被单独设过的机器会自动跟着变。

每一项有「锁」：锁住的由 IT 统一配置，机器上的用户改不了。默认只放开备份和通知
这类改了也不降低安全线的项。

用法（在 server 目录下，服务端需已启动）。每条写成一行——PowerShell 的续行符是
反引号 ` 而不是 \\：

    # 看有哪些可配项、现在是什么状态
    python -m scripts.setup_security --list

    # 改全厂默认：把备份上限提到 2GB
    python -m scripts.setup_security --set backup_quota_mb=2048

    # 全厂锁住某一项（本来就锁着的不用再锁）
    python -m scripts.setup_security --lock system_tools

    # 给某台机器单独放开，让那台的用户自己能开关
    python -m scripts.setup_security --device 3 --unlock system_tools --note "工模组要查机台服务"

    # 给某台机器直接设成开启
    python -m scripts.setup_security --device 3 --set system_tools=true

    # 取消某台机器的单独设置，回到全厂默认
    python -m scripts.setup_security --device 3 --reset

    # 看某台机器最终生效的样子
    python -m scripts.setup_security --device 3 --list
"""

import argparse
import sys

import httpx

from app.config import get_settings


def parse_value(text: str):
    low = text.strip().lower()
    if low in ("true", "yes", "on", "1"):
        return True
    if low in ("false", "no", "off", "0"):
        return False
    try:
        return int(text)
    except ValueError:
        return text


def main() -> int:  # noqa: C901
    parser = argparse.ArgumentParser(description="配置 Flyknit 安全中心")
    parser.add_argument("--server", default="http://localhost:8000", help="服务端地址")
    parser.add_argument("--device", type=int, help="只对这台机器生效（设备 id，用 --devices 查）")
    parser.add_argument("--set", action="append", default=[], metavar="KEY=VALUE", help="设置某一项的值")
    parser.add_argument("--lock", action="append", default=[], metavar="KEY", help="锁住某一项（用户改不了）")
    parser.add_argument("--unlock", action="append", default=[], metavar="KEY", help="放开某一项（用户可以自己改）")
    parser.add_argument("--note", default="", help="给 --device 的改动写个备注，方便以后回想为什么开的")
    parser.add_argument("--reset", action="store_true", help="取消这台机器的单独设置")
    parser.add_argument("--list", action="store_true", help="显示当前配置")
    parser.add_argument("--devices", action="store_true", help="列出设备及其 id")
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

    if client.get(api + "/security/catalog").status_code == 404:
        print("服务端还在跑旧代码——它没有安全中心这组接口。")
        print("请到服务端那个窗口按 Ctrl+C 停掉，然后重新启动：")
        print("  python -m uvicorn app.main:app --host 0.0.0.0 --port 8000")
        return 1

    def call(method: str, path: str, **kwargs):
        r = client.request(method, api + path, **kwargs)
        if r.status_code >= 400:
            try:
                detail = r.json().get("detail") or r.text
            except ValueError:
                detail = r.text
            print(f"\n服务端拒绝了 {method} {path}（HTTP {r.status_code}）：{str(detail)[:300]}")
            sys.exit(1)
        return r.json() if r.content else None

    if args.devices:
        for d in call("GET", "/devices"):
            print(f"  [{d['id']:>3}] {d['machine_name']:<18}{d['user_name']:<16}{d.get('observed_ip') or ''}")
        return 0

    def show(effective: dict, title: str) -> None:
        print(f"\n{title}")
        print(f"  {'项':<24}{'当前值':<10}{'谁能改'}")
        for key, item in effective.items():
            who = "IT 统一配置" if item["locked"] else "用户可改"
            value = {True: "开", False: "关"}.get(item["value"], item["value"])
            print(f"  {key:<24}{str(value):<10}{who}   {item['title']}")

    if args.reset:
        if args.device is None:
            parser.error("--reset 需要 --device")
        call("DELETE", f"/devices/{args.device}/policy")
        print(f"设备 {args.device} 的单独设置已取消，回到全厂默认。")
        return 0

    values = {}
    for pair in args.set:
        if "=" not in pair:
            parser.error(f"--set 要写成 KEY=VALUE：{pair}")
        key, _, raw = pair.partition("=")
        values[key.strip()] = parse_value(raw)
    locks = {k: True for k in args.lock}
    locks.update({k: False for k in args.unlock})

    if not values and not locks:
        if args.device is not None:
            current = call("GET", f"/devices/{args.device}/policy")
            show(current["effective"], f"设备 {args.device}（{current['machine_name']}）最终生效的设置")
            if current["note"]:
                print(f"\n  备注：{current['note']}")
        else:
            current = call("GET", "/security")
            show(current["effective"], "全厂默认设置")
            print("\n给某台机器单独放开：--device <id> --unlock <项>")
        return 0

    if args.device is not None:
        saved = call("GET", f"/devices/{args.device}/policy")
        body = {
            "overrides": {**saved["overrides"], **values},
            "locks": {**saved["locks"], **locks},
            "note": args.note or saved["note"],
        }
        result = call("PUT", f"/devices/{args.device}/policy", json=body)
        show(result["effective"], f"设备 {args.device}（{result['machine_name']}）更新后")
        print("\n客户端下次拉配置（最多 10 分钟）后生效。")
    else:
        saved = call("GET", "/security")
        body = {"values": {**saved["values"], **values}, "locks": {**saved["locks"], **locks}}
        result = call("PUT", "/security", json=body)
        show(result["effective"], "全厂默认更新后")
        print("\n所有没被单独设置过的机器，下次拉配置后跟着变。")

    return 0


if __name__ == "__main__":
    sys.exit(main())
