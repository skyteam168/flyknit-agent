"""
给员工端推「配置变了，马上来拉」的信号。

实现用的是长轮询：客户端带着自己手里的版本号来问，服务端要么立刻告诉它版本不一样了，
要么把这个请求挂起最多若干秒，一旦有人改了配置（安全中心、命令策略、设备单独设置……）
就立刻放行。这样 IT 一改，员工端一两秒内就拉到新配置并生效，不用等十分钟的定时刷新。

进程内内存实现，单机足够；多实例部署时每个实例各自维护，客户端换实例会因版本号对不上
而立即返回一次，照样自愈。
"""

from __future__ import annotations

import asyncio

_revision = 0
_waiters: set[asyncio.Event] = set()


def current() -> int:
    return _revision


def bump() -> None:
    """配置发生变化时调用：版本号 +1，并唤醒所有在等的客户端。"""
    global _revision
    _revision += 1
    for ev in list(_waiters):
        ev.set()


async def wait_for_change(since: int, timeout: float) -> int:
    """
    等到版本号和 since 不一样，或超时为止，返回当前版本号。

    客户端把上次拿到的版本号作为 since 传进来；只要期间有变化就立刻返回。
    """
    if _revision != since:
        return _revision
    ev = asyncio.Event()
    _waiters.add(ev)
    try:
        await asyncio.wait_for(ev.wait(), timeout)
    except asyncio.TimeoutError:
        pass
    finally:
        _waiters.discard(ev)
    return _revision
