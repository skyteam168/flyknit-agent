# 钩子（Hooks）

状态：第一期已实现，总开关默认关闭。2026-11。

## 1. 为什么

「工具规则」（`routers/tool_rules.py`）是写死的正则：工具名 + 参数匹配 → 拦下 / 必须确认 / 提醒 / 打码。
有些判断写不成正则，要问公司自己的系统：这个物料编码在不在 ERP 的冻结名单里、这笔金额有没有超过这个人的授权、
这个文件路径是不是受控文档……也有 IT 想把 AI 的每一次写操作登记到自己的系统里。

钩子参照 Claude Code 的 PreToolUse / PostToolUse / Stop hooks：IT 在后台配一个 HTTP 地址，
AI 调用工具前、工具做完、一轮任务结束时，服务端把这件事发过去。

## 2. 做法

```
AI 要调用工具 ─► 员工端：策略判定（blocked_patterns → CommandAnalyzer → 权限 → 工具规则）
                   │ 没被拦、且有匹配的 pre_tool 钩子
                   ▼
            POST /api/v1/client/hooks/check（设备令牌）
                   │ 服务端按自己的配置重新匹配、算范围，去问每个匹配的钩子（带签名）
                   ▼
            {decision: allow | ask | deny, reason}  ─► 员工端只收紧判定，再走确认 / 执行
工具做完 / 一轮结束 ─► POST /api/v1/client/hooks/notify（马上返回，服务端后台转发）
```

| 层 | 位置 | 做什么 |
| --- | --- | --- |
| 服务端 | `app/models.py` `Hook`、`HookCall` | 钩子配置（地址、加密的签名密钥、事件、工具、参数匹配、超时、出错时怎么办、范围）；调用记录 |
| 服务端 | `app/services/hook_service.py` | 匹配、范围、请求体（截断）、签名、调用、取最严、出错按 `on_error` |
| 服务端 | `app/routers/hooks.py` | 后台接口（看 / 建改删 / 测试 / 总开关）、员工端 `check` / `notify` |
| 服务端 | `/client/config` 的 `hooks` | 只下发匹配条件（事件、工具、参数匹配、`on_error`、超时），**不带地址和密钥** |
| 员工端 Core | `Security/ToolHooks.cs` | 本地先筛、参数打码、调用前收紧（`Tighten`）、做完和结束时通知 |
| 员工端 Core | `Agent/AgentLoop.cs` | 判定之后、确认之前调 `BeforeAsync`；收尾时调 `After` |
| 员工端 | `ToolContext.Hooks` | 子代理（`ForChild`）、派出去的专家（`ExpertDispatcher`）、命令行 / IDE（`HeadlessSession`）同样生效 |
| 员工端 | `AgentHost` | 配置刷新时更新钩子；办事任务一轮结束发 `stop` |
| 后台 | `admin/src/views/Hooks.vue` | 「运维 → 钩子」 |

## 3. 规则

1. **只能收紧**（`ToolHooks.Tighten`，有单测）：
   - `allow`：判定不变——本来要确认的照样要确认；
   - `ask`：至少要确认，而且「以后自动允许」「本次都允许」都跳不过（`AlwaysAsk`）；
   - `deny`：拦下，AI 看到原因；
   - 本来就被拦下的不再去问钩子；
   - 回答看不懂（不是 allow / ask / deny）按 `ask` 处理。
2. **几个钩子都匹配**：取最严的（deny > ask > allow）。
3. **出错**（超时、连不上、非 2xx、回答不是 JSON）：按这个钩子的 `on_error`，默认 `ask`；员工端连不上服务端时，按匹配钩子里最严的 `on_error`。停止任务不算出错。
4. **服务端执行**：员工端报什么工具，服务端都按自己的配置重新匹配、重新算范围（部门 ∪ 电脑，空 = 全公司，`item_scope`），员工端改不了要问哪些钩子。
5. **地址和密钥只在服务端**：员工端配置里只有匹配条件；签名密钥用 `crypto.encrypt` 存，后台接口只返回「有没有设」。
6. **发出去的内容**：员工端先用 `SecretRedactor` 给参数和结果打码（密码、令牌、Cookie）并截到 2 万字；服务端再把每个字符串截到 2000 字。`stop` 只在办事模式发（对话、翻译不发）。
7. **谁能配**：看——管理员都能看；建、改、删、测试、总开关——超级管理员（钩子能收到每个员工调用工具的参数，和改安全策略同一档）。
8. **总开关**（设置表 `hooks.enabled`）默认关：关着时员工端拿到的钩子为空，`check` 一律 `allow`。
9. **调用记录**（`hook_calls`）：不存参数原文，只存工具名、结果、原因、出错、耗时；和审计日志同一保留期。

## 4. 钩子收到什么、要回什么

请求：`POST <地址>`，`Content-Type: application/json`，`X-Flyknit-Event: pre_tool | post_tool | stop`，
设了密钥时 `X-Flyknit-Signature: sha256=<HMAC-SHA256(密钥, 请求体原文) 的十六进制>`。

```json
{
  "event": "pre_tool",
  "hook": "ERP 写操作审核",
  "time": "2026-11-27T01:23:45+00:00",
  "device": {"id": 12, "machine_name": "PC-QC-01", "owner": "黎氏雪", "department": "质检部"},
  "conversation_id": "…", "run_id": "…", "task_id": "…", "grant_id": "",
  "tool": {"name": "mcp__erp__create_order", "arguments": {"material": "A01", "qty": 500}}
}
```

- `post_tool` 多一个 `"result": {"ok": true, "summary": "…（最多 1000 字）"}`；
- `stop` 没有 `tool`，有 `"stop": {"status": "succeeded | failed | stopped", "summary": "…"}`；
- 后台「测试」发的示例请求带 `"test": true`。

`pre_tool` 的回答：HTTP 200，`{"decision": "allow" | "ask" | "deny", "reason": "给员工和 AI 看的原因"}`。
`post_tool`、`stop` 的回答只看是不是 2xx。

## 5. 验收

- 服务端：`tests/test_hooks.py`（权限、总开关、下发不带地址密钥、服务端匹配、取最严、出错按 on_error、范围、签名、截断、测试按钮、通知）。
- Core：`ToolHooksTests`（只收紧、看不懂按 ask、只问匹配的、被拦的不问、连不上按 on_error、停止不算出错、打码、通知）、
  `AgentLoopTests.AHookCanForceConfirmationAndBlockButNeverLoosen`（ask 时「本次都允许」也照样问、deny 时文件没写出来）。
- 真机：配一个内网钩子（比如几行 Python 的 Flask），对 `write_file` 回 `ask`，看员工端是否每次都弹确认、后台调用记录是否完整。

## 6. 以后

- 统一插件包里带钩子（和工具规则一样按部门下发）；
- `pre_tool` 允许钩子改写参数（例如补上审批单号）——需要另外设计，第一期不做；
- 钩子结果写进员工端审计（现在只在服务端的调用记录里）。
