# 编码模式（AI IDE）

定位：**AI Agent Workspace / AI IDE**。员工在 FlyknitBuddy 里直接看到 AI 改代码，像 Cursor 那样。

## 1. 布局

| 位置 | 内容 |
| --- | --- |
| 左边 | 项目文件 / 技能 / MCP 连接器 / 知识（资料库） |
| 中间 | Monaco 编辑器：多标签、AI 改动对比、Ctrl+S 保存 |
| 右边 | AI Agent：就是这个对话（消息 + 输入框），编码模式下放到最右边、可拖宽度 |
| 底部 | 终端 / 日志 / Agent Trace（这一轮 AI 每一步做了什么，可打开完整链路） |

标题栏的「编码」按钮进出；只在 **开发者工具开着**（安全中心 `developer_tools`，默认关）、办事对话选了工作区时出现。
布局（各栏宽高、选中的标签）记在本机，下次打开照旧。

## 2. 为什么不内嵌完整 VS Code

调研过 openvscode-server、code-server、微软 VS Code Server、Eclipse Theia：

- 官方只发 Linux 版本 / 没有 Windows 安装包，或者二进制受微软许可约束不能随我们分发；
- 微软插件市场条款只允许微软自家产品使用，只能用 Open VSX，而 Open VSX 2025–2026 年多次被恶意插件（GlassWorm）攻击；
- 内嵌 IDE 自带终端、任务、插件，直接在员工电脑上执行，绕过命令分级、确认、审计（违反开发规则第 3 节）。

所以只用 **Monaco**（VS Code 的编辑器内核，MIT）。AI 照样走我们自己的工具（`write_file` / `edit_file` / `multi_edit` / `run_shell` …），
确认、审计、分级一样不少；没有插件生态。

## 3. 实现

- web：`client/web/src/features/code/`（`registerFeature`，事件前缀 `code.`）。Monaco 按需加载（第一次进编码模式才下载），
  只带编辑器本体 + 常用语言的语法高亮，不带 TS/JSON/CSS/HTML 语言服务。
- Core：`Flyknit.Core/Code/CodeFeature`（`IBridgeModule`，方法 `code.load` / `code.save`）。
  - 只读写这个对话工作区里的文件（`WorkspaceBrowser.Resolve`，`..\`、绝对路径跳不出去）；
  - 路径上有符号链接 / 目录联接指到工作区外的：能看不能存；
  - 二进制（开头 8KB 有 0 字节）、超过 200 万字符的：只读；
  - 保存带打开时的版本号：期间被 AI 改过就不覆盖，界面让员工选「用 AI 的」或「用我的」（沿用文档画布 `DocumentCanvas`）；
  - 员工自己保存不走 AI 的命令确认（和在记事本里改一样），每次保存记日志（只记路径和字节数）。
- 宿主：AI 的 `write_file` / `edit_file` / `multi_edit` 成功后发 `code.changed`（完整路径）。界面里开着的文件刷新成对比视图；
  「跟随 AI」开着时自动打开没开过的文件（没开过的不知道改之前的样子，只标「AI 改过」）。
- 底部「终端」复用右侧终端标签；「日志」是这个对话每次工具调用的完整输出；「Agent Trace」是这一轮的工具步骤，
  「完整链路」打开已有的执行链路（每一步耗时、token）。

## 4. 后续可以做

- 在编辑器里选中一段代码「问 AI / 让 AI 改这里」；
- 「在 VS Code 中打开」按钮（员工自己装了 VS Code 时）；
- 文件树跟着 AI 新建 / 删除文件自动刷新（现在点刷新按钮）。
