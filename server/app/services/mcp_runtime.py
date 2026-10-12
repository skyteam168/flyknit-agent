"""
MCP 运行环境：员工电脑一般没装 Node.js、uv、Python，也上不了外网，npx / uvx 类的连接器起不来。

服务端替员工电脑做两件事（服务端能上网，员工电脑只要能连服务端）：
1. 提供便携版运行环境（Node.js、uv、Python）：第一次有电脑要时从官方地址下载、缓存，之后直接给缓存。
   服务端也上不了外网时，管理员可以在后台上传离线包。
2. 当 npm 和 PyPI 的缓存代理：连接器第一次启动要下载的包经服务端转一道并缓存，员工电脑不用直连外网。

这里只管配置、下载和缓存；接口在 routers/mcp_runtime.py。
"""

from __future__ import annotations

import asyncio
import hashlib
import logging
import os
from dataclasses import dataclass, field
from pathlib import Path

import httpx
from sqlalchemy.ext.asyncio import AsyncSession

from ..config import get_settings
from . import settings_store

log = logging.getLogger("flyknit.mcp_runtime")

KEY = "mcp_runtime"

#: 公共源和国内镜像。工厂在越南，官方源一般最快；连不上时管理员在后台切到镜像
NPM_UPSTREAMS = {"official": "https://registry.npmjs.org", "npmmirror": "https://registry.npmmirror.com"}
PYPI_UPSTREAMS = {"official": "https://pypi.org/simple", "tuna": "https://pypi.tuna.tsinghua.edu.cn/simple"}
NODE_DIST = {"official": "https://nodejs.org/dist", "npmmirror": "https://npmmirror.com/mirrors/node"}

DEFAULT = {
    "enabled": True,
    "npm": "official",
    "pypi": "official",
    "node_dist": "official",
    #: 每个运行环境可以改版本或直接给下载地址（公司内网文件服务器等）
    "overrides": {},
}

MAX_RUNTIME_BYTES = 300 * 1024 * 1024


@dataclass
class Runtime:
    name: str
    title: str
    version: str
    archive: str  # zip / tar.gz
    root: str  # 压缩包里要去掉的顶层文件夹
    bin: list[str] = field(default_factory=lambda: [""])
    url: str = ""

    @property
    def filename(self) -> str:
        return f"{self.name}-{self.version}.{self.archive}"


def catalog(conf: dict) -> list[Runtime]:
    """当前配置下要提供的运行环境（版本、下载地址）。"""
    ov = conf.get("overrides") or {}

    def pick(name: str, default_version: str) -> tuple[str, str]:
        o = ov.get(name) or {}
        return (str(o.get("version") or default_version).strip(), str(o.get("url") or "").strip())

    node_v, node_url = pick("node", "22.12.0")
    uv_v, uv_url = pick("uv", "0.5.11")
    py_v, py_url = pick("python", "3.12.8+20241206")
    dist = NODE_DIST.get(conf.get("node_dist", "official"), NODE_DIST["official"])
    py_ver, _, py_build = py_v.partition("+")
    return [
        Runtime(
            "node", "Node.js（npx）", node_v, "zip", f"node-v{node_v}-win-x64", [""],
            node_url or f"{dist}/v{node_v}/node-v{node_v}-win-x64.zip",
        ),
        Runtime(
            "uv", "uv（uvx）", uv_v, "zip", "", [""],
            uv_url or f"https://github.com/astral-sh/uv/releases/download/{uv_v}/uv-x86_64-pc-windows-msvc.zip",
        ),
        Runtime(
            "python", "Python", py_ver, "tar.gz", "python", ["", "Scripts"],
            py_url or (
                f"https://github.com/astral-sh/python-build-standalone/releases/download/{py_build}/"
                f"cpython-{py_ver}%2B{py_build}-x86_64-pc-windows-msvc-install_only.tar.gz"
            ),
        ),
    ]


