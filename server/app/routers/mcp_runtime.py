"""
MCP 运行环境与 npm / PyPI 缓存代理（说明见 services/mcp_runtime.py）。

- 员工端：GET /client/runtimes 拿清单，GET /client/runtimes/{name}/download 下载便携版运行环境；
- 包代理：/mirror/npm/... 和 /mirror/pypi/...，npm、uv 直接把它当源用。只转发公共包，不需要登录；
  只会去配置好的上游，不会被拿来访问任意地址；
- 后台：配置、查看缓存、预先下载、上传离线包。
"""

import json
import logging
import os
import re
import time
from pathlib import Path
from urllib.parse import quote, urljoin, urlparse

import anyio
import httpx
from fastapi import APIRouter, Depends, File, HTTPException, Request, UploadFile, status
from fastapi.responses import FileResponse, Response
from pydantic import BaseModel, Field
from sqlalchemy.ext.asyncio import AsyncSession

from ..crypto import new_token
from ..db import get_session
from ..deps import require_admin, require_device
from ..models import Device
from ..services import mcp_runtime as rt_store
from ..services import settings_store

log = logging.getLogger("flyknit.mcp_runtime")
router = APIRouter(prefix="/api/v1", tags=["mcp-runtime"])

#: 包的元数据（版本列表）缓存多久；过期了上游连不上时仍用旧的
META_TTL = 300
MAX_PACKAGE_BYTES = 200 * 1024 * 1024


def _base(request: Request) -> str:
    return str(request.base_url).rstrip("/")


# ---------- 员工端 ----------

@router.get("/client/runtimes")
async def client_runtimes(request: Request, _: Device = Depends(require_device), session: AsyncSession = Depends(get_session)):
    """连接器需要 npx / uvx 时，员工端按这份清单从这里下载便携版运行环境，并把 npm / PyPI 指向服务端的缓存代理。"""
    conf = await rt_store.get_conf(session)
    if not conf.get("enabled"):
        return {"enabled": False, "npm_registry": "", "pypi_index": "", "runtimes": []}
    base = _base(request)
    runtimes = []
    for rt in rt_store.catalog(conf):
        path = rt_store.cached(rt)
        runtimes.append({
            "name": rt.name, "version": rt.version, "archive": rt.archive, "root": rt.root, "bin": rt.bin,
            "sha256": rt_store.sha_of(path) if path else "", "size": path.stat().st_size if path else 0,
        })
    return {
        "enabled": True,
        "npm_registry": f"{base}/api/v1/mirror/npm/",
        "pypi_index": f"{base}/api/v1/mirror/pypi/simple/",
        "runtimes": runtimes,
    }


@router.get("/client/runtimes/{name}/download")
async def download_runtime(name: str, request: Request, _: Device = Depends(require_device),
                           session: AsyncSession = Depends(get_session)):
    conf = await rt_store.get_conf(session)
    if not conf.get("enabled"):
        raise HTTPException(status.HTTP_404_NOT_FOUND, "IT 没有开启 MCP 运行环境")
    rt = next((r for r in rt_store.catalog(conf) if r.name == name), None)
    if rt is None:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "没有这个运行环境")
    try:
        path = await rt_store.ensure(request.app.state.http, rt)
    except rt_store.RuntimeError_ as exc:
        raise HTTPException(status.HTTP_502_BAD_GATEWAY, str(exc)) from exc
    return FileResponse(path, media_type="application/octet-stream", filename=rt.filename,
                        headers={"X-Runtime-Sha256": rt_store.sha_of(path)})


# ---------- npm 缓存代理 ----------

async def _enabled_conf(session: AsyncSession) -> dict:
    conf = await rt_store.get_conf(session)
    if not conf.get("enabled"):
        raise HTTPException(status.HTTP_404_NOT_FOUND, "IT 没有开启 MCP 运行环境")
    return conf


async def _fetch_to(http: httpx.AsyncClient, url: str, target: Path) -> None:
    """下载到缓存（先写临时文件再改名）。"""
    tmp = target.with_name(f".{new_token()[:10]}-{target.name}")
    target.parent.mkdir(parents=True, exist_ok=True)
    try:
        async with http.stream("GET", url, timeout=httpx.Timeout(30, read=300), follow_redirects=True) as resp:
            if resp.status_code == 404:
                raise HTTPException(status.HTTP_404_NOT_FOUND, "包不存在")
            if resp.status_code >= 400:
                raise HTTPException(status.HTTP_502_BAD_GATEWAY, f"上游返回 HTTP {resp.status_code}")
            total = 0
            with tmp.open("wb") as f:
                async for chunk in resp.aiter_bytes(1024 * 1024):
                    total += len(chunk)
                    if total > MAX_PACKAGE_BYTES:
                        raise HTTPException(status.HTTP_502_BAD_GATEWAY, "包太大了")
                    f.write(chunk)
        os.replace(tmp, target)
    except httpx.HTTPError as exc:
        raise HTTPException(status.HTTP_502_BAD_GATEWAY, f"服务器连不上包的源：{exc}") from exc
    finally:
        tmp.unlink(missing_ok=True)


