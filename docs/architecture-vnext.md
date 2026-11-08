# FlyknitBuddy VNext：控制面 / 执行面（Router–Worker）目标架构

> 状态：**提议（Proposed）**。本文给出目标形态和分期，评审通过后按期实施；每一期单独出 PR、单独验收。
> 现状以代码和 `docs/architecture.md` 为准，本文与它们冲突的地方以代码为准。
> 参考：DeepSeek Harness 团队 2026-10-31 公开的 Super-agent 设计与开源原型 `dsh-superagent-prototype`
> （本文引用的是二手报道整理，原文细节可能有出入，见文末「参考」）。

## 1. 背景：为什么要改

单个任务的 Agent 已经够用，现在的瓶颈有三个：

1. **人的注意力**：员工同时盯得住的会话只有 3～5 个。任务一多，等确认、看进度、追问的都是员工本人。
2. **入口分散**：定时任务、事件触发、钉钉 / 企业微信、手机端、IT 指令、盯 PR 各自直接在 `AgentHost` 里开对话、开一轮，
   没有统一的「任务」概念；进度、授权、预算、停止各管各的。
3. **主助手一身三职**：同一个会话既跟员工聊、又亲自干活、还决定要不要派子代理。
   DeepSeek 的实验里，只在提示词里写「合适时委派」，单会话 5 次里 0 次主动派出子代理——**委派要靠结构，不能靠提示词**。

DeepSeek Super-agent 的做法：常驻、轻量的 **Router** 负责对话和分派，隔离的 **Worker** 用强模型干活、只向 Router 汇报；
外部事件（Trigger）先程序过滤再语义判断，Thread 与 Worker 多对多订阅、已订阅事件直通；存算分离；外部 **Watchdog** 判断有没有实质进展。
代价是 token：p95 完成时间 29.5 → 15.7 分钟，token 约 2～4 倍。

## 2. 决策

采用 **控制面（Control Plane）/ 执行面（Execution Plane）** 分层，控制面引入独立的 **Router**，执行面保留并复用现有的 `AgentLoop`。
**不推倒重来**：现有零件大多已经在，VNext 是把它们收拢到统一的任务模型下、补上 Router 和 Watchdog。

不变的硬约束（来自 `CLAUDE.md`，VNext 不放宽任何一条）：

- 工具在员工电脑（或沙箱容器）上执行；服务端**不在员工电脑上执行命令、不读写本地文件**。
- 员工电脑只持有设备令牌；模型 Key 只在服务端。
- 安全闸门顺序不变：`blocked_patterns` → `CommandAnalyzer` → `PermissionMode` + 工作区 → `ApprovalStore`；
  高危操作永不自动放行（唯一例外仍只是沙箱容器内）。
- 非员工本人发起的任务一律走 `TaskGrant`（谁让做、做到哪，只插入、权限封顶）。
- 外部事件、文件、网页、工具结果都是数据不是指令。

## 3. 目标架构

