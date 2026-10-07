"""
「贴一个链接就能引入」：管理员把 MCP 的地址、介绍页、GitHub 仓库或官方注册表里的名字贴进来，
服务端去读、去找配置，整理成草稿交给表单。

认这几种：

- 直接的 MCP 地址：https://mcp.example.com/mcp、.../sse
- 介绍页（AgentUni、Glama、Smithery、mcp.so、厂商文档……）：从页面里找 {"mcpServers": …} 配置、
  `claude mcp add …` 命令、以 /mcp 或 /sse 结尾的地址，顺带取页面标题、介绍、图标
- GitHub 仓库：读 README 找同样的东西
- 官方注册表（registry.modelcontextprotocol.io）里的名字：io.github.xxx/yyy，按 server.json 生成——
  在线地址（remotes）优先，其次 npm / PyPI 包（npx / uvx 启动）

在线地址没写任何凭证的，顺手试连一次：对方回 401 就标成「浏览器授权登录」，不用管理员自己猜。

只有管理员能调（后台接口），读外部网页有大小和时间上限。
"""

from __future__ import annotations

import html
import json
import logging
import re
from dataclasses import dataclass, field
from urllib.parse import quote, urljoin, urlparse

import httpx

from .mcp_catalog import Draft, McpConfigError, PROTOCOL_VERSION, parse_import, slugify

log = logging.getLogger("flyknit.mcp")

REGISTRY = "https://registry.modelcontextprotocol.io"
REGISTRY_NAME = re.compile(r"^[a-z0-9][a-z0-9.-]*\.[a-z]{2,}/[A-Za-z0-9._-]+$")
MAX_PAGE_BYTES = 2 * 1024 * 1024
FETCH_TIMEOUT = 15.0

_ENDPOINT = re.compile(r"https?://[^\s\"'<>`)\]]+?/(?:mcp|sse)/?(?=[\s\"'<>`)\],]|$)")
_CLI = re.compile(r"claude\s+mcp\s+add[^\n<]*")
_REGISTRY_MENTION = re.compile(r"\b(?:io|com|ai|dev)\.[a-z0-9.-]+/[A-Za-z0-9._-]+\b")


@dataclass
class PageMeta:
    title: str = ""
    description: str = ""
    icon: str = ""
    url: str = ""


@dataclass
class LinkDraft:
    """草稿 + 从页面或注册表里顺带拿到的展示信息。"""

    draft: Draft
    description: str = ""
    homepage: str = ""
    icon: str = ""
    source: str = ""
    notes: list[str] = field(default_factory=list)


def looks_like_link(text: str) -> bool:
    t = text.strip()
    return "\n" not in t and (t.startswith(("http://", "https://")) or bool(REGISTRY_NAME.match(t)))


async def resolve(http: httpx.AsyncClient, text: str) -> list[LinkDraft]:
    """贴进来的是一段配置就照旧解析；是链接或注册表名字就去找。"""
    t = (text or "").strip()
    if not looks_like_link(t):
        return [LinkDraft(d, source="config") for d in parse_import(t)]
    if not t.startswith("http"):
        found = await from_registry(http, t)
    else:
        found = await from_url(http, t)
    for item in found:
        await _detect_oauth(http, item)
    return found


# ---------- 链接 ----------
async def from_url(http: httpx.AsyncClient, url: str) -> list[LinkDraft]:
    parsed = urlparse(url)
    host = parsed.netloc.lower()

    if host.endswith("registry.modelcontextprotocol.io"):
        name = _registry_name_in(url)
        if not name:
            raise McpConfigError("注册表链接里没找到服务名，请直接贴服务名，例如 io.github.owner/server")
        return await from_registry(http, name)

    if host in ("github.com", "www.github.com"):
        parts = [p for p in parsed.path.split("/") if p]
        if len(parts) >= 2:
            readme = await _fetch_text(http, f"https://raw.githubusercontent.com/{parts[0]}/{parts[1]}/HEAD/README.md")
            if readme:
                meta = PageMeta(title=parts[1], url=url)
                found = await _from_text(http, readme, meta)
                if found:
                    return found
        raise McpConfigError("这个仓库的 README 里没找到 MCP 配置，请贴厂商给的配置或地址")

    if _is_endpoint_path(parsed.path):
        # 看起来就是 MCP 地址：不去读（GET 一个 MCP 地址通常只会得到 405 或一条挂着的流）
        return [_endpoint_draft(url, PageMeta(url=url))]

    try:
        resp = await http.get(url, headers={"Accept": "text/html,application/json,text/plain;q=0.9,*/*;q=0.5"},
                              timeout=FETCH_TIMEOUT, follow_redirects=True)
    except httpx.HTTPError as exc:
        raise McpConfigError(f"读不到这个链接：{exc}") from exc

    ctype = resp.headers.get("content-type", "")
    if resp.status_code in (401, 405, 406) or "text/event-stream" in ctype:
        return [_endpoint_draft(str(resp.url), PageMeta(url=url))]
    if resp.status_code >= 400:
        raise McpConfigError(f"读不到这个链接（HTTP {resp.status_code}）")
    body = resp.content[:MAX_PAGE_BYTES].decode(resp.encoding or "utf-8", errors="replace")

    if "json" in ctype:
        try:
            data = json.loads(body)
        except json.JSONDecodeError:
            data = None
        if isinstance(data, dict) and ("remotes" in data or "packages" in data):
            return _from_server_json(data, source="server.json")
        if data is not None:
            return [LinkDraft(d, source="config") for d in parse_import(body)]

    meta = page_meta(body, str(resp.url))
    found = await _from_text(http, body, meta)
    if not found:
        raise McpConfigError("这个页面里没找到 MCP 配置（mcpServers、claude mcp add 或 /mcp 地址）。可以把页面上的配置直接复制进来")
    return found


