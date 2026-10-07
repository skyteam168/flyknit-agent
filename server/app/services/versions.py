"""版本号比较。

客户端自己报当前版本，服务端据此判断有没有更新。字符串比较在这里是错的——
"0.10.0" < "0.9.0" 是字典序的结论，但 0.10 比 0.9 新。

只认 X.Y.Z 这种点分数字，后面可以带一个后缀（0.9.0-beta.2）。带后缀的比不带的旧，
这是 semver 的规矩：预发布版排在正式版前面。
"""

from __future__ import annotations

import re

_PART = re.compile(r"^(\d+(?:\.\d+)*)(?:[-+](.*))?$")


def parse(version: str | None) -> tuple[tuple[int, ...], str]:
    """
    把版本号拆成（数字段, 后缀）。认不出来的当成 0，这样它比任何正式版本都旧——
    认不出来就该收到更新，而不是被当成最新的。
    """
    text = (version or "").strip().lstrip("vV")
    match = _PART.match(text)
    if match is None:
        return (0,), ""
    numbers = tuple(int(p) for p in match.group(1).split("."))
    return numbers, (match.group(2) or "")


def _key(version: str | None) -> tuple:
    numbers, suffix = parse(version)
    # 段数不同时右侧补零：1.2 和 1.2.0 是同一个版本
    padded = numbers + (0,) * (4 - len(numbers)) if len(numbers) < 4 else numbers[:4]
    # 没有后缀的排在后面（更新）：1.0.0 > 1.0.0-rc1
    return padded, (0, suffix) if suffix else (1, "")


def compare(left: str | None, right: str | None) -> int:
    """左边比右边新返回 1，旧返回 -1，一样返回 0。"""
    a, b = _key(left), _key(right)
    return (a > b) - (a < b)


def is_newer(candidate: str | None, current: str | None) -> bool:
    """candidate 是不是比 current 新。"""
    return compare(candidate, current) > 0
