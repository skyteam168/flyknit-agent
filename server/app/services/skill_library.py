"""公司技能库：保存技能包（zip），支持从 GitHub / 社区下载链接导入或管理员上传。

客户端一般上不了外网，所以由服务端统一下载、校验后再分发给客户端。
技能包格式与社区开放标准一致：目录内必须有 SKILL.md，frontmatter 至少包含 name 和 description。
"""

from __future__ import annotations

import io
import re
import zipfile
from dataclasses import dataclass
from pathlib import Path
from urllib.parse import urlparse

import httpx

MAX_TOTAL_BYTES = 64 * 1024 * 1024
MAX_SINGLE_BYTES = 32 * 1024 * 1024
MAX_FILES = 2000
VALID_NAME = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{1,63}$")
# 可执行程序不允许进入技能库
BLOCKED_SUFFIXES = {".exe", ".msi", ".com", ".scr", ".dll", ".lnk", ".pif", ".cpl", ".sys"}


class SkillError(Exception):
    """技能包不合法。"""


@dataclass
class ParsedSkill:
    name: str
    description: str
    version: str
    author: str
    files: list[str]
    data: bytes  # 只含这一个技能的 zip


def storage_dir(root: Path) -> Path:
    d = root / "skills"
    d.mkdir(parents=True, exist_ok=True)
    return d


def split_frontmatter(text: str) -> tuple[dict[str, str], str]:
    """解析 SKILL.md 的 YAML frontmatter（与客户端 SkillCatalog.SplitFrontmatter 行为一致）。"""
    meta: dict[str, str] = {}
    normalized = text.replace("\r\n", "\n").lstrip("﻿")
    if not normalized.startswith("---\n"):
        return meta, normalized
    end = normalized.find("\n---", 4)
    if end < 0:
        return meta, normalized
    header = normalized[4:end]
    body_start = normalized.find("\n", end + 4)
    body = "" if body_start < 0 else normalized[body_start + 1 :]

    current: str | None = None
    block: list[str] = []
    for line in header.split("\n"):
        if current is not None and (line.startswith((" ", "\t")) or not line.strip()):
            item = line.strip()
            block.append(item[2:].strip().strip("\"'") if item.startswith("- ") else item)
            continue
        if current is not None:
            meta[current.lower()] = " ".join(x for x in block if x).strip()
            current, block = None, []
        if ":" not in line or line.startswith(" "):
            continue
        key, _, value = line.partition(":")
        key, value = key.strip().lower(), value.strip()
        if value in (">", "|", ">-", "|-", ""):
            current = key
            continue
        meta[key] = value.strip("\"'")
    if current is not None:
        meta[current.lower()] = " ".join(x for x in block if x).strip()
    return meta, body


def _safe_members(zf: zipfile.ZipFile) -> list[zipfile.ZipInfo]:
    members, total = [], 0
    for info in zf.infolist():
        if info.is_dir():
            continue
        name = info.filename.replace("\\", "/")
        if name.startswith("/") or ".." in name.split("/") or ":" in name:
            raise SkillError(f"压缩包里的路径不安全：{info.filename}")
        if Path(name).suffix.lower() in BLOCKED_SUFFIXES:
            raise SkillError(f"技能包里含有可执行程序（{name}），不允许导入")
        total += info.file_size
        if info.file_size > MAX_SINGLE_BYTES or total > MAX_TOTAL_BYTES:
            raise SkillError("技能包解压后体积过大")
        members.append(info)
        if len(members) > MAX_FILES:
            raise SkillError(f"技能包里的文件超过 {MAX_FILES} 个")
    if not members:
        raise SkillError("压缩包是空的")
    return members


