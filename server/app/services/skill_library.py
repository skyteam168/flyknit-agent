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


# ---------- 在后台编写技能 ----------
#: 能在后台直接编辑的文本文件；其余文件（图片、模板文档等）原样保留
TEXT_SUFFIXES = {
    ".md", ".txt", ".py", ".js", ".ts", ".json", ".yaml", ".yml", ".csv", ".ps1", ".psm1", ".sh",
    ".bat", ".cmd", ".html", ".htm", ".css", ".xml", ".sql", ".ini", ".toml", ".cfg", ".vbs",
}
MAX_TEXT_BYTES = 256 * 1024
MAX_AUTHORED_FILES = 50


def _is_text(path: str, raw: bytes) -> bool:
    if Path(path).suffix.lower() not in TEXT_SUFFIXES or len(raw) > MAX_TEXT_BYTES:
        return False
    try:
        raw.decode("utf-8")
    except UnicodeDecodeError:
        return False
    return True


def read_source(data: bytes) -> dict:
    """把技能包拆成可编辑的样子：SKILL.md、其他文本文件（带内容）、二进制文件（只列出来）。"""
    try:
        zf = zipfile.ZipFile(io.BytesIO(data))
    except zipfile.BadZipFile as exc:
        raise SkillError("技能包文件损坏，请重新导入") from exc
    skill_md = ""
    files: list[dict] = []
    with zf:
        for info in zf.infolist():
            if info.is_dir():
                continue
            name = info.filename.replace("\\", "/")
            rel = name.split("/", 1)[1] if "/" in name else name
            raw = zf.read(info)
            if rel.lower() == "skill.md":
                skill_md = raw.decode("utf-8", "replace").lstrip("﻿")
                continue
            if _is_text(rel, raw):
                files.append({"path": rel, "content": raw.decode("utf-8"), "binary": False, "size": len(raw)})
            else:
                files.append({"path": rel, "content": None, "binary": True, "size": len(raw)})
    files.sort(key=lambda f: f["path"].lower())
    return {"skill_md": skill_md, "files": files}


def binary_files(data: bytes) -> dict[str, bytes]:
    """旧技能包里不能在后台编辑的文件，保存时原样带上。"""
    out: dict[str, bytes] = {}
    with zipfile.ZipFile(io.BytesIO(data)) as zf:
        for info in zf.infolist():
            if info.is_dir():
                continue
            name = info.filename.replace("\\", "/")
            rel = name.split("/", 1)[1] if "/" in name else name
            raw = zf.read(info)
            if rel.lower() != "skill.md" and not _is_text(rel, raw):
                out[rel] = raw
    return out


def check_path(path: str) -> str:
    rel = path.replace("\\", "/").strip()
    rel = rel[2:] if rel.startswith("./") else rel
    if not rel or rel.startswith("/") or ".." in rel.split("/") or ":" in rel or "//" in rel:
        raise SkillError(f"文件路径不合法：{path}")
    if rel.lower() == "skill.md":
        raise SkillError("SKILL.md 单独编辑，不要放在其他文件里")
    if Path(rel).suffix.lower() in BLOCKED_SUFFIXES:
        raise SkillError(f"技能包里不能放可执行程序（{rel}）")
    return rel


def set_meta(skill_md: str, key: str, value: str) -> str:
    """改 SKILL.md frontmatter 里的一项（没有就加上）。"""
    text = skill_md.replace("\r\n", "\n").lstrip("﻿")
    if not text.startswith("---\n"):
        return text
    end = text.find("\n---", 4)
    if end < 0:
        return text
    header = text[4:end].split("\n")
    line = f"{key}: {value}"
    for i, h in enumerate(header):
        if re.match(rf"^{re.escape(key)}\s*:", h, re.I):
            header[i] = line
            break
    else:
        header.append(line)
    return "---\n" + "\n".join(header) + text[end:]


def bump_version(version: str) -> str:
    """1.2.3 → 1.2.4；不是数字版本就在后面加 .1；没有版本从 1.0.0 开始。"""
    v = version.strip()
    if not v:
        return "1.0.0"
    m = re.match(r"^(.*?)(\d+)$", v)
    if m and re.match(r"^v?[\d.]*$", v, re.I):
        return f"{m.group(1)}{int(m.group(2)) + 1}"
    return f"{v}.1"


def build_package(name: str, skill_md: str, files: list[tuple[str, str]], keep: dict[str, bytes] | None = None) -> ParsedSkill:
    """
    按后台编辑的内容打包。SKILL.md 里的 name 必须和技能标识一致：员工电脑按这个名字安装和更新。
    keep 是旧包里的二进制文件（图片、模板），原样放回去。
    """
    if not VALID_NAME.match(name):
        raise SkillError("技能标识只能用小写字母、数字、点、横线和下划线，2～64 个字符")
    if not skill_md.strip():
        raise SkillError("SKILL.md 不能为空")
    if len(files) + len(keep or {}) > MAX_AUTHORED_FILES:
        raise SkillError(f"技能包里的文件不能超过 {MAX_AUTHORED_FILES} 个")
    buffer = io.BytesIO()
    seen: set[str] = set()
    with zipfile.ZipFile(buffer, "w", zipfile.ZIP_DEFLATED) as out:
        out.writestr(f"{name}/SKILL.md", skill_md.replace("\r\n", "\n").encode("utf-8"))
        for path, content in files:
            rel = check_path(path)
            if rel.lower() in seen:
                raise SkillError(f"文件重名：{rel}")
            raw = content.encode("utf-8")
            if len(raw) > MAX_TEXT_BYTES:
                raise SkillError(f"{rel} 太大了（单个文本文件最多 256 KB）")
            seen.add(rel.lower())
            out.writestr(f"{name}/{rel}", raw)
        for rel, raw in (keep or {}).items():
            if rel.lower() not in seen:
                seen.add(rel.lower())
                out.writestr(f"{name}/{rel}", raw)
    [skill] = parse_packages(buffer.getvalue())
    if skill.name != name:
        raise SkillError(f"SKILL.md 里的 name 是「{skill.name}」，要和技能标识「{name}」一致")
    return skill
