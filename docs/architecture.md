# Flyknit 架构说明

完整技术方案（含 UI 设计、Skills 体系、安全机制、路线图）维护在在线文档《Flyknit 智能办公助手 技术方案》中，本文件只记录与代码直接相关的约定。

## 已确认的前提

- 客户端仅支持 Windows 10 / 11。
- 主力模型：内部 Qwen3.5-397B（OpenAI 兼容接口）；阿里云百炼等云模型仅作备用。
- 不做数据外发限制。
- SMB 服务账号在管理后台配置，加密存储。
- 有害命令（rm -rf、format、diskpart 等）直接阻止；一般命令经用户确认后执行。
- 界面语言：简体中文、越南语、英语。

## 分工

```
客户端（C# .NET 8 + WPF + WebView2）           服务端（Python FastAPI）
├─ 悬浮球 / 托盘 / 主窗口                         ├─ 模型网关  /api/v1/chat/completions
├─ Web 聊天界面（Vue 3，中/越/英）                 ├─ 客户端配置 /api/v1/client/config（策略、场景）
├─ Agent 循环（思考→规划→执行→观察）               ├─ 设备注册  /api/v1/devices/register
├─ 工具层 + 命令拦截 + 用户确认                     ├─ 审计上报  /api/v1/audit
├─ 本地记忆 agent.md / soul.md / role.md / memory.md ├─ 管理接口  /api/v1/admin/*
├─ 会话存储（SQLite）                              └─ 文档服务（阶段三）
└─ 特权安装服务（阶段三）
```

**Agent 循环和工具执行在客户端，模型调用、密钥和策略在服务端。** 客户端只持有设备 Token，不持有模型 Key。

## 场景（scene）路由

客户端请求网关时，`model` 字段填场景名，服务端按后台配置映射到真实模型：

| scene | 用途 |
| --- | --- |
| `chat` | 普通对话 |
| `agent` | Agent 多步任务（需支持工具调用） |
| `translate` | 翻译模式 |
| `title` | 会话标题总结、记忆整理 |
| `vision` | 带图片的请求 |

主模型请求失败（连接失败、5xx、超时）且尚未开始输出时，自动切换到该场景配置的备用模型。

## 命令策略

策略由服务端 `GET /api/v1/client/config` 下发，客户端 `CommandPolicy` 执行：

1. 命中 `blocked_patterns`（正则，忽略大小写）→ **直接阻止**，上报审计。
2. 命中 `readonly_commands` 且 `auto_run_readonly = true` → 自动执行。
3. 其余 → 弹出确认卡片，用户同意后执行。

脚本文件（.ps1 / .bat / .cmd / .py）执行前同样扫描内容。

## 记忆体系

参照 Claude Code / Codex 的 Agent 范式，分四层，实现在 `Flyknit.Core/Context` 与 `Flyknit.Core/Memory`。

| 层 | 存放位置 | 作用 | 何时写 | 何时读 |
| --- | --- | --- | --- | --- |
| 短期（工作上下文） | SQLite `conversations.summary` | 让长对话不超出模型上下文 | 超出预算时自动压缩 | 每次请求模型前 |
| 项目 | 工作区的 `FLYKNIT.md` / `AGENTS.md` / `CLAUDE.md` | 这个工作区的固定约定 | 用户自己写 | 组装系统提示词时 |
| 长期（语义） | `memory.md`、`lessons.md` | 偏好、常用信息、经验、教训 | memory_write 工具、任务复盘 | 按与当前任务的相关度挑选 |
| 情景 | `episodes.json` | 做过的任务及当时的做法 | 任务复盘 | 相似任务开始时、memory_search |

**上下文压缩**（`ContextManager`）：预算 = 模型上下文长度（服务端通过 `X-Flyknit-Context` 下发）− 8K 输出预留。
超过 50% 先裁剪较早的工具输出（保留开头，需要时模型重新读取）；超过 75% 让模型把较早的对话压缩成结构化摘要
（目标、已完成、关键信息、问题、下一步），摘要放进系统提示词，最近 25% 预算的消息保留原文。
摘要保存在会话里，下次打开继续使用；编辑或重新生成删掉了摘要覆盖的消息时，摘要自动作废。

**停止任务**：取消令牌会连子进程一起结束正在跑的命令；已发出的工具调用补一条「用户已停止任务，未执行」的结果，保证下次请求的上下文合法；
已产生的消息照常入库，不生成标题。对话没有锁死，用户回复「继续」即可接着做。
任务中途已经写入的记忆和命令授权保留；整轮复盘仍然执行，但只记教训——
成功经验被清空、不沉淀技能、历史任务的结果不会标成 success。

**复盘**（`Reflector`，参照 Reflexion）：办事任务结束后在后台让模型回顾经过，输出 JSON：
可复用的步骤、用户偏好、常用信息、成功经验、失败教训，以及（可选）一个技能。
偏好等写入长期记忆（自动去重），任务写入 `episodes.json`；同类任务成功 2 次以上时，把技能写成
`skills/learned/<name>/SKILL.md`（frontmatter 带 `source: learned`），之后与手写技能一样被加载。
用户点踩会立刻对那一轮做一次复盘，重点记录教训。可以在界面的「记忆」里查看、删除，或关掉自动学习。

