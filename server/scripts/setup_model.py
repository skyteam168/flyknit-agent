"""命令行配置模型（管理后台网页完成前使用）。

自动读取 server/.env 中的管理员令牌，调用本机服务端的管理接口：
添加模型提供方 → 添加模型 → 把指定场景路由到该模型 → 测试连通性。

用法（在 server 目录下，服务端需已启动）。
每条写成一行——PowerShell 的续行符是反引号 ` 而不是 \，别照搬 Linux 的换行写法：
    python -m scripts.setup_model --base-url http://10.0.0.10:8000/v1 --api-key sk-xxx --model qwen3.5-397b

    # 作为备用模型（例如阿里云百炼），只挂到各场景的 fallback：
    python -m scripts.setup_model --provider-name 阿里云百炼 --base-url "https://dashscope.aliyuncs.com/compatible-mode/v1" --api-key "sk-xxx" --model qwen-plus --fallback

    # 同步提供方的全部对话模型，供客户端输入框选择（可与 --model 一起用，--model 作为默认模型）：
    python -m scripts.setup_model --provider-name 阿里云百炼 --base-url "https://...compatible-mode/v1" --api-key "sk-xxx" --sync --model qwen3.8-max

    # 只把某个提供方的地址改回来（不动模型和路由）：
    python -m scripts.setup_model --provider-name 阿里云百炼 --base-url "https://xxx/compatible-mode/v1" --only-provider

    # 查看当前配置：
    python -m scripts.setup_model --list
"""

import argparse
import sys

import httpx

from app.config import get_settings

# asr 用 setup_asr.py 配，这里列出来只是为了 --scenes 校验时不误报
SCENES = ["chat", "agent", "translate", "title", "vision", "asr"]


