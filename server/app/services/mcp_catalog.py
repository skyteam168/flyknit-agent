"""
MCP 连接器目录：校验厂商配置、从现成的配置导入、在服务端试连一次。

配置的写法和 Claude Code 的 .mcp.json 保持一致（type / url / headers / command / args / env，
${KEY} 占位符），管理员可以把厂商文档里给的配置原样贴进来。

和技能库（skill_library）完全独立：不共用表、不共用接口、不共用名字空间。
"""

from __future__ import annotations

import json
import re
import shlex
from dataclasses import dataclass, field
from typing import Any

import httpx

TRANSPORTS = ("http", "sse", "stdio")
AUTH_MODES = ("none", "fields", "oauth")

#: 厂商 id 同时是员工端工具名的一段（mcp__<id>__<tool>），所以只许小写字母、数字、- 和 _
VENDOR_ID = re.compile(r"^[a-z0-9][a-z0-9_-]{1,39}$")
FIELD_KEY = re.compile(r"^[A-Za-z_][A-Za-z0-9_]{0,40}$")
PLACEHOLDER = re.compile(r"\$\{([A-Za-z_][A-Za-z0-9_]*)(?::-([^}]*))?\}")

#: 名字里带这些词的占位符，导入时默认当成要保密的值
SECRET_HINT = re.compile(r"key|token|secret|password|passwd|pwd|credential|auth", re.IGNORECASE)

#: 图标直接存进库里，太大会把列表接口拖慢
MAX_ICON_CHARS = 300_000

PROTOCOL_VERSION = "2025-06-18"


class McpConfigError(ValueError):
    """配置写错了。消息直接给管理员看。"""


# ---------- 校验 ----------
def placeholders(*values: Any) -> set[str]:
    """url / headers / env / args 里引用到的 ${KEY}。"""
    found: set[str] = set()

    def walk(v: Any) -> None:
        if isinstance(v, str):
            found.update(m.group(1) for m in PLACEHOLDER.finditer(v))
        elif isinstance(v, dict):
            for item in v.values():
                walk(item)
        elif isinstance(v, (list, tuple)):
            for item in v:
                walk(item)

    for value in values:
        walk(value)
    return found


def validate(data: dict[str, Any]) -> None:
    """保存前的检查。写错的配置到了员工电脑上只会变成「连不上」，不如在这里就指出来。"""
    vendor_id = data.get("id", "")
    if not VENDOR_ID.match(vendor_id or ""):
        raise McpConfigError("标识只能用小写字母、数字、- 和 _，2~40 个字符，例如 tencent-docs")
    transport = data.get("transport", "http")
    if transport not in TRANSPORTS:
        raise McpConfigError(f"不支持的连接方式：{transport}")
    if data.get("auth", "none") not in AUTH_MODES:
        raise McpConfigError(f"不支持的登录方式：{data.get('auth')}")

    if transport in ("http", "sse"):
        url = (data.get("url") or "").strip()
        # ${BASE:-https://…} 按默认值看；整段地址由员工填（${URL}/mcp）的不在这里查
        bare = PLACEHOLDER.sub(lambda m: m.group(2) if m.group(2) is not None else "x", url)
        whole_from_field = re.match(r"^\$\{[A-Za-z_][A-Za-z0-9_]*\}", url) is not None
        if not whole_from_field and not re.match(r"^https?://[^\s/]+", bare):
            raise McpConfigError("在线服务要填 http:// 或 https:// 开头的地址")
    else:
        if not (data.get("command") or "").strip():
            raise McpConfigError("本地进程要填启动命令，例如 npx")
        if data.get("auth") == "oauth":
            raise McpConfigError("本地进程不支持浏览器授权登录，请改用「填写密钥」")

    keys: list[str] = []
    for f in data.get("fields") or []:
        key = f.get("key", "")
        if not FIELD_KEY.match(key):
            raise McpConfigError(f"填写项的变量名不合法：{key!r}（字母、数字、下划线，不以数字开头）")
        if key in keys:
            raise McpConfigError(f"填写项 {key} 重复了")
        keys.append(key)

    used = placeholders(data.get("url"), data.get("headers"), data.get("env"), data.get("args"), data.get("command"))
    # ${VAR:-默认值} 带了默认值的可以不定义
    with_default = set()
    for text in _strings(data.get("url"), data.get("headers"), data.get("env"), data.get("args"), data.get("command")):
        with_default.update(m.group(1) for m in PLACEHOLDER.finditer(text) if m.group(2) is not None)
    missing = sorted(used - set(keys) - with_default)
    if missing:
        raise McpConfigError(f"配置里用到了 {', '.join('${' + k + '}' for k in missing)}，但下面的填写项里没有定义")

    icon = data.get("icon") or ""
    if len(icon) > MAX_ICON_CHARS:
        raise McpConfigError("图标太大了，请换一张 200KB 以内的图片")
    if icon and not (icon.startswith("data:image/") or re.match(r"^https?://", icon)):
        raise McpConfigError("图标要么是图片链接，要么上传图片")