async def _from_text(http: httpx.AsyncClient, raw: str, meta: PageMeta) -> list[LinkDraft]:
    text = html.unescape(re.sub(r"<[^>]+>", " ", raw)) if "<" in raw else raw
    drafts: list[Draft] = []

    for config in _json_configs(text):
        try:
            drafts.extend(parse_import(json.dumps(config)))
        except McpConfigError:
            continue
    for line in _CLI.findall(text):
        try:
            drafts.extend(parse_import(line.strip()))
        except McpConfigError:
            continue

    drafts = _dedupe(drafts)
    if not drafts:
        endpoints = list(dict.fromkeys(m.rstrip(".,;") for m in _ENDPOINT.findall(text)))
        if endpoints:
            return [_endpoint_draft(u, meta) for u in endpoints[:5]]
        for name in dict.fromkeys(_REGISTRY_MENTION.findall(text)):
            try:
                return await from_registry(http, name)
            except McpConfigError:
                continue
        return []

    # 一个页面介绍的是一个服务：配置里的键名常常是一长串注册表名，用页面标题当名字更像样
    same_service = len(drafts) <= 2
    out = []
    for d in drafts:
        if same_service and meta.title:
            d.name = meta.title
            d.id = slugify(meta.title)
        out.append(LinkDraft(d, description=meta.description, homepage=meta.url, icon=meta.icon, source="page"))
    _unique_ids(out)
    return out


def _json_configs(text: str) -> list[dict]:
    """页面里所有能解析出来、带 mcpServers 的 JSON。"""
    decoder = json.JSONDecoder()
    found, seen = [], set()
    for m in re.finditer(r'"mcpServers"\s*:', text):
        # 往前找包住它的 {，最多试几层
        start = m.start()
        for _ in range(4):
            start = text.rfind("{", 0, start)
            if start < 0 or m.start() - start > 4000:
                break
            try:
                obj, _end = decoder.raw_decode(text[start:])
            except json.JSONDecodeError:
                continue
            if isinstance(obj, dict) and isinstance(obj.get("mcpServers"), dict):
                key = json.dumps(obj, sort_keys=True)
                if key not in seen:
                    seen.add(key)
                    found.append(obj)
                break
    return found


def _dedupe(drafts: list[Draft]) -> list[Draft]:
    seen, out = set(), []
    for d in drafts:
        key = (d.transport, d.url.rstrip("/"), d.command, tuple(d.args))
        if key not in seen:
            seen.add(key)
            out.append(d)
    return out


def _unique_ids(items: list[LinkDraft]) -> None:
    taken: set[str] = set()
    for item in items:
        base, n = item.draft.id, 2
        while item.draft.id in taken:
            suffix = f"-{n}" if item.draft.transport != "stdio" or n > 2 else "-local"
            item.draft.id = (base[: 40 - len(suffix)] + suffix)
            n += 1
        taken.add(item.draft.id)


def _is_endpoint_path(path: str) -> bool:
    p = path.rstrip("/").lower()
    return p.endswith("/mcp") or p.endswith("/sse") or p in ("/mcp", "/sse")


def _endpoint_draft(url: str, meta: PageMeta) -> LinkDraft:
    parsed = urlparse(url)
    transport = "sse" if parsed.path.rstrip("/").lower().endswith("/sse") else "http"
    name = meta.title or _host_name(parsed.netloc)
    return LinkDraft(
        Draft(id=slugify(name), name=name, transport=transport, url=url),
        description=meta.description, homepage=meta.url if meta.url != url else "", icon=meta.icon, source="endpoint",
    )