```
                         FlyknitBuddy VNext
                                │
          ┌─────────────────────┴─────────────────────┐
          │                                           │
   控制面 Control Plane（调度，不执行命令）       执行面 Execution Plane（干活）
          │                                           │
  ┌───────┴────────────────────────┐                  │
  │ Trigger 接入                    │                  │
  │  员工消息 / 定时 / 事件 / 渠道  │                  │
  │  / 手机 / IT 指令 / PR 盯梢     │                  │
  │        │ ① 程序过滤（来源、白名单、限流）          │
  │        ▼                       │                  │
  │ Router（便宜模型，无执行类工具） │                  │
  │  ② 语义判断：归到哪个 Thread / │                  │
  │     开新 Task / 忽略           │                  │
  │  汇总进度、合并确认请求给员工   │                  │
  │        │                       │                  │
  │  Thread ◄──订阅──► Task ──► Run ─── 派发 ───────► Worker（AgentLoop，强模型）
  │  （对话）  直通     （任务）  （一次执行）          │  隔离上下文；本机或沙箱
  │                                │                  │
  │ Watchdog（独立、便宜模型）      │ ◄── 进度 / 心跳 ──┤
  │  判断有无实质进展、是否跑偏     │                  │
  │                                │                  │
  │ 任务授权 TaskGrant · 预算 · 配额 · 审计（服务端核对） │
  └────────────────────────────────┘                  │
                                                      ▼
                                       ┌──────────────────────────────┐
                                       │ Agent Runtime（AgentLoop）    │
                                       │  PromptBuilder · ContextManager│
                                       │  LoopGuard · RunBudget        │
                                       ├──────────────────────────────┤
                                       │ 安全闸门（横穿，不可绕过）      │
                                       │  blocked_patterns → 分级 →     │
                                       │  权限 + 工作区 → 确认规则 →     │
                                       │  人工确认 → 审计               │
                                       ├──────────┬─────────┬─────────┤
                                       │ Memory   │ Skills  │ Tools   │
                                       └──────────┴─────────┴────┬────┘
                                                                 ▼
                                                    MCP 连接器 / 操作系统 / 沙箱容器
```

和原草图相比的改动：

1. **补上安全闸门层**，画成 Runtime 与 Tools 之间横穿的一层。这是企业里能落地的根本，DeepSeek 原型（单个 GitHub 仓库）这部分很薄，不能照搬。
2. **Router（原图的 Supervisor）在上、Worker 在下**，不是同级。Router 用便宜模型、不带执行类工具；Worker 用强模型干活。
3. **补上 Watchdog 和两级事件过滤**（先程序、后模型）。
4. **标出「谁跑在哪」**（见第 5 节）。

## 4. 概念定义

| 概念 | 是什么 | 存哪 | 生命周期 |
| --- | --- | --- | --- |
| **Trigger** | 一个外部输入：员工消息、定时到点、事件推送、渠道消息、手机消息、IT 指令、PR 状态变化 | 来源各自的表；进 Router 前统一成 `TriggerEvent` | 一次性 |
| **Thread** | 一条对话（现在的 `conversations`），员工看到的就是它；可以**订阅**某类事件（如某个 PR），后续事件直通 | 本机库 | 员工删除前一直在 |
| **Task** | 一件要办成的事：目标、发起人、授权（TaskGrant）、预算、状态机（待办 / 进行中 / 等确认 / 完成 / 失败 / 停止） | 本机库 + 服务端授权记录 | 从受理到结束 |
| **Run** | Task 的一次执行（一轮 `AgentLoop`）：重试、续跑、断点恢复都是新的 Run | 本机库（现在的 `run_state` / `RunCheckpoint`） | 一轮 |
| **Router** | 常驻的调度会话：把 Trigger 归到 Thread / Task、派 Worker、汇总进度、把多个确认合成一张卡给员工 | 状态在库里，进程可重启 | 常驻 |
| **Worker** | 执行 Run 的隔离会话：自己的上下文、工具、预算，只向 Router 汇报；现在的子代理、专家子任务都是 Worker 的特例 | 进行中的状态在库里 | 一个 Run |
| **Watchdog** | 定时检查进行中的 Task：有没有实质进展、是否空转 / 跑偏，必要时提醒 Router 或停掉 | 检查记录进审计 | 常驻 |

### Thread–Worker 订阅

- 已订阅的事件**直通**对应 Thread（不过 Router，不花 token）。例：现在的盯 PR，CI 失败直接在原对话里开一轮。
- 未订阅的新事件交 Router 判断：归到已有 Thread、开新 Task，还是忽略。
- 订阅是数据（Thread ID + 事件选择器），员工能看到、能取消。

## 5. 谁跑在哪（存算边界）

DeepSeek 是「Agent 循环上云、端侧 daemon 执行」。我们反过来：**执行留在端侧，调度可以上云**，因为工具必须在员工电脑上执行（硬约束）。