def _strings(*values: Any):
    for v in values:
        if isinstance(v, str):
            yield v
        elif isinstance(v, dict):
            yield from _strings(*v.values())
        elif isinstance(v, (list, tuple)):
            yield from _strings(*v)


def needs_input(fields: list[dict], preset: dict[str, str]) -> list[str]:
    """员工连接时还得自己填的项（管理员没统一填好的必填项）。"""
    return [f["key"] for f in fields if f.get("required", True) and not preset.get(f["key"])]


# ---------- 导入 ----------
@dataclass
class Draft:
    """从一段现成配置里解析出来、还没保存的厂商。"""

    id: str
    name: str
    transport: str = "http"
    url: str = ""
    command: str = ""
    args: list[str] = field(default_factory=list)
    env: dict[str, str] = field(default_factory=dict)
    headers: dict[str, str] = field(default_factory=dict)
    auth: str = "none"
    fields: list[dict] = field(default_factory=list)
    preset: dict[str, str] = field(default_factory=dict)


def slugify(name: str) -> str:
    slug = re.sub(r"[^a-z0-9_-]+", "-", name.strip().lower()).strip("-_")
    if len(slug) < 2:
        slug = f"mcp-{slug or 'server'}"
    return slug[:40]


def parse_import(text: str) -> list[Draft]:
    """
    把厂商文档里给的配置变成草稿。认这几种写法：

    - Claude Code / Claude Desktop / Cursor 的 {"mcpServers": {"名字": {...}}}
    - 单个服务的 {"type": "http", "url": ...}（名字取域名）
    - 一行 `claude mcp add --transport http 名字 地址 --header "Authorization: Bearer xxx"`

    配置里直接写死的密钥（Authorization 头、带 KEY/TOKEN 字样的环境变量）会被挪成
    「管理员预填」的填写项：加密存库，配置里只留 ${KEY}。
    """
    text = (text or "").strip()
    if not text:
        raise McpConfigError("请粘贴配置内容")
    if text.startswith("claude ") or text.startswith("claude mcp"):
        return [_from_cli(text)]
    try:
        data = json.loads(text)
    except json.JSONDecodeError as exc:
        raise McpConfigError(f"不是合法的 JSON（第 {exc.lineno} 行）：{exc.msg}") from exc
    if not isinstance(data, dict):
        raise McpConfigError("配置应该是一个 JSON 对象")
    servers = data.get("mcpServers") or data.get("servers")
    if isinstance(servers, dict):
        if not servers:
            raise McpConfigError("mcpServers 里没有服务")
        return [_from_entry(name, entry) for name, entry in servers.items() if isinstance(entry, dict)]
    if "url" in data or "command" in data:
        name = data.get("name") or _name_from_url(data.get("url", "")) or data.get("command", "server")
        return [_from_entry(str(name), data)]
    raise McpConfigError("没认出配置格式：需要 mcpServers，或者包含 url / command 的服务配置")


def _name_from_url(url: str) -> str:
    m = re.match(r"^https?://([^/:]+)", url or "")
    if not m:
        return ""
    parts = [p for p in m.group(1).split(".") if p not in ("www", "mcp", "api", "com", "cn", "net", "io", "dev")]
    return parts[0] if parts else m.group(1)


