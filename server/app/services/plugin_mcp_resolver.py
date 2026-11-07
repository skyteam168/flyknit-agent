"""PluginMcpResolver：把 Claude Code 插件里的 MCP 配置统一解析成 PluginMcpDefinition。

插件 MCP 只是连接器（mcp_vendors）的一个新来源：解析出来的定义经 IT 在后台导入时审核、按部门下发，
员工端照常走 McpService 连接，确认、规则、审计一样不少。员工端不读本机 ~/.claude/plugins。

配置从哪里来（按这个顺序收集，全部合并）：
1. plugin.json 的 mcpServers：对象 = 内联配置；字符串 = 包内相对路径（./mcp.claude-code.json）
2. 包根目录的 .mcp.json、mcp.json
同一个服务名出现在多处：配置一样的只算一个（记一条说明），不一样的直接报错，不静默覆盖。

每份配置认三种写法：
- {"mcpServers": {"名字": {...}}}            （B，官方 figma / postman）
- {"名字": {"command": ..., "args": [...]}}   （A，官方 playwright）
- {"名字": {"type": "http", "url": ...}}      （D）

安全检查（不通过就拒收整个插件，管理员能看到原因）：
- 引用路径只能是包内文件：不能有 ..、绝对路径、盘符、UNC；
- 本机进程只允许 ALLOWED_COMMANDS 里的启动命令（插件里写的命令，不是 Windows 上实际包装的 cmd.exe）；
- 插件的 env 不能设 PROTECTED_ENV 里的变量；不能用 ${CLAUDE_PLUGIN_ROOT}（要在员工电脑上跑插件自带的程序）；
- @latest、没写版本号的包：不拒收，预览里提示固定版本。
命令白名单只挡明显的 shell 和下载工具：npx、python 本身就能运行任意代码，真正的把关是 IT 看过预览再导入。
"""

from __future__ import annotations

import json
import re
from dataclasses import asdict, dataclass, field
from pathlib import PurePosixPath

from . import mcp_catalog

#: 插件里的本机进程只能用这些命令启动。docker 能挂载本机磁盘，不在默认名单里，需要的话 IT 在「连接器」里单独配置
ALLOWED_COMMANDS = {"npx", "uvx", "node", "python", "python3", "py", "uv"}
#: 插件的 env 不能设的变量（会改变子进程找程序、加载代码、走代理的方式）
PROTECTED_ENV = {"PATH", "PATHEXT", "COMSPEC", "SYSTEMROOT", "WINDIR", "NODE_OPTIONS", "NODE_PATH",
                 "PYTHONPATH", "PYTHONSTARTUP", "PYTHONHOME", "LD_PRELOAD", "LD_LIBRARY_PATH", "DYLD_INSERT_LIBRARIES"}
PROTECTED_ENV_PREFIXES = ("NPM_CONFIG_", "DOTNET_", "COR_", "CORECLR_")
PROTECTED_ENV_SUFFIXES = ("_PROXY",)
CONFIG_FILES = (".mcp.json", "mcp.json")


class PluginMcpError(Exception):
    """插件里的 MCP 配置不合法或不安全。"""


@dataclass
class PluginMcpDefinition:
    """插件里声明的一个 MCP 服务，统一后的样子（还不是连接器，导入时再决定新建 / 复用 / 冲突）。"""

    name: str  # 连接器 id（服务名规范化后）
    transport_type: str  # http / sse / stdio
    command: str = ""
    args: list[str] = field(default_factory=list)
    url: str = ""
    env: dict[str, str] = field(default_factory=dict)
    headers: dict[str, str] = field(default_factory=dict)
    auth: str = "none"
    fields: list[dict] = field(default_factory=list)
    source_plugin: str = ""
    source_plugin_version: str = ""
    source_manifest: str = ""
    source_mcp_config: str = ""  # 例如 .mcp.json、mcp.claude-code.json、plugin.json#mcpServers
    source_server_name: str = ""  # 插件里写的原名
    warnings: list[str] = field(default_factory=list)

    def config(self) -> dict:
        """连接方式本身（判断「配置完全一致」用）：不含来源、说明和管理员预填的值。"""
        return {"transport": self.transport_type, "url": self.url.strip(), "command": self.command.strip(),
                "args": list(self.args), "env": dict(sorted(self.env.items())),
                "headers": dict(sorted(self.headers.items()))}

    def to_dict(self) -> dict:
        return asdict(self)


def safe_relative(path: str) -> str:
    """plugin.json 里引用的配置文件路径 → 包内相对路径。越界一律拒绝。"""
    raw = (path or "").strip()
    p = raw.replace("\\", "/")
    if not p:
        raise PluginMcpError("plugin.json 的 mcpServers 是空路径")
    if p.startswith("/") or p.startswith("//") or re.match(r"^[A-Za-z]:", p) or raw.startswith("\\\\"):
        raise PluginMcpError(f"plugin.json 的 mcpServers 只能指向插件包里的文件，不能用绝对路径：{raw}")
    parts = [x for x in PurePosixPath(p).parts if x not in ("", ".")]
    if not parts or any(x == ".." for x in parts) or any(":" in x for x in parts):
        raise PluginMcpError(f"plugin.json 的 mcpServers 只能指向插件包里的文件：{raw}")
    return "/".join(parts)


def _entries(data: object, where: str) -> list[tuple[str, dict]]:
    if not isinstance(data, dict):
        raise PluginMcpError(f"{where} 应该是一个 JSON 对象")
    try:
        return mcp_catalog.server_entries(data)
    except mcp_catalog.McpConfigError as exc:
        raise PluginMcpError(f"{where}：{exc}") from exc


