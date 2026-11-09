# 待开发清单

> 2026-11-09 整理，额度恢复后按本文顺序开发。每一项单独出 PR、单独验收；安全相关的改动都要补测试（`CLAUDE.md` §7），
> 声称完成前跑过相关测试并写明验证了什么。
>
> 来源：
> - VNext 第 1～4 期：`docs/architecture-vnext.md`（参照 DeepSeek Harness Super-agent）。第 0 期（只读子代理并行数可配置）这次**不做**。
> - 旧电脑升级后补 PATH：11-09 讨论，命令行 `flyknit` 在老装机上找不到。
> - 输入框和命令行的斜杠命令：11-09 参照 Claude Code 的分析。

## 总览与建议顺序

| 顺序 | 项目 | 估算 | 依赖 | 价值 |
| --- | --- | --- | --- | --- |
| 1 | [A. 旧电脑升级后补 PATH](#a-旧电脑升级后补-path) | 0.5 天 | 无 | 老电脑升级后直接能敲 `flyknit` |
| 2 | [B1. 斜杠命令第一批（桌面端）](#b1-第一批桌面端接上已有功能) | 1.5 天 | 无 | 体感最直接 |
| 3 | [B2. 斜杠命令第二批（开发 / 管理）](#b2-第二批开发者和管理类) | 1 天 | B1 | |
| 4 | [B3. 斜杠命令第三批（命令行）](#b3-第三批命令行-flyknit) | 0.5 天 | 无 | |
| 5 | [C1. VNext 第 1 期：统一任务模型](#c1-第-1-期统一任务模型) | 2～3 天 | 无 | 后面三期的地基 |
| 6 | [C3. VNext 第 3 期：Watchdog](#c3-第-3-期watchdog) | 1～2 天 | C1 | 省额度、敢放手 |
| 7 | [C2. VNext 第 2 期：Router](#c2-第-2-期router) | 3～5 天 | C1 | 多任务体感最大 |
| 8 | [C4. VNext 第 4 期：并行写（隔离）](#c4-第-4-期并行写隔离) | 3～5 天 | C1、C2 | 开发 / IT 部门 |

A 和 B 改动小、马上能用，先做；C 是架构改动，C1 做完再按 C3 → C2 → C4。
C3 排在 C2 前面：投入小、能省钱，而且 C2 上线后并行任务变多，最需要它兜底。

---

## A. 旧电脑升级后补 PATH

**问题**：命令行功能加入前装的电脑，自动升级会把 `flyknit.exe` 放进 `C:\Program Files\FlyknitBuddy`，
但把这个目录加进系统 PATH 只有安装程序（Setup.exe）做，运维代理升级时不做。结果老电脑升级后敲 `flyknit` 提示「不是内部或外部命令」。

**做法**：
- 运维代理（`Flyknit.Agent/ClientUpdater.cs`，SYSTEM 身份）整目录替换成功后，检查系统 PATH 里有没有员工端目录，没有就补上；
  复用安装程序的 `Flyknit.Setup/PathList.cs`（只加这一个目录，不动别的项，去重、大小写不敏感）。
- 只在员工端目录里确实有 `flyknit.exe` 时补。
- 补完广播 `WM_SETTINGCHANGE`，新开的命令行窗口立即生效（已开的窗口不会变，这是 Windows 的规矩）。
- 卸载时照旧由安装程序去掉。

**安全**：只写 HKLM 的 Path 值里这一项；目录是代理自己管的 Program Files 路径，员工改不了；不接受来自服务端或员工的路径。

**测试**：`PathList` 已有测试；补「已在 PATH 不重复加」「目录里没有 flyknit.exe 不加」「替换失败回滚时不改 PATH」。

**验收**：拿一台命令行功能之前装的电脑，发布新版本 → 自动升级完成 → 新开 cmd 输入 `flyknit --version` 能运行。

---

## B. 输入框和命令行的斜杠命令

参照 Claude Code（`/compact`、`/permissions`、`/model`、`/context`、`/rewind`……）。
现状：桌面端只有内置的 `/btw`、`/init`，加上 IT 和员工的快捷指令（提示词模板）；命令行有 `/new /resume /plan /usage /clear /help /exit`。
后台大部分能力都有了（见下表「宿主接口」），缺的是斜杠命令这个入口。

### 通用设计

1. **内置命令优先**：和快捷指令重名时以内置为准；后台发布快捷指令时提示「和内置命令重名，不会生效」。
2. **不发给模型**：调权限、换模型这类在本地执行，对话里插一行灰色提示（如「已切换为只读」），不占 token；
   只有 `/compact`、`/review`、`/init` 会调用模型。
3. **不开后门**：每个命令走和按钮完全相同的宿主接口；IT 锁定、部门 / 单台电脑的安全设置、专家封顶、完全权限的二次确认一个不少。
4. **弹出列表分组**：「内置 / 公司 / 我的」；输完命令名按空格显示可选参数（如 `/permission ` 后列出三档）。
5. **三语**：命令名统一英文；说明和提示 `zh-CN` / `vi-VN` / `en-US` 三套（`check-i18n.mjs`）。
6. 手机端不做。

### B1. 第一批：桌面端，接上已有功能

| 命令 | 作用 | 宿主接口 | 备注 |
| --- | --- | --- | --- |
| `/compact [要保留的重点]` | 立即压缩对话 | `context.compact` | 附的话传给压缩提示词（「重点保留……」）；`ContextManager.CompactNowAsync` 要能接收这段说明 |
| `/permission [只读\|工作区\|完全]` | 调这个对话的权限 | `setPermission` | 不带参数弹选择器；「完全」照样弹 `FullAccessDialog`；被锁 / 封顶时提示原因 |
| `/model [名字]` | 换模型 | `setModel` / `listModels` | 只能选后台配置的；参数自动补全 |
| `/plan` | 打开 / 关闭计划模式 | `setPlanMode` | |
| `/new`（`/clear`） | 新对话 | `createConversation` | 沿用当前工作区和模式 |
| `/context` | 上下文占用明细 | `contextUsage` | 打开现有明细 |
| `/usage` | 今日额度 | `usageStats` | 打开用量对话框 |
| `/rewind` | 撤销上一轮的文件改动 | `listChanges` / `revertChanges` | 打开现有还原对话框，不直接还原 |
| `/fork` | 从当前位置分出新对话 | `forkConversation` | |
| `/memory` | 记忆管理 | `memoryOverview` | |
| `/status` | 汇总卡片：版本、模型、权限、工作区、模式、连接器、今日额度 | 现有数据 | 新做一个卡片 |
| `/help` | 列出全部内置命令和快捷指令 | | |

改哪里：`client/web/src/components/Composer.vue`（`slashItems` 加内置项和分组、参数补全）、新建 `client/web/src/builtinCommands.ts`（命令表和执行）、
`i18n/messages.ts`；`/compact` 的说明参数要动 `WebBridge.cs` 的 `context.compact` 和 Core 的 `ContextManager`。

测试：web 构建（类型 + i18n）；Core 补「压缩带说明」；`/permission` 被锁定时不生效（复用现有权限测试）。

### B2. 第二批：开发者和管理类

| 命令 | 作用 | 说明 |
| --- | --- | --- |
| `/review` | 审查当前未提交的改动 | 开了开发者工具才有；做法同 `/init`：内置提示词 + 只读（计划模式），不改文件 |
| `/mcp` | 连接器状态 | 列出连接 / 断开 / 出错，带重连按钮（`listMcp` / `connectMcp`） |
| `/export` | 导出对话为 Markdown | **新做**：宿主写文件到工作区（走 `write_file` 同样的工作区限制），不含 `<turn-context>` 参考资料 |
| `/doctor` | 网络和登录诊断 | `diagnoseNetwork` |
| `/feedback`（`/bug`） | 意见反馈 | 自动带上当前对话 ID |
| `/long`、`/sandbox` | 切换长任务 / 沙箱模式 | `setLongMode` / `setSandbox`；沙箱受安全中心开关限制 |

### B3. 第三批：命令行 `flyknit`

| 命令 | 作用 | 说明 |
| --- | --- | --- |
| `/compact [重点]` | 压缩当前会话 | `HeadlessSession` 加手动压缩 |
| `/permission [只读\|工作区]` | 调权限 | 命令行没有完全权限 |
| `/context` | 上下文占用 | |
| `/status` | 版本、工作区、权限、开发者工具、深度思考、会话 ID | |
| `/think [开\|关]` | 切换深度思考 | `HeadlessEnvironment.DeepThinking` 改成会话级可变 |

`/rewind` 先不做（命令行没有改动备份的界面）。改哪里：`Flyknit.Core/Headless/InteractiveShell.cs`、`HeadlessSession.cs`，测试进 `InteractiveShellTests`。

---

## C. VNext 第 1～4 期

详细设计见 `docs/architecture-vnext.md`（概念定义、存算边界、安全设计、成本控制）。本节只列每期的交付和验收。

### C1. 第 1 期：统一任务模型

**交付**：
- 本机库新增 `tasks`、`runs`、`thread_subscriptions` 表（只加不删）；Task 状态机：待办 / 进行中 / 等确认 / 完成 / 失败 / 停止。
- 统一入口 `TriggerEvent`：员工消息、定时任务（`Scheduling/*`）、事件触发、钉钉 / 企业微信 / 手机（`AgentHost.Channels.cs`）、IT 指令、盯 PR（`AgentHost.PullRequests.cs`）都改为「登记 Task → 开 Run」。
- `TaskGrant`、`RunCheckpoint` / `run_state` 挂到 Task / Run 下；现有行为不变。
- 盯 PR 改成一种 Thread 订阅：已订阅事件直通原对话。
- 任务中心按 Task 展示：来源、授权、进度、预算、停止。

**安全**：非员工本人发起的 Task 一律带 `TaskGrant`；Task 级 token 预算。
**测试**：每种入口都登记成 Task；状态机迁移；订阅直通；停止 / 续跑回归。
**验收**：定时任务、手机发的任务、盯 PR 触发的修复，在任务中心里都能看到来源、授权和进度，能统一停止。

### C3. 第 3 期：Watchdog

**交付**：
- 客户端定时（如每 5 分钟）检查进行中的 Task：最近几步有没有新进展（新文件、计划推进、工具结果变化）。
- 判断「卡住 / 空转 / 跑偏」时提醒员工；连续空转超过阈值自动停下（照常补结果、写审计）。
- 用便宜模型（`title` 场景或新增 `watchdog` 场景），按用途统计里记成 `watchdog`。
- 服务端只看心跳超时（客户端离线时任务中心标出来）。

**测试**：空转判定、提醒、自动停止；不误伤正常的长任务（长时间编译、跑测试）。
**验收**：故意让一个任务反复做同一件事，几分钟内收到提醒并被停下；正常长任务不受影响。

### C2. 第 2 期：Router

**交付**：
- 常驻的轻量 Router 会话（第一期放在客户端 `AgentHost`），便宜模型，**没有执行类工具**，
  只有「归到 Thread / 开 Task / 派 Worker / 回复员工 / 请求确认」这些调度动作。
- 接未订阅的事件（渠道消息、事件触发）：判断归到哪个对话、开新任务还是忽略；先程序过滤（来源、白名单、限流），再模型判断。
- 员工一句话交代多件事时拆成多个 Task 分派；按 Task 汇总进度，多个确认合成一张卡片。
- 新的事件来源默认只观察（预演）。

**安全**：Router 工具清单单测断言没有执行类工具；Worker 权限 ≤ Task 授权；事件内容按数据处理（注入用例）。
**成本**：Router 走便宜模型；按用途统计加 `router`，上线前后对比。
**验收**：同时交代 3 件事，员工只看到一个汇总和合并后的确认卡片；钉钉里的追问自动归到对应任务。

### C4. 第 4 期：并行写（隔离）

**交付**：
- 多个会改文件的 Worker 同时干活时，每个在自己的隔离环境里：本机 Docker 沙箱（沙箱第一期已有，一个 Worker 一个容器）
  或独立 git worktree + 任务分支；产出用 PR 交回，由主对话 / Router 汇总。
- 本机上的写操作仍然一次一个、照常确认；并行写只在沙箱里。
- 只读 Worker 的并行数沿用现有上限（第 0 期不做，保持 3）。

**依赖说明**：`architecture-vnext.md` 里写的依赖是「沙箱第二期」（远程执行节点 + GitHub App）。
如果第二期还没做，先用**本机 Docker 沙箱**实现每个 Worker 一个容器，远程节点以后再接。

**测试**：两个写 Worker 改同一个仓库互不覆盖；出口只推 `flyknit/` 任务分支；容器外的文件不被改。
**验收**：让它「同时修这 3 个 bug」，得到 3 个独立的任务分支 / PR，各自 CI 通过。

---

## 不在本清单里的

- VNext 第 0 期（只读子代理并行数可配置）、第 5 期（Router 上服务端）。
- token 优化第 4 项（复盘改用便宜模型、减少触发），等按用途统计跑几天有数据再定。
- REA / Craft Anything / Playwright 实机接入指南（等你确认要不要写）。
- 沙箱第二期（远程执行节点 + GitHub App）。
- GitLab / Gitee 支持。
