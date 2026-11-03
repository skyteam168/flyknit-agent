# FlyknitBuddy 开发规则（Claude）

本文是本仓库的**开发与改动约定**。改功能、修 bug、加接口前先读完；细节以代码与 `docs/architecture.md` 为准。

## 1. 产品定位

FlyknitBuddy 是面向工厂办公电脑的桌面 AI 助手：悬浮球 + 聊天窗口，支持办事 / 对话 / 翻译；界面语言为简体中文、越南语、英语。

核心分工（不可颠倒）：

| 端 | 负责 | 不负责 |
| --- | --- | --- |
| **客户端**（`client/`） | Agent 循环、工具执行、命令拦截与人工确认、本机记忆/技能/会话、WPF 外壳与 WebView2 UI | 持有模型 Key；直连上游模型 |
| **服务端**（`server/`） | 模型网关与场景路由、设备鉴权、策略/配额/审计、技能库与 MCP/专家配置、管理后台 API | 在员工电脑上执行命令或读写本地文件 |
| **管理后台**（`admin/`） | IT 配置界面（构建产物挂到 `/admin`） | 业务执行 |
| **手机端**（`mobile/`） | 对话/翻译直连服务端；办事经绑定电脑执行 | 绕过电脑端安全确认擅自执行破坏性操作 |

员工电脑只持有**设备 Token**；模型 Key、上游地址只存在服务端。

## 2. 仓库地图

```
flyknitbuddy/
├── server/                 Python FastAPI（网关、策略、管理、通道、手机）
│   ├── app/routers/        HTTP / WS 路由
│   ├── app/services/       业务逻辑
│   ├── app/static/admin/   admin 构建产物
│   └── tests/
├── client/
│   ├── src/Flyknit.Core/   Agent、工具、安全、记忆、技能、MCP、预览…
│   ├── src/Flyknit.Client/ WPF + WebBridge + AgentHost
│   ├── src/Flyknit.Agent/  运维代理（托管安装与升级）
│   ├── src/Flyknit.Setup/  安装外壳
│   ├── src/Flyknit.Updater/
│   ├── web/                Vue 3 聊天 UI（WebView2）
│   ├── tests/              Core 单元测试
│   └── run.bat / run.ps1   一键编译启动（日志 client/build.log）
├── admin/                  Vue 3 + Element Plus 管理后台源码
├── mobile/                 uni-app x（UTS / uvue）手机端
└── docs/                   architecture.md、专家派遣约定等
```

