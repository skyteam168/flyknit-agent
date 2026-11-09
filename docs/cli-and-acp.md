# 命令行与 IDE 接入（flyknit / flyknit -p / flyknit acp）

第四批对外接口。`flyknit.exe` 是和员工端（FlyknitBuddy.exe）装在同一个文件夹里的控制台程序，共用 `Flyknit.Core`：

- **`flyknit`**：在命令行里和 FlyknitBuddy 对话（同 Codex CLI / Claude Code 的终端体验），确认在终端里问；
- **`flyknit -p "要做的事"`**：无界面执行一次办事任务，结果输出 JSON，给脚本、计划任务和 CI 用；
- **`flyknit acp`**：ACP（Agent Client Protocol，协议版本 1）服务，JSON-RPC 走标准输入输出，
  让 Zed、JetBrains（以及装了 ACP 插件的 VS Code）直接把 FlyknitBuddy 当 AI 助手用。

不做独立 SDK：命令行加 ACP 已经覆盖「从别的程序调用」，等有具体需求再做。

## 1. 开关和前提

| 条件 | 说明 |
| --- | --- |
| 安全中心「命令行 / IDE 接入」（`cli_access`） | 默认关、锁定。IT 在管理后台打开后才能用；关着时 `flyknit -p` 退出码 4，IDE 里开会话会报同样的原因 |
| 这台电脑、这个 Windows 账号登录过员工端 | 命令行读 `%APPDATA%\Flyknit\settings.json` 里的服务器地址和设备令牌（DPAPI，换账号解不开）。命令行自己不登录、不存令牌 |
| 开发者工具（`developer_tools`） | 可选。开着时多了 grep / multi_edit / git_*，任务走 `code` 场景 |

## 2. 安全模型

和员工端是同一套东西：同一个设备令牌、同一份服务端配置，闸门一个不少
（`blocked_patterns` → `CommandAnalyzer` 定级 → 权限档 + 工作区 → 员工确认过的「以后自动执行」规则）。

| | 命令行 `-p` | IDE（ACP）；终端对话 `flyknit` 同 IDE |
| --- | --- | --- |
| 需要确认的操作 | **默认一律拒绝**，写进输出的 `declined`。加 `--allow-writes` 只放行员工端也能设成「以后自动执行」的那一类（有可记规则、非高危、IT 没要求每次确认），放行一次、不写规则 | 发 `session/request_permission`，IDE 弹自己的确认框 |
| 高危操作（删除、覆盖、改注册表 / 服务 / 账号、装卸软件、解释器和间接执行） | 任何参数都不放行 | 只给「允许一次 / 拒绝」，没有「总是允许」；IDE 回了没给过的选项按拒绝 |
| 权限档 | `readonly` 或 `workspace`，**没有完全权限** | 员工端设置里的默认权限（同样没有完全权限） |
| 工作区 | `-w` 指定，默认当前目录 | IDE 给的 `cwd`（必须是存在的绝对路径） |
| 审计 | 照常上报，来源 `actor = cli` | 来源 `actor = ide`（终端对话是 `cli`） |
| 额度 | 照常计入这台设备，超额退出码 5 | 同左，超额时把原因作为回答发给 IDE |

没有的东西（都需要员工端界面或会碰员工个人数据）：长期记忆、浏览器、连接器（MCP）、子代理、向员工提问、派遣专家。
IDE 在 `session/new` 里带来的 MCP 服务器**不接**：连接器只认 IT 在后台配的（不变量 6）。
命令行和 IDE 的对话不进员工端的历史记录。IDE 会话每一轮结束存到本机 `%APPDATA%\Flyknit\ide-sessions\<会话 ID>.json`
（和员工端的对话记录一样只在这台电脑上、不上传；最多留 50 个、30 天），IDE 关掉以后能从会话列表打开接着聊。
重新打开时权限档、安全设置按当时的配置，不从文件里读；`flyknit -p` 不保存。

## 3. flyknit（终端对话）

```
PS D:\project> flyknit
FlyknitBuddy 0.1.0  命令行对话
工作区 D:\project · 权限：工作区内修改 · 开发者工具：开 · 深度思考：关
> 把 utils.py 里的两个 TODO 修掉
  ▸ 读取 utils.py
    ✓ 共 120 行
  ▸ 修改 utils.py
  ⚠ 需要确认：修改 D:\project\utils.py
    [y] 允许一次  [a] 以后自动  [n] 拒绝 > y
    已允许这一次
    ✓ 已修改 2 处
两个 TODO 都改好了：……
· 2 次工具 · 3.1k tokens · 8.4 秒
```

