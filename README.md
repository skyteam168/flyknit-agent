# FlyknitBuddy 智能办公助手

<img src="docs/flyknitbuddy-logo.svg" width="96" alt="FlyknitBuddy" />

面向工厂办公电脑的桌面 AI 助手：右下角一个悬浮球，点开就能用中文、越南语或英语对话、翻译、整理文件、分析表格、安装软件、操作电脑。

完整技术方案见 [docs/architecture.md](docs/architecture.md)。

## 功能一览

| | |
| --- | --- |
| **三种模式** | 办事（调用工具操作电脑）、对话、翻译（中 / 越 / 英 / 柬等 8 种语言） |
| **界面语言** | 简体中文、Tiếng Việt、English |
| **安全** | 危险命令（rm -rf、格式化、改系统目录等）直接拦截；三档权限：仅可查看 / 工作区内修改 / 完全权限 |
| **人工确认** | 按命令副作用分级：查询和工作区内生成文件的命令直接跑；删除、覆盖、改注册表 / 服务 / 账号、装卸软件每次都问 |
| **工作区** | 每个任务绑定一个文件夹，产出文件都放在里面 |
| **记忆** | 自动压缩长对话；记住用户偏好和习惯；每次任务后复盘，沉淀经验教训；同类任务成功多次自动生成技能 |
| **技能** | 兼容社区 SKILL.md 标准，可从 GitHub、社区链接、zip、共享盘安装，支持热插拔和启停 |
| **产出文件** | 任务生成的文件在对话里直接给出卡片：右侧分屏预览（Word / Excel / PPT / PDF / 图片 / CSV / 代码 / 压缩包）、用默认应用打开、在资源管理器中定位 |
| **定时任务** | 每天 / 每个工作日 / 每周 / 每月 / 仅一次，到点自动执行，关机错过的开机补跑 |
| **用量** | 按模式统计 token，管理员可设每台电脑每天的上限 |
| **其他** | Windows 系统通知（可在通知上直接确认）、多轮对话与回收站、点赞点踩、编辑重发、模型切换 |

## 整体结构

```
员工电脑（Windows 10/11）              公司服务器（Linux / Windows）
┌──────────────────────────┐        ┌─────────────────────────────┐
│ FlyknitBuddy.exe         │        │ FlyknitBuddy Server         │
│  ├ 悬浮球 / 托盘         │ HTTPS  │  ├ 模型网关（场景路由）     │   ┌──────────────┐
│  ├ WebView2 聊天界面     │ ◄────► │  ├ 管理接口                 │ ◄─┤ 内部模型     │
│  ├ Agent 循环 + 工具     │  8000  │  ├ 命令策略下发             │   │ 阿里云百炼   │
│  └ 本地记忆 / 技能       │        │  ├ 技能库                   │   └──────────────┘
│    %APPDATA%\Flyknit\    │        │  └ 用量配额 / 审计          │
└──────────────────────────┘        └─────────────────────────────┘
```

员工电脑不直接连模型，所有请求都经过服务器，模型 Key 只存在服务器上。

## 仓库结构

```
flyknit/
├── server/                 Python FastAPI 服务端（模型网关、管理接口、策略、技能库、用量、审计）
│   ├── app/                服务端代码
│   ├── scripts/            管理员命令行工具
│   └── docker-compose.yml
├── client/
│   ├── Flyknit.sln
│   ├── src/Flyknit.Core/   客户端核心库（Agent 循环、工具、命令拦截、会话存储、记忆、技能）
│   ├── src/Flyknit.Client/ WPF 外壳（悬浮球、托盘、系统通知、WebView2 主窗口）
│   ├── tests/              核心库单元测试
│   ├── web/                聊天界面（Vue 3 + TypeScript，中 / 越 / 英三语）
│   ├── run.bat             一键编译并启动（开发调试用），纯英文，只负责调起 run.ps1
│   └── run.ps1             实际的编译步骤；任何一步失败都会停住并打印日志，日志写在 client\build.log
└── docs/                   架构与开发文档
```

---

# 部署步骤

整个过程分四步：**部署服务端 → 配置模型 → 打包客户端 → 装到员工电脑**。
第一次部署大约需要半小时，之后新增一台员工电脑只要几分钟。

## 准备

