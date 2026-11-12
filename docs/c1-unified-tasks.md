# 方案：VNext 第 1 期——统一任务模型（backlog C1）

> 2026-11-13 草案，待确认后开发。总体设计见 `docs/architecture-vnext.md`（第 4、6、9 节）。
> 本期只在**员工端本机**做，服务端只多一列审计字段（可选，见第 7 节）。

## 1. 要解决的问题

现在「一件要办的事」散在好几个地方，各管各的：

| 入口 | 现在怎么开始 | 记在哪 |
| --- | --- | --- |
| 员工在聊天窗口发话 | `AgentHost.Send` | `conversations.run_started_at / run_origin='ui'`、`run_state` 断点 |
| 钉钉 / 企业微信 / 手机 | `ChannelRelay` → `RunChannelMessageAsync` | 上面那两列（`remote`）+ `task_grants` + 内存里的 `_remoteRuns` |
| 指令中心（IT 指令） | `InstructionPoller` → `RunInstructionAsync` | `task_grants`（`instruction`） |
| 定时任务 / 文件夹监视 | `ScheduleRunner` → `Send` | `scheduled_tasks` 的上次结果 + `task_grants`（`schedule` / `event`） |
| 事件触发 | 通道（`event`） | `task_grants` |
| 盯 PR 自动修复 | `AgentHost.PullRequests` → `Send` | `pr_watches.auto_fixes`，**没有授权记录** |
| 中断后续跑、长任务自动续跑 | `ResumeInterrupted`、`QueueAutoContinue` | 内存计数 + `run_state` |

后果：
- **任务中心看不全**。现在按对话列「正在跑 / 上次没跑完 / 最近跑完」，分不清是谁发起的、按什么授权在跑、一共用了多少 token。
- **没法按「任务」停**。只能一个对话一个对话地停。
- **后面几期没有地基**。Watchdog（C3）要按任务看有没有进展；Router（C2）要把事件归到任务、按任务汇总确认。现在都没有「任务」这个东西可挂。

## 2. 原则

1. **只加不改执行路径**。`AgentLoop`、权限闸门、确认、授权封顶全部不动。新加的任务账本挂在现有的「开始跑 / 停下」节点上，记账失败不影响任务执行。
2. **老字段照写**。`run_started_at`、`run_origin`、`run_state` 继续写；降级到老版本时，中断续跑照样能用。
3. **本机库只加不删**（加表、加列）。
4. **安全不变**。不是员工本人发起的任务照旧先有 `TaskGrant` 才跑；任务账本只记录，不放宽任何东西。

## 3. 概念落地

| 概念 | 本期定义 |
| --- | --- |
| **Task** | 一件要办成的事。外部来的（手机 / 钉钉 / 企业微信 / 指令 / 定时 / 文件夹 / 事件 / PR 自动修复）：**一次触发 = 一个 Task**。员工自己在窗口里说话：**从开始跑到停下来的一串 = 一个 Task**，包括中途排队的话、插话、长任务自动续跑、中断后续跑。对话空闲后再发话，就是新的 Task。 |
| **Run** | Task 里的一次 `AgentLoop` 运行。第一轮、续跑、自动续跑、中断恢复，各是一个 Run。 |
| **Thread** | 就是现在的对话（`conversations`），不改。 |
| **订阅** | 对话订阅某类事件。本期只有一种：**盯着的 PR**。 |
| **TriggerEvent** | 各入口统一交给任务账本的那份信息：来源、引用、发起人、对话、授权。 |

**状态机**（Task）：

```
pending ──开始──► running ◄──► waiting_confirm
   │                 │  ╲
   │                 │   ╲ 程序被关
   │                 ▼    ▼
   └──取消──► stopped  interrupted ──续跑──► running
                ▲        │
                └─放弃───┘
running ──► done（正常结束） / failed（出错、预算用完、原地打转） / stopped（员工或 IT 停的）
```

迁移规则写成纯函数（`TaskStateMachine.Next(state, event)`）。不允许的迁移只记日志、不抛异常，记账不能挡住任务。

## 4. 数据（本机 SQLite，只加）