async def _cached_meta(http: httpx.AsyncClient, url: str, cache: Path, accept: str) -> tuple[bytes, str]:
    """元数据（版本列表）：新鲜的直接用；过期了去上游拿，拿不到就用旧的（上游临时连不上也能装已缓存过的包）。"""
    fresh = cache.exists() and time.time() - cache.stat().st_mtime < META_TTL
    if not fresh:
        try:
            resp = await http.get(url, headers={"Accept": accept}, timeout=30, follow_redirects=True)
            if resp.status_code == 404:
                raise HTTPException(status.HTTP_404_NOT_FOUND, "包不存在")
            if resp.status_code < 400:
                cache.parent.mkdir(parents=True, exist_ok=True)
                tmp = cache.with_name(f".{new_token()[:10]}-{cache.name}")
                tmp.write_bytes(resp.content)
                os.replace(tmp, cache)
                cache.with_name(cache.name + ".type").write_text(resp.headers.get("content-type", ""))
            elif not cache.exists():
                raise HTTPException(status.HTTP_502_BAD_GATEWAY, f"上游返回 HTTP {resp.status_code}")
        except httpx.HTTPError as exc:
            if not cache.exists():
                raise HTTPException(status.HTTP_502_BAD_GATEWAY, f"服务器连不上包的源：{exc}") from exc
            log.warning("上游连不上，用缓存的 %s：%s", url, exc)
    ctype_file = cache.with_name(cache.name + ".type")
    return cache.read_bytes(), ctype_file.read_text() if ctype_file.exists() else ""


@router.get("/mirror/npm/{path:path}")
async def npm_mirror(path: str, request: Request, session: AsyncSession = Depends(get_session)):
    conf = await _enabled_conf(session)
    upstream = rt_store.npm_upstream(conf)
    path = path.replace("%2f", "/").replace("%2F", "/").strip("/")
    if not path or ".." in path.split("/"):
        raise HTTPException(status.HTTP_404_NOT_FOUND, "包不存在")
    http = request.app.state.http
    try:
        if path.endswith(".tgz"):
            # 包本身：同一个版本的文件不会变，下载过就一直用
            target = rt_store.safe_cache_path("npm", "tarballs", path)
            if not target.exists():
                await _fetch_to(http, f"{upstream}/{quote(path, safe='@/')}", target)
            return FileResponse(target, media_type="application/octet-stream")
        accept = request.headers.get("accept", "application/json")
        variant = "corgi" if "vnd.npm.install-v1" in accept else "full"
        cache = rt_store.safe_cache_path("npm", "meta", f"{path}.{variant}.json")
    except ValueError as exc:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "包不存在") from exc
    body, ctype = await _cached_meta(http, f"{upstream}/{quote(path, safe='@/')}", cache, accept)
    # 包文件的下载地址改成经过这里
    mine = f"{_base(request)}/api/v1/mirror/npm"
    text = body.decode("utf-8", "replace").replace(f'"{upstream}/', f'"{mine}/')
    return Response(text, media_type=ctype.split(";")[0] or "application/json")


# ---------- PyPI 缓存代理（Simple API，PEP 503 / 691） ----------

_HREF = re.compile(r'href="([^"]+)"')


def _file_hosts(conf: dict) -> set[str]:
    return {"files.pythonhosted.org", urlparse(rt_store.pypi_upstream(conf)).netloc}


def _to_proxy(url: str, page_url: str, mine: str, hosts: set[str]) -> str:
    absolute = urljoin(page_url, url)
    u = urlparse(absolute)
    if u.netloc not in hosts:
        return url
    rewritten = f"{mine}/files/{u.netloc}{u.path}"
    return rewritten + (f"#{u.fragment}" if u.fragment else "")


@router.get("/mirror/pypi/simple/{project}")
@router.get("/mirror/pypi/simple/{project}/")
async def pypi_project(project: str, request: Request, session: AsyncSession = Depends(get_session)):
    conf = await _enabled_conf(session)
    name = re.sub(r"[-_.]+", "-", project).lower()
    if not re.fullmatch(r"[a-z0-9][a-z0-9-]{0,200}", name):
        raise HTTPException(status.HTTP_404_NOT_FOUND, "包不存在")
    upstream = rt_store.pypi_upstream(conf).rstrip("/")
    page_url = f"{upstream}/{name}/"
    wants_json = "application/vnd.pypi.simple.v1+json" in request.headers.get("accept", "")
    accept = "application/vnd.pypi.simple.v1+json" if wants_json else "text/html"
    cache = rt_store.safe_cache_path("pypi", "meta", f"{name}.{'json' if wants_json else 'html'}")
    body, ctype = await _cached_meta(request.app.state.http, page_url, cache, accept)
    mine = f"{_base(request)}/api/v1/mirror/pypi"
    hosts = _file_hosts(conf)
    text = body.decode("utf-8", "replace")
    if "json" in ctype:
        data = json.loads(text)
        for f in data.get("files", []):
            if isinstance(f.get("url"), str):
                f["url"] = _to_proxy(f["url"], page_url, mine, hosts)
        return Response(json.dumps(data), media_type="application/vnd.pypi.simple.v1+json")
    text = _HREF.sub(lambda m: f'href="{_to_proxy(m.group(1), page_url, mine, hosts)}"', text)
    return Response(text, media_type="text/html")