def _host_name(netloc: str) -> str:
    parts = [p for p in netloc.split(":")[0].split(".") if p not in ("www", "mcp", "api", "com", "cn", "net", "io", "dev", "ai", "app")]
    return parts[0] if parts else netloc


def page_meta(body: str, url: str) -> PageMeta:
    def meta(*names: str) -> str:
        for name in names:
            m = re.search(
                rf'<meta[^>]+(?:property|name)\s*=\s*["\']{re.escape(name)}["\'][^>]*content\s*=\s*["\']([^"\']*)["\']', body, re.I
            ) or re.search(
                rf'<meta[^>]+content\s*=\s*["\']([^"\']*)["\'][^>]*(?:property|name)\s*=\s*["\']{re.escape(name)}["\']', body, re.I
            )
            if m and m.group(1).strip():
                return html.unescape(m.group(1).strip())
        return ""

    title = meta("og:title", "twitter:title")
    if not title:
        m = re.search(r"<title[^>]*>(.*?)</title>", body, re.I | re.S)
        title = html.unescape(m.group(1).strip()) if m else ""
    # 「XXX | 站点名」「XXX - MCP Server」这类后缀去掉
    title = re.split(r"\s+[|·—–]\s+|\s+-\s+(?=[^-]*$)", title)[0].strip()[:100]
    icon = meta("og:image")
    if not icon:
        m = re.search(r'<link[^>]+rel\s*=\s*["\'][^"\']*icon[^"\']*["\'][^>]*href\s*=\s*["\']([^"\']+)["\']', body, re.I)
        icon = m.group(1) if m else ""
    return PageMeta(
        title=title,
        description=meta("og:description", "description", "twitter:description")[:300],
        icon=urljoin(url, icon) if icon else "",
        url=url,
    )


async def _fetch_text(http: httpx.AsyncClient, url: str) -> str:
    try:
        resp = await http.get(url, timeout=FETCH_TIMEOUT, follow_redirects=True)
    except httpx.HTTPError:
        return ""
    if resp.status_code != 200:
        return ""
    return resp.content[:MAX_PAGE_BYTES].decode("utf-8", errors="replace")


# ---------- 官方注册表 ----------
def _registry_name_in(url: str) -> str:
    m = re.search(r"(?:search=|servers/)([a-z0-9.-]+\.[a-z]{2,}(?:%2F|/)[A-Za-z0-9._-]+)", url)
    return m.group(1).replace("%2F", "/") if m else ""


async def from_registry(http: httpx.AsyncClient, name: str) -> list[LinkDraft]:
    try:
        resp = await http.get(f"{REGISTRY}/v0/servers", params={"search": name, "version": "latest"}, timeout=FETCH_TIMEOUT)
    except httpx.HTTPError as exc:
        raise McpConfigError(f"连不上 MCP 官方注册表：{exc}") from exc
    if resp.status_code != 200:
        raise McpConfigError(f"MCP 官方注册表返回 HTTP {resp.status_code}")
    items = (resp.json() or {}).get("servers") or []
    servers = [i.get("server", i) for i in items if isinstance(i, dict)]
    exact = [s for s in servers if s.get("name") == name]
    pick = (exact or servers)[:1]
    if not pick:
        raise McpConfigError(f"注册表里没有 {name}")
    return _from_server_json(pick[0], source="registry")


