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

首次配置模型（以内部 Qwen3.5-397B 为例）：

```bash
# 1. 添加模型提供方
curl -X POST http://localhost:8000/api/v1/admin/providers \
  -H "Authorization: Bearer $ADMIN_TOKEN" -H "Content-Type: application/json" \
  -d '{"name":"内部模型","base_url":"http://10.0.0.10:8000/v1","api_key":"sk-xxx"}'

# 2. 添加模型（provider_id 用上一步返回的 id）
curl -X POST http://localhost:8000/api/v1/admin/models \
  -H "Authorization: Bearer $ADMIN_TOKEN" -H "Content-Type: application/json" \
  -d '{"provider_id":1,"name":"Qwen3.5-397B","model":"qwen3.5-397b","supports_tools":true}'

# 3. 把各场景路由到该模型（chat / agent / translate / title / vision）
curl -X PUT http://localhost:8000/api/v1/admin/routes/agent \
  -H "Authorization: Bearer $ADMIN_TOKEN" -H "Content-Type: application/json" \
  -d '{"model_id":1,"fallback_model_id":null}'
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

首次启动会在 `%APPDATA%\Flyknit\settings.json` 生成配置，填写服务器地址与注册密钥后重启即可。

## 开发进度

| 阶段 | 内容 | 状态 |
| --- | --- | --- |
| 一 · MVP | 模型网关、后台接口、悬浮球、聊天界面、会话管理、三语界面、翻译模式 | 进行中 |
| 二 · Agent | Agent 循环、工具集、命令拦截与确认、本地记忆、审计 | 核心已搭建 |
| 三 · 办公能力 | Excel/PPT/Word、SMB 安装服务、定时任务、Skills 市场、自动更新 | 未开始 |
| 四 · 扩展 | 部门角色模板、划词翻译、MCP 接入、用量配额 | 未开始 |
