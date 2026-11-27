# 可观测性（OpenTelemetry 导出）

状态：第一期已实现（追踪），总开关默认关闭。2026-11。

## 1. 为什么

后台的用量统计、审计日志能回答「谁用了多少」「谁做了什么」，但回答不了运维的问题：
某个任务为什么这么慢、哪个模型最近错误多、哪个连接器的工具经常失败、一个任务里调了几次模型。
公司如果已经有可观测平台（Grafana、Jaeger、SigNoz、云厂商的 APM），最好把 FlyknitBuddy 的数据也接进去，和别的系统放在一起看。

## 2. 做法

只在服务端做，员工电脑不用改、也不直连采集器：

| 数据 | 从哪来 | 变成什么 |
| --- | --- | --- |
| 模型调用 | 网关 `/api/v1/chat/completions` 转发上游 | span `chat <模型>`（kind=CLIENT），GenAI 语义约定：`gen_ai.request.model`、`gen_ai.usage.input_tokens` / `output_tokens`，另有场景、用途、缓存 token、HTTP 状态 |
| 工具调用 | 员工端上报的审计 `/api/v1/audit` | span `execute_tool <工具>`：`gen_ai.tool.name`、风险判定、谁批准的、成功与否、谁调的（主助手 / 专家）、连接器来源 |

- **同一个任务一条 trace**：trace id 由「电脑 + 任务编号」（没有任务编号时用对话编号）算出来；
- **格式**：OTLP/HTTP 的 JSON 编码，`POST <地址>/v1/traces`，不加任何第三方依赖（`services/telemetry.py`）；
- **发送**：内存队列（最多 5000 条，满了丢最旧的并计数），后台每 5 秒批量发一次（每批最多 500 条）；
  采集器出错这一批丢掉、记下原因，**不重试不堆积**，也不影响任何业务请求。

| 层 | 位置 |
| --- | --- |
| 服务端 | `app/services/telemetry.py`（队列、编码、发送、统计）、`app/routers/observability.py`（后台设置、测试） |
| 服务端 | `routers/gateway.py`（模型调用）、`routers/client.py` 的 `/audit`（工具调用）、`main.py`（后台发送循环） |
| 后台 | `admin/src/views/Observability.vue`「运维 → 可观测性」 |

## 3. 规则

1. **不带内容**：不发提示词、回答、工具参数、工具结果、文件路径。
2. **默认不带身份**：只带电脑编号和部门；「带上电脑和使用者」打开后才带 `host.name`、`enduser.id`。
3. **总开关**默认关；关着时什么都不进队列。
4. **谁能改**：看——管理员都能看（请求头只显示名字）；改、测试——超级管理员。请求头的值（API Key 等）用 `crypto.encrypt` 加密存储，后台不回显。
5. **配置生效**：保存后立即生效（当前进程）；多进程部署时，后台发送循环每 30 秒重新读一次配置。

## 4. 验收

- 服务端：`tests/test_observability.py`（权限、请求头不回显、关着不进队列、OTLP JSON 格式、GenAI 属性、不带内容、同一任务同一条 trace、
  采集器出错丢弃不堆积、带身份要手动打开、队列有上限、测试按钮）。
- 真机：起一个 OpenTelemetry Collector（或 Jaeger all-in-one，开 OTLP/HTTP 4318 端口），后台填地址、点「发一条测试数据」，
  再让员工跑一个办事任务，在 Jaeger 里按 `flyknitbuddy` 服务查到这条任务的追踪。

## 5. 以后

- 指标（metrics）：按场景、模型的 token、延迟直方图、工具失败率（现在可以在采集器里从 span 生成）；
- 员工端本机步骤（规划、压缩、等待确认）的细分 span；
- 日志（logs）导出：拦截、钩子、数据脱敏事件。
