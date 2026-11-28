# AI 专用文件夹（少弹确认）

员工反馈：工作区选的是专门给 AI 用的文件夹（比如 `D:\AIWORK`，里面都是 AI 的产出），AI 在里面删个临时文件、跑个 Python 脚本还要一次次点「允许」，不像智能体。

## 做法

员工在输入框下方的工作区菜单里勾「设为 AI 专用文件夹」。任务的工作区是这个文件夹、而且 IT 打开了安全中心的 `trusted_folders` 时：

| 操作 | 普通工作区（工作区内修改） | AI 专用文件夹 |
| --- | --- | --- |
| 文件夹里新建、修改文件 | 自动 | 自动 |
| 文件夹里删除文件（delete_path，进回收站） | 确认 | **自动** |
| 认不出的命令（python 脚本、自研工具），只涉及文件夹里面 | 确认 | **自动** |
| 高危命令（del、Remove-Item、覆盖），只涉及文件夹里面 | 确认 | **自动** |
| 涉及文件夹外的路径 | 按权限档 | 按权限档（不变） |
| 动态执行（`& $cmd`、管道给 iex、编码命令、下载即执行） | 确认 / 禁止 | 确认 / 禁止（不变） |
| 禁止规则、系统级工具、网络白名单 | 拦 | 拦（不变） |
| 读到疑似提示词注入以后 | 除只读外都确认 | 同左（不变） |
| 一次删除超过「批量删除阈值」的目录 | 确认 | 确认（不变） |

只放宽文件夹里面，不扩大范围。和「完全权限」不同：完全权限能改工作区外的文件；AI 专用文件夹不能。

## 两道闸

1. **IT**：安全中心 `trusted_folders`，默认关、默认锁定，可以按全厂 / 部门 / 单台电脑设置。关着时员工看不到这个选项，以前标过的也不生效（`ToolContext.TrustedWorkspace` 同时要求「员工标了」和「IT 开着」）。
2. **不能标的地方**（`TrustedFolders.Ineligible`，判断前先解开 junction / 符号链接）：
   - 磁盘根目录、网络共享（`\\server\share`）、网络盘；
   - 系统目录（Windows、System32、Program Files、ProgramData）以及包含它们的上层文件夹；
   - 用户目录本身、它下面第一层（桌面、文档、下载……整个文件夹）、AppData；
   - 不存在的文件夹。
   每次判定时都重新检查：标过的文件夹后来被换成指向系统目录的链接，就不再算 AI 专用。

## 实现

- Core：`Security/TrustedFolders.cs`（存储、能不能标、`trust.get` / `trust.set` 桥接模块），`PermissionRules.ForWrite / ForCommand` 的 `trusted` 参数，`ToolContext.WorkspaceMarkedTrusted / TrustedWorkspace`；专家、子代理跟着同一个工作区走。
- 存储：本机 `%APPDATA%\Flyknit\trusted-folders.json`，只对这台电脑、这个 Windows 用户有效，最多 50 个。
- 界面：`client/web/src/features/trust/`，开关在 `WorkspacePicker`；打开前弹确认说明后果。
- 审计：每次工具调用照常写审计，判定原因里写「AI 专用文件夹，只影响文件夹里面：…」；标上 / 取消写进客户端日志。
- 测试：`TrustedFolderTests`。

## 同一批修的误判

- `$env:PYTHONIOENCODING="utf-8"` 这类环境变量赋值以前被当成「执行变量里的内容」。现在只有调用操作符（`& $x`、`. $x`）、`.Invoke()`、`[scriptblock]::Create` 才算；右边会执行命令的赋值（`$env:X = (Remove-Item a)`）不当成无害。
- `> $null` / `> nul` 不算写文件。
- 完全权限下，认不出的命令提到工作区外的**普通**文件夹（比如 python 里读 `~\Documents\Flyknit\output`）不再确认——完全权限本来就允许改工作区外的文件；路径解析不出来、磁盘根目录、受保护的系统目录照样确认。
- 动态执行以前在完全权限下会被直接放行（判定器给的「要确认」在权限档那层丢了），现在任何权限档都要确认。