def _from_entry(name: str, entry: dict) -> Draft:
    transport = str(entry.get("type") or entry.get("transport") or ("stdio" if entry.get("command") else "http")).lower()
    if transport in ("streamable-http", "streamablehttp", "http-stream"):
        transport = "http"
    if transport not in TRANSPORTS:
        raise McpConfigError(f"{name}：不支持的连接方式 {transport}（只支持 http / sse / stdio）")
    draft = Draft(
        id=slugify(name),
        name=name,
        transport=transport,
        url=str(entry.get("url") or entry.get("serverUrl") or ""),
        command=str(entry.get("command") or ""),
        args=[str(a) for a in entry.get("args") or []],
        env={str(k): str(v) for k, v in (entry.get("env") or {}).items()},
        headers={str(k): str(v) for k, v in (entry.get("headers") or {}).items()},
    )
    _lift_secrets(draft)
    return draft


def _from_cli(text: str) -> Draft:
    try:
        tokens = shlex.split(text)
    except ValueError as exc:
        raise McpConfigError(f"命令解析失败：{exc}") from exc
    if tokens[:3] != ["claude", "mcp", "add"]:
        raise McpConfigError("只认识 claude mcp add 命令")
    transport, headers, env, positional, rest = "stdio", {}, {}, [], []
    i = 3
    while i < len(tokens):
        t = tokens[i]
        if t == "--":
            rest = tokens[i + 1:]
            break
        if t in ("--transport", "-t") and i + 1 < len(tokens):
            transport = tokens[i + 1].lower()
            i += 2
            continue
        if t in ("--header", "-H") and i + 1 < len(tokens):
            key, _, value = tokens[i + 1].partition(":")
            headers[key.strip()] = value.strip()
            i += 2
            continue
        if t in ("--env", "-e") and i + 1 < len(tokens):
            key, _, value = tokens[i + 1].partition("=")
            env[key.strip()] = value
            i += 2
            continue
        if t in ("--scope", "-s", "--client-id", "--callback-port") and i + 1 < len(tokens):
            i += 2
            continue
        if t.startswith("-"):
            i += 1
            continue
        positional.append(t)
        i += 1
    if not positional:
        raise McpConfigError("命令里没有服务名")
    name = positional[0]
    entry: dict[str, Any] = {"type": transport, "headers": headers, "env": env}
    if transport in ("http", "sse"):
        if len(positional) < 2:
            raise McpConfigError("命令里没有服务地址")
        entry["url"] = positional[1]
    else:
        command = rest or positional[1:]
        if not command:
            raise McpConfigError("命令里没有启动命令（写在 -- 后面）")
        entry["command"], entry["args"] = command[0], command[1:]
    return _from_entry(name, entry)


def _lift_secrets(draft: Draft) -> None:
    """把写死的密钥挪成填写项；${VAR} 占位符也变成填写项。"""
    used = placeholders(draft.url, draft.headers, draft.env, draft.args)
    for key in sorted(used):
        draft.fields.append(_field(key, secret=bool(SECRET_HINT.search(key))))

    for header, value in list(draft.headers.items()):
        if PLACEHOLDER.search(value) or not value:
            continue
        if header.lower() in ("authorization", "x-api-key", "api-key", "x-auth-token") or SECRET_HINT.search(header):
            scheme, _, token = value.partition(" ")
            key = _unique_key(draft, "API_KEY")
            if token and scheme.lower() in ("bearer", "token", "basic"):
                draft.headers[header] = f"{scheme} ${{{key}}}"
                draft.preset[key] = token
            else:
                draft.headers[header] = f"${{{key}}}"
                draft.preset[key] = value
            draft.fields.append(_field(key, secret=True))

    for var, value in list(draft.env.items()):
        if PLACEHOLDER.search(value) or not value or not SECRET_HINT.search(var):
            continue
        key = _unique_key(draft, re.sub(r"[^A-Za-z0-9_]", "_", var).upper())
        draft.env[var] = f"${{{key}}}"
        draft.preset[key] = value
        draft.fields.append(_field(key, secret=True))

    if draft.fields:
        draft.auth = "fields"


def _unique_key(draft: Draft, base: str) -> str:
    taken = {f["key"] for f in draft.fields}
    key, n = base, 2
    while key in taken:
        key, n = f"{base}_{n}", n + 1
    return key


def _field(key: str, secret: bool) -> dict:
    return {"key": key, "label": key, "secret": secret, "required": True, "placeholder": "", "help": ""}


# ---------- 服务端试连 ----------
def expand(template: str, values: dict[str, str]) -> str:
    def repl(m: re.Match) -> str:
        value = values.get(m.group(1))
        return value if value else (m.group(2) or "")

    return PLACEHOLDER.sub(repl, template)