| | 要求 |
| --- | --- |
| **服务器** | 一台能被厂内网络访问的机器，装 Docker（推荐）或 Python 3.11+。要能访问模型服务（内部推理服务器 / 阿里云百炼） |
| **打包用的电脑** | 任意一台 Windows 10/11，装 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) 和 [Node.js 20+](https://nodejs.org/)。只用来编译，员工电脑不需要装这些 |
| **员工电脑** | Windows 10（1809 及以上）或 Windows 11。Win11 自带 WebView2；Win10 如果没有，装一次 [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)（免费、静默安装） |
| **网络** | 员工电脑能访问服务器的 8000 端口 |

> Windows 7 不支持。

---

## 第一步：部署服务端

### 方式 A：Docker（推荐）

```bash
git clone https://github.com/skyteam168/flkynit-agent.git
cd flkynit-agent/server
cp .env.example .env
```

编辑 `.env`，**三个值必须改**：

```ini
FLYKNIT_ADMIN_TOKEN=自己想一个长一点的管理员令牌
FLYKNIT_SECRET_KEY=一长串随机字符，用来加密模型 Key，部署后不要再改
FLYKNIT_ENROLLMENT_KEY=发给员工的注册密钥
```

> `FLYKNIT_SECRET_KEY` 改了之后，已经保存的模型 Key 和 SMB 密码会解不开，需要重新配置。

启动：

```bash
docker compose up -d
docker compose logs -f server      # 看启动日志，Ctrl+C 退出查看
```

这套 compose 同时起了 PostgreSQL，数据和技能包都存在 docker 卷里，升级不丢。

### 方式 B：直接用 Python 跑

适合先试用，或者公司不方便用 Docker。

```bash
cd server
python -m venv .venv
. .venv/bin/activate                  # Windows: .venv\Scripts\activate
pip install -r requirements.txt
cp .env.example .env                  # 同样改上面三个值
uvicorn app.main:app --host 0.0.0.0 --port 8000
```

默认用 SQLite（`server/flyknit.db`），人数不多完全够用。关掉窗口服务就停了，正式用建议配成系统服务（Linux 用 systemd，Windows 用 nssm）。

### 验证

浏览器打开 `http://<服务器地址>:8000/healthz`，看到 `{"status":"ok",...}` 就成功了。
接口文档在 `http://<服务器地址>:8000/docs`。

---

## 第二步：配置模型

**下面所有 `python -m scripts.xxx` 命令都在 `server` 目录执行**，脚本会自动读取 `.env` 里的管理员令牌。

- Docker 部署的，前面加 `docker compose exec server`，例如
  `docker compose exec server python -m scripts.setup_model --list`
- Python 直接跑的，另开一个窗口，先激活虚拟环境再执行

### 配置内部模型

```bash
python -m scripts.setup_model \
  --base-url http://10.0.0.10:8000/v1 \
  --model qwen3.5-397b --name Qwen3.5-397B
```

### 配置阿里云百炼

```bash
python -m scripts.setup_model --provider-name 阿里云百炼 \
  --base-url https://<你的工作空间>.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1 \
  --api-key sk-你的Key \
  --sync --model qwen3.8-max
```

`--sync` 会把该提供方的全部对话模型同步进来，员工在输入框里可以自己切换（向量、语音、图像类模型会自动过滤掉）。
`--model` 指定默认模型。加 `--fallback` 表示作为备用：主模型挂了自动切过去。

### 查看当前配置

```bash
python -m scripts.setup_model --list
```

模型按**场景**路由：`agent` 办事、`chat` 对话、`translate` 翻译、`title` 生成标题、`vision` 识图。
默认一个模型管所有场景，也可以给不同场景配不同模型（`--scenes agent,chat`）。

---

## 第三步：可选配置

这几项不配也能用，按需要开。

### 公司技能库

技能是写给 AI 的工作说明。员工电脑一般上不了外网，所以由服务器统一导入，客户端一键安装。

```bash
# 从 GitHub 仓库导入（仓库里有几个技能就导入几个）
python -m scripts.setup_skill --import https://github.com/owner/skills-repo

# 只要仓库里的某一个
python -m scripts.setup_skill --import https://github.com/owner/skills-repo/tree/main/skills/excel-report

# 社区站（ClawHub 等）：把技能包 zip 的下载链接贴进来
python -m scripts.setup_skill --import https://xxx/excel-report.zip

# 完全离线：上传本地 zip
python -m scripts.setup_skill --upload /tmp/excel-report.zip

# 标记必装：所有电脑自动安装，员工不能停用或卸载
python -m scripts.setup_skill --require excel-report

python -m scripts.setup_skill --list
```

导入时会检查：必须有 SKILL.md 和 description、拒绝路径穿越、拒绝含 .exe 的包、脚本内容过一遍危险命令规则。

### Token 配额

```bash
python -m scripts.setup_quota --daily 200000     # 每台电脑每天 20 万 token
python -m scripts.setup_quota --daily 0          # 不限制（默认）
python -m scripts.setup_quota                    # 看配额 + 各电脑用量排行
```

超额后网关直接拒绝（不会白白消耗上游额度），员工界面上会提示联系 IT，默认显示
`jamesyang@shenzhougroup.com` 和分机 `7815`，要改用 `--email` / `--phone`。

### 命令拦截策略

默认策略已经覆盖 rm -rf、format、diskpart、改注册表、关防火墙、关杀毒、改系统目录等，一般不用动。
要加自己的规则（比如禁止访问某个目录）：

```bash
# 1) 取出当前策略
curl -H "Authorization: Bearer <管理员令牌>" \
  http://localhost:8000/api/v1/admin/policy > policy.json

# 2) 编辑 policy.json：blocked_patterns 加正则，readonly_commands 加可自动执行的只读命令

# 3) 写回（会校验正则是否合法）
curl -X PUT http://localhost:8000/api/v1/admin/policy \
  -H "Authorization: Bearer <管理员令牌>" \
  -H "Content-Type: application/json" -d @policy.json
```

客户端每 10 分钟拉一次策略，改完不用重装客户端。

### SMB 共享盘账号

要让 AI 从共享盘装软件，在管理接口 `PUT /api/v1/admin/smb` 配置服务账号（密码加密保存）。

---

## 第四步：打包客户端

在那台装了 .NET SDK 和 Node.js 的 Windows 电脑上：

```powershell
git clone https://github.com/skyteam168/flkynit-agent.git
cd flkynit-agent\client

# 1) 编译聊天界面
cd web
npm install
npm run build
cd ..

# 2) 打包成不依赖运行时的独立程序
dotnet publish src\Flyknit.Client -c Release -r win-x64 --self-contained true -o publish\FlyknitBuddy
```

产物在 `client\publish\FlyknitBuddy\`，里面的 `FlyknitBuddy.exe` 就是主程序。
整个文件夹约 150 MB，拷到员工电脑任意位置（或放共享盘）即可运行，**员工电脑不需要装 .NET**。

如果公司电脑已经统一装了 .NET 8 Desktop Runtime，可以改用
`--self-contained false`，产物只有十几 MB。

> GitHub Actions 也会自动编译：推送代码后在 Actions 页面下载 `FlyknitBuddy-win-x64`。

---

## 第五步：装到员工电脑

1. 把 `FlyknitBuddy` 文件夹拷到员工电脑，例如 `C:\Program Files\FlyknitBuddy\`
2. 双击 `FlyknitBuddy.exe`
3. 第一次启动会弹出连接窗口，填两项：
   - **服务器地址**：`http://<服务器IP>:8000`
   - **注册密钥**：`.env` 里的 `FLYKNIT_ENROLLMENT_KEY`
4. 连接成功后右下角出现悬浮球，点开就能用

**开机自启**：给 `FlyknitBuddy.exe` 建个快捷方式，放进
`C:\Users\<用户名>\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup`。
批量部署可以用组策略或登录脚本拷贝这个快捷方式。

每台电脑注册后会拿到自己的设备令牌，存在 `%APPDATA%\Flyknit\settings.json`。
管理员可以在 `GET /api/v1/admin/devices` 看到所有电脑，`PATCH` 可以停用某一台。

---

# 日常使用

## 员工怎么用

- **悬浮球**：点一下打开/收起，可以拖到屏幕任意位置，拖文件到球上直接发给 AI
- **三种模式**：输入框左下角切换。办事模式会调用工具操作电脑，另外两种只回答
- **工作区和权限**：输入框下方，办事模式才显示。默认权限是「工作区内修改」，够日常用
- **什么时候会问你**：查目录、看文件、跑构建这些不会打断你；只有删除、覆盖、改注册表 / 服务 / 账号、
  装卸软件这类才会弹确认。认不出的命令也会问，问过以后可以选「以后自动执行」，
  记住的是命令前缀（比如 `mytool report`）加当前工作区，不是某一条命令——删除类命令无论如何都会每次询问
- **侧栏**：技能（安装和启停）、记忆（看 AI 记住了什么）、定时任务、用量（token 消耗和安全记录）、回收站
- **产出文件**：任务生成文件后，对话里会出现文件卡片。点「预览」在右侧分屏打开（分屏边缘可以拖拽调宽窄），
  或者从右边的小箭头里选「用默认应用打开」「在文件资源管理器中显示」
- **系统通知**：只在你看不到这个任务时才弹——窗口被别的程序挡住、最小化、切到别的虚拟桌面、
  你正在看别的任务，或者离开座位超过 2 分钟。窗口就在眼前时不会打扰你

## 定时任务

侧栏「定时任务」→「新建任务」，写一句指令（跟平时在输入框里打的话一样）再选个频率就行，比如：

| 任务名 | 指令 | 频率 |
| --- | --- | --- |
| 整理昨天的报表 | 把共享盘 `\\fs01\报表\昨天` 里的 Excel 合并成一张汇总表，放到工作区 | 每个工作日 08:30 |
| 清理临时文件 | 删掉工作区里 30 天前的临时文件，删之前先列出来给我看 | 每周一 09:00 |

几点要知道的：

- **程序要开着**（收在托盘里就行）。关机期间错过的任务，开机后会补跑最近一次，超过 12 小时就跳过
- 每次运行会新建一个任务会话，列表里点「查看上次结果」就能看完整过程
- 需要确认的操作：以前允许过的同样命令自动通过；其余会弹系统通知，可以直接在通知上点允许。
  **5 分钟没人应答按拒绝处理**，任务会继续往下走。危险命令照样拦截，不会因为是定时任务就放宽
- 任务可以随时用开关停用，或点「立即运行」手动跑一次试试

## 快捷键

| 按键 | 作用 |
| --- | --- |
| Ctrl + Alt + Space | 打开 / 收起主窗口 |
| Ctrl + N | 新建任务 |
| Enter / Shift + Enter | 发送 / 换行 |
| 双击标题 | 重命名任务 |
| 双击顶栏 | 最大化 / 还原 |

## 管理员常用命令

```bash
python -m scripts.setup_model --list        # 模型配置
python -m scripts.setup_skill --list        # 技能库
python -m scripts.setup_quota               # 配额与各电脑用量

# 查看危险命令拦截记录
curl -H "Authorization: Bearer <管理员令牌>" \
  "http://localhost:8000/api/v1/admin/audit?decision=blocked"
```

员工自己也能在「用量」面板里看到本机的拦截和放行记录。

## 数据存放位置

**服务器**：数据库（模型配置、设备、审计、用量）+ `server/data/skills/` 技能包。
Docker 部署的都在卷里，备份 `docker volume` 即可。

**员工电脑**：`%APPDATA%\Flyknit\`

```
settings.json        服务器地址、设备令牌、界面语言、工作区、权限默认值
approvals.json       已记住的命令授权
data\history.db      对话记录、本机安全记录
memory\              agent.md / soul.md / role.md、memory.md 偏好、lessons.md 经验、episodes.json 历史任务
skills\              已安装技能（org\ 企业必装，learned\ 自动沉淀）
logs\                运行日志，排查问题时看这里
```

默认工作区是 `我的文档\Flyknit`（不是上面的数据目录）。

同一台电脑上每个 Windows 账号的技能、记忆、对话、设备令牌都是独立的。
要给整台机器统一预装技能，把技能文件夹拷到 `C:\ProgramData\FlyknitBuddy\skills\`，
这台电脑所有账号都能看到，且是只读的（用户不能停用或卸载）。

## 升级

**服务端**

```bash
git pull
docker compose up -d --build          # Docker
# 或：pip install -r requirements.txt && 重启 uvicorn
```

数据库表结构会在启动时自动升级（缺的表、字段、索引都会补上），不用手动迁移。
升级完成后日志里会有一行 `表结构已升级：...`，没有改动就不打印。

**客户端**

重新执行第四步打包，把新的文件夹覆盖到员工电脑（先关掉 FlyknitBuddy）。
`%APPDATA%\Flyknit\` 不会被动，对话记录、记忆、技能、设置全部保留。

---

# 常见问题

**启动客户端提示「管理员令牌无效」**
服务端用的令牌和 `server/.env` 里的不一致。改完 `.env` 必须重启服务端。

**界面一直白屏**
八成是没编译聊天界面。在 `client\web` 执行 `npm run build`，确认
`client\src\Flyknit.Client\wwwroot\index.html` 存在。还不行就看
`%APPDATA%\Flyknit\logs\` 里当天的日志。

**模型下拉框里只有一个模型**
配置时没加 `--sync`。重新执行一次带 `--sync` 的 `setup_model` 命令。

**提示「服务端尚未配置模型」**
第二步还没做，或者场景路由没配上。执行 `python -m scripts.setup_model --list` 确认。

**编译时提示文件被占用**
FlyknitBuddy 还在运行。`taskkill /F /IM FlyknitBuddy.exe` 之后重试（`run.bat` 会自动做这一步）。

**系统通知点了没反应 / 收不到通知**
第一次用会在开始菜单建一个 FlyknitBuddy 快捷方式（Windows 要求，用来标识发通知的程序），是正常现象。
公司策略关掉通知的话，程序会自动退回到托盘气泡提示。

**服务端日志报 `table xxx has no column named yyy`**
v0.4 之前的版本有这个问题：老库升级上来缺字段。升到最新版本后启动时会自动补齐，
日志里能看到 `表结构已升级：...`。如果还有，把日志发出来。

**员工说「今天的 token 已用完」**
执行 `python -m scripts.setup_quota --daily <更大的数>` 提高额度，立即生效。

---

# 开发

## 本地开发

```bash
# 服务端（热重载）
cd server && uvicorn app.main:app --reload

# 聊天界面（浏览器里预览，不需要 Windows，用的是模拟数据）
cd client/web && npm install && npm run dev

# Windows 客户端：一键编译并启动
client\run.bat
```

`run.bat` 会自动检查前端依赖是否跟得上 `package.json`（比对 `node_modules\.package-lock.json` 的时间戳），
拉到新依赖时自动 `npm install`，不用手动装。每一步的输出都写进 `client\build.log`。任何一步失败窗口都会停住、把报错行挑出来显示，
失败时把 `build.log` 发出来即可，不用截图。它开头还会检查源码是否完整——
**源码必须解压到空文件夹**，覆盖旧目录会留下已删除或改名的文件，照样编译不过。

`npm run dev` 用的是 `src/mockHost.ts` 模拟宿主，可以在浏览器里调整界面，不用每次都编译 WPF。

## 测试

```bash
cd server && pip install -r requirements-dev.txt && python -m pytest -q    # 服务端 85 个
dotnet test client/tests/Flyknit.Core.Tests                                # 核心库 195 个
cd client/web && npx vue-tsc --noEmit                                      # 界面类型检查
```

推送到 GitHub 后 `.github/workflows/ci.yml` 会自动跑服务端测试、核心库测试，并在 Windows 上编译客户端。

## 开发进度

| 阶段 | 内容 | 状态 |
| --- | --- | --- |
| 一 · MVP | 模型网关、后台接口、悬浮球、聊天界面、会话管理、三语界面、翻译模式 | 已完成 |
| 二 · Agent | Agent 循环、文件与命令工具、打开软件、命令拦截与确认、审计 | 已完成 |
| 三 · 工作区与权限 | 工作区、三档权限、命令授权记忆、系统通知、最大化 | 已完成 |
| 四 · 记忆与技能 | 上下文压缩、长期记忆、任务复盘、技能安装与管理、用量配额、安全记录 | 已完成 |
| 五 · 自动化 | 定时任务 | 已完成 |
| 六 · 办公能力 | 管理后台网页、Excel/PPT/Word 模板、SMB 安装服务、自动更新 | 未开始 |
| 七 · 扩展 | 部门角色模板、划词翻译、MCP 接入 | 未开始 |
