# FlyknitBuddy 架构审计与实施计划（软件工程 / 逆向重建能力）

> 审计日期：2026-11-07 · 审计基线：`claude/trusting-cannon-qmkq7q` @ a59a4e6
> 性质：**只审计、不改代码**。本文的结论来自读源码和本机实测。凡是「未验证」的地方都单独标出。

---

## 0. 先说三个前提

### 0.1 提示词假设的技术栈和实际不一样

| 提示词假设 | 实际情况 | 影响 |
| --- | --- | --- |
| 服务端（Python）跑 Agent Runtime | Agent 循环在**员工电脑的 C# 客户端** `client/src/Flyknit.Core`（`Agent/AgentLoop.cs`）；FastAPI 服务端只做模型网关、鉴权、策略、配额、审计、管理后台 | MCP Client Manager、Tool Executor、Shell、文件工具**都应该放在 Core**，不能搬到服务端。服务端不在员工电脑上执行命令，这是 CLAUDE.md 不变量 §1 |
| Redis 存运行态，PostgreSQL 存持久化 | 没有 Redis。服务端默认 SQLite（`sqlite+aiosqlite`），可选 Postgres。任务运行态在客户端本机 SQLite（`Storage/ConversationStore.cs`） | 单机桌面助手**不需要 Redis**。引入它属于「不必要的分布式系统」，与既定约束冲突。建议沿用本机 SQLite 做断点续跑，服务端只存审计和派遣状态 |
| 要从零建 MCP Client | Core 已有一套完整的 MCP 客户端，约 2,600 行：`McpClient` / `McpConnection` / `McpTransport` / `StdioTransport` / `McpRuntime` / `McpOAuth`，另有 Client 侧的 `McpService` | 是**补缺**，不是重写 |

### 0.2 REA / Craft Anything 的真实验收不能在这个云容器里完成

这个容器是 Linux，没有 Windows 桌面，也没有 Ghidra/IDA 这类逆向工具链。另外，本次运行从 npm 下载的第三方 MCP 包被会话的自动权限策略拦下了（见 §3.1）。所以：

- 本次的「真实测试」用的是**自写的、按 MCP 协议实现的 stdio 服务端**（真实子进程、真实 JSON-RPC 管道），被测对象是 Core 里**真实的** `McpConnection`；
- **REA、Craft Anything、Playwright MCP 的端到端验收必须在你的 Windows 机器上做**。§6 给了验收清单。在那之前，不会声称这部分「完成」。

### 0.3 关于「逆向研究和重建 Box 类 SaaS」

技术上可以做，但有合规边界，工具层会照此设计：

- 可以研究公开行为、接口形状、交互流程，然后**自己实现**；
- 不复制对方的专有代码、资源、品牌，不绕过对方的鉴权或许可证保护；
- 抓到的 Cookie、Token、密码一律不进模型上下文和日志（你提示词里的要求，也是现有记忆安全规则 §3.7 的延伸）。

---

## 1. 现状总表（有 / 缺 / 设计问题）

图例：✅ 已有且实测或有单测 · 🟡 有但不完整 · ❌ 没有

### 1.1 Agent Runtime（`Agent/AgentLoop.cs`，1101 行）

| 能力 | 状态 | 证据 / 说明 |
| --- | --- | --- |
| 多轮工具调用循环 | ✅ | `AgentLoop.RunAsync`；`LoopGuard` 防死循环；`RunBudget` 管 token 上限、无进展提醒和停止 |
| 并行工具调用 | 🟡 | `MaxParallelTools = 3`，但**只有实现了 `IParallelTool` 的工具能并行**，目前全仓只有 `AskExpertTool`（`AgentLoop.cs:800`）。只读的 MCP 工具（`readOnlyHint`）和 `read_file` 都是串行 |
| 停止任务补合法结果 | ✅ | CLAUDE.md 不变量 §3.4；`HistoryRepair.cs` |
| 上下文压缩 | ✅ | `Context/ContextManager.cs`、`SessionMemory.cs`（摘要请求带 `enable_thinking=false`） |
| 计划模式 | ✅ | `PlanMode.cs`、`update_plan` |
| 格式错误、内容审核重试 | ✅ | `LeakRetryNotice`（工具标记泄漏到正文时重试一次）、`ContentFilter` |
| 网络与 5xx 自动重试 | ❌ | `FlyknitServerClient` 遇到超时、断流、429、5xx 只给出中文报错（`:313-315`、`:1247`），**不自动退避重试**，长任务会因为一次网络抖动整轮失败 |

