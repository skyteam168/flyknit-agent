"""
消息通知的规则（docs/notices.md）：类型、正文清洗、可见性、媒体签名。

正文是 IT 在后台用富文本编辑器写的 HTML，员工端会在 WebView 里渲染。所以保存前在服务端按白名单清洗：
- 只留排版类标签（段落、标题、列表、引用、表格、链接、图片、视频……），脚本、样式表、iframe、表单等整段丢掉；
- 属性按标签白名单留；链接只认 http / https / mailto；
- 图片、视频只认本服务器上传的媒体（/api/v1/notices/media/{id}），外部地址整个元素丢掉：
  员工电脑不去连外网，也不会被外部图片跟踪是谁打开了通知；
- 行内样式只留对齐和颜色。
员工端渲染前还会再清洗一遍（两道），这里是主防线。
"""

from __future__ import annotations

import hashlib
import hmac
import re
import time
from datetime import datetime, timezone
from html import escape
from html.parser import HTMLParser

from ..models import Device, Notice
from . import item_scope

#: 消息类型：员工端按类型显示图标和颜色，后台按类型筛选
KINDS = ("upgrade", "feature", "maintenance", "security", "policy", "event", "tip", "general", "report")
KIND_LABELS = {
    "upgrade": "系统升级",
    "feature": "功能介绍",
    "maintenance": "停机维护",
    "security": "安全提醒",
    "policy": "制度通知",
    "event": "培训活动",
    "tip": "使用技巧",
    "general": "一般通知",
    "report": "定时报告",
}

MAX_BODY = 200_000
MEDIA_PREFIX = "/api/v1/notices/media/"
_MEDIA_SRC = re.compile(r"^/api/v1/notices/media/([0-9a-f]{32})(?:\?[^\"'<>\s]*)?$")

_ALLOWED: dict[str, set[str]] = {
    "p": set(), "br": set(), "hr": set(), "div": set(), "span": set(),
    "h1": set(), "h2": set(), "h3": set(), "h4": set(),
    "strong": set(), "b": set(), "em": set(), "i": set(), "u": set(), "s": set(), "del": set(),
    "sub": set(), "sup": set(), "mark": set(), "code": set(), "pre": set(), "blockquote": set(),
    "ul": set(), "ol": {"start"}, "li": set(),
    "a": {"href", "title"},
    "img": {"src", "alt", "width", "height"},
    "video": {"src", "poster", "width"},
    "figure": set(), "figcaption": set(),
    "table": set(), "thead": set(), "tbody": set(), "tr": set(), "th": {"colspan", "rowspan"}, "td": {"colspan", "rowspan"},
}
_VOID = {"br", "hr", "img"}
#: 连内容一起丢掉的标签
_DROP = {"script", "style", "iframe", "object", "embed", "svg", "math", "template", "noscript", "form", "textarea",
         "select", "button", "input", "head", "title", "link", "meta", "frame", "frameset", "applet", "base"}
_STYLE_PROPS = {"text-align": re.compile(r"^(left|right|center|justify)$"),
                "color": re.compile(r"^(#[0-9a-fA-F]{3,8}|rgba?\([\d\s.,%]+\)|[a-zA-Z]{3,20})$"),
                "background-color": re.compile(r"^(#[0-9a-fA-F]{3,8}|rgba?\([\d\s.,%]+\)|[a-zA-Z]{3,20})$")}
_STYLE_TAGS = {"p", "div", "span", "h1", "h2", "h3", "h4", "td", "th", "li", "mark", "figcaption"}


def media_id(src: str) -> str | None:
    m = _MEDIA_SRC.match((src or "").strip())
    return m.group(1) if m else None


def _safe_href(value: str) -> str | None:
    v = (value or "").strip()
    low = re.sub(r"[\s\x00-\x1f]", "", v).lower()
    if low.startswith(("http://", "https://", "mailto:")):
        return v
    return None


def _style(value: str) -> str | None:
    kept = []
    for part in (value or "").split(";"):
        if ":" not in part:
            continue
        prop, val = (x.strip() for x in part.split(":", 1))
        rule = _STYLE_PROPS.get(prop.lower())
        if rule and rule.match(val):
            kept.append(f"{prop.lower()}: {val}")
    return "; ".join(kept) or None