```sql
CREATE TABLE tasks (
  id TEXT PRIMARY KEY,               -- t + guid
  conversation_id TEXT NOT NULL,
  origin TEXT NOT NULL,              -- ui / mobile / dingtalk / wecom / instruction / schedule / folder / event / pr
  reference TEXT NOT NULL DEFAULT '',-- 定时任务 ID、指令运行号、通道消息号、PR 盯点 ID……
  title TEXT NOT NULL DEFAULT '',
  requested_by TEXT NOT NULL DEFAULT '',  -- 「指令中心」「钉钉：张三」「定时任务：日报」
  grant_id TEXT NULL,                -- task_grants.id（员工本人发起的为空）
  status TEXT NOT NULL,
  token_budget INTEGER NOT NULL DEFAULT 0,   -- 0 = 不限
  tokens_used INTEGER NOT NULL DEFAULT 0,
  runs INTEGER NOT NULL DEFAULT 0,
  stop_reason TEXT NOT NULL DEFAULT '',
  created_at TEXT NOT NULL, started_at TEXT NULL, finished_at TEXT NULL
);
CREATE TABLE runs (
  id TEXT PRIMARY KEY,               -- 沿用现在 ToolContext.RunId
  task_id TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
  kind TEXT NOT NULL,                -- first / queued / continue / auto_continue / resume / autofix
  answer_id TEXT NULL,               -- 这轮最后一条回答（关联 run_stats.message_id）
  status TEXT NOT NULL, stop_reason TEXT NOT NULL DEFAULT '',
  prompt_tokens INTEGER NOT NULL DEFAULT 0, completion_tokens INTEGER NOT NULL DEFAULT 0,
  started_at TEXT NOT NULL, finished_at TEXT NULL
);
CREATE TABLE thread_subscriptions (
  id TEXT PRIMARY KEY,
  conversation_id TEXT NOT NULL,
  kind TEXT NOT NULL,                -- 本期只有 pr
  selector TEXT NOT NULL,            -- JSON：{repo, number}；对应 pr_watches.id
  created_at TEXT NOT NULL, ended_at TEXT NULL, ended_reason TEXT NOT NULL DEFAULT ''
);
```

- 对话删除（进回收站再彻底删）时，任务和订阅跟着删。
- `run_stats` 不动，`runs.answer_id` 关联它。
- **老数据回填**：启动时把 `conversations.run_started_at` 还有值（上次没跑完）、却没有对应任务的，补成 `interrupted` 状态的任务。这样任务中心一升级就能看到它们。只做一次，幂等。

## 5. 各入口怎么接（改动都很小）

在 `AgentHost` 加一个 `Tasks` 服务（`AgentHost.Tasks.cs`，partial），提供 `Begin(TriggerEvent)`、`RunStarted / RunEnded`、`Waiting / Resumed`、`End`。各入口只在已有的节点上多调一次：

| 入口 | 挂在哪 |
| --- | --- |
| 员工发话 | `Send` 里已有的 `MarkRunStarted` 处：对话空闲 → `Begin(ui)`；已有进行中的任务（排队、插话） → 只开新 Run |
| 钉钉 / 企业微信 / 手机 / 事件 | `RunChannelMessageAsync` 里 `IssueGrantAsync` 之后：`Begin(origin, grant)` |
| 指令中心 | `RunInstructionAsync` 里 `IssueGrantAsync` 之后 |
| 定时 / 文件夹 | `ScheduleRunner` 里 `IssueGrant` 之后 |
| PR 自动修复 | 发 `FixPrompt` 之前 `Begin(pr)`。同时**补上授权记录**（origin `pr`，权限上限 = 对话当前权限），和其他非员工发起的任务一致 |
| 自动续跑 / 中断续跑 | 同一个 Task 下开新 Run（`auto_continue` / `resume`） |
| 等确认 | 现有的确认回调（Inbox / Confirm）进出时标 `waiting_confirm` / `running` |
| 结束 | 现有的 `MarkRunEnded` 处：按 `StopReason` 落到 done / failed / stopped |

`TaskGrant.Origins` 加 `pr`、`folder`（文件夹监视现在借用 `event`，分开记更清楚）。服务端的授权上报接口要同时认这两个值（第 7 节）。

## 6. 任务中心

