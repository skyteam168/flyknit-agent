# 配置模型

员工端发请求时不指定具体模型，只说**场景**，服务端按场景路由到你配置的模型：

| 场景 | 用途 |
| --- | --- |
| `agent` | 办事（调用工具） |
| `code` | 开了开发者工具的办事任务（没单独配置时用 `agent` 的模型） |
| `chat` | 对话 |
| `translate` | 翻译 |
| `title` | 生成标题、整理记忆这类短请求 |
| `vision` | 带图片的请求 |
| `embedding` | 向量（按意思检索记忆；不配就只按字面匹配） |
| `asr` | 语音转文字 |

默认一个模型管所有场景，也可以给不同场景配不同模型，并设置**备用模型**：主模型出问题时自动切过去。

## 用管理后台配置

管理后台「模型」页：先添加提供方（地址和密钥），再添加模型，最后在场景路由里选每个场景用哪个模型。

## 用命令行配置

在 `server` 目录执行（Docker 部署时前面加 `docker compose exec server`）：

```bash
# OpenAI 兼容的内部推理服务
python -m scripts.setup_model --base-url http://<模型服务地址>/v1 --model <模型名> --name <显示名>

# 云上的模型服务：--sync 同步全部对话模型，员工可以在输入框里切换
python -m scripts.setup_model --provider-name <名称> --base-url <服务地址> --api-key <密钥> --sync --model <默认模型>

# 查看当前配置
python -m scripts.setup_model --list
```

`--scenes agent,chat` 只配某几个场景，`--fallback` 设为备用模型。

## 思考模式

不同模型打开「深度思考」的参数写法不一样。在模型设置里选「思考开关写法」和「思考回传」，员工端不用针对某家模型做特殊处理。

## 额度

管理后台「配额与用量」：设每台电脑每天的 token 上限（全局默认，也可以单独调整某台）。超额后服务端直接拒绝，员工界面提示联系 IT。