async def get_conf(session: AsyncSession) -> dict:
    conf = await settings_store.get_value(session, KEY, DEFAULT)
    return {**DEFAULT, **conf}


def npm_upstream(conf: dict) -> str:
    return NPM_UPSTREAMS.get(conf.get("npm", "official"), NPM_UPSTREAMS["official"])


def pypi_upstream(conf: dict) -> str:
    return PYPI_UPSTREAMS.get(conf.get("pypi", "official"), PYPI_UPSTREAMS["official"])


# ---------- 运行环境缓存 ----------

def runtime_dir() -> Path:
    d = Path(get_settings().data_dir) / "mcp_runtimes"
    d.mkdir(parents=True, exist_ok=True)
    return d


def cached(rt: Runtime) -> Path | None:
    path = runtime_dir() / rt.filename
    return path if path.exists() and path.with_suffix(path.suffix + ".sha256").exists() else None


def sha_of(path: Path) -> str:
    sidecar = path.with_suffix(path.suffix + ".sha256")
    return sidecar.read_text().strip() if sidecar.exists() else ""


def _sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def store(rt: Runtime, tmp: Path) -> Path:
    """把下载或上传好的临时文件放进缓存（先算好 sha 再改名，别人拿不到半截的）。"""
    target = runtime_dir() / rt.filename
    digest = _sha256_file(tmp)
    os.replace(tmp, target)
    target.with_suffix(target.suffix + ".sha256").write_text(digest)
    # 同一个运行环境的旧版本删掉
    for old in runtime_dir().glob(f"{rt.name}-*"):
        if not old.name.startswith(rt.filename):
            old.unlink(missing_ok=True)
    return target


_locks: dict[str, asyncio.Lock] = {}


class RuntimeError_(Exception):
    """准备运行环境失败（下载不了等），detail 是给人看的中文。"""


async def ensure(http: httpx.AsyncClient, rt: Runtime) -> Path:
    """有缓存直接用；没有就从上游下载一份。同一个运行环境同时只下载一次。"""
    if path := cached(rt):
        return path
    lock = _locks.setdefault(rt.name, asyncio.Lock())
    async with lock:
        if path := cached(rt):
            return path
        tmp = runtime_dir() / f".{rt.filename}.part"
        log.info("下载 MCP 运行环境 %s %s：%s", rt.name, rt.version, rt.url)
        try:
            async with http.stream("GET", rt.url, timeout=httpx.Timeout(30, read=300), follow_redirects=True) as resp:
                if resp.status_code >= 400:
                    raise RuntimeError_(f"下载 {rt.title} 失败：HTTP {resp.status_code}（{rt.url}）")
                total = 0
                with tmp.open("wb") as f:
                    async for chunk in resp.aiter_bytes(1024 * 1024):
                        total += len(chunk)
                        if total > MAX_RUNTIME_BYTES:
                            raise RuntimeError_(f"{rt.title} 的安装包太大了，请检查下载地址")
                        f.write(chunk)
        except httpx.HTTPError as exc:
            tmp.unlink(missing_ok=True)
            raise RuntimeError_(
                f"服务器下载 {rt.title} 失败：{exc}。服务器上不了外网时，请在管理后台「MCP 连接器 → 运行环境」上传离线包"
            ) from exc
        except BaseException:
            tmp.unlink(missing_ok=True)
            raise
        return await asyncio.to_thread(store, rt, tmp)


# ---------- npm / PyPI 缓存代理 ----------

def mirror_dir(kind: str) -> Path:
    d = Path(get_settings().data_dir) / "mirror" / kind
    d.mkdir(parents=True, exist_ok=True)
    return d


def safe_cache_path(kind: str, *parts: str) -> Path:
    """缓存文件路径：只允许正常的包名、文件名字符，防止 ../ 跳出缓存目录。"""
    base = mirror_dir(kind).resolve()
    rel = "/".join(p.strip("/") for p in parts if p)
    path = (base / rel).resolve()
    if base not in path.parents:
        raise ValueError("路径不合法")
    return path
