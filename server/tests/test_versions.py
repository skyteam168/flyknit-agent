"""版本号比较。

字符串比较在这里是错的："0.10.0" < "0.9.0" 是字典序的结论，但 0.10 比 0.9 新。
按字典序判断的话，员工电脑升到 0.9 之后就再也收不到 0.10 了。
"""

import pytest

from app.services.versions import compare, is_newer, parse


@pytest.mark.parametrize("newer,older", [
    ("0.10.0", "0.9.0"),      # 这条是整个模块存在的理由
    ("1.0.0", "0.9.9"),
    ("0.2.0", "0.1.9"),
    ("0.1.10", "0.1.9"),
    ("2.0.0", "1.99.99"),
    ("1.0.0", "1.0.0-rc1"),   # 正式版比预发布新
    ("1.0.0-rc2", "1.0.0-rc1"),
    ("0.1.0", "乱写的"),        # 认不出来的当最旧：认不出来就该收到更新
])
def test_newer_versions_win(newer, older):
    assert is_newer(newer, older)
    assert not is_newer(older, newer)
    assert compare(newer, older) == 1
    assert compare(older, newer) == -1


@pytest.mark.parametrize("a,b", [
    ("1.2.0", "1.2.0"),
    ("1.2", "1.2.0"),         # 段数不同不代表版本不同
    ("v1.2.0", "1.2.0"),      # 前面的 v 不算数
    (" 1.2.0 ", "1.2.0"),
])
def test_same_versions_are_equal(a, b):
    assert compare(a, b) == 0
    assert not is_newer(a, b) and not is_newer(b, a)


def test_an_empty_version_is_the_oldest():
    # 老客户端可能根本不报版本号，它应该收到更新
    assert is_newer("0.0.1", None)
    assert is_newer("0.0.1", "")
    assert not is_newer("", "0.0.1")


def test_parse_keeps_the_suffix():
    assert parse("1.2.3-beta.1") == ((1, 2, 3), "beta.1")
    assert parse("1.2.3") == ((1, 2, 3), "")
    assert parse("什么也不是") == ((0,), "")
