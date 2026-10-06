"""命令行配置语音转文字（管理后台网页完成前使用）。

和 setup_model.py 一样读取 server/.env 里的管理员令牌，调本机的管理接口：
添加提供方 → 添加 ASR 模型 → 把 asr 场景路由过去 → **实测走哪条路能通**。

最后那步是重点。ASR 有两种调用方式，不同的部署开放的不一样：

    inline     音频用 base64 直接放进请求体，一次往返出结果，不需要任何 URL。
               录音输入都是几十秒的短音频，这条路最合适。
    filetrans  只收 file_url 的接口。内网没有公网地址，所以先用百炼的临时文件
               上传换一个 oss:// 地址再提交任务、轮询结果。文件 48 小时自动失效，
               不用自建 OSS。

脚本会真发一段 1 秒的音频去试，把能通的那条写进模型配置，之后客户端就不用每次试错。

用法（在 server 目录下，服务端需已启动）。
下面每条都写成一行——PowerShell 的续行符是反引号 ` 而不是 \\，不要照搬 Linux 的换行写法：

    # 典型：百炼专属网关
    python -m scripts.setup_asr --base-url "https://ws-xxxx.ap-southeast-1.maas.aliyuncs.com/api/v1" --api-key "sk-xxx" --model qwen3-asr-flash

    # 只想看看哪条路能通，先不改配置：
    python -m scripts.setup_asr --base-url "..." --api-key "sk-xxx" --model qwen3-asr-flash --probe-only

    # 已经知道只能走文件上传：
    python -m scripts.setup_asr --base-url "..." --api-key "sk-xxx" --model qwen3-asr-flash-filetrans --transport filetrans

    # 服务端不在本机时，指明地址：
    python -m scripts.setup_asr --server http://10.0.0.10:8000 --base-url "..." --api-key "sk-xxx" --model qwen3-asr-flash

    # 配全厂热词（机台号、工序名这些，能明显减少同音字错误）：
    python -m scripts.setup_asr --hotwords "七号机台,飞织鞋面,楦头,后道" --only-hotwords

    # 查看当前配置：
    python -m scripts.setup_asr --list
"""

import argparse
import asyncio
import math
import struct
import sys

import httpx

from app.config import get_settings
from app.services import asr


def sample_wav(seconds: float = 1.0, rate: int = 16000) -> bytes:
    """一段很轻的音频，只用来探测通道是否可用，不指望识别出内容。"""
    frames = int(rate * seconds)
    body = bytearray()
    for i in range(frames):
        # 220Hz 的小振幅正弦，纯静音有的上游会直接判无效
        v = int(1200 * math.sin(2 * math.pi * 220 * i / rate))
        body += struct.pack("<h", v)
    header = (
        b"RIFF" + struct.pack("<I", 36 + len(body)) + b"WAVEfmt " + struct.pack("<I", 16)
        + struct.pack("<HHIIHH", 1, 1, rate, rate * 2, 2, 16)
        + b"data" + struct.pack("<I", len(body))
    )
    return header + bytes(body)


async def probe(target: asr.AsrTarget, audio: bytes) -> tuple[str | None, dict[str, str]]:
    """逐条试，返回第一条能通的通道和每条的结果说明。"""
    notes: dict[str, str] = {}
    async with httpx.AsyncClient(timeout=httpx.Timeout(120, connect=15)) as client:
        for name in asr.TRANSPORTS:
            try:
                result = await asr.try_transport(client, target, audio, "wav", name)
            except asr.AsrError as exc:
                notes[name] = str(exc)
                continue
            except httpx.HTTPError as exc:
                notes[name] = f"网络不通：{exc}"
                continue
            heard = (result.text or "").strip()
            notes[name] = "可用" + (f"（识别到「{heard[:20]}」）" if heard else "（没识别出内容，但通道是通的）")
            return name, notes
    return None, notes