## 用量与配额

模型返回的 `usage` 由**服务端**在转发时解析（流式请求边转发边扫 SSE 的 `usage` 行），
按「设备 + 日期 + 场景」累加到 `usage_daily` 表，客户端改不了这个数字。

- 管理员用 `scripts/setup_quota.py` 设置每台电脑每天的 token 上限（0 = 不限）和额度联系人。
- 网关在转发前检查当天用量，超额返回 `429` + `type: quota_exceeded`，消息里带联系人邮箱和电话，客户端直接显示。
- 客户端「用量」面板读 `GET /api/v1/client/usage`：今天总量与进度条、按模式（办事 / 对话 / 翻译）分布、最近 7 天柱状图。

## 安全记录

每次工具调用的判定都会上报审计（`POST /api/v1/audit`，带 `scene` 字段），同时在本机留一份：

- 本机表 `security_events` 只记有安全意义的四类：`blocked` 拦截、`approved` 用户放行、`remembered` 记住后自动放行、`rejected` 用户拒绝；
  自动执行的只读操作不记。最多保留 500 条，可在界面清空。
- 「用量与安全 → 安全记录」按类别筛选，每条显示模式（办事 / 对话 / 翻译）、工具、命令原文、拦截原因和所属任务。
- 服务端的 `audit_logs` 保留全量，管理员通过 `GET /api/v1/admin/audit` 查看所有电脑的记录。

## 技能（Skills）

技能是写给模型的工作说明，格式沿用社区开放标准，所以 GitHub、ClawHub 等处发布的技能可以直接安装：

```
skill-name/
├─ SKILL.md     必需。frontmatter 至少有 name 和 description，可选 version、author、license、keywords
├─ scripts/     可选，脚本
├─ references/  可选，参考资料
└─ assets/      可选，模板文件
```

**技能目录**（按优先级，同名时靠前的生效）：