@router.get("/mirror/pypi/files/{host}/{path:path}")
async def pypi_file(host: str, path: str, request: Request, session: AsyncSession = Depends(get_session)):
    conf = await _enabled_conf(session)
    if host not in _file_hosts(conf) or ".." in path.split("/"):
        raise HTTPException(status.HTTP_404_NOT_FOUND, "文件不存在")
    try:
        target = rt_store.safe_cache_path("pypi", "files", host, path)
    except ValueError as exc:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "文件不存在") from exc
    if not target.exists():
        await _fetch_to(request.app.state.http, f"https://{host}/{path}", target)
    return FileResponse(target, media_type="application/octet-stream")


# ---------- 管理后台 ----------

class RuntimeConfIn(BaseModel):
    enabled: bool = True
    npm: str = Field(default="official", pattern="^(official|npmmirror)$")
    pypi: str = Field(default="official", pattern="^(official|tuna)$")
    node_dist: str = Field(default="official", pattern="^(official|npmmirror)$")
    overrides: dict[str, dict[str, str]] = {}


def _dir_size(path: Path) -> int:
    return sum(f.stat().st_size for f in path.rglob("*") if f.is_file()) if path.exists() else 0


async def _status(session: AsyncSession) -> dict:
    conf = await rt_store.get_conf(session)
    items = []
    for rt in rt_store.catalog(conf):
        path = rt_store.cached(rt)
        items.append({"name": rt.name, "title": rt.title, "version": rt.version, "url": rt.url,
                      "cached": path is not None, "size": path.stat().st_size if path else 0})
    mirror = Path(rt_store.mirror_dir("npm")).parent
    return {**conf, "runtimes": items, "mirror_size": await anyio.to_thread.run_sync(_dir_size, mirror)}


@router.get("/admin/mcp-runtime", dependencies=[Depends(require_admin)])
async def get_runtime_conf(session: AsyncSession = Depends(get_session)):
    return await _status(session)


@router.put("/admin/mcp-runtime", dependencies=[Depends(require_admin)])
async def put_runtime_conf(data: RuntimeConfIn, session: AsyncSession = Depends(get_session)):
    overrides = {
        k: {kk: str(vv).strip()[:500] for kk, vv in v.items() if kk in ("version", "url") and str(vv).strip()}
        for k, v in data.overrides.items() if k in ("node", "uv", "python")
    }
    for name, o in overrides.items():
        if o.get("url") and not re.match(r"^https?://", o["url"]):
            raise HTTPException(status.HTTP_400_BAD_REQUEST, f"{name} 的下载地址要以 http:// 或 https:// 开头")
    await settings_store.set_value(session, rt_store.KEY, {**data.model_dump(), "overrides": overrides})
    return await _status(session)


@router.post("/admin/mcp-runtime/{name}/prefetch", dependencies=[Depends(require_admin)])
async def prefetch_runtime(name: str, request: Request, session: AsyncSession = Depends(get_session)):
    """在服务器上先下载好：员工第一次用时不用等。"""
    conf = await rt_store.get_conf(session)
    rt = next((r for r in rt_store.catalog(conf) if r.name == name), None)
    if rt is None:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "没有这个运行环境")
    try:
        await rt_store.ensure(request.app.state.http, rt)
    except rt_store.RuntimeError_ as exc:
        raise HTTPException(status.HTTP_502_BAD_GATEWAY, str(exc)) from exc
    return await _status(session)


@router.post("/admin/mcp-runtime/{name}/upload", dependencies=[Depends(require_admin)])
async def upload_runtime(name: str, file: UploadFile = File(...), session: AsyncSession = Depends(get_session)):
    """服务器也上不了外网时：在能上网的电脑上下载好，传上来。"""
    conf = await rt_store.get_conf(session)
    rt = next((r for r in rt_store.catalog(conf) if r.name == name), None)
    if rt is None:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "没有这个运行环境")
    expected = ".zip" if rt.archive == "zip" else ".tar.gz"
    if not (file.filename or "").lower().endswith(expected):
        raise HTTPException(status.HTTP_400_BAD_REQUEST, f"{rt.title} 的离线包应该是 {expected} 文件（{rt.url.rsplit('/', 1)[-1]}）")
    tmp = rt_store.runtime_dir() / f".{new_token()[:10]}-{rt.filename}"
    total = 0
    try:
        with tmp.open("wb") as f:
            while chunk := await file.read(1024 * 1024):
                total += len(chunk)
                if total > rt_store.MAX_RUNTIME_BYTES:
                    raise HTTPException(status.HTTP_400_BAD_REQUEST, "文件太大了")
                f.write(chunk)
        await anyio.to_thread.run_sync(rt_store.store, rt, tmp)
    finally:
        tmp.unlink(missing_ok=True)
    log.info("管理员上传了 MCP 运行环境 %s %s（%s 字节）", rt.name, rt.version, total)
    return await _status(session)
