# FlyknitBuddy 开发规则（Cursor）

本文件给 Cursor Agent 使用，与根目录 `CLAUDE.md` **同等效力**。冲突时以「安全不变量」与现有测试为准。详细架构见 `docs/architecture.md`。

## 每次动手前

1. 判断改动落在哪一层：`server` / `Flyknit.Core` / `Flyknit.Client` / `client/web` / `admin` / `mobile`。
2. 若涉及安全、命令、权限、配额、MCP、专家派遣、手机办事——先读对应测试与 `CLAUDE.md` §3。
3. 跨端协议（桥接事件、派遣 API、mobile WS）必须**双边/三边一起改**，并更新 `docs/` 里相关约定。

## 系统一句话

**Agent 循环与工具在客户端；模型 Key、场景路由、策略与配额在服务端。** 客户端只带设备 Token。

## 绝对禁止

- 在客户端直连上游模型或写入模型 Key；设备 Token 不以明文落盘。
- 让 Destructive / 间接执行被「记住后自动」或被专家身份放宽。
- 对话模式挂工具，或停止任务不补 tool 结果导致上下文非法。
- MCP 读本机环境变量做密钥替换；MCP 名覆盖内置工具（必须 `mcp__vendor__tool`）。
- 只改中文文案、漏掉 `vi-VN` / `en-US`（web）；手机端漏改 `i18n.uts`。
- 只改 `bridge.ts` 或只改 `WebBridge.cs`（协议以 `kind: request|response|event` 为准）。
- 用共享 admin token 当「能看聊天」的身份；弱化 `locked` 安全开关默认。
- 提交 `.env`、真实密钥、本机业务 data、无意义 `build.log`。

## 改哪里（速查）

| 你想… | 去… |
| --- | --- |
| 改 Agent / 工具 / 记忆 / 技能 / 命令策略 | `client/src/Flyknit.Core/` + `client/tests/Flyknit.Core.Tests/` |
| 改托盘、通知、WebView 桥、安装升级交互 | `client/src/Flyknit.Client/`（`AgentHost` partial 拆分） |
| 改聊天 UI、确认条、设置页 | `client/web/src/`（文案 → `i18n/messages.ts`） |
| 改网关、设备、配额、审计、通道 | `server/app/routers/` + `services/` + `server/tests/` |
| 改 IT 后台页面 | `admin/src/` → `server/app/static/admin` |
| 改手机 App | `mobile/` ↔ `server/app/routers/mobile.py` |
| 专家派遣契约 | `docs/expert-delegation-contract.md` + subtasks + Core Delegation + web |
| 运维代理 / 安装 / 升级 | `Flyknit.Agent` / `Setup` / `Updater`（不跑 AgentLoop） |

## 内置工具名（勿擅自重命名）

文件：`read_file` `list_dir` `search_files` `write_file` `delete_path`  
Shell：`run_shell`（`shell_output` `shell_stop`）  
Agent：`update_plan` `memory_write` `memory_search` `load_skill` `search_skills` `suggest_task`  
交互：`ask_user` `ask_expert` `submit_result`  
浏览器：`browser_*`；Windows：`open_app`

新工具：`ITool` → `ToolRegistry` → Policy → 单测；产出用 `ToolResult.Outputs`。

## 场景名（网关 model / scene）

`chat` | `agent` | `translate` | `title` | `vision` | `embedding` | `asr`  
客户端传场景名；服务端映射真实模型。安全闸门：`blocked` → 副作用分级 → 权限/工作区 → 批准规则。

## 开发命令

```text
# 服务端
cd server && python -m pytest

# 客户端核心测试
dotnet test client/tests/Flyknit.Core.Tests

# 桌面 Web（含 i18n 检查）
cd client/web && npm run build

# 管理后台
cd admin && npm run build

# 一键编客户端并启动
client\run.bat
# 失败日志：client\build.log
```

## Cursor 工作方式

- **最小 diff**：只改任务需要的文件；不做顺手重构、不扩需求。
- **先读后写**：安全与 Agent 相关代码先读现有实现与测试再改。
- **契约清单**：改桥接 / 派遣 / mobile 时，在回复里列出已同步的文件。
- **验证**：相关测试或 build 跑过再宣称完成；跑不了就说明原因与手工验证步骤。
- **语言**：与用户沟通用简体中文；代码注释风格跟随文件（多为中文说明意图）。
- **文档**：行为约定写入 `CLAUDE.md` / 本文件或 `docs/`；不要把一次性讨论当成唯一真相。

## 安全改动检查清单

改命令执行、权限、确认、MCP、定时任务、通道远程办事时，自问：

- [ ] `blocked_patterns` 是否仍优先？复合命令是否按最危险段？
- [ ] Destructive / 间接执行是否仍每次确认且不可规则放行？
- [ ] 工作区外写入/删除是否仍按权限模式处理？
- [ ] 停止/取消是否补齐 tool 结果？chat 是否仍无工具？
- [ ] 审计是否仍上报/本地记录该记的事件？
- [ ] 无人值守超时未确认是否仍视为拒绝？
- [ ] 是否有单测覆盖新分支？

## 与 CLAUDE.md 的关系

- `CLAUDE.md`：完整分层说明、扩展清单、风格与文档优先级。
- `CURSOR.md`：Cursor 会话中的强约束与速查。

两者一起遵守。新增全局规则时，两处都要更新要点，避免只改一边。
