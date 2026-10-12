"""
管理端登录的失败次数限制，防在线爆破密码。

同一个用户名 10 分钟内错 5 次、或同一个来源地址错 50 次（服务器在反向代理后面时所有人共用一个地址，不能太紧），就先挡 10 分钟。
状态放在进程内存里：单进程部署够用，重启清零也无妨——要的是让爆破慢到没意义，不是记账。
"""

import time
from collections import deque

WINDOW_SECONDS = 600
MAX_PER_USER = 5
MAX_PER_IP = 50
#: 记录的键太多时（有人拿随机用户名刷）顺手清掉过期的，免得内存一直涨
_PRUNE_AT = 10_000

_fails: dict[str, deque[float]] = {}


def _keys(username: str, ip: str) -> list[tuple[str, int]]:
    keys = [(f"u:{username.strip().lower()}", MAX_PER_USER)]
    if ip:
        keys.append((f"ip:{ip}", MAX_PER_IP))
    return keys


def _recent(key: str, now: float) -> int:
    q = _fails.get(key)
    if q is None:
        return 0
    while q and q[0] <= now - WINDOW_SECONDS:
        q.popleft()
    if not q:
        del _fails[key]
        return 0
    return len(q)


def blocked(username: str, ip: str) -> bool:
    now = time.monotonic()
    return any(_recent(key, now) >= limit for key, limit in _keys(username, ip))


def failed(username: str, ip: str) -> None:
    now = time.monotonic()
    if len(_fails) > _PRUNE_AT:
        for key in list(_fails):
            _recent(key, now)
    for key, _ in _keys(username, ip):
        _fails.setdefault(key, deque()).append(now)


def succeeded(username: str) -> None:
    """登录成功清掉这个用户名的失败记录。来源地址的不清：同一处试别的号照样要数。"""
    _fails.pop(_keys(username, "")[0][0], None)


def reset() -> None:
    """测试用：每个用例从干净的状态开始。"""
    _fails.clear()
