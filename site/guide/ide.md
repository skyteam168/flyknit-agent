# 在 IDE 里用

FlyknitBuddy 可以作为 AI 助手接进 **VS Code、Zed、JetBrains（IntelliJ IDEA、PyCharm 等）**，在编辑器旁边的面板里对话、看它改文件和跑命令。用的是 ACP（Agent Client Protocol）标准，背后是同一个 `flyknit.exe`。

前提和 [终端对话](/guide/terminal#开始之前) 一样：IT 打开「命令行 / IDE 接入」、这台电脑登录过 FlyknitBuddy。

## VS Code

**1. 装 ACP 插件**

在扩展商店搜索 `ACP`，装一个 ACP 客户端插件（如「ACP Client」「ACP — Agent Client Protocol」）。

**2. 找到 flyknit.exe 的完整路径**

正式安装的一般是 `C:\Program Files\FlyknitBuddy\flyknit.exe`。不确定时在命令行里运行：

```bat
where flyknit
```

**3. 在用户设置里加上 FlyknitBuddy**

按 `Ctrl+Shift+P` → 输入「Open User Settings (JSON)」，加一段（路径换成第 2 步看到的，`\` 要写成 `\\`）：

```json
"agent_servers": {
  "FlyknitBuddy": {
    "command": "C:\\Program Files\\FlyknitBuddy\\flyknit.exe",
    "args": ["acp"],
    "cwd": "${workspaceFolder}"
  }
}
```

不同插件的配置项名称可能不同（比如 `acp.agents`），以插件说明为准；关键是三样：程序路径、参数 `acp`、工作目录是当前项目。

**4. 完全退出 VS Code 再打开**

VS Code 只在启动时读一次配置和 PATH。重开后在插件的面板里选 FlyknitBuddy 开始对话。

::: tip 一定要写完整路径
插件提示「FlyknitBuddy not installed」「找不到命令」，几乎都是 `command` 只写了 `flyknit`。写成带 `.exe` 的完整路径即可。
:::

## Zed

`settings.json`：

```json
"agent_servers": {
  "FlyknitBuddy": { "type": "custom", "command": "flyknit", "args": ["acp"] }
}
```

## JetBrains

按 JetBrains 的说明添加一个自定义 ACP 智能体（不同版本入口位置不同）：命令填 `flyknit.exe` 的完整路径，参数填 `acp`。

## 在 IDE 里用起来是什么样

- **工作区**就是 IDE 打开的项目文件夹。
- **确认**：需要确认的操作由 IDE 弹出确认框（有的插件显示在窗口顶部中间）。普通写入有「允许一次 / 总是允许 / 拒绝」，删除、覆盖这类高危操作只有「允许一次 / 拒绝」。
- **以前的会话**：每轮对话都会保存在这台电脑上（最多 50 个、保留 30 天，不上传）。关掉 IDE 再打开，可以在插件的会话列表里找到并接着聊；也可以在命令行里用 `flyknit --resume` 接着聊。
- **思考过程**：默认不开深度思考，回答和过程会成段地显示，不会一个字一个字地刷。确实需要时把参数写成 `["acp", "--think"]`。
- **停止**：用插件自己的停止按钮，已经开始的操作会照常收尾。

## 不提供的能力

长期记忆、浏览器、连接器、子代理、向你提问——这些需要 FlyknitBuddy 的界面。IDE 自带的 MCP 服务器也不会接入（连接器只认 IT 配置的）。

::: tip 升级
IDE 开着 FlyknitBuddy 智能体时，FlyknitBuddy 的自动升级会等它退出。要升级时先在 IDE 里断开智能体，或关掉 IDE。
:::

## 遇到问题

| 现象 | 处理 |
| --- | --- |
| 插件显示「not installed」或找不到命令 | `command` 写成带 `.exe` 的完整路径，完全退出 VS Code 再打开 |
| 连上了但开会话时报「IT 没有开启命令行 / IDE 接入」 | 找 IT 在安全中心打开 |
| 报「还没登录」 | 打开桌面端 FlyknitBuddy 登录一次 |
| 很慢 | 确认没加 `--think`；读很多文件的任务（比如分析整个项目）本身要几分钟，面板里能看到每一步在做什么 |
| 会话列表是空的 | 只有升级后的新会话才会保存；会话按项目文件夹分开，换了文件夹列表不一样 |
