# 命令行脚本与 CI

IT 在安全中心打开「**命令行 / IDE 接入**」后，`flyknit.exe` 有三种用法，用的都是这台电脑上 FlyknitBuddy 的登录，权限、确认、额度、审计都一样：

| 用法 | 适合 | 说明 |
| --- | --- | --- |
| `flyknit` | 自己在命令行里来回交代、需要确认 | 见 [终端对话](/guide/terminal) |
| `flyknit -p "……"` | 脚本、计划任务、CI：跑一次拿结果 | 本页 |
| `flyknit acp` | VS Code、Zed、JetBrains 里当 AI 助手 | 见 [在 IDE 里用](/guide/ide) |

## flyknit -p

跑一次就结束：结果默认是 JSON，写到标准输出，方便脚本读取。

在命令行里直接运行时，会边做边显示每一步在做什么（`▸` 开始、`✓` 成功、`✗` 失败、`… 思考中`），最后输出结果；被脚本调用、或输出重定向到文件时只输出结果，内容不变。

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
| `--think` | 让 AI 每一步先深度思考，更慢、更费额度；默认关，不跟 FlyknitBuddy 里的「深度思考」开关走 |

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

## 不提供的能力

长期记忆、浏览器、连接器、子代理、向你提问——这些需要 FlyknitBuddy 的界面。

## 放进计划任务的例子

每天早上 8 点汇总日报（Windows「任务计划程序」里新建任务，操作填）：

```bat
"C:\Program Files\FlyknitBuddy\flyknit.exe" -p "把今天的日报汇总成一张表，存成 汇总-今天日期.xlsx" -w D:\日报 --allow-writes --format text --timeout 1800
```

- `--allow-writes` 只放行普通写入（新建、修改文件这类能设成「以后自动执行」的操作）；删除、覆盖等高危操作任何时候都不放行。
- 退出码不是 0 时可以在任务计划程序的历史里看到，用 `--format json` 能看到具体原因。