def _is_protected(key: str) -> bool:
    k = key.upper()
    return k in PROTECTED_ENV or k.startswith(PROTECTED_ENV_PREFIXES) or k.endswith(PROTECTED_ENV_SUFFIXES)


def _command_name(command: str) -> str:
    base = command.replace("\\", "/").rsplit("/", 1)[-1].lower()
    for ext in (".cmd", ".exe", ".bat", ".ps1"):
        if base.endswith(ext):
            base = base[: -len(ext)]
    return base


def _unpinned(command: str, args: list[str]) -> str | None:
    """npx / uvx 运行的包有没有固定版本。返回没固定的那个包（第一个不是开关的参数）。"""
    pkg = next((a for a in args if not a.startswith("-")), "")
    if not pkg or ":" in pkg or pkg.startswith((".", "/", "\\")):
        return None  # 本地路径、git / URL：不在这里判断
    if pkg.endswith("@latest"):
        return pkg
    if command == "uvx":
        return None if ("==" in pkg or "@" in pkg) else pkg
    body = pkg[1:] if pkg.startswith("@") else pkg  # @scope/name@1.2.3
    return None if "@" in body else pkg


def _definition(name: str, entry: dict, where: str, plugin_id: str, version: str, manifest_rel: str) -> PluginMcpDefinition:
    try:
        draft = mcp_catalog.draft_from_entry(name, entry)
    except mcp_catalog.McpConfigError as exc:
        raise PluginMcpError(f"{where}：{exc}") from exc
    text = json.dumps([draft.command, draft.args, draft.env, draft.url, draft.headers], ensure_ascii=False)
    if "CLAUDE_PLUGIN_ROOT" in text:
        raise PluginMcpError(f"MCP 服务 {name} 要在员工电脑上运行插件自带的程序（${{CLAUDE_PLUGIN_ROOT}}），不支持。"
                             "请改成在线服务（http），或由 IT 在「连接器」里单独配置")
    d = PluginMcpDefinition(
        name=draft.id, transport_type=draft.transport, command=draft.command, args=draft.args, url=draft.url,
        env=draft.env, headers=draft.headers, auth=draft.auth, fields=draft.fields,
        source_plugin=plugin_id, source_plugin_version=version, source_manifest=manifest_rel,
        source_mcp_config=where, source_server_name=name,
    )
    if draft.preset:
        # 插件里写死的密钥不随包走：管理员装好后在「连接器」里填
        d.warnings.append(f"MCP 服务 {name} 里写死的密钥没有导入，装好后请在「连接器」页面填写")
    if d.transport_type == "stdio":
        cmd = _command_name(d.command)
        if cmd not in ALLOWED_COMMANDS:
            raise PluginMcpError(f"MCP 服务 {name} 的启动命令 {d.command!r} 不在允许范围内"
                                 f"（插件里只能用 {'、'.join(sorted(ALLOWED_COMMANDS))}）。需要的话由 IT 在「连接器」里单独配置")
        bad = sorted(k for k in d.env if _is_protected(k))
        if bad:
            raise PluginMcpError(f"MCP 服务 {name} 的 env 不能设置 {'、'.join(bad)}（会改变程序的加载方式或网络走向）")
        if cmd in ("npx", "uvx") and (pkg := _unpinned(cmd, d.args)):
            d.warnings.append(f"MCP 服务 {name} 用的包 {pkg} 没有固定版本（每次启动可能拉到新版本），建议改成固定版本号")
    try:
        mcp_catalog.validate({**asdict(draft), "fields": draft.fields})
    except mcp_catalog.McpConfigError as exc:
        raise PluginMcpError(f"MCP 服务 {name}：{exc}") from exc
    return d


def resolve(files: dict[str, object], read_text, manifest: dict, manifest_rel: str,
            plugin_id: str, plugin_version: str) -> tuple[list[PluginMcpDefinition], list[str]]:
    """
    files：包内相对路径 → 任意（只用来判断文件在不在）；read_text(rel) 读文件内容。
    返回（定义列表，说明）。不合法、不安全的直接抛 PluginMcpError。
    """
    sources: list[tuple[str, object]] = []
    inline = manifest.get("mcpServers")
    if isinstance(inline, dict):
        sources.append((f"{manifest_rel}#mcpServers", {"mcpServers": inline}))
    elif isinstance(inline, str):
        rel = safe_relative(inline)
        if rel not in files:
            raise PluginMcpError(f"plugin.json 的 mcpServers 指向 {inline}，但插件包里没有这个文件")
        sources.append((rel, rel))
    elif inline is not None:
        raise PluginMcpError("plugin.json 的 mcpServers 应该是对象，或者包内 JSON 文件的相对路径")
    for rel in CONFIG_FILES:
        if rel in files and all(s[0] != rel for s in sources):
            sources.append((rel, rel))

    defs: dict[str, PluginMcpDefinition] = {}
    notes: list[str] = []
    for where, src in sources:
        if isinstance(src, str):
            try:
                data = json.loads(read_text(src))
            except json.JSONDecodeError as exc:
                raise PluginMcpError(f"{where} 不是合法的 JSON（第 {exc.lineno} 行）：{exc.msg}") from exc
        else:
            data = src
        for name, entry in _entries(data, where):
            d = _definition(name, entry, where, plugin_id, plugin_version, manifest_rel)
            if d.name in defs:
                first = defs[d.name]
                if first.config() != d.config():
                    raise PluginMcpError(f"MCP 服务 {d.source_server_name} 在 {first.source_mcp_config} 和 {where} 里都有，"
                                         "而且配置不一样。请只保留一份")
                notes.append(f"MCP 服务 {d.source_server_name} 在 {first.source_mcp_config} 和 {where} 里重复声明，配置相同，按一个算")
                continue
            defs[d.name] = d
    return list(defs.values()), notes
