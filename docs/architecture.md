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

## 客户端与 Web 界面的通信

WebView2 中的页面通过 `window.chrome.webview.postMessage` 发送 JSON 消息，宿主通过 `PostWebMessageAsJson` 推送事件。消息格式：

```json
{ "type": "chat.send", "id": "req-1", "payload": { "conversationId": "...", "text": "..." } }
```

宿主推送的事件类型：`chat.delta`、`chat.reasoning`、`tool.started`、`tool.confirm`、`tool.finished`、`plan.updated`、`chat.done`、`chat.error`、`conversation.updated`。

完整列表见 `client/web/src/bridge.ts` 与 `client/src/Flyknit.Client/Bridge/WebBridge.cs`，两边需保持一致。

## 本地目录

```
%APPDATA%\Flyknit\
├─ settings.json      服务器地址、设备 Token、界面语言
├─ data\history.db    会话与消息
├─ memory\            agent.md / soul.md / role.md / memory.md
└─ skills\            已安装的 Skills（org\ 为企业下发，只读）
```