| 用法 | 说明 |
| --- | --- |
| `flyknit` / `flyknit "第一句话"` | 进入对话；工作区默认当前目录（`-w` 指定），`--permission`、`--think` 同 `-p` |
| `flyknit -c` | 接着这个工作区最近的一次对话 |
| `flyknit --resume [会话 ID]` | 列出以前的对话选一个；带 ID 直接打开 |

- **输出**：回答边生成边打印；工具调用一行一个（`▸` 开始，`✓` / `✗` 结束，带结果第一行）；计划更新时列出来；
  等模型回复时行首显示「… 思考中」，第一个字出来就擦掉；每轮结束一行页脚（工具次数、token、耗时，没做完时写原因）。
  思考过程只有 `--think` 时显示。输出被重定向、设了 `NO_COLOR` 或终端不支持时不用颜色、不写占位。
- **确认**：在终端里问。普通写入是 `[y] 允许一次 [a] 以后自动 [n] 拒绝`（「以后自动」记成和员工端共用的规则）；
  高危操作只有 `[y] [n]`，输入 `a` 会再问一次；直接回车、输入结束都按拒绝。并行的工具同时要确认时一个一个问。
- **命令**：`/new` 新会话、`/resume [序号]` 打开以前的、`/plan` 看计划、`/usage` 本会话 token、`/clear` 清屏、`/help`、`/exit`。
- **输入**：回车发送；行尾 `\` 接着下一行；单独一行 `"""` 开始多行输入，再一行 `"""` 结束。
- **Ctrl+C**：任务进行中停下这一轮（已发出的工具调用照常补结果、审计照常上报），回到输入；空闲时第一次只提示，两秒内再按一次退出。
- **会话**：每轮结束存到本机（和 IDE 的会话同一个目录、同一个列表，见第 5 节），所以 IDE 里的对话也能在终端里 `/resume` 接着聊，反过来也一样。
- **编码和颜色**：对话期间把控制台输入输出切到 UTF-8、退出时切回；老的控制台窗口自动打开「虚拟终端处理」才用颜色。

实现：`Flyknit.Core/Headless/InteractiveShell.cs`（`InteractiveShell`、`ConsoleObserver`、`ConsoleConfirm`、`ConsoleStyle`），
入口和 Ctrl+C 在 `Flyknit.Cli/Program.cs`，测试 `InteractiveShellTests`。

## 3.1 flyknit -p

```
flyknit -p "把 docs 目录里的 md 文件列出来" --format json
flyknit -p - < task.txt                      # 要做的事从标准输入读
flyknit -p "跑一下测试并修复" -w D:\repo --allow-writes --timeout 1800
```

| 参数 | 说明 |
| --- | --- |
| `-w, --workspace <目录>` | 工作区，默认当前目录 |
| `--permission readonly\|workspace` | 默认按员工端设置 |
| `--format json\|text\|stream-json` | json（默认，一个对象）、text（只输出回答）、stream-json（每行一个事件，最后一行 `type=result`） |
| `--allow-writes` | 见上表 |
| `--timeout <秒>` | 超时停下，退出码 124 |
| `--think` | 让模型每一步先深度思考。默认关，**不跟员工端的「深度思考」开关走**：命令行 / IDE 的任务常常几十步，每步都先想一大段会慢很多、也费 token |

输出（json）：

```json
{
  "ok": true, "exit_code": 0, "stop_reason": "completed",
  "answer": "……", "error": null,
  "session_id": "cli-…", "workspace": "D:\\repo", "permission": "workspace",
  "model": "Qwen3.5-397B", "steps": 3, "usage": { "prompt_tokens": 1200, "completion_tokens": 300 },
  "tool_calls": [ { "name": "write_file", "summary": "新建文件 a.txt", "decision": "auto", "ok": true } ],
  "declined":   [ { "name": "run_shell", "summary": "执行命令：del a.txt", "reason": "高危操作……" } ],
  "duration_ms": 5321
}
```

`decision`：auto（自动）/ remembered（员工的自动执行规则）/ approved（--allow-writes 放行）/ rejected（拒绝）/ blocked（被拦）。

编码：
- 直接显示在命令行窗口时，运行期间把窗口切到 UTF-8，中文原样显示，退出时切回原来的编码；
- 输出被管道或重定向时（脚本、CI 读取），json / stream-json 里的非 ASCII 字符写成 `\uXXXX`，
  中文 Windows 上 PowerShell 5 按 GBK 解码也不会乱码，`ConvertFrom-Json` 后就是原文；
- `--format text` 被重定向时是 UTF-8，PowerShell 5 里要先 `[Console]::OutputEncoding = [Text.Encoding]::UTF8` 再读。

退出码（脚本靠它判断，不改已有数字）：

| 码 | 含义 |
| --- | --- |
| 0 | 完成 |
| 1 | 没做完（模型服务出错、空转、预算用完、步数到上限、失败太多） |
| 2 | 参数不对 |
| 3 | 没登录 / 登录失效 / 设备被停用 |
| 4 | IT 没开「命令行 / IDE 接入」 |
| 5 | 今日额度用完 |
| 6 | 连不上公司服务器 |
| 124 | 超时 |
| 130 | Ctrl+C 中断（已发出的工具调用照常补结果、审计照常上报） |

## 4. flyknit acp（IDE）

实现的方法：`initialize`、`authenticate`（空操作）、`session/new`、`session/prompt`、`session/cancel`、
`session/list`（`agentCapabilities.sessionCapabilities.list`；可按 `cwd` 过滤，一页 50 个，`nextCursor` 翻页）、
`session/load`（`loadSession: true`；先把历史推给 IDE——员工的话、回答、工具调用和结果，思考过程不重放——再回结果，
工作区用保存时的，会话 ID 只认 `acp-` 加十六进制，不能拿来拼别的路径）；
推给 IDE 的更新：`user_message_chunk`（只在重新打开时）、`agent_message_chunk`、`agent_thought_chunk`、`tool_call`、`tool_call_update`、`plan`；
向 IDE 请求：`session/request_permission`。不支持图片和音频输入。
标准输出只有协议消息，日志写标准错误。

IDE 里的配置（以各 IDE 当前版本的文档为准，下面是写这份文档时的格式）：

- **Zed**：`settings.json`
  ```json
  "agent_servers": {
    "FlyknitBuddy": { "type": "custom", "command": "flyknit", "args": ["acp"] }
  }
  ```
- **JetBrains**（AI Assistant 的 ACP 智能体配置 `acp.json`）
  ```json
  { "agent_servers": { "FlyknitBuddy": { "command": "C:\\Program Files\\FlyknitBuddy\\flyknit.exe", "args": ["acp"] } } }
  ```
- **VS Code**：装支持 ACP 的插件后，按插件说明把命令填成 `flyknit acp`。
- 要深度思考时参数写 `["acp", "--think"]`（默认关，理由同 `-p --think`）。
- 回答和思考的片段攒约 60 毫秒（或 2000 字）发一条 `session/update`，不再每个字一条：IDE 每收到一条都要重新渲染，思考一长界面就卡。
  工具调用、计划、确认请求发出之前会先把攒着的片段发掉，顺序不变。

## 5. 安装和升级

- `client/scripts/publish-client.ps1` 把 `flyknit.exe` 发布进员工端文件夹，打进同一个 zip；
- 安装程序（FlyknitSetup）把 `C:\Program Files\FlyknitBuddy` 加进系统 PATH（新开的命令行窗口生效），卸载时去掉；
- 运维代理升级员工端时，等 FlyknitBuddy 和 flyknit 都退出才换文件（IDE 开着 `flyknit acp` 时，关掉 IDE 或 IDE 里的智能体后才会升级）。

## 6. 怎么测

**已在 Linux 开发容器里跑过的**（见 PR 说明）：Core 单测（参数、开关、确认策略、退出码、ACP 协议、取消）；
真实服务端（uvicorn）+ 假的上游模型 + 编译出的 `flyknit` 程序端到端；
ACP 用官方 npm 包 `@agentclientprotocol/sdk` 里生成的 zod 结构校验服务发出的每一条消息。

**要在 Windows 实机上测的**：

1. 管理后台 → 安全中心打开「命令行 / IDE 接入」（不开时先跑一次，确认退出码 4）；
2. 员工端登录后，新开 PowerShell：`flyknit --version`（确认 PATH 生效）；
3. `cd` 到一个测试目录：
   - `flyknit -p "在这里新建 hello.txt，内容写你好"` → 文件生成、`ok: true`；
   - `flyknit -p "删除 hello.txt" --allow-writes` → 文件还在，`declined` 里有一条「高危操作……」；
   - `flyknit -p "列出这个目录" --format text`、`--format stream-json`；
   - `echo 你好 | flyknit -p - --format text`（中文不乱码）；
4. 管理后台 → 审计里能看到来源 cli 的记录；
5. Zed 或 JetBrains 按第 4 节配置后新建一个对话：
   - 让它改一个文件（工作区内的写入不弹确认，直接改）；
   - 让它跑一个命令（IDE 弹确认框：允许一次 / 总是允许 / 拒绝）；
   - 让它删一个文件（确认框只有允许一次 / 拒绝）；
   - 跑一个长任务时点 IDE 的停止，确认能停下；
6. 换一个没登录过员工端的 Windows 账号跑 `flyknit -p x`，应提示先登录（退出码 3）。
