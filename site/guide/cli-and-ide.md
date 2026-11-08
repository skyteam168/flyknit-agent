# 命令行与 IDE

IT 在安全中心打开「**命令行 / IDE 接入**」后，可以在脚本、CI 和开发工具里调用 FlyknitBuddy。`flyknit.exe` 和 FlyknitBuddy 装在一起，用的是这台电脑上的登录，权限、确认、额度、审计都一样。

## flyknit -p

在 PowerShell 或命令提示符里：

```powershell
flyknit -p "把 docs 目录里的 md 文件列出来"
flyknit -p "跑一下测试并修复" -w D:\repo --allow-writes --timeout 1800
echo 总结一下这个目录 | flyknit -p - --format text
```

| 参数 | 说明 |
| --- | --- |
| `-w, --workspace <目录>` | 工作区，默认当前目录 |
| `--permission readonly\|workspace` | 默认跟 FlyknitBuddy 的设置走；命令行没有完全权限 |
| `--format json\|text\|stream-json` | 默认 json；text 只输出回答；stream-json 每行一个事件 |
| `--allow-writes` | 放行那些在 FlyknitBuddy 里能设成「以后自动执行」的普通操作 |
| `--timeout <秒>` | 超时停下 |

**需要确认的操作默认一律拒绝**，记在输出的 `declined` 里。删除、覆盖等高危操作加任何参数都不放行。

退出码：

| 码 | 含义 |
| --- | --- |
| 0 | 完成 |
| 1 | 没做完 |
| 2 | 参数不对 |
| 3 | 没登录或登录失效 |
| 4 | IT 没开「命令行 / IDE 接入」 |
| 5 | 今日额度用完 |
| 6 | 连不上公司服务器 |
| 124 / 130 | 超时 / 被中断 |

输出被脚本读取时，JSON 里的中文会写成 `\uXXXX` 转义，PowerShell 的 `ConvertFrom-Json` 读出来就是原文。

## 在 IDE 里用（ACP）

`flyknit acp` 是 ACP（Agent Client Protocol）服务，Zed、JetBrains 原生支持，VS Code 要装 ACP 插件。

**Zed**（`settings.json`）：

```json
"agent_servers": {
  "FlyknitBuddy": { "type": "custom", "command": "flyknit", "args": ["acp"] }
}
```

**VS Code**：装一个 ACP 客户端插件（如「ACP Client」或「ACP — Agent Client Protocol」），在用户设置里加：

```json
"agent_servers": {
  "FlyknitBuddy": {
    "command": "C:\\Program Files\\FlyknitBuddy\\flyknit.exe",
    "args": ["acp"],
    "cwd": "${workspaceFolder}"
  }
}
```

`command` 建议写完整路径（VS Code 只在启动时读一次 PATH）。各插件的配置项名称以插件说明为准。

在 IDE 里：工作区就是打开的项目文件夹；需要确认的操作由 IDE 弹确认框，高危操作只有「允许一次 / 拒绝」。

::: tip 升级
IDE 开着 `flyknit acp` 时，FlyknitBuddy 的自动升级会等它退出。要升级时先在 IDE 里断开智能体，或关掉 IDE。
:::

## 不提供的能力

长期记忆、浏览器、连接器、子代理、向你提问——这些需要 FlyknitBuddy 的界面。IDE 带来的 MCP 服务器也不会接入（连接器只认 IT 配置的）。
