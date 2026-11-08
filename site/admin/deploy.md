# 部署服务端

服务端负责转发 AI 请求、下发安全设置、管理员工电脑。员工电脑只和它通信，模型密钥只存在服务端。

## 准备

| | 要求 |
| --- | --- |
| 服务器 | 一台员工电脑能访问到的机器（Linux 或 Windows），装 Docker（推荐）或 Python 3.11+；要能访问你们使用的模型服务 |
| 打包用的电脑 | 一台 Windows 10/11，装 .NET 8 SDK 和 Node.js 20+，只用来编译员工端 |
| 员工电脑 | Windows 10（1809 及以上）或 Windows 11 |

## 方式一：Docker（推荐）

在服务端代码的 `server` 目录：

```bash
cp .env.example .env
```

编辑 `.env`，**这三项必须改**：

```ini
FLYKNIT_ADMIN_TOKEN=自己想一个足够长的管理员令牌
FLYKNIT_SECRET_KEY=一长串随机字符，用来加密模型密钥，部署后不要再改
FLYKNIT_ENROLLMENT_KEY=员工电脑注册用的密钥
```

::: warning
`FLYKNIT_SECRET_KEY` 改了以后，已保存的模型密钥都会解不开，需要重新配置。换服务器时要和数据一起带走。
:::

启动：

```bash
docker compose up -d
docker compose logs -f server
```

这套配置同时起了 PostgreSQL，数据存在 Docker 卷里，升级不丢。公司已有 PostgreSQL 时，用 `docker-compose.external-db.yml`，在 `.env` 里填好 `POSTGRES_HOST / PORT / DB / USER / PASSWORD`。

## 方式二：直接用 Python 运行

适合先试用：

```bash
cd server
python -m venv .venv
. .venv/bin/activate          # Windows: .venv\Scripts\activate
pip install -r requirements.txt
cp .env.example .env          # 同样改上面三项
uvicorn app.main:app --host 0.0.0.0 --port 8000
```

默认用 SQLite，人数不多完全够用。正式使用建议配成系统服务（Linux 用 systemd，Windows 用 nssm）。

## 验证

浏览器打开 `http://<服务器地址>:<端口>/healthz`，看到 `{"status":"ok"}` 即可。部署好之后：

| 地址 | 内容 |
| --- | --- |
| `/` | 产品主页，员工可以在这里下载 |
| `/admin` | 管理后台 |
| `/help` | 本帮助中心 |

## 升级

拉取新代码后重新构建、启动即可，数据库结构会自动补齐（只加不删）：

```bash
docker compose up -d --build
```

## 数据存放

- **数据库**：管理员账号、模型配置（密钥加密保存）、安全设置、设备、用量、审计记录等。SQLite 每天自动备份；PostgreSQL 请自行定时 `pg_dump`；
- **`server/data/`**：员工端安装包、技能包、反馈附件，换服务器时要一起拷；
- **`server/.env`**：换服务器时原样带走。