def main() -> int:
    parser = argparse.ArgumentParser(description="配置 Flyknit 模型")
    parser.add_argument("--server", default="http://localhost:8000", help="服务端地址，默认 http://localhost:8000")
    parser.add_argument("--provider-name", default="内部模型", help="提供方名称，同名时复用")
    parser.add_argument("--base-url", help="OpenAI 兼容接口地址，通常以 /v1 结尾")
    parser.add_argument("--api-key", default="", help="接口密钥，内部模型没有可留空")
    parser.add_argument("--model", help="上游模型名，例如 qwen3.5-397b")
    parser.add_argument("--name", help="界面显示名，默认与 --model 相同")
    parser.add_argument("--no-tools", action="store_true", help="模型不支持工具调用（不能用于办事模式）")
    parser.add_argument("--vision", action="store_true", help="模型支持图片输入")
    parser.add_argument("--scenes", default="chat,agent,translate,title", help="路由到哪些场景，逗号分隔")
    parser.add_argument("--fallback", action="store_true", help="作为备用模型，不替换主模型")
    parser.add_argument("--sync", action="store_true", help="从提供方 /models 接口同步全部对话模型，供客户端选择")
    parser.add_argument("--include", default="", help="同步时只保留名称匹配该正则的模型，例如 qwen")
    parser.add_argument("--only-provider", action="store_true",
                        help="只改提供方的地址/密钥，不碰模型和路由（地址填错时用它修回来）")
    parser.add_argument("--list", action="store_true", help="只显示当前配置")
    args = parser.parse_args()

    token = get_settings().admin_token
    api = args.server.rstrip("/") + "/api/v1/admin"
    client = httpx.Client(headers={"Authorization": f"Bearer {token}"}, timeout=90)

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

    if args.list:
        providers = {p["id"]: p for p in call("GET", "/providers")}
        models = {m["id"]: m for m in call("GET", "/models")}
        print("模型：")
        for m in models.values():
            p = providers.get(m["provider_id"], {})
            print(f"  [{m['id']}] {m['name']}（{m['model']}）@ {p.get('base_url')}  工具调用={m['supports_tools']} 看图={m['supports_vision']}")
        print("场景路由：")
        for r in call("GET", "/routes"):
            main_m = models.get(r["model_id"], {}).get("name", "未配置")
            fb = models.get(r["fallback_model_id"], {}).get("name", "无")
            print(f"  {r['scene']:<10} 主模型：{main_m}  备用：{fb}")
        return 0

    if args.only_provider:
        # 只改提供方的地址和密钥，不碰模型和场景路由。地址写错时用它修回来。
        if not args.base_url:
            parser.error("--only-provider 需要 --base-url")
        provider = next((p for p in call("GET", "/providers") if p["name"] == args.provider_name), None)
        if not provider:
            print(f"没有名为「{args.provider_name}」的提供方。现有的有：")
            for p in call("GET", "/providers"):
                print(f"  {p['name']}  →  {p['base_url']}")
            return 1
        body = {"base_url": args.base_url}
        if args.api_key:
            body["api_key"] = args.api_key
        before = provider["base_url"]
        provider = call("PATCH", f"/providers/{provider['id']}", json=body)
        used_by = [m["name"] for m in call("GET", "/models") if m["provider_id"] == provider["id"]]
        print(f"已更新提供方：{provider['name']}")
        print(f"  地址：{before}  →  {provider['base_url']}")
        if not args.api_key:
            print("  密钥：未改动（没有传 --api-key）")
        print(f"  影响的模型：{'、'.join(used_by) if used_by else '（暂无）'}")
        return 0

    if not args.base_url or not (args.model or args.sync):
        parser.error("需要 --base-url，以及 --model 或 --sync（或使用 --list 查看配置）")

    # 提供方：同名复用并更新地址、密钥
    provider = next((p for p in call("GET", "/providers") if p["name"] == args.provider_name), None)
    if provider:
        provider = call("PATCH", f"/providers/{provider['id']}", json={"base_url": args.base_url, "api_key": args.api_key})
        print(f"已更新提供方：{provider['name']}")
    else:
        provider = call("POST", "/providers", json={"name": args.provider_name, "base_url": args.base_url, "api_key": args.api_key})
        print(f"已添加提供方：{provider['name']}")

    if args.sync:
        print("正在同步模型列表…")
        params = {"include": args.include} if args.include else {}
        result = call("POST", f"/providers/{provider['id']}/sync-models", params=params)
        print(f"提供方共有 {result['total']} 个模型，新增 {len(result['added'])} 个，跳过非对话模型 {result['skipped']} 个")
        for name in result["added"][:30]:
            print(f"  + {name}")
        if len(result["added"]) > 30:
            print(f"  …另外 {len(result['added']) - 30} 个")
        if not args.model:
            routes = call("GET", "/routes")
            if not any(r["model_id"] for r in routes):
                print("提示：还没有默认模型，请再加上 --model 指定一个默认模型。")
            print("客户端重新打开后即可在输入框里选择模型。")
            return 0

    # 模型：同一提供方下同名复用
    display = args.name or args.model
    model = next(
        (m for m in call("GET", "/models") if m["provider_id"] == provider["id"] and m["model"] == args.model), None
    )
    body = {
        "name": display,
        "model": args.model,
        "supports_tools": not args.no_tools,
        "supports_vision": args.vision,
    }
    if model:
        model = call("PATCH", f"/models/{model['id']}", json=body)
        print(f"已更新模型：{display}")
    else:
        model = call("POST", "/models", json={"provider_id": provider["id"], **body})
        print(f"已添加模型：{display}")

    # 场景路由
    routes = {r["scene"]: r for r in call("GET", "/routes")}
    for scene in [s.strip() for s in args.scenes.split(",") if s.strip()]:
        if scene not in SCENES:
            print(f"跳过未知场景：{scene}")
            continue
        current = routes[scene]
        if args.fallback:
            payload = {"model_id": current["model_id"], "fallback_model_id": model["id"]}
        else:
            payload = {"model_id": model["id"], "fallback_model_id": current["fallback_model_id"]}
        call("PUT", f"/routes/{scene}", json=payload)
        print(f"场景 {scene} → {'备用' if args.fallback else '主模型'} {display}")

    print("正在测试连通性…")
    result = call("POST", f"/models/{model['id']}/test")
    if result.get("ok"):
        print(f"连接成功，耗时 {result.get('elapsed_ms')} ms。现在可以在客户端里发消息了。")
        return 0
    print(f"连接失败：{result.get('error')}")
    print("常见原因：地址缺少 /v1、模型名写错、服务器上无法访问该地址、密钥错误。修正后重新运行本命令即可。")
    return 2


if __name__ == "__main__":
    sys.exit(main())