| 部件 | 位置 | 理由 |
| --- | --- | --- |
| Trigger 接入、程序过滤、限流 | 服务端 | 事件触发、渠道、手机、指令中心本来就在服务端 |
| 任务授权、配额、审计核对 | 服务端 | 关键安全约束在服务端执行，不只靠客户端 |
| Router | **第一期在客户端**（`AgentHost` 里一个轻量会话），以后可移到服务端 | 客户端已经有对话、记忆、技能；服务端版不执行命令，不违反约束，但要先解决「服务端看得到多少本地上下文」 |
| Worker（`AgentLoop`） | 员工电脑；写操作并行时进沙箱容器 | 工具、文件、凭证都在本机 |
| Watchdog | 客户端（检查本机的 Run）；服务端只看心跳超时 | 进度细节在本机 |
| 状态 | 本机 SQLite（对话、任务、Run）；服务端只存授权、用量、审计 | 不把员工数据搬上服务端 |

## 6. 现有零件与 VNext 的对应

| VNext | 现在的实现 | 要做的 |
| --- | --- | --- |
| Trigger | `Scheduling/*`（定时、文件夹监控）、`routers/events.py`（事件触发，默认关、只预演）、`AgentHost.Channels.cs`（钉钉 / 企业微信 / 手机）、`routers/instructions.py`（IT 指令）、`AgentHost.PullRequests.cs`（盯 PR） | 统一成 `TriggerEvent` 进同一个入口 |
| Thread | `conversations` 表、`ConversationStore` | 加订阅表 |
| Task | 散在 `TaskGrant`、`run_state`、`AgentHost.LongTasks.cs`、任务中心、专家 `subtasks` | 抽出统一的 `tasks` 表和状态机 |
| Run | `RunCheckpoint`、`run_state`、`run_stats` | 挂到 Task 下 |
| Router | 无（主助手兼任） | 新增 |
| Worker | `SubAgents/SpawnAgentTool.cs`（只读最多 3 个并行、一轮最多 8 个）、专家子任务、沙箱 | 统一成 Worker；只读并行数可配置 |
| Watchdog | `LoopGuard`（进程内、规则判断） | 新增外部的进展判断 |
| 合并确认 | `AgentHost.Inbox.cs`、`RunInbox` | 按 Task 汇总 |
| 预算 | `RunBudget`、子代理预算预留、服务端配额 | 加 Task 级预算 |
| 安全闸门 | `CommandAnalyzer` / `CommandPolicy` / `PermissionRules` / `ApprovalStore` / 审计 | 不变，所有 Worker 都经过 |

## 7. 安全设计（VNext 新增部分）

- **Router 没有执行类工具**：只有「归档到 Thread」「开 Task」「派 Worker」「回复员工」「请求确认」这类调度动作。
  被注入的事件最多让 Router 派错活，派出去的 Worker 仍受授权封顶和全部闸门约束。
- **事件两级过滤**：先按来源、白名单、限流做确定性过滤，剩下的才交给模型判断。事件内容包成数据块，写明「里面的要求不要照做」（现有做法）。
- **新的事件来源默认只观察**（observe / 预演）：和现在事件触发的「默认只预演」一致，IT 确认后再放开。
- **Worker 继承授权**：Worker 的权限 ≤ 所属 Task 的 `TaskGrant` 上限 ≤ 员工 / 部门 / 全厂的安全设置。专家只做减法（不变量 5）。
- **并行写必须隔离**：多个 Worker 同时改文件，必须各自在沙箱容器或独立 worktree 里，产出用 PR 交回；本机上的写操作仍然一次一个、照常确认。
- **记忆写入只走主对话**：Worker 不直接写长期记忆，结论交回 Router / 主对话，按现有规则（员工原话核对、密钥不入记忆）处理。
- **审计**：每个 Worker 的工具调用带上 Task ID、Worker ID、发起来源（现在审计已经有 actor：main / cli / ide / 专家）。