### 1.2 LLM Provider / DeepSeek Tool Calling

| 能力 | 状态 | 证据 / 说明 |
| --- | --- | --- |
| 场景路由（客户端只传场景名） | ✅ | 网关 `routers/gateway.py`，场景的 `extra_body` 由后台配置 |
| 读取流式 `reasoning_content` | ✅ | `Gateway/SseAccumulator.cs:90` |
| 本地保存思考内容 | ✅ | `ChatMessage.Reasoning`，`AgentLoop.cs:406` 把它写进 assistant 消息 |
| **思考内容回传给模型** | ❌ **阻断级缺陷** | `Chat/OpenAiSerializer.cs` **不序列化 `Reasoning`**，`grep reasoning_content` 在请求路径上为 0 处。DeepSeek 官方「Thinking Mode」文档写明：请求带 `tools` 时，之前的 `reasoning_content` 必须在后续请求中全部回传，否则返回 **400**。社区还报告：工具调用消息的思考为空时，字段缺失也会 400，传空字符串可以通过。后果：**一旦后台把 `agent` 场景配成 DeepSeek 思考模式，第二次模型请求就会 400，办事模式整体不可用** |
| 思考模式开关 | 🟡 | 只能在后台场景的 `extra_body` 里写死，客户端无法按任务开或关（摘要类请求已显式关闭） |