def _from_server_json(server: dict, source: str) -> list[LinkDraft]:
    """按官方 server.json 生成草稿：在线地址优先，其次本地包。"""
    full = str(server.get("name") or "server")
    title = str(server.get("title") or full.split("/")[-1])
    description = str(server.get("description") or "")[:300]
    homepage = str(server.get("websiteUrl") or (server.get("repository") or {}).get("url") or "")
    icons = server.get("icons") or []
    icon = str(icons[0].get("src", "")) if icons and isinstance(icons[0], dict) else ""

    out: list[LinkDraft] = []
    remotes = sorted(server.get("remotes") or [], key=lambda r: 0 if r.get("type") == "streamable-http" else 1)
    for remote in remotes:
        kind = remote.get("type")
        if kind not in ("streamable-http", "sse") or not remote.get("url"):
            continue
        draft = Draft(id=slugify(title), name=title, transport="http" if kind == "streamable-http" else "sse")
        draft.url = _template(str(remote["url"]), draft, remote.get("variables") or {})
        for h in remote.get("headers") or []:
            hname = str(h.get("name") or "")
            if not hname:
                continue
            value = str(h.get("value") or "")
            if value and "{" not in value and not h.get("isSecret"):
                draft.headers[hname] = value
                continue
            key = re.sub(r"[^A-Za-z0-9_]", "_", hname).upper().strip("_") or "TOKEN"
            draft.headers[hname] = re.sub(r"\{[^}]+\}", "${" + key + "}", value) if "{" in value else "${" + key + "}"
            _add_field(draft, key, h)
        out.append(LinkDraft(draft, description=description, homepage=homepage, icon=icon, source=source))

    for pkg in server.get("packages") or []:
        transport = (pkg.get("transport") or {}).get("type", "stdio")
        if transport != "stdio":
            continue
        kind, ident, version = pkg.get("registryType"), str(pkg.get("identifier") or ""), str(pkg.get("version") or "")
        if not ident:
            continue
        if kind == "npm":
            command, args = pkg.get("runtimeHint") or "npx", ["-y", f"{ident}@{version}" if version else ident]
        elif kind == "pypi":
            command, args = pkg.get("runtimeHint") or "uvx", [f"{ident}=={version}" if version else ident]
        elif kind == "oci":
            command, args = "docker", ["run", "-i", "--rm", ident]
        else:
            continue
        draft = Draft(id=slugify(title) + "-local" if out else slugify(title), name=title if not out else f"{title}（本地）",
                      transport="stdio", command=command, args=args)
        for arg in pkg.get("packageArguments") or []:
            value = arg.get("value") or arg.get("default")
            if arg.get("type") == "named" and arg.get("name"):
                draft.args.append(str(arg["name"]))
            if value:
                draft.args.append(str(value))
        for env in pkg.get("environmentVariables") or []:
            ename = str(env.get("name") or "")
            if not ename:
                continue
            default = env.get("default")
            draft.env[ename] = "${" + ename + (f":-{default}" if default and not env.get("isRequired") else "") + "}"
            _add_field(draft, ename, env)
        out.append(LinkDraft(draft, description=description, homepage=homepage, icon=icon, source=source))

    if not out:
        raise McpConfigError(f"{full} 在注册表里没有可用的连接方式（在线地址或 npm / PyPI 包）")
    for item in out:
        if item.draft.fields:
            item.draft.auth = "fields"
    _unique_ids(out)
    return out


def _template(url: str, draft: Draft, variables: dict) -> str:
    """注册表的 {var} 地址模板换成我们的 ${VAR} 填写项。"""

    def repl(m: re.Match) -> str:
        var = m.group(1)
        key = re.sub(r"[^A-Za-z0-9_]", "_", var).upper()
        spec = variables.get(var) or {}
        _add_field(draft, key, {**spec, "isSecret": spec.get("isSecret", False)})
        default = spec.get("default")
        return "${" + key + (f":-{default}" if default else "") + "}"

    return re.sub(r"\{([A-Za-z0-9_.-]+)\}", repl, url)


def _add_field(draft: Draft, key: str, spec: dict) -> None:
    if any(f["key"] == key for f in draft.fields):
        return
    draft.fields.append({
        "key": key,
        "label": key,
        "secret": bool(spec.get("isSecret", True)),
        "required": bool(spec.get("isRequired", True)) and not spec.get("default"),
        "placeholder": str(spec.get("default") or ""),
        "help": str(spec.get("description") or "")[:300],
    })


# ---------- 自动判断要不要浏览器登录 ----------
async def _detect_oauth(http: httpx.AsyncClient, item: LinkDraft) -> None:
    """没写任何凭证的在线地址，试着握一次手：回 401 说明要登录，标成浏览器授权登录。"""
    d = item.draft
    if d.transport != "http" or d.fields or d.headers or "${" in d.url:
        return
    try:
        resp = await http.post(
            d.url,
            json={"jsonrpc": "2.0", "id": 1, "method": "initialize",
                  "params": {"protocolVersion": PROTOCOL_VERSION, "capabilities": {}, "clientInfo": {"name": "FlyknitBuddy-Admin", "version": "1.0"}}},
            headers={"Accept": "application/json, text/event-stream"},
            timeout=8.0,
            follow_redirects=True,
        )
    except httpx.HTTPError:
        item.notes.append("从服务器上没连到这个地址，保存后请点「测试」或在员工端试连")
        return
    if resp.status_code == 401:
        d.auth = "oauth"
        item.notes.append("对方要求登录（HTTP 401），已设为「浏览器授权登录」：员工点「连接」时登录自己的账号")
    elif resp.status_code in (404, 405):
        d.transport = "sse" if d.url.rstrip("/").endswith("/sse") else d.transport
        item.notes.append(f"对方对 POST 回了 HTTP {resp.status_code}，可能是旧版 SSE 服务；员工端会自动改用 SSE 再试")


def registry_url(name: str) -> str:
    return f"{REGISTRY}/v0/servers?search={quote(name)}"
