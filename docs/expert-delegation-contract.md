# 专家派遣：接口约定

主助手（主 agent）把子任务派给专家执行。员工端负责执行，服务端负责授权、计数、预算硬上限和审计。
本文是员工端、服务端、界面三方共用的约定，改动时三方一起改。

## 1. 设置项

`server/app/services/security_settings.py` 新增：

| key | 类型 | 默认 | 说明 |
|---|---|---|---|
| `expert_delegation` | bool | false（锁定，员工不能改） | 允许主助手派遣专家 |

员工端常量 `SecuritySettings.ExpertDelegation = "expert_delegation"`，读取时 fallback 为 **false**。

## 2. 服务端：子任务登记（设备鉴权）

### `POST /api/v1/client/subtasks`

请求：

```json
{
  "run_id": "主任务运行 ID（≤64）",
  "conversation_id": "对话 ID",
  "expert_id": "专家 ID",
  "expert_version": 3,
  "task": "任务说明（≤20000 字）",
  "token_cap": 400000,
  "timeout_seconds": 900
}
```

校验（任一失败即拒绝，**拒绝不计入次数**）：

| 情况 | HTTP | `detail.code` |
|---|---|---|
| 该设备生效的 `expert_delegation` 为关 | 403 | `delegation_disabled` |
| 专家不存在 / 未发布 / `allow_delegate=false` | 403 | `expert_unavailable` |
| 同一设备同一 `run_id` 已登记 3 次 | 409 | `delegation_limit` |

- 次数按「登记成功」计：失败、超时、取消的派遣都算一次；重试就是一次新的登记。
- 并发安全：`(device_id, run_id, seq)` 唯一约束，`seq` = 已有数量 + 1，冲突时重算，超过 3 返回 409。
- `token_cap` 夹到 [1000, 400000]，`timeout_seconds` 夹到 [60, 900]。

响应 201：

```json
{
  "id": "st_<32 位十六进制>",
  "seq": 1,
  "status": "running",
  "expert": { "id": "...", "version": 3, "published_version": 3, "name": "MES 生产分析专家" },
  "token_cap": 400000,
  "deadline_at": "2026-10-12T10:00:00Z"
}
```

### `POST /api/v1/client/subtasks/{id}/finish`

请求：

```json
{
  "status": "completed | partial | needs_input | failed | cancelled | timed_out",
  "summary": "≤2000 字",
  "result": { "...": "交卷全文，序列化后 ≤64KB，超出截断并标记" },
  "error_code": "",
  "error": "",
  "tool_calls": 7,
  "local_tokens": 12345
}
```

- 只有 `running` 的子任务能被结束：`UPDATE ... WHERE status='running'` 原子完成。
- 已经是终态（含服务端因超时、关闭开关而终止的）时**不覆盖**，返回 200 和已保存的状态，`already_final: true`。

### `POST /api/v1/client/subtasks/{id}/cancel`

请求 `{ "reason": "user | disabled | parent | timeout" }`。`running` → `cancelled`（`reason=timeout` 时 → `timed_out`）。终态不变。

### `GET /api/v1/client/subtasks/{id}`

返回当前状态。

以上三个接口统一返回：

```json
{
  "id": "st_...",
  "status": "running",
  "final": false,
  "already_final": false,
  "tokens": { "prompt": 0, "completion": 0, "estimated_calls": 0 },
  "error_code": ""
}
```

## 2.1 专家字段：`helper`

后台专家、`GET /api/v1/client/experts` 都带 `helper: bool`（默认 false，「通用辅助」）。员工端：

- 候选名单：`helper=true` 且允许派遣的专家总在名单里（最多 5 位），不占按相关度取的前 5 名额；
- 主助手提示词里标 `[通用辅助]`，只读专家标 `[只读]`。

## 2.2 只读专家：并行、免确认（员工端）

- 主助手同一轮里连续几个 `ask_expert`、派的都是 `read_only` 专家时，同时执行（一次最多 3 个，`AgentLoop.MaxParallelTools`）。
  判定和登记按顺序，执行一起跑，结果按调用顺序写回；中间夹着非只读的就按顺序一个一个来。
- 每次登记仍各算一次；同一个主任务最多 3 次的上限照旧由服务端保证（并发登记时第 4 个返回 409）。
- 预算：每位专家从主任务剩余预算里**原子地**占一份（`RunBudget.TryReserve`），同时跑的几位加起来不超过「剩余 − 留给主助手的 3 万」，跑完放回、按实际用量计入。
- 只读专家的工具需要确认时不弹卡片，直接按「被安全策略阻止」返回给专家（员工设过「总是允许」的除外）。专家应写进 `open_questions` 交回。
- 事件照旧按 `callId` 区分，几张专家卡片可能同时在跑。

## 3. 服务端：模型网关强制