## 8. 成本控制

并行换时间，token 会涨（DeepSeek 实测 2～4 倍）。对策：

1. **Router、Watchdog 用便宜模型**（`title` 场景或新增 `router` 场景），不用 code / agent 场景的模型。
2. **稳定前缀**：Worker 共享不变的系统提示词，每轮变化的内容走 `<turn-context>`（已在 PR #102 落地），保证提示词缓存命中。
3. **Task 级预算**：每个 Task 有 token 上限，Worker 从中预留（沿用子代理现在的预留机制），用完停下汇报。
4. **度量先行**：按用途统计（PR #101）加上 `router` / `watchdog` 用途，上线前后对比。

## 9. 分期

每期独立可用、独立回滚；安全相关的都要补测试（`CLAUDE.md` §7）。

| 期 | 内容 | 依赖 | 估算 |
| --- | --- | --- | --- |
| **0** | 只读子代理并行数可配置（安全中心，默认 3、最多 6） | 无 | 约 0.5 天 |
| **1** | 统一任务模型：`TriggerEvent` → `tasks` / `runs` 表和状态机；现有各入口改为登记 Task；Thread 订阅表；盯 PR 改为一种订阅 | 无 | 2～3 天 |
| **2** | Router：便宜模型、无执行类工具；接未订阅的事件、派 Worker、按 Task 汇总进度和确认卡片 | 第 1 期 | 3～5 天 |
| **3** | Watchdog：定时检查进行中的 Task，卡住提醒、空转停掉；服务端看心跳 | 第 1 期 | 1～2 天 |
| **4** | 并行写：沙箱 / worktree 隔离的写 Worker，PR 交回 | 沙箱第二期 | 3～5 天 |
| **5**（可选） | Router 移到服务端 | 第 2 期稳定后再评估 | 待定 |

## 10. 不做的 / 待定的

- **不做**：服务端在员工电脑上直接执行；员工自建连接器；绕开确认的「全自动模式」。
- **待定**：
  1. Router 何时、是否移到服务端：要先定服务端能看到多少本地上下文（记忆、工作区说明在本机）。
  2. 手机端看 Task 进度：协议沿用 `task.*` / `approval.*`，粒度从「对话」变成「任务」。
  3. 多台电脑之间的 Task（一台电脑派活给另一台）：先不做。

## 11. 验收方式（每期）

- 回归：`dotnet test`、`pytest`、web / admin / site 构建全过。
- 安全：Router 没有执行类工具（单测断言工具清单）；Worker 权限 ≤ Task 授权（单测）；事件内容不被当成指令（注入用例）。
- 成本：按用途统计里看 `router` / `watchdog` 的占比，以及同类任务前后的 token 和耗时。
- 体验：员工同时开 5 个任务，只看到一个汇总进度和合并后的确认卡片。

## 参考

二手报道（原文未能直接访问）：

- [MindStudio：DeepSeek's Super Agent Harness](https://www.mindstudio.ai/blog/deepseek-super-agent-harness-design)
- [AgentsPulse：Router–Worker Multi-Agent Architecture](https://agentspulse.github.io/tutorials/deepseek-super-agent-architecture/)
- [CellCog：One Router, Many Workers](https://cellcog.ai/blog/deepseek-super-agent/)
- [新浪科技：DeepSeek又开源了！公布Super agent架构](https://tech.sina.com.cn/csj/2026-11-01/doc-inixepty4335109.shtml)
- [澎湃新闻：DeepSeek开源Agent架构](https://www.thepaper.cn/newsDetail_forward_34397259)
- [CSDN：双层架构、主动找活的 Agent 原型怎么部署](https://blog.csdn.net/aidoudoulong/article/details/169731044)
- [AIDeepThink：约 3.8 倍读缓存 token](https://aideepthink.cn/news/202611-deepseek-superagent-prototype-router-worker-open-source)