@dataclass
class ProbeResult:
    ok: bool
    server_name: str = ""
    server_version: str = ""
    tools: list[dict] = field(default_factory=list)
    error: str = ""


async def probe(http: httpx.AsyncClient, url: str, headers: dict[str, str], timeout: float = 20.0) -> ProbeResult:
    """
    用 Streamable HTTP 走一遍 initialize → tools/list，确认地址、密钥都对。
    只给管理员在后台点「测试连接」用；员工电脑上的连接由客户端自己做。
    """
    base = {
        "Accept": "application/json, text/event-stream",
        "Content-Type": "application/json",
        **headers,
    }
    try:
        init, session = await _rpc(http, url, base, 1, "initialize", {
            "protocolVersion": PROTOCOL_VERSION,
            "capabilities": {},
            "clientInfo": {"name": "FlyknitBuddy-Admin", "version": "1.0"},
        }, timeout)
        h = dict(base)
        if session:
            h["Mcp-Session-Id"] = session
        h["MCP-Protocol-Version"] = str(init.get("protocolVersion") or PROTOCOL_VERSION)
        await _notify(http, url, h, "notifications/initialized", timeout)
        tools: list[dict] = []
        cursor = None
        for page in range(20):  # 防止服务端翻页翻不完
            listed, _ = await _rpc(http, url, h, 2 + page, "tools/list", {"cursor": cursor} if cursor else {}, timeout)
            for t in listed.get("tools") or []:
                tools.append({"name": t.get("name", ""), "description": (t.get("description") or "")[:300]})
            cursor = listed.get("nextCursor")
            if not cursor:
                break
        info = init.get("serverInfo") or {}
        return ProbeResult(ok=True, server_name=str(info.get("name", "")), server_version=str(info.get("version", "")), tools=tools)
    except McpProbeError as exc:
        return ProbeResult(ok=False, error=str(exc))
    except httpx.TimeoutException:
        return ProbeResult(ok=False, error="连接超时")
    except httpx.HTTPError as exc:
        return ProbeResult(ok=False, error=f"连不上：{exc}")


class McpProbeError(Exception):
    pass


async def _rpc(http, url, headers, rid, method, params, timeout) -> tuple[dict, str]:
    body = {"jsonrpc": "2.0", "id": rid, "method": method, "params": params}
    resp = await http.post(url, json=body, headers=headers, timeout=timeout)
    if resp.status_code in (401, 403):
        raise McpProbeError(f"对方拒绝了（HTTP {resp.status_code}）：密钥不对，或者这个服务需要浏览器授权登录")
    if resp.status_code == 404:
        raise McpProbeError("地址不对（HTTP 404）")
    if resp.status_code >= 400:
        raise McpProbeError(f"HTTP {resp.status_code}：{resp.text[:200]}")
    session = resp.headers.get("mcp-session-id", "")
    ctype = resp.headers.get("content-type", "")
    message = None
    if "text/event-stream" in ctype:
        for payload in sse_data(resp.text):
            try:
                candidate = json.loads(payload)
            except json.JSONDecodeError:
                continue
            if isinstance(candidate, dict) and candidate.get("id") == rid:
                message = candidate
                break
    else:
        try:
            message = resp.json()
        except ValueError as exc:
            raise McpProbeError("对方返回的不是 MCP 消息，地址可能填错了") from exc
    if not isinstance(message, dict):
        raise McpProbeError("对方没有回应这个请求")
    if "error" in message:
        err = message["error"] or {}
        raise McpProbeError(f"{method} 失败：{err.get('message', err)}")
    return message.get("result") or {}, session


async def _notify(http, url, headers, method, timeout) -> None:
    await http.post(url, json={"jsonrpc": "2.0", "method": method}, headers=headers, timeout=timeout)


def sse_data(text: str) -> list[str]:
    """把 text/event-stream 拆成一条条 data。多行 data 按规范用换行拼起来。"""
    events, lines = [], []
    for raw in text.replace("\r\n", "\n").split("\n"):
        if raw == "":
            if lines:
                events.append("\n".join(lines))
                lines = []
            continue
        if raw.startswith("data:"):
            lines.append(raw[6:] if raw[5:6] == " " else raw[5:])
    if lines:
        events.append("\n".join(lines))
    return events
