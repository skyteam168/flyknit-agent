"""默认命令策略。管理员可通过 PUT /api/v1/admin/policy 覆盖，下发给所有客户端。

规则顺序：blocked_patterns（直接阻止）→ readonly_commands（可选自动）→ 其余全部需用户确认。
blocked_patterns 为正则，客户端以忽略大小写方式匹配完整命令文本。
"""

DEFAULT_POLICY: dict = {
    "version": 1,
    "auto_run_readonly": True,
    # 递归强制删除：删 node_modules 和删 C:\ 是两回事，所以不按动词一刀切，
    # 而是解析出目标再判。命中这里只是「要看目标」，不等于拦截。
    # 目标在系统目录、盘符根、或者根本解析不出来（变量、通配符）才拦。
    # 这些目录不许被整个删掉，哪怕权限给到最大。和 protected_roots 不同：
    # protected_roots 连写都不让（所以不能包含 C:\\Users，否则用户连自己的文档都写不了），
    # 这里只管「整个删掉」。
    "no_delete_roots": [
        "C:\\",
        "D:\\",
        "C:\\Users",
        "C:\\Windows",
        "C:\\Program Files",
        "C:\\Program Files (x86)",
        "C:\\ProgramData",
    ],
    "recursive_delete_patterns": [
        r"\brm\s+(-[a-z]*r[a-z]*f|-[a-z]*f[a-z]*r)\b",
        r"\brm\s+-(-recursive|-force)",
        r"\bremove-item\b(?=.*-r(ecurse)?\b)(?=.*-fo(rce)?\b)",
        r"\b(rd|rmdir)\s+/s\b",
        r"\bdel\s+(/[a-z]\s+)*/s\b",
        r"\berase\s+(/[a-z]\s+)*/s\b",
    ],
    "blocked_patterns": [
        # 磁盘与引导
        r"\bformat(\.com)?\s+[a-z]:",
        r"\bformat-volume\b",
        r"\bclear-disk\b",
        r"\bdiskpart\b",
        r"\bbcdedit\b",
        r"\bbootrec\b",
        r"\bvssadmin\s+delete\b",
        r"\bwbadmin\s+delete\b",
        r"\bcipher\s+/w\b",
        # 注册表系统分支
        r"\breg\s+(delete|add|import)\s+\"?hk(lm|ey_local_machine)",
        r"\bremove-item\b.*\bhklm:",
        r"\bset-itemproperty\b.*\bhklm:",
        # 系统服务、启动项
        r"\bsc(\.exe)?\s+(delete|config)\b",
        r"\b(stop|remove|set)-service\b",
        r"\bschtasks\b.*\b/delete\b.*\b/tn\s+\"?\\microsoft",
        # 安全防护
        r"\bset-mppreference\b",
        r"\bnetsh\s+(adv)?firewall\b.*\b(off|disable)",
        r"\bset-netfirewallprofile\b.*-enabled\s+false",
        # 账号与权限
        r"\bnet\s+(user|localgroup)\b.*(/add|/delete|/active)",
        r"\bnet\s+user\s+\S+\s+\S+",  # 修改密码
        r"\b(new|remove|set)-localuser\b",
        r"\b(add|remove)-localgroupmember\b",
        r"\btakeown\b.*\b(c:\\windows|c:\\program files)",
        r"\bicacls\b.*\b(c:\\windows|c:\\program files)",
        # 关机重启
        r"\bshutdown(\.exe)?\s+(/|-)[srpf]\b",
        r"\b(stop|restart)-computer\b",
        # 下载并执行
        r"\b(iwr|invoke-webrequest|curl|wget)\b.*\|\s*(iex|invoke-expression)\b",
        r"\biex\s*\(\s*(new-object\s+net\.webclient|iwr|invoke-webrequest)",
        # 编码执行（常用于绕过检查）
        r"\bpowershell(\.exe)?\b.*\s-e(nc|ncodedcommand)?\s+[a-z0-9+/=]{20,}",
        # 系统目录写删
        r"\b(del|erase|remove-item|rm|move|move-item)\b.*\bc:\\windows\b",
        r"\b(del|erase|remove-item|rm)\b\s+(\"?[a-z]:\\\"?)(\s|$)",
    ],
    "readonly_commands": [
        "ipconfig",
        "ping",
        "tracert",
        "nslookup",
        "hostname",
        "whoami",
        "systeminfo",
        "dir",
        "tree",
        "tasklist",
        "get-process",
        "get-childitem",
        "get-content",
        "get-date",
        "get-location",
        "get-netipaddress",
        "get-computerinfo",
        "test-connection",
        "get-volume",
        "get-psdrive",
    ],
    "writable_roots": [
        "%USERPROFILE%",
        "D:\\",
        "E:\\",
    ],
    "protected_roots": [
        "C:\\Windows",
        "C:\\Program Files",
        "C:\\Program Files (x86)",
        "C:\\ProgramData",
    ],
    "batch_confirm_threshold": 20,
}
