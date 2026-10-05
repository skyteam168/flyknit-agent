# Flyknit 智能办公助手

面向工厂办公电脑的桌面 AI 助手：右下角悬浮球，点开即可用中文、越南语或英语与 AI 对话、翻译、处理文件、执行办公任务。

完整技术方案见 [docs/architecture.md](docs/architecture.md)。

## 仓库结构

```
flyknit/
├── server/                 Python FastAPI 服务端（模型网关、管理接口、策略下发、审计）
├── client/
│   ├── Flyknit.sln
│   ├── src/Flyknit.Core/   客户端核心库（Agent 循环、工具、命令拦截、会话存储、记忆、Skills）
│   ├── src/Flyknit.Client/ WPF 外壳（悬浮球、托盘、WebView2 主窗口）
│   ├── tests/              核心库单元测试
│   └── web/                聊天界面（Vue 3 + vue-i18n，中/越/英三语）
└── docs/                   架构与开发文档
```

## 快速开始

### 服务端

```bash
cd server
python -m venv .venv && . .venv/bin/activate      # Windows: .venv\Scripts\activate
pip install -r requirements.txt
cp .env.example .env                               # 修改 ADMIN_TOKEN、SECRET_KEY、ENROLLMENT_KEY
uvicorn app.main:app --host 0.0.0.0 --port 8000
```

或使用 Docker：`docker compose up -d`。

接口文档：启动后访问 `http://<服务器>:8000/docs`。

配置模型（服务端启动后，在 server 目录另开一个窗口执行，脚本会自动读取 .env 中的管理员令牌）：

```bash
# 同步提供方的全部对话模型供客户端选择，并指定默认模型
python -m scripts.setup_model --provider-name 阿里云百炼 \
  --base-url https://<工作空间>.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1 \
  --api-key sk-xxx --sync --model qwen3.8-max

# 内部模型作为默认模型；加 --fallback 则作为备用
python -m scripts.setup_model --base-url http://10.0.0.10:8000/v1 --model qwen3.5-397b --name Qwen3.5-397B

# 查看当前配置
python -m scripts.setup_model --list
```

### 聊天界面（浏览器预览）

```bash
cd client/web
npm install
npm run dev          # 浏览器中使用模拟桥接预览界面
npm run build        # 产物输出到 client/src/Flyknit.Client/wwwroot
```

### Windows 客户端

需要 Windows 10/11、.NET 8 SDK、WebView2 Runtime（Win11 自带）。

```powershell
cd client\web; npm install; npm run build; cd ..
dotnet test tests\Flyknit.Core.Tests
dotnet run --project src\Flyknit.Client
```

首次启动会弹出连接窗口，填写服务器地址与注册密钥即可。配置保存在 `%APPDATA%\Flyknit\settings.json`。

## 开发进度

| 阶段 | 内容 | 状态 |
| --- | --- | --- |
| 一 · MVP | 模型网关、后台接口、悬浮球、聊天界面、会话管理（多轮、自动标题、改名、删除、回收站）、三语界面、翻译模式 | 代码已完成，待 Windows 联调 |
| 二 · Agent | Agent 循环、文件与命令工具、打开软件、命令拦截与确认、本地记忆、Skills 加载、审计 | 代码已完成，待 Windows 联调 |
| 三 · 办公能力 | 管理后台网页、Excel/PPT/Word、SMB 安装服务、定时任务、Skills 市场、自动更新 | 未开始 |
| 四 · 扩展 | 部门角色模板、划词翻译、MCP 接入、用量配额 | 未开始 |

## 测试

```bash
cd server && pip install -r requirements-dev.txt && python -m pytest -q     # 服务端
dotnet test client/tests/Flyknit.Core.Tests                                 # 客户端核心库
```

推送到 GitHub 后，`.github/workflows/ci.yml` 会自动运行服务端测试、核心库测试，并在 Windows 上编译客户端，编译产物可在 Actions 页面下载。

## 快捷键

| 按键 | 作用 |
| --- | --- |
| Ctrl + Alt + Space | 打开 / 收起主窗口 |
| Ctrl + N | 新建对话 |
| Enter / Shift + Enter | 发送 / 换行 |
| F2 或双击 | 重命名对话 |