| 目录 | 来源 | 谁能改 |
| --- | --- | --- |
| `C:\ProgramData\FlyknitBuddy\skills\` | IT 在这台机器上统一预装 | 只读，这台电脑所有 Windows 用户共用 |
| `%APPDATA%\Flyknit\skills\org\` | 公司技能库里标记必装的，客户端自动下发 | 只读，不能停用或卸载 |
| `%APPDATA%\Flyknit\skills\learned\` | 复盘自动沉淀 | 可停用、可删除 |
| `%APPDATA%\Flyknit\skills\<name>\` | 用户自己安装 | 可停用、可卸载 |

除机器级目录外都在 `%APPDATA%` 下，所以**每个 Windows 用户的技能、记忆、对话都是独立的**。

**安装来源**：本地 zip、文件夹（含共享盘）、公司技能库（服务端）、直链下载。
工厂电脑通常上不了外网，所以由服务端统一从 GitHub / 社区链接导入，客户端再从公司技能库安装；
管理员标记为「必装」的技能，客户端启动和每次刷新配置时自动下发到 `skills\org\`。

**安装流程**（`Flyknit.Core/Skills/SkillPackage.cs`）：解压到临时目录 → 校验 → 整体替换，失败不留残骸。
校验包括：必须有 SKILL.md 和 description、拒绝路径穿越（zip slip）、大小与文件数上限、
拒绝 .exe/.dll 等可执行程序、脚本内容过一遍命令策略（命中危险规则直接拒绝）。一个压缩包里有多个技能时全部安装。

**热插拔**：`FileSystemWatcher` 监听技能目录，变化后防抖 400ms 重新扫描并通知界面，不需要重启；
直接往技能文件夹拷目录同样生效。停用的技能名存在 settings.json，停用后不进提示词，load_skill 也会拒绝；企业必装的不能停用或卸载。

**模型如何发现和使用**（渐进式披露）：系统提示词里只放「名称 + 描述」清单；
模型判断相关后调用 `load_skill` 读取正文和文件列表。技能超过 12 个时按与当前任务的相关度只列出前 N 个，
其余通过 `search_skills` 工具按关键词检索，避免提示词被技能挤满。

## 工作区与权限

每个办事任务有自己的工作区（默认 `我的文档\Flyknit`，可在输入框下方添加、切换）和权限模式。
判定顺序：管理员禁止规则 → 权限模式 → 工作区范围（实现见 `Flyknit.Core/Security/Permissions.cs`）。

| 操作 | 仅可查看 | 工作区内修改（默认） | 完全权限 |
| --- | --- | --- | --- |
| 读取文件、查看目录、查询类命令 | 自动 | 自动 | 自动 |
| 在工作区内写文件 | 阻止 | 自动 | 自动 |
| 在工作区外写文件 | 阻止 | 阻止 | 自动 |
| 删除（移入回收站） | 阻止 | 每次确认 | 工作区内自动，工作区外每次确认 |
| 普通命令 | 阻止 | 确认，同样的命令确认一次后自动通过 | 自动 |
| 命令的工作目录在工作区外 | — | 阻止 | 允许 |
| 危险命令（禁止规则） | 阻止 | 阻止 | 阻止 |

- 完全权限需要在弹窗中勾选“我已了解风险”，只对当前任务生效，不会成为新任务的默认值。
- 已允许的命令记录在 `%APPDATA%\Flyknit\approvals.json`，按“Shell + 规范化后的命令”（忽略多余空格和大小写）匹配，可在设置中撤销。删除和大批量删除不会被记住。

## 定时任务

用户在侧栏「定时任务」里写一句指令和一个频率，到点客户端自动新建一个办事任务去执行，不需要有人守着。

**为什么跑在客户端。** 任务要操作的是这台电脑的文件、共享盘和命令，服务端碰不到；
而且权限判定、工作区限制、命令确认这套逻辑都在客户端，放服务端会变成两套。
代价是程序得开着（可以收在托盘里），关机期间的任务靠下面的补跑机制。

**频率**（`Flyknit.Core/Scheduling/ScheduleSpec.cs`）：手动、每小时、每天、每个工作日（周一到周五）、每周、每月、仅一次。
按本机本地时间算；工厂都在同一个时区，不处理夏令时。每月 31 号这种在小月自动落到当月最后一天。

**调度**（`Flyknit.Client/Services/ScheduleRunner.cs`）：

- 启动 10 秒后开始，之后每 30 秒查一次到点的任务，串行执行，不会并发压住机器
- **先排下次时间再运行**，所以某一次失败或程序崩了也不会反复触发同一次
- **补跑**：关机错过的任务，开机后补跑最近一次，超过 **12 小时**就跳过（避免早上开机一次性跑一堆）。
  任务上的「补跑错过的任务」可以关掉，关掉后只跑 5 分钟内的
- 「仅一次」执行完自动停用，留在列表里可以看结果
- 每次运行新建一个会话，标题固定为 `任务名 · 日期时间`，不让 AI 改掉；列表里点「查看上次结果」直接跳进去

**无人值守时的确认。** 任务按自己配置的权限模式跑。需要确认的操作：已记住的授权照常自动通过；
其余照常弹 Windows 系统通知和界面确认卡片，等 **5 分钟**；没人应答按**拒绝**处理，让任务走完剩下的步骤而不是一直挂着。
拒绝记录会写进安全记录，并标明来自定时任务。危险命令和权限模式的限制不因为是定时任务而放宽。

**状态**：`running` / `ok` / `stopped`（被手动结束）/ `failed`。回写由 `AgentHost.RunFinished` 触发，
结果摘要截前 300 字存在任务上。记忆、复盘、经验教训与手动任务完全一样，定时跑出来的经验也会沉淀。

存储在 `history.db` 的 `scheduled_tasks` 表（`Flyknit.Core/Scheduling/ScheduledTaskStore.cs`）。

## 客户端与 Web 界面的通信

WebView2 中的页面通过 `window.chrome.webview.postMessage` 发送 JSON 消息，宿主通过 `PostWebMessageAsJson` 推送事件。消息格式：

```json
{ "type": "chat.send", "id": "req-1", "payload": { "conversationId": "...", "text": "..." } }
```

宿主推送的事件类型：`chat.delta`、`chat.reasoning`、`tool.started`、`tool.confirm`、`tool.finished`、`plan.updated`、`chat.done`、`chat.error`、`conversation.updated`、`schedules.changed`。

完整列表见 `client/web/src/bridge.ts` 与 `client/src/Flyknit.Client/Bridge/WebBridge.cs`，两边需保持一致。

## 数据库升级

服务端每次启动时自动对比模型定义和实际表结构（`app/migrate.py`）：缺失的表、字段、索引都会补上，
不改类型、不删任何东西，SQLite 和 PostgreSQL 都适用。

SQLAlchemy 的 `create_all` 只建缺失的**表**，不会给已有的表加**字段**——
v0.4 给 `audit_logs` 加 `scene` 时踩过这个坑，老库升级上来后审计上报全部 500。
以后再加字段不用手动迁移，但记得给非空字段写默认值，否则 SQLite 不允许加列（迁移会退化成可空并记一条告警）。

## 本地目录

```
%APPDATA%\Flyknit\
├─ settings.json      服务器地址、设备 Token、界面语言、工作区、权限默认值
├─ approvals.json     已记住的命令授权
├─ data\history.db    会话与消息（含摘要、模型与 token 用量）、本机安全记录、定时任务
├─ memory\
│  ├─ agent.md / soul.md / role.md   行为准则、语气、用户身份
│  ├─ memory.md       偏好与习惯、常用信息
│  ├─ lessons.md      成功经验、失败教训
│  └─ episodes.json   历史任务
└─ skills\            已安装的 Skills
   ├─ org\            企业下发（必装，只读）
   ├─ learned\        复盘自动沉淀
   └─ <name>\         用户自己安装的
```

默认工作区是 `我的文档\Flyknit`（不是上面的数据目录），用户可以另外添加。