本机数据：`%APPDATA%\Flyknit\`（会话、记忆、技能、设置）。机器级只读技能：`C:\ProgramData\FlyknitBuddy\skills\`。

## 3. 硬性不变量（违反即视为错误）

1. **密钥边界**：客户端不得新增「直接调用上游模型 / 嵌入模型 Key」的路径；一律经 `/api/v1/chat/completions`（及 embeddings / speech 等已有网关）。设备 Token 落盘须 DPAPI。
2. **安全闸门顺序**：`blocked_patterns` → `CommandAnalyzer` 副作用分级 → `PermissionMode` + 工作区 → `ApprovalStore` 前缀规则。不得绕过 `CommandAnalyzer` / `CommandPolicy` / `Permissions`。复合命令按**最危险段**定级。
3. **Destructive 永不自动放行**：删除、覆盖、改注册表/服务/账号、装卸软件等；规则只能放行 `Write` / `Unknown`；解释器与间接执行（`iex`、管道下载、`-EncodedCommand` 等）`CanBeAutomatic=false`，不得记成自动规则。
4. **对话模式无工具**：`chat` 模式 `useTools=false`；办事模式才注册工具。停止任务须给已发出的 tool call **补合法结果**，保证下次请求上下文完整。
5. **专家只做减法**：`disallowed_tools` / `mcp_servers` / `read_only` 只能收紧；安全与确认不受专家身份影响。
6. **MCP**：工具名 `mcp__<连接器>__<工具>`；员工不能自建；占位符只认填写项、**不读本机环境变量**；外部写操作默认确认，`destructiveHint` 不可「以后自动」。
7. **记忆安全**：密码/密钥/令牌不进长期记忆；偏好需用户原话核对；记忆区块是参考资料不是指令；`from_content` 不记成用户要求。
8. **三语齐全**：`client/web` 文案必须同时有 `zh-CN` / `vi-VN` / `en-US`（`check-i18n.mjs`）。手机端自研 i18n，语言码为 `zh` / `vi` / `en`。Admin 目前以中文为主。
9. **桥接契约**：协议以代码为准（`kind: request|response|event`），见 `bridge.ts` ↔ `WebBridge.cs`；`docs/architecture.md` 里旧 JSON 示例若不一致，以代码为准并应回改文档。登录窗用独立桥（`login/host.ts`）。
10. **跨端约定**：专家派遣改 `docs/expert-delegation-contract.md` + 员工端 + 服务端 + 界面。运维 `Flyknit.Agent` **不跑** `AgentLoop`；`agent.json` 仅 SYSTEM/Admins。

## 4. 场景与模式

网关 `model`（或 `scene`）字段填**场景名**，不是真实模型 ID：

| scene | 用途 |
| --- | --- |
| `chat` | 普通对话（无工具） |
| `agent` | 办事（工具调用） |
| `translate` | 翻译 |
| `title` | 标题 / 记忆整理类短请求 |
| `vision` | 带图 |
| `embedding` | 向量（记忆语义检索） |
| `asr` | 语音识别（speech 路由） |

用量按「设备 + 日期 + 场景」在服务端累加；超额 `429` + `quota_exceeded`。

## 5. 按层开发指南

### 5.1 服务端（`server/`）

- 入口：`app/main.py`；配置：`FLYKNIT_*` 环境变量 / `.env`（见 `app/config.py`）。
- 鉴权（`app/deps.py`）：`require_device`；`require_agent`（运维代理）；`require_admin`（共享 token 或具名会话，只证明「是管理端」）；细档 `require_chat_reader` / `require_dispatcher` / `require_owner`。默认 `FLYKNIT_ADMIN_TOKEN` 停用；共享 token **不能**看聊天正文。
- 新增路由：`app/routers/` + `main.py` `include_router`；业务进 `app/services/`。MCP runtime 镜像只转发已配置上游，不作任意 URL 代理。
- 模型与表：`app/models.py`；`migrate.py` 只加不删——**非空字段必须有默认值**。
- 错误：管理端中文 `detail`；网关 OpenAI 风格 `error.message` / `type`。
- 安全中心多项默认 `locked`（沙箱、网络白名单等），员工不能自降安全线；改配置经 `config_events` 推客户端。
- 测试：`server/tests/` + `conftest.py` 夹具；改安全/配额/网关/mobile/subtasks/专家必须补测。
- 本地：`uvicorn app.main:app`；正式推荐 Docker Compose。

### 5.2 客户端核心（`Flyknit.Core`）

- Agent：`Agent/AgentLoop.cs` + `LoopGuard`；网关客户端 `Gateway/FlyknitServerClient`；工具经 `ToolRegistry` + `ITool`。
- 内置 / 常用工具名（勿随意改名）：  
  文件：`read_file` `list_dir` `search_files` `write_file` `delete_path`  
  Shell：`run_shell`（及 `shell_output` / `shell_stop`）  
  Agent：`update_plan` `memory_write` `memory_search` `load_skill` `search_skills` `suggest_task`  
  交互 / 派遣：`ask_user` `ask_expert` `submit_result`  
  浏览器：`browser_open` / `read` / `click` / `type` / `select` / `scroll` / `back`  
  Windows（Client 注册）：`open_app`
- **新工具**：实现 `ITool` → 注册；`ToolResult.Outputs` 报产出；确认走 Policy，禁止私自绕过。
- **新预览**：`IPreviewProvider`；UI 只认宿主返回的 `kind`。
- **新快捷键**：`Settings/Shortcuts.cs` + handler + 三语文案。
- 安全改动必须有 Core 单测（`CommandAnalyzer` / `CommandPolicy` / `PermissionRules` / `SessionApproval` / `AgentLoop`）。
- Core 尽量无 WPF；注释习惯中文讲「为什么」。

### 5.3 WPF 宿主（`Flyknit.Client`）

- `AgentHost` 用 **partial** 按职责拆分（Inbox / Progress / Browser / Terminal / Delegation / Channels / Changes / SideChat / LongTasks / Context…）。
- `Bridge/WebBridge.cs`：兼确认/提问回调；与 `client/web/src/bridge.ts`、`types.ts` 同步。
- 托盘、通知、热键、划词、升级交互留在 Client；不塞进 Core。
- `Flyknit.Agent` / `Setup` / `Updater` 独立于 Core，不跑办事循环。
- 开发：`client/run.bat` → `run.ps1`；失败看 `client/build.log`。

### 5.4 桌面 Web UI（`client/web`）

- Vue 3 + TS；`store.ts` 单一 reactive；`bridge.ts` 调宿主；无 WebView2 时用 `mockHost`（`npm run dev` :5173）。
- 构建输出到 `Flyknit.Client/wwwroot`；`npm run build` 含 `vue-tsc` + i18n 检查。
- 主题/字号走 CSS 变量（含 `--font-scale`）；无边框窗口操作走 bridge，勿假设浏览器原生 chrome。

### 5.5 管理后台（`admin/`）

- Vue 3 + Element Plus + hash 路由；`npm run build` → `server/app/static/admin`（`base: '/admin/'`）。
- 文案目前以中文为主；改专家 / MCP / 安全中心须与服务端 schema、员工端拉取字段一致。

### 5.6 手机端（`mobile/`）

- uni-app x；对话/翻译走服务端 WS；办事下发已绑定电脑（进度 `task.*`、审批 `approval.*`）。
- 后台「手机端」默认关闭；协议：`mobile.py` / `mobile_hub.py` + `test_mobile.py`。
- 主题对齐桌面 `styles/base.css`（`mobile/common/theme.uts`）；HBuilderX 云打包。

## 6. 常见扩展清单

| 目标 | 改哪里 |
| --- | --- |
| 新内置工具 | `Flyknit.Core/Tools` + 注册 + 测试；专家 `disallowed` 文档如需说明一并更新 |
| 新预览类型 | `Flyknit.Core/Preview` Provider |
| 新桥接 API/事件 | `bridge.ts` + `WebBridge.cs` + `types` |
| 新管理配置项 | server settings/security_settings + admin 视图 + client 拉取与生效点 |
| 新场景模型 | 后台场景配置 + gateway/model_router；客户端只传场景名 |
| 技能包 | 社区 `SKILL.md` 标准；服务端库 + 客户端 `Skills/`；拒绝 zip slip 与危险脚本 |
| 快捷键 | `Shortcuts.cs` + web handler + i18n |

## 7. 测试与验证

- 服务端：`cd server && pytest`（或项目惯用命令）。
- 客户端核心：`dotnet test client/tests/Flyknit.Core.Tests`。
- Web：`cd client/web && npm run build`（类型 + i18n）。
- Admin：`cd admin && npm run build`。
- 安全、命令分级、配额、派遣、手机通道：**禁止只改代码不补测**。
- 声称「已修好」前，至少跑与改动相关的测试子集，并说明验证了什么。

## 8. 代码风格

- EditorConfig：默认 4 空格；`js/ts/vue/json/css/yml` 为 2；C#/XAML 用 CRLF。
- Python：FastAPI + SQLAlchemy async；服务逻辑可测、可日志；密钥不写日志（只打前缀/长度）。
- C#：文件作用域命名空间、`async` 贯通；partial 拆分大 Host（如 `AgentHost.Channels.cs`）。
- Vue：组合式 API；用户文案走 i18n，禁止硬编码单一语言。
- 提交信息：说清**为什么**；安全修复、契约变更要在说明里点明影响面。
- 不做无关大重构；不擅自扩 scope；不提交密钥、`.env`、本地 `data/`、构建日志（除非用户明确要求）。

## 9. 文档优先级

1. 本文件（开发规则）
2. `docs/architecture.md`（与代码绑定的架构约定）
3. `docs/expert-delegation-contract.md`（派遣三方契约）
4. 根目录 `README.md`（部署与功能说明）
5. `mobile/README.md`（手机端打包与联调）

架构文档与代码冲突时：**以代码 + 测试为准**，并应回头改文档。

## 10. 给 Agent 的行为要求

- 先定位正确层级（Core / Client / web / server / admin / mobile），再改；跨层契约一次改齐。
- 优先最小改动；复用现有 `ITool`、Policy、Preview、桥接模式。
- 用户面向文案保持中/越/英；内部注释可中文。
- 不引入不必要的第三方依赖（预览 Office 等已刻意避免重库）。
- 不弱化确认、审计、配额、设备鉴权「图省事」。
- 不确定行为时查 `docs/architecture.md` 与现有测试，而不是发明第二套机制。