def main() -> int:  # noqa: C901
    parser = argparse.ArgumentParser(description="配置 Flyknit 语音转文字")
    parser.add_argument("--server", default="http://localhost:8000", help="服务端地址，默认 http://localhost:8000")
    parser.add_argument("--provider-name", default="阿里云百炼", help="提供方名称，同名时复用")
    parser.add_argument("--base-url", help="接口地址，填到 /api/v1 或 /compatible-mode/v1 都行")
    parser.add_argument("--api-key", default="", help="接口密钥")
    parser.add_argument("--model", help="上游模型名，例如 qwen3-asr-flash")
    parser.add_argument("--name", help="界面显示名，默认与 --model 相同")
    parser.add_argument("--transport", choices=["auto", "inline", "filetrans"], default="auto",
                        help="调用方式。默认 auto：实测哪条通就用哪条")
    parser.add_argument("--language", default="", help="固定识别语言（zh / vi / en）。留空则跟界面语言走")
    parser.add_argument("--hotwords", default="", help="全厂热词，逗号分隔，例如 七号机台,飞织鞋面（只对 inline 生效）")
    parser.add_argument("--vocabulary-id", default="", help="上游注册好的热词表 id（filetrans 走这个）")
    parser.add_argument("--only-hotwords", action="store_true", help="只更新热词，不改模型配置")
    parser.add_argument("--probe-only", action="store_true", help="只测哪条路能通，不写入任何配置")
    parser.add_argument("--list", action="store_true", help="只显示当前配置")
    args = parser.parse_args()

    base = args.server.rstrip("/")
    api = base + "/api/v1/admin"
    client = httpx.Client(headers={"Authorization": f"Bearer {get_settings().admin_token}"}, timeout=120)

    try:
        client.get(f"{base}/healthz").raise_for_status()
    except httpx.HTTPError as exc:
        print(f"无法连接服务端 {args.server}：{exc}\n"
              f"请先在另一个窗口启动：python -m uvicorn app.main:app --host 0.0.0.0 --port 8000")
        return 1

    def call(method: str, path: str, **kwargs):
        r = client.request(method, api + path, **kwargs)
        if r.status_code == 403:
            print("管理员令牌无效：服务端用的令牌和本机 server/.env 不一致。改完 .env 要重启服务端。")
            sys.exit(1)
        if r.status_code >= 400:
            # 把服务端自己的说明打出来，比抛一串 traceback 有用
            try:
                detail = r.json().get("detail") or r.text
            except ValueError:
                detail = r.text
            print(f"\n服务端拒绝了 {method} {path}（HTTP {r.status_code}）：{str(detail)[:300]}")
            sys.exit(1)
        return r.json() if r.content else None

    # asr 是新加的场景。服务端如果还跑着旧代码就不认它，这里先确认，
    # 免得提供方和模型都建好了才在最后一步失败，留下半截配置。
    if "asr" not in {r["scene"] for r in call("GET", "/routes")}:
        print("服务端还在跑旧代码——它的场景列表里没有 asr，配置写不进去。")
        print("请到服务端那个窗口按 Ctrl+C 停掉，然后重新启动：")
        print("  python -m uvicorn app.main:app --host 0.0.0.0 --port 8000")
        print("（代码已经拉下来了，但正在运行的进程还是启动时加载的那份，必须重启才生效。）")
        return 1

    def show() -> int:
        route = next((r for r in call("GET", "/routes") if r["scene"] == "asr"), None)
        if not route or not route.get("model_id"):
            print("还没有配置语音转文字。客户端上的麦克风按钮点了会提示去配。")
            return 0
        models = {m["id"]: m for m in call("GET", "/models")}
        providers = {p["id"]: p for p in call("GET", "/providers")}
        m = models.get(route["model_id"], {})
        p = providers.get(m.get("provider_id"), {})
        extra = m.get("extra_body") or {}
        print(f"语音转文字：{m.get('name')}（{m.get('model')}）")
        print(f"  接口地址：{p.get('base_url')}")
        print(f"  调用方式：{extra.get('asr_transport', 'auto')}")
        print(f"  识别语言：{extra.get('asr_language') or '跟界面语言'}")
        words = extra.get("hotwords") or ""
        print(f"  热词：{words if words else '（未配置）'}")
        return 0

    if args.list:
        return show()

    if args.only_hotwords:
        if not args.hotwords:
            parser.error("--only-hotwords 需要同时给出 --hotwords")
        route = next((r for r in call("GET", "/routes") if r["scene"] == "asr"), None)
        if not route or not route.get("model_id"):
            print("还没有配置语音转文字模型，请先完整跑一次本脚本。")
            return 1
        model = next(m for m in call("GET", "/models") if m["id"] == route["model_id"])
        extra = dict(model.get("extra_body") or {})
        extra["hotwords"] = args.hotwords
        call("PATCH", f"/models/{model['id']}", json={"extra_body": extra})
        print(f"热词已更新：{args.hotwords}")
        return 0

    if not args.base_url or not args.model:
        parser.error("需要 --base-url 和 --model（或用 --list 查看当前配置）")

    target = asr.AsrTarget(
        model=args.model,
        base_url=args.base_url,
        api_key=args.api_key,
        transport="auto",
        language=args.language or None,
    )
    print(f"正在测试 {target.root} 上的 {args.model} …")
    print("（会发一段 1 秒的测试音频，两条路各试一次）")
    chosen, notes = asyncio.run(probe(target, sample_wav()))

    print("\n探测结果：")
    for name, note in notes.items():
        label = "内联音频 inline  " if name == "inline" else "文件上传 filetrans"
        print(f"  {label}  {note}")
    for name in ("inline", "filetrans"):
        if name not in notes:
            print(f"  {'内联音频 inline  ' if name == 'inline' else '文件上传 filetrans'}  未测试（前一条已通过）")

    if chosen is None:
        print("\n两条路都不通，没有写入任何配置。常见原因：")
        print("  · 地址写错——填到 /api/v1 或 /compatible-mode/v1 为止，不要带后面的路径")
        print("  · 模型名写错，或这个部署没开放该模型")
        print("  · 密钥不对，或服务器访问不到这个地址（走代理的话检查服务端的出网设置）")
        print("  · 该部署只开放了实时语音接口（WebSocket），这两条都不支持")
        return 2

    if args.probe_only:
        print(f"\n可用通道：{chosen}。--probe-only 模式，没有写入配置。")
        return 0

    # 探测时用 auto，写配置时固定成实测通过的那条，省掉客户端每次的试错
    transport = args.transport if args.transport != "auto" else chosen
    extra = {"asr_transport": transport}
    if args.language:
        extra["asr_language"] = args.language
    if args.hotwords:
        extra["hotwords"] = args.hotwords
    if args.vocabulary_id:
        extra["asr_vocabulary_id"] = args.vocabulary_id

    # 语音的地址格式和对话不一样（/api/v1 对 /compatible-mode/v1），所以绝不能去改
    # 别人在用的提供方——改了会把挂在它下面的对话模型一起带沟里。
    provider = next((p for p in call("GET", "/providers") if p["name"] == args.provider_name), None)
    if provider:
        others = [m for m in call("GET", "/models") if m["provider_id"] == provider["id"] and m["model"] != args.model]
        if others and provider["base_url"] != args.base_url:
            print(f"\n提供方「{provider['name']}」下面还挂着别的模型："
                  f"{'、'.join(m['name'] for m in others)}")
            print(f"  它现在的地址是 {provider['base_url']}，和语音要用的 {args.base_url} 不一样。")
            print("  改了会把那些模型也指到错的地址上，所以这里不动它。")
            if args.provider_name == parser.get_default("provider_name"):
                args.provider_name = f"{args.provider_name}（语音）"
                provider = next((p for p in call("GET", "/providers") if p["name"] == args.provider_name), None)
                print(f"  改用单独的提供方：{args.provider_name}")
            else:
                print("  请用 --provider-name 另起一个名字，例如 --provider-name \"百炼语音\"。")
                return 1

    if provider:
        provider = call("PATCH", f"/providers/{provider['id']}",
                        json={"base_url": args.base_url, "api_key": args.api_key})
        print(f"\n已更新提供方：{provider['name']}")
    else:
        provider = call("POST", "/providers",
                        json={"name": args.provider_name, "base_url": args.base_url, "api_key": args.api_key})
        print(f"\n已添加提供方：{provider['name']}")

    display = args.name or args.model
    body = {
        "name": display,
        "model": args.model,
        "supports_tools": False,   # 语音模型不参与工具调用
        "supports_vision": False,
        "extra_body": extra,
    }
    model = next((m for m in call("GET", "/models")
                  if m["provider_id"] == provider["id"] and m["model"] == args.model), None)
    if model:
        model = call("PATCH", f"/models/{model['id']}", json=body)
        print(f"已更新模型：{display}")
    else:
        model = call("POST", "/models", json={"provider_id": provider["id"], **body})
        print(f"已添加模型：{display}")

    route = next((r for r in call("GET", "/routes") if r["scene"] == "asr"), {})
    call("PUT", "/routes/asr", json={"model_id": model["id"],
                                     "fallback_model_id": route.get("fallback_model_id")})
    print(f"场景 asr → {display}（调用方式 {transport}）")
    print("\n配好了。客户端重新打开后，输入框右下角的麦克风按钮就能用了。")

    if transport == "filetrans" and args.hotwords and not args.vocabulary_id:
        print("\n注意：filetrans 这条路不收现拼的热词，刚才那些词不会生效。")
        print("要让热词起作用，得先在百炼控制台注册一份热词表，再把它的 id 填进来：")
        print("  python -m scripts.setup_asr --base-url ... --api-key ... --model ... --vocabulary-id vocab-xxx")
    elif not args.hotwords and not args.vocabulary_id:
        print("\n建议再配一次热词，机台号、工序名这类词不配基本都会识别错：")
        if transport == "filetrans":
            print("  （这条路要用上游注册好的词表）--vocabulary-id vocab-xxx")
        else:
            print('  python -m scripts.setup_asr --hotwords "七号机台,飞织鞋面,楦头" --only-hotwords')
    return 0


if __name__ == "__main__":
    sys.exit(main())
