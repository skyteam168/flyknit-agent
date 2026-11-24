# 电脑操作（computer use）

状态：第一期（P0 探测 + P1 无障碍树操作）已实现，默认关闭。2026-11。

## 1. 为什么、做什么

工厂里不少老系统（ERP、WMS 客户端等）没有接口、没有网页版，员工每天在里面手工查询、录入。
电脑操作让 AI 直接操作这些 Windows 程序的窗口。

**路线选择**（参考微软 UFO2「能用 API 就用 API，其次控件树，最后视觉」、CursorTouch Windows-MCP「无障碍树、不需要视觉模型」）：

| 优先级 | 方式 | 现状 |
| --- | --- | --- |
| 1 | 连接器 / 中台业务接口 | 已有 |
| 2 | 浏览器工具（网页系统） | 已有 |
| 3 | **UI Automation 无障碍树**（本功能） | 第一期 |
| 4 | 截图 + 视觉模型（自绘界面兜底） | 第二期，未做 |
| 5 | 独立桌面里操作（不打扰员工） | 以后，未做 |

无障碍树读的是控件的名称、类型、状态（读屏软件用的那套），不截图、不需要视觉模型，又准又省 token；
读不到的（自绘界面、远程桌面里的程序、游戏）如实告诉员工，等第二期视觉兜底。

## 2. 结构

| 层 | 位置 | 做什么 |
| --- | --- | --- |
| Core | `Flyknit.Core/Desktop/DesktopModels.cs` | 窗口、控件、快照，驱动接口 `IDesktopDriver` |
| Core | `Flyknit.Core/Desktop/DesktopRisk.cs` | 风险判定、按键白名单（全部有单测） |
| Core | `Flyknit.Core/Desktop/DesktopTools.cs` | 6 个工具 + 功能模块 `DesktopRunExtension` |
| 宿主 | `Flyknit.Client/Services/UiaDesktopDriver.cs` | UI Automation 驱动（读、点、填、按键、滚动） |
| 宿主 | `Flyknit.Client/Windows/DesktopBanner.cs` | 屏幕顶部「正在操作」提示条 + 停止 |
| 宿主 | `Flyknit.Client/Services/AgentHost.Desktop.cs` | 接线：每个对话一个驱动、提示条、任务结束收起 |
| 服务端 | `security_settings.py` | `computer_use`（默认关、锁定）、`computer_use_confirm_all`（默认开、锁定） |
| 服务端 | `default_policy.py`、`admin.py` | 命令策略 `desktop_apps`（允许的程序，默认空），保存时规范化 |
| 后台 | `admin/src/components/DesktopAppsCard.vue` | 安全中心里编辑允许的程序 |
| 工具 | `client/scripts/uia-probe.ps1` | 可行性探测：某个程序能读到多少控件 |

工具：`desktop_windows`（列出允许的窗口）、`desktop_read`（读控件，带编号）、`desktop_click`、`desktop_type`、
`desktop_key`、`desktop_scroll`。工具类别 `desktop`（专家、团队可以整类关掉）。

## 3. 安全

不变量（违反即错误）：

1. **开关**：安全中心「电脑操作」默认关、锁定；关着时功能模块不出场，没有任何 desktop 工具。
2. **程序白名单**：只能操作命令策略 `desktop_apps` 里的程序；空 = 一个都不能操作。窗口按进程名判断，
   `desktop_read` 指定的句柄在执行前按进程名再判一次（防 AI 编句柄）。
3. **永远不能操作**（写死在 `DesktopRisk.NeverAllowed`，IT 写进白名单也不生效）：
   - 命令行、脚本宿主、系统管理工具——否则能绕过 `CommandAnalyzer` / 命令规则；
   - 资源管理器、桌面、开始菜单——否则能绕过删除规则、打开运行框；
   - 浏览器——用 `browser_*`（有外网和提交规则）；
   - 远程桌面、远程控制；
   - FlyknitBuddy 自己（包括 WebView2）——AI 不能自己点「允许」。驱动也不列出本进程的窗口。
4. **按键白名单**（`DesktopKeys`）：只认固定的命名键和 Ctrl/Shift/Alt 组合；Win 键、Alt+Tab/Esc/Space、Ctrl+Alt、
   Ctrl+Esc、单个字母一律不认（防 Win+R 打开运行框敲命令）。
5. **确认**：
   - 密码框（UIA `IsPassword`）、名称像密码 / 验证码的输入框：拦下，AI 不代填（驱动里再拦一次）；
   - 身份证、银行卡：先问；
   - 名称像提交、保存、删除、审批、过账、入库……的按钮，回车、Ctrl+S、Delete、Alt+F4 等按键：先问；
   - 「仅可查看」权限：不能填写、不能点提交类按钮、只能按浏览类按键；
   - 「每一步都先确认」开着（默认）：点击、填写、非浏览类按键都先问；
   - 这些确认都**不能记成「以后自动」**。
6. **界面文字是资料不是指令**：工具输出里标明，提示词里也写了。
7. **员工随时能停**：AI 每次操作前屏幕顶部显示提示条（置顶、不抢焦点），点「停止」立刻停掉该任务；任务结束收起。
8. **审计**：每次工具调用照常写审计（底座的确认与审计不变）。

## 4. 验收

- Core 单测：`DesktopTests`（风险判定、按键白名单、工具行为、功能模块出场条件）。
- 服务端：`test_admin.py` 里 `desktop_apps` 规范化与下发、开关默认值。
- **Windows 真机（必须）**：
  1. 运行 `client/scripts/uia-probe.ps1` 探测要接入的程序，结论为「适合」再加进白名单；
  2. 打开开关、加白名单，让 AI 在测试账套里：查询一条记录、填一个查询条件、翻页、点保存（应弹确认）；
  3. 检查：密码框拒填、白名单外程序列不出来、Win+R 被拒、提示条出现且「停止」有效、任务结束提示条收起。

## 5. 以后

- 第二期：读不到控件时截图 + 视觉模型（`vision` 场景）或服务端部署 OmniParser 做兜底；
- 第三期：参考 UFO2 的「画中画」，AI 在独立桌面会话里操作，员工可以同时干自己的活；
- Java（Swing）程序需要 Java Access Bridge，必要时单独接入。