文档来源：[DeepSeek Thinking Mode](https://api-docs.deepseek.com/guides/thinking_mode/)，以及社区问题 [deepseek-harness#739](https://github.com/deepseek-ai/deepseek-harness/discussions/739)、[#1850](https://github.com/deepseek-ai/deepseek-harness/discussions/1850)、[#3857](https://github.com/deepseek-ai/deepseek-harness/discussions/3857)。注意：本容器访问不了 `api-docs.deepseek.com`，以上依据搜索结果的摘要。**实现前要在能访问的网络里复读一次原文。**

### 1.3 MCP Client（实测见 §3）

| 能力 | 状态 | 说明 |
| --- | --- | --- |
| STDIO / Streamable HTTP / 旧版 SSE | ✅ | `McpConnection.ConnectAsync`；HTTP 遇到 404/405 自动降级到 SSE |
| initialize / tools/list / tools/call | ✅ | 实测通过 |
| 动态发现（`listChanged`） | ✅ | `RefreshToolsAsync` + `ToolsChanged` 事件 |
| 多服务器同时连接 | ✅ | `McpService._runtime` 按连接器 ID 管理 |
| 状态与健康 | 🟡 | 有 `McpStatus`（Connecting/Connected/NeedsAuth/Failed/Disconnected），但**没有周期性 ping 或延迟健康检查**，进程卡住不退出时看不出来 |
| 崩溃检测 | ✅ | 实测：进程退出后马上触发 `Closed`，挂起的调用立刻失败、不会卡死 |
| 断线重连 | 🟡 | **远程**连接按 1/2/4/8/16 秒退避重连五次（`McpService.cs:559`）；**stdio 崩溃不自动拉起**，只标记为 Failed |
| 超时 | ✅ | 每个连接器有 `timeout_ms`，超时后发 `notifications/cancelled` |
| 取消 | 🟡 | 实测：员工点「停止」时本地 700ms 就返回，但**不给服务器发 `notifications/cancelled`**（`McpTransport.cs:97` 只在超时分支发），服务端进程会继续执行。对逆向工具这种长跑任务是资源泄漏 |
| 单服务器日志 | 🟡 | 只保留 stderr 尾部（`StdioTransport.StderrTail`），用在报错文案里；没有按服务器滚动保存的日志，也没有日志查看界面 |
| 并发调用 | 🟡 | 传输层实测支持（5 个 1 秒的调用共 1005ms）；但 Agent 层不并行（见 1.1） |
| Schema 转换 | ✅ | `McpTool` 把 `inputSchema` 转成 function 参数；**`outputSchema` 和 `structuredContent` 只当文本**（实测能拿到 JSON 文本） |
| 图片 / 资源内容 | ❌ | 实测：MCP 返回图片时，模型只看到 `[图片：image/png…这里无法显示]`。对 Playwright 截图、逆向界面截图是硬缺口 |
| resources / prompts | ❌ | `McpClient.cs:21` 的注释写明故意不做 |
| 权限与确认 | ✅ | `readOnlyHint` 直接执行；`destructiveHint` 每次确认、不能设成「以后自动」（不变量 §3.6） |
| 连接器来源 | ✅ | 只能来自服务端（员工不能自建）；插件连接器的环境变量隔离已在上一批完成 |

### 1.4 Skill Loader

| 能力 | 状态 | 说明 |
| --- | --- | --- |
| 多级技能根目录 | ✅ | 机器级（ProgramData）、组织、学习所得、个人（`SkillCatalog`） |
| `load_skill` / `search_skills` | ✅ | |
| 版本字段 | 🟡 | 只读取 frontmatter 的 `version`（`SkillCatalog.cs:42`），没有升级、比较、回滚 |
| **声明 MCP 依赖** | ❌ | 技能无法声明「需要 `mcp__rea__*`」；依赖缺失时不会如实报告，模型可能编造工具名 |
| 安装 / 禁用 / 更新 | 🟡 | 服务端技能库和插件包可以下发；客户端没有按技能禁用 |

### 1.5 Tool Registry / Executor / Shell / 文件

| 能力 | 状态 | 说明 |
| --- | --- | --- |
| 注册表与动态替换 | ✅ | `ToolRegistry.Replace/RemoveWhere`；`load_tools` 按需暴露（`ToolExposure.cs`） |
| 安全闸门顺序 | ✅ | `blocked_patterns` → `CommandAnalyzer` → `PermissionMode` + 工作区 → `ApprovalStore`（不变量 §3.2，有单测） |
| `run_shell` 及后台输出、停止 | ✅ | |
| 读、写、`edit_file`、删除（带备份） | ✅ | `FileBackup`、`FileChanges` |
| **内容搜索（grep）** | ❌ | `search_files` 只按文件名 glob 匹配，不搜内容，也不支持 `**` |
| 多处编辑（multi_edit） | ❌ | |
| 结构化 Git 工具 | ❌ | 只能经 `run_shell` 跑 git，风险分级按通用命令处理 |
| 工作区说明文件 | ✅ | 读取 FLYKNIT.md、AGENTS.md、CLAUDE.md；**没有 `/init` 生成** |
| 大输出落盘 | ✅ | `ToolOutputSpill` |

### 1.6 会话、任务、断点续跑

| 能力 | 状态 | 说明 |
| --- | --- | --- |
| 每步立刻存库 | ✅ | `AgentHost.LongTasks.cs`，本机 SQLite |
| 中断任务识别与「继续」 | ✅ | `ConversationStore.InterruptedRuns()`；长任务模式自动续跑有上限 |
| 子任务（专家派遣）中断恢复 | 🟡 | 进程没了就标成 `interrupted` 失败，**不续跑**（`AgentHost.Delegation.cs:98`） |
| Task ID / Step ID / 阶段 / 重试次数等结构化状态 | 🟡 | 有会话、运行记录和 `Trace` 步骤，但**没有面向「多阶段工程任务」的阶段（stage）模型**，重试次数和审批没有汇总到同一个任务视图 |

### 1.7 Human-in-the-loop 与审计

| 能力 | 状态 |
| --- | --- |
| 确认弹窗、`ask_user`、前缀规则、会话内批准、任务授权（task_grants，只追加） | ✅ |
| 审计记录工具来源（`mcp_source`） | ✅ |
| 凭证脱敏进日志 | 🟡：网关不记录密钥；**MCP 工具返回内容里的 Cookie/Token 没有统一脱敏**，浏览器抓包类工具上线前必须补 |

---

## 2. 设计问题（按严重度）

1. **P0：DeepSeek 思考模式加工具调用会 400**（1.2）。修复点集中：`OpenAiSerializer` 为 assistant 消息输出 `reasoning_content`（带工具调用时空字符串也要输出），历史修复与压缩时保留该字段。改动小，影响面是「办事模式能否用 DeepSeek 推理模型」。
2. **P1：取消不通知 MCP 服务器**（1.3）。长跑的逆向分析会在后台继续占用 CPU。
3. **P1：MCP 图片丢失**（1.3）。截图类工具结果对模型不可见。可以复用现有 `vision` 场景的图片消息通道，不新增上游直连。
4. **P1：网关调用没有退避重试**（1.1）。长任务的稳定性差。
5. **P2：只读工具不并行**。`McpTool`（`readOnlyHint=true`）和只读文件工具可以实现 `IParallelTool`。
6. **P2：stdio 连接器崩溃后不自动拉起**，也没有健康探测和按服务器的日志。
7. **P2：技能不能声明 MCP 依赖**。依赖缺失时模型可能编造工具名。
8. **P2：程序员基础工具缺位**：grep、`**` glob、multi_edit、git、`/init`。这正是你说的「第一批」。

---

## 3. 本次实测记录

### 3.1 测试方法

- 被测对象：`Flyknit.Core.Mcp.McpConnection`（真实代码，未改动）。
- 对端：自写的 Python stdio MCP 服务端，实现了 initialize、tools/list、tools/call、错误、睡眠、图片、structuredContent、主动崩溃。它是一个真实子进程，通过 stdin/stdout 走 JSON-RPC。
- 测试程序放在 scratchpad（`mcpharness/`），**没有进仓库**。
- 原计划直接跑 `@modelcontextprotocol/server-everything`，但运行刚下载的 npm 包被会话的自动权限策略拒绝，所以改用自写服务端。也因此，**第三方 MCP 包的兼容性本次没有验证**。

### 3.2 结果（全部实际运行输出）

```
[PASS] initialize + tools/list: 6 tools, 133ms, transport=stdio
[PASS] tools/call: Echo: hi
[PASS] bad args -> isError: missing message
[PASS] unknown tool -> exception: McpRpcException: Unknown tool: nope
[PASS] 5 parallel calls (1s each): 1005ms -> 10,11,12,13,14
[PASS] timeout (4s): 4007ms McpException: tools/call 超时（4 秒没有回应）
[PASS] cancel: 700ms TaskCanceledException
[PASS] connection usable after timeout/cancel: Echo: still alive
[PASS] image content: text='[图片：image/png，约 0 KB，这里无法显示]'   ← 能跑，但图片对模型不可见
[PASS] structuredContent: text='{"temp":21,"unit":"C"}'
[PASS] crash mid-call fails (not hang): McpException: python3 已退出（退出码 ?）
[PASS] Closed event fired
[PASS] call after crash fails fast: 0ms
```

附带发现：

- 服务端 stderr 只收到超时那一次的 `notifications/cancelled`，**用户取消那次没有收到**，与源码一致；
- 崩溃时退出码显示 `?`，存在读取 `HasExited` 的竞态，属于小问题。

### 3.3 现有测试套件

见 §7。

---

## 4. 目标架构（在现有基础上补，不另起炉灶）

```
                 ┌──────────── 服务端（FastAPI，不执行命令）────────────┐
                 │ 网关 /chat/completions（场景路由、配额、审计）        │
                 │ MCP 连接器与插件库（来源唯一）、技能库、安全中心       │
                 │ 「开发者模式」总开关（默认关闭，按设备或部门下发）      │
                 └──────────────▲───────────────────────────────────────┘
                                │ 设备 Token（DPAPI）
 ┌───────────────────── 员工端 Flyknit.Core ─────────────────────────────┐
 │ AgentLoop ──► ToolRegistry ──► 安全闸门（不变）──► ITool               │
 │   │  ▲                                     ├─ 文件、grep、multi_edit  │
 │   │  └─ OpenAiSerializer（补 reasoning）    ├─ git_*（新，分级）        │
 │   ▼                                        ├─ run_shell               │
 │ ContextManager（保留 reasoning）            └─ McpTool（并行、图片）     │
 │                                                  ▲                   │
 │ McpService（Client）＝ MCP Client Manager ───────┘                   │
 │   补：用户取消通知、stdio 自动拉起、ping 健康、按服务器日志            │
 │ SkillCatalog：补 requires.mcp 依赖声明与可用性报告                     │
 │ ConversationStore：补「阶段」字段，沿用本机 SQLite                     │
 └──────────────────────────────────────────────────────────────────────┘
```

不做的事：Redis、服务端执行命令、客户端直连上游模型、员工自建 MCP。

---

## 5. 分阶段实施计划

每个阶段都单独提 PR，列出改动文件、原因、新增测试、测试结果，并跑全量回归。新能力默认关闭。

### 阶段 0：阻断修复（建议最先做，单独 PR）

| 项 | 文件 | 测试 |
| --- | --- | --- |
| 回传 `reasoning_content`（带 tool_calls 时为空也输出空串） | `Chat/OpenAiSerializer.cs`、`Context/ContextManager.cs`、`Agent/HistoryRepair.cs` | Core 单测：序列化快照；压缩与修复后字段保留 |
| 网关 5xx、429、断流的有限退避重试（仅限「还没开始输出」的请求，避免重复执行） | `Gateway/FlyknitServerClient.cs` | Core 单测：模拟 HttpMessageHandler |

### 阶段 1：开发者基础工具（你说的「第一批」，GitHub 优先）

全部受 IT「开发者模式」开关控制（服务端 `security_settings` 新字段，默认关闭，经 `config_events` 下发）。

| 项 | 说明 |
| --- | --- |
| `grep`（内容搜索） | 正则、glob 过滤、上下文行、结果上限；限制在工作区内；跳过 `.git`、`node_modules`、二进制 |
| `search_files` 支持 `**` | 向后兼容 |
| `multi_edit` | 单文件多处替换，原子写入，复用 `FileBackup` |
| `git_status` / `git_diff` / `git_log` | 只读，自动执行 |
| `git_commit` / `git_branch` / `git_push` | 写操作要确认；`push --force`、`reset --hard`、`clean -fd` 定为 Destructive（永不自动）；`CommandAnalyzer` 给 `run_shell` 里的 git 同样分级 |
| GitHub | 经服务端配置的 GitHub MCP 连接器（不在客户端存 PAT）；GitLab、Gitee 以后再接 |
| `/init` | 扫描工作区，生成 FLYKNIT.md 草稿，写入前确认 |
| `code` 场景 | 网关新增场景，后台可选专用模型 |

测试：Core 单测覆盖每个工具和 git 风险分级（安全改动必须补测）；服务端测开关；web 执行 `npm run build`（含三语检查）。

### 阶段 2：MCP Client Manager 补全

用户取消时发 `notifications/cancelled`；stdio 崩溃后按退避自动拉起（有次数上限）；周期 `ping` 健康检查和延迟；按服务器滚动日志（落在 `%APPDATA%\Flyknit\logs\mcp\<id>.log`，日志先脱敏）；`McpTool` 对 `readOnlyHint` 实现 `IParallelTool`；图片内容经 `vision` 场景回给模型；结构化内容保留 JSON。

测试：§3 的测试程序改写成正式的 Core 集成测试，包括自写服务端（在 Linux CI 可跑）。

### 阶段 3：Skill 与 MCP 协同

`SKILL.md` frontmatter 增加 `requires: { mcp: [rea, craft] , tools: [...] }`。`load_skill` 时检查依赖，缺什么就如实写进结果（「需要的连接器 rea 未连接」），不让模型猜工具名。技能支持禁用和版本比较。

### 阶段 4：多阶段工程任务与断点续跑

在 `ConversationStore` 的运行记录上增加阶段、步骤、重试、审批汇总（只加列，带默认值）；任务面板展示；续跑时从最后一个完成的步骤继续。不引入 Redis。

### 阶段 5：逆向与重建工作流（需要 Windows 实机）

REA、Craft Anything、Playwright MCP 由 IT 在后台配置成连接器。逆向技能包声明依赖。浏览器抓包结果统一脱敏（Cookie、Authorization、Set-Cookie、密码字段），再进入上下文。

---

## 6. Windows 实机验收清单（阶段 2 和 5 的完成标准）

1. 后台配置 REA、Craft Anything、Playwright 三个 stdio 连接器，员工端显示 Connected，工具名为 `mcp__<id>__<tool>`，与真实 `tools/list` 一致；
2. 每个连接器跑一次只读调用和一次需确认的调用，审计里有 `mcp_source`；
3. 跑一个长分析任务时点「停止」，确认服务端进程收到取消、CPU 回落；
4. 用任务管理器杀掉连接器进程，确认自动拉起并且下一次调用成功；
5. Playwright 截图结果能被模型「看到」并描述；
6. 抓包结果里的 Cookie、Token 在上下文和日志中都是 `***`；
7. agent 场景配成 DeepSeek 思考模式，连续 5 轮以上工具调用无 400。

这 7 项在你的机器上跑通之前，相关阶段只算「代码完成、待实机验收」。

---

## 7. 现有测试基线

本次在 Linux 容器里实际运行的结果：

| 套件 | 命令 | 结果 |
| --- | --- | --- |
| Core 单测 | `dotnet test client/tests/Flyknit.Core.Tests` | **901 通过 / 0 失败** |
| 服务端 | `cd server && pytest -q` | **493 通过 / 3 跳过 / 0 失败** |
| WPF 宿主（Flyknit.Client） | — | **未运行**：Linux 没有 WindowsDesktop SDK |
| web / admin 构建 | — | 本次未改前端，未运行 |

后续每个阶段的 PR 都以这两个数字为回归基线。

---

## 8. 实施进度

| 阶段 | 状态 | 说明 |
| --- | --- | --- |
| 阶段 0 | 代码完成，Linux 单测通过 | 思考内容回传按数据决定（不按厂商写死）；服务端按模型配置「思考开关写法」（enable_thinking / thinking / chat_template_kwargs / 不传）和「思考回传」（自动 / 不回传）；网关请求在没收到任何回答前对连不上、超时、502/503/504、非额度 429 退避重试 2 次 |
| 阶段 1 | 代码完成，Linux 单测通过，待 Windows 实机验证 | 安全中心「开发者工具」（默认关）；grep、search_files 支持 `**`、multi_edit、git_status / git_diff / git_log / git_commit；git 与 gh 命令按子命令 + 关键参数定级；`/init` 生成 FLYKNIT.md；`code` 场景 |