class _Cleaner(HTMLParser):
    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.out: list[str] = []
        self.stack: list[str] = []
        self.dropping = 0
        self.media: set[str] = set()
        self._dropping_video = False

    def _attrs(self, tag: str, attrs: list[tuple[str, str | None]]) -> list[tuple[str, str]] | None:
        allowed = _ALLOWED[tag]
        kept: list[tuple[str, str]] = []
        for name, value in attrs:
            name = name.lower()
            value = value or ""
            if name == "style" and tag in _STYLE_TAGS:
                if (s := _style(value)) is not None:
                    kept.append(("style", s))
                continue
            if name not in allowed:
                continue
            if name == "href":
                href = _safe_href(value)
                if href is None:
                    continue
                kept.append(("href", href))
            elif name in ("src", "poster"):
                mid = media_id(value)
                if mid is None:
                    if name == "src":
                        return None  # 外部或非法地址：整个图片 / 视频不要
                    continue
                self.media.add(mid)
                kept.append((name, MEDIA_PREFIX + mid))
            elif name in ("width", "height", "colspan", "rowspan", "start"):
                if value.strip().isdigit() and 0 < int(value) <= 4000:
                    kept.append((name, value.strip()))
            else:
                kept.append((name, value[:200]))
        if tag in ("img", "video") and not any(n == "src" for n, _ in kept):
            return None
        if tag == "a" and any(n == "href" for n, _ in kept):
            kept += [("target", "_blank"), ("rel", "noopener noreferrer")]
        if tag == "video":
            kept.append(("controls", ""))
        return kept

    def _emit_start(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        if self.dropping:
            if tag in _DROP and tag not in _VOID:
                self.dropping += 1
            return
        if tag in _DROP:
            self.dropping = 1
            return
        if tag not in _ALLOWED:
            return  # 不认识的标签：去掉标签，留内容
        kept = self._attrs(tag, attrs)
        if kept is None:
            if tag == "video":
                # 视频本身不要了，里面的 <source> 之类也一起不要
                self.dropping = 1
                self._dropping_video = True
            return
        text = "".join(f' {n}="{escape(v, quote=True)}"' if v else f" {n}" for n, v in kept)
        self.out.append(f"<{tag}{text}>")
        if tag not in _VOID:
            self.stack.append(tag)

    def handle_starttag(self, tag, attrs):
        self._emit_start(tag.lower(), attrs)

    def handle_startendtag(self, tag, attrs):
        tag = tag.lower()
        self._emit_start(tag, attrs)
        if tag not in _VOID and self.stack and self.stack[-1] == tag:
            self.handle_endtag(tag)

    def handle_endtag(self, tag):
        tag = tag.lower()
        if self.dropping:
            if tag in _DROP or (tag == "video" and self._dropping_video):
                self.dropping -= 1
                if not self.dropping:
                    self._dropping_video = False
            return
        if tag not in self.stack:
            return
        while self.stack:
            top = self.stack.pop()
            self.out.append(f"</{top}>")
            if top == tag:
                break

    def handle_data(self, data):
        if not self.dropping:
            self.out.append(escape(data, quote=False))

    def result(self) -> str:
        while self.stack:
            self.out.append(f"</{self.stack.pop()}>")
        return "".join(self.out)


def sanitize_html(html: str) -> tuple[str, set[str]]:
    """清洗正文，返回（干净的 HTML，引用到的媒体 ID）。"""
    cleaner = _Cleaner()
    cleaner.feed((html or "")[:MAX_BODY])
    cleaner.close()
    return cleaner.result(), cleaner.media


def plain_text(html: str, limit: int = 120) -> str:
    """从正文取纯文字（列表摘要用）。"""
    text = re.sub(r"<(br|/p|/div|/li|/h\d)[^>]*>", " ", html or "")
    text = re.sub(r"<[^>]+>", "", text)
    from html import unescape

    text = re.sub(r"\s+", " ", unescape(text)).strip()
    return text[:limit]


def safe_action_url(url: str) -> str:
    url = (url or "").strip()
    return url if re.match(r"^https?://", url, re.IGNORECASE) else ""


def _aware(dt: datetime | None) -> datetime | None:
    if dt is None:
        return None
    return dt if dt.tzinfo else dt.replace(tzinfo=timezone.utc)


def live(notice: Notice, now: datetime | None = None) -> bool:
    """已发布、到了发布时间、还没过期。"""
    now = now or datetime.now(timezone.utc)
    if notice.status != "published":
        return False
    start = _aware(notice.publish_at) or _aware(notice.published_at)
    if start and start > now:
        return False
    end = _aware(notice.expire_at)
    return not (end and end <= now)


def visible(notice: Notice, device: Device, now: datetime | None = None) -> bool:
    """员工端看得到：在生效时间内，且这台电脑在范围内（部门 ∪ 电脑，空 = 全公司）。"""
    return live(notice, now) and item_scope.in_scope(notice, device)


# ---------- 媒体 ----------

#: 能上传的类型：按文件头判断。图片 10MB，视频 300MB
IMAGE_MAX = 10 * 1024 * 1024
VIDEO_MAX = 300 * 1024 * 1024


def sniff_mime(head: bytes) -> str | None:
    if head.startswith(b"\x89PNG\r\n\x1a\n"):
        return "image/png"
    if head.startswith(b"\xff\xd8\xff"):
        return "image/jpeg"
    if head[:6] in (b"GIF87a", b"GIF89a"):
        return "image/gif"
    if head[:4] == b"RIFF" and head[8:12] == b"WEBP":
        return "image/webp"
    if head[4:8] == b"ftyp":
        return "video/mp4"
    if head.startswith(b"\x1a\x45\xdf\xa3"):
        return "video/webm"
    return None


def _key(secret: str) -> bytes:
    return hashlib.sha256(("notice-media:" + secret).encode()).digest()


def sign(media: str, secret: str, ttl: int = 12 * 3600, now: float | None = None) -> str:
    """后台编辑器里看图用的临时地址（img 标签带不了 Authorization）。"""
    exp = int((now or time.time()) + ttl)
    sig = hmac.new(_key(secret), f"{media}:{exp}".encode(), hashlib.sha256).hexdigest()[:32]
    return f"{MEDIA_PREFIX}{media}?exp={exp}&sig={sig}"


def verify(media: str, exp: str, sig: str, secret: str, now: float | None = None) -> bool:
    if not exp.isdigit() or int(exp) < (now or time.time()):
        return False
    want = hmac.new(_key(secret), f"{media}:{exp}".encode(), hashlib.sha256).hexdigest()[:32]
    return hmac.compare_digest(want, sig or "")


def signed_body(html: str, secret: str) -> str:
    """把正文里的媒体地址换成带签名的（给后台编辑器、预览用）。"""
    return re.sub(r'(src|poster)="/api/v1/notices/media/([0-9a-f]{32})"',
                  lambda m: f'{m.group(1)}="{escape(sign(m.group(2), secret), quote=True)}"', html or "")