- **新桥接接口** `tasks.list`：返回进行中 / 等确认 / 中断 / 最近结束的任务。每条带上：来源、发起人、授权（权限上限、工作区）、Run 数、token 用量（/ 预算）、计划进度（沿用 `RunSnapshots` 的步数）、所属对话。
- **新桥接接口** `tasks.stop`：按任务停止。进行中的走现有 `Stop(conversationId)`，排队中的直接取消，中断的标为 stopped。
- **界面**：任务中心改成按任务列。
  - 每张卡片：来源图标和名称、标题、发起人、状态、进度、token 用量；可以「停止」，也可以「打开对话」。
  - 筛选：全部 / 进行中 / 等确认 / 中断 / 已结束；按来源筛。
  - 原来的「上次没跑完 → 续跑 / 放弃」保留，改成挂在任务上。
- **订阅**：对话的「会话信息」里列出这个对话订阅了哪些 PR，可以取消（等于停止盯这个 PR）。
- 三语文案。

## 7. 预算和审计

- **Task 级 token 预算**：安全中心加一项「单个任务的 token 上限（万）」。
  - **默认 0 = 不限**，所以现在的行为不变；可以按部门 / 单台电脑设置。
  - 一个任务的所有 Run 累计用量超过上限时，下一个 Run 不再开，任务落到 failed，原因写「任务额度用完」。
  - 单次运行的上限（现在的「单次任务 token 上限」）照旧生效。
- **审计带任务编号**：工具调用的审计记录多一个 `task_id`（员工端 `AuditEntry` + 服务端 `audit_logs` 加一列，只加不删、默认空串），后台审计列表能按任务筛。
  - 这是本期唯一的服务端改动，也可以推到 C2 再做（见第 10 节问题 3）。

## 8. 不做的

- Router（C2）、Watchdog（C3）、并行写（C4）。
- 命令行 / IDE 会话进任务账本：它们有自己的会话库，C2 再统一。
- 手机端按任务看进度（`architecture-vnext.md` 第 10 节，第 5 期）。
- 专家子任务（`subtasks`）、子代理：它们是某个 Run 里的步骤，不是独立的 Task，保持原样。

## 9. 测试和验收

**单元测试（Core）**：
- 状态机：每条合法迁移；不合法的迁移不抛异常、状态不变。
- 任务库：建任务、开 Run、累计 token、结束；对话删除后跟着删；老数据回填幂等。
- 预算：默认不限；设了上限，累计超过就不再开新 Run。
- 授权：`pr`、`folder` 进 `TaskGrant.Origins`；PR 自动修复的授权权限不超过对话当前权限。
- 粒度：员工排队的话、插话、自动续跑都算同一个任务；空闲后再说话算新任务。

**服务端**（如果做第 7 节的审计列）：`task_id` 写入和筛选、迁移测试；授权上报认 `pr` / `folder`。

**界面**：web 构建（类型 + 三语）。Chromium + 模拟宿主：任务中心按任务列、筛选、停止、打开对话、订阅列表和取消。

**验收（Windows 实机）**：分别从下面几个入口触发一次：
- 定时任务
- 手机发的任务
- 指令中心
- 盯 PR 的自动修复
- 员工自己说话

在任务中心都能看到来源、授权和进度，都能在任务中心停止；关掉程序再打开，没做完的任务显示为「中断」，可以续跑。

## 10. 需要你确认的

1. **员工自己说话怎么算任务**：按「从开始跑到停下来的一串」算一个（推荐），还是每句话一个？后者任务中心会很碎。
2. **任务级 token 上限**：本期就加（安全中心新项，默认不限），还是留到 C3 Watchdog 一起做？
3. **审计带任务编号**：本期就做（服务端加一列），还是留到 C2？
4. **PR 自动修复补授权记录**：本期补上（推荐，和别的非员工发起任务一致），还是先只记任务？

## 11. 估算和拆分

| 步骤 | 内容 | 估算 |
| --- | --- | --- |
| C1a | 表、状态机、任务库、回填 + 单测 | 0.5 天 |
| C1b | 各入口接上（含 PR 自动修复补授权） | 1 天 |
| C1c | 订阅表（PR）+ 会话信息里的订阅列表 | 0.5 天 |
| C1d | 任务中心改版 + `tasks.list` / `tasks.stop` | 1 天 |
| C1e | 任务级预算 + 审计 task_id（看第 10 节 2、3 的决定） | 0.5 天 |
| **合计** | | **约 3～3.5 天**（backlog 原估 2～3 天，多的是任务中心改版和回填） |

每一步单独 commit，同一个 PR。