`POST /api/v1/chat/completions` 请求体可以带 `flyknit_subtask_id`（转发上游前删掉）。

带了子任务 ID 时：

| 情况 | 处理 |
|---|---|
| 不是本设备的子任务 | 403，`error.type = "subtask_unknown"` |
| 状态不是 `running` | 409，`error.type = "subtask_closed"` |
| 已过 `deadline_at` | 先置为 `timed_out`，再 409 `subtask_closed` |
| 该设备 `expert_delegation` 已关闭 | 先置为 `cancelled`（`error_code=disabled`），再 409 `subtask_closed` |
| 已用 token ≥ `token_cap` | 429，`error.type = "subtask_budget"` |

- 子任务请求的 `tools` 里删掉名为 `ask_expert` 的工具（专家不能再派遣）。
- 响应结束后把用量累加到子任务。上游没返回用量（prompt 和 completion 都是 0）时**不能当 0**：按字符估算（提示约 3 字符/token，回答约 2 字符/token），并给 `estimated_calls` 加 1。

不带子任务 ID、且该设备 `expert_delegation` 为关时：从 `tools` 里删掉 `ask_expert`。

## 4. 审计

`POST /api/v1/audit` 的条目新增两个可选字段，`audit_logs` 表加同名列：

- `subtask_id`：专家执行时的子任务 ID，主助手自己的调用为空；
- `actor`：`"main"` 或 `"expert:<专家 ID>"`。

`summary` 仍带 `[专家名]` 前缀，只用于展示。

## 5. 后台

- `GET /api/v1/admin/experts/delegation` → `{ "enabled": false }`
- `PUT /api/v1/admin/experts/delegation`（仅 owner）`{ "enabled": true }`：写入安全设置并通知在线客户端。
- `GET /api/v1/admin/subtasks?limit=100&expert_id=&status=` → 最近的派遣记录：设备、员工、专家、状态、摘要、token、耗时、错误。

服务端的派遣记录属于审计数据，**不随员工删除对话而删除**，和审计日志使用同一保留期。

## 6. 员工端 ↔ 界面

### 事件（宿主推给页面）

每个事件都带 `conversationId`、`callId`（主助手那次 `ask_expert` 调用的 ID）、`subtaskId`、`seq`。`seq` 是该子任务内单调递增的序号，页面丢弃 `seq` 不大于已收到最大值的事件。

| type | 其它字段 |
|---|---|
| `expert.started` | `expert: {id, name, avatar, version}`、`task`（≤300 字）、`startedAt` |
| `expert.step` | `kind: "tool_started" \| "tool_completed" \| "waiting_confirmation" \| "thinking"`、`name`、`summary`、`ok?` |
| `expert.finished` | `subtask: SubtaskDto` |

确认卡片事件 `tool.confirm` 新增可选的 `actor: {kind: "expert", id, name, avatar, subtaskId, toolName, summary}`。这时 `callId` 是专家内部那次工具调用的 ID（不是主助手的 `ask_expert`），宿主保证先发 `waiting_confirmation` 的 `expert.step` 再发 `tool.confirm`。

### 页面请求

- `subtasks.list {conversationId}` → `SubtaskDto[]`（不含 trace），打开对话和页面重连时用来恢复状态。
- `subtasks.get {id}` → `SubtaskDto`（含 trace）。

### SubtaskDto

```ts
{
  id: string; callId: string; conversationId: string
  expert: { id: string; name: string; avatar: string; version: number }
  task: string; context: string
  status: 'running' | 'waiting_confirmation' | 'completed' | 'partial' | 'needs_input' | 'failed' | 'cancelled' | 'timed_out'
  summary: string
  findings: {
    id: string; statement: string
    evidence: { ref: string; kind: 'tool' | 'file'; label: string; check: 'ok' | 'missing' | 'failed_call' }[]
    verification: 'supported' | 'source_verified' | 'unverified' | 'verification_failed'
    note: string
  }[]
  openQuestions: string[]; nextSuggestions: string[]
  artifacts: { path: string; name: string; exists: boolean }[]
  confidence: number | null
  toolCalls: { name: string; count: number; failed: number }[]
  tokens: { prompt: number; completion: number; estimated: boolean }
  startedAt: string; finishedAt: string | null; durationMs: number
  errorCode: string; error: string
  trace: string | null   // TraceDto JSON，和消息上的 trace 同格式
}
```

`waiting_confirmation` 只在运行中出现，终态是后六个。

### 执行记录

主助手执行记录里，派遣是一步 `kind: "subtask"`：`name` 为专家名，`detail` 是子任务 ID（执行记录每步的 detail 最多 300 字，放不下整份记录）。界面用 `subtasks.get {id}` 取回 SubtaskDto，其中的 `trace` 才是专家的完整执行记录。