def parse_packages(data: bytes) -> list[ParsedSkill]:
    """从一个 zip 里解析出所有技能（社区仓库常常一个仓库放多个技能）。"""
    try:
        zf = zipfile.ZipFile(io.BytesIO(data))
    except zipfile.BadZipFile as exc:
        raise SkillError("不是合法的 zip 文件") from exc
    with zf:
        members = _safe_members(zf)
        roots = sorted(
            {m.filename.replace("\\", "/").rsplit("/", 1)[0] if "/" in m.filename.replace("\\", "/") else ""
             for m in members if m.filename.replace("\\", "/").rsplit("/", 1)[-1].lower() == "skill.md"}
        )
        if not roots:
            raise SkillError("压缩包里没有找到 SKILL.md，这不是一个技能包")

        skills: list[ParsedSkill] = []
        for root in roots:
            prefix = f"{root}/" if root else ""
            own = [
                m for m in members
                if m.filename.replace("\\", "/").startswith(prefix)
                # 排除嵌套的其他技能
                and not any(m.filename.replace("\\", "/").startswith(f"{r}/") for r in roots if r != root and r.startswith(prefix))
            ]
            skill_md = next(m for m in own if m.filename.replace("\\", "/").lower() == f"{prefix.lower()}skill.md")
            meta, body = split_frontmatter(zf.read(skill_md).decode("utf-8", "replace"))
            name = (meta.get("name") or (root.rsplit("/", 1)[-1] if root else "")).strip().lower()
            name = re.sub(r"[^a-z0-9._-]+", "-", name).strip("-.")
            if not VALID_NAME.match(name):
                raise SkillError(f"技能名不合法：{name or '(空)'}")
            description = (meta.get("description") or "").strip()
            if not description:
                raise SkillError(f"{name} 的 SKILL.md 缺少 description")

            buffer = io.BytesIO()
            with zipfile.ZipFile(buffer, "w", zipfile.ZIP_DEFLATED) as out:
                for m in own:
                    rel = m.filename.replace("\\", "/")[len(prefix):]
                    if rel:
                        out.writestr(f"{name}/{rel}", zf.read(m))
            skills.append(
                ParsedSkill(
                    name=name,
                    description=description,
                    version=(meta.get("version") or "").strip(),
                    author=(meta.get("author") or "").strip(),
                    files=[m.filename.replace("\\", "/")[len(prefix):] for m in own],
                    data=buffer.getvalue(),
                )
            )
        return skills


def github_zip_url(url: str) -> str:
    """GitHub 页面链接转成 zip 下载链接；其他链接原样返回。"""
    u = urlparse(url)
    if u.netloc.lower() not in ("github.com", "www.github.com"):
        return url
    parts = [p for p in u.path.split("/") if p]
    if len(parts) < 2:
        return url
    owner, repo = parts[0], parts[1].removesuffix(".git")
    branch = parts[3] if len(parts) >= 4 and parts[2] in ("tree", "blob") else "HEAD"
    return f"https://codeload.github.com/{owner}/{repo}/zip/{branch}"


def github_subdir(url: str) -> str:
    """GitHub 链接里指向子目录时返回该子目录（用于只导入仓库中的一个技能）。"""
    u = urlparse(url)
    if u.netloc.lower() not in ("github.com", "www.github.com"):
        return ""
    parts = [p for p in u.path.split("/") if p]
    return "/".join(parts[4:]) if len(parts) > 4 and parts[2] in ("tree", "blob") else ""


async def download(client: httpx.AsyncClient, url: str) -> bytes:
    """下载技能包。只接受 http(s)，大小受限。"""
    target = github_zip_url(url)
    if urlparse(target).scheme not in ("http", "https"):
        raise SkillError("只支持 http(s) 链接")
    try:
        resp = await client.get(target, timeout=120, follow_redirects=True)
    except httpx.HTTPError as exc:
        raise SkillError(f"下载失败：{exc}") from exc
    if resp.status_code >= 400:
        raise SkillError(f"下载失败：HTTP {resp.status_code}")
    if len(resp.content) > MAX_TOTAL_BYTES:
        raise SkillError("下载的文件过大")
    return resp.content


def filter_subdir(skills: list[ParsedSkill], subdir: str) -> list[ParsedSkill]:
    """链接指向仓库子目录时，只保留名字与子目录末段相同的技能。"""
    if not subdir:
        return skills
    wanted = re.sub(r"[^a-z0-9._-]+", "-", subdir.rstrip("/").rsplit("/", 1)[-1].lower()).strip("-.")
    matched = [s for s in skills if s.name == wanted]
    return matched or skills
