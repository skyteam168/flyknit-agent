# 消息通知

状态：已实现（2026-11）。IT 在管理后台写通知，员工端左下角铃铛 → 消息中心查看。

## 1. 消息类型

| kind | 名称 | 用途 |
| --- | --- | --- |
| `upgrade` | 系统升级 | 新版本、升级时间和影响 |
| `feature` | 功能介绍 | 新功能怎么用，配图或短视频 |
| `maintenance` | 停机维护 | 哪个系统、什么时候、影响什么（建议设过期时间） |
| `security` | 安全提醒 | 钓鱼邮件、资料外发、密码安全 |
| `policy` | 制度通知 | 公司制度、流程变更 |
| `event` | 培训活动 | 培训、考试、活动报名 |
| `tip` | 使用技巧 | FlyknitBuddy 的小技巧 |
| `general` | 一般通知 | 其他 |

另有两个跨类型的属性：

- **重要**：员工端置顶；打开程序时没读过的直接弹窗（每条只弹一次）。
- **按钮**：详情底部一个按钮（文字 + http/https 地址），用系统浏览器打开。

后台每种类型都有正文模板，正文为空时可一键套用。

## 2. 数据与接口（服务端）

表（`app/models.py`，`create_all` 新建，不改旧表）：

- `notices`：类型、标题、摘要、正文 HTML、重要、按钮、使用范围（部门 ∪ 电脑，空 = 全公司）、状态（draft / published / withdrawn）、定时发布 `publish_at`、过期 `expire_at`；
- `notice_reads`：哪台电脑什么时候读了（唯一键 notice + device）；
- `notice_media`：上传的图片、视频，文件在 `data/notice_media/{32位hex}`。

接口（`app/routers/notices.py`，规则在 `app/services/notice_service.py`）：

| 路径 | 身份 | 说明 |
| --- | --- | --- |
| `GET/POST /api/v1/admin/notices`、`PUT/DELETE /{id}`、`POST /{id}/publish`、`/{id}/withdraw`、`GET /{id}/reads` | 管理端 | 列表带已读数 / 范围内电脑数；已发布的要先撤回才能删 |
| `POST /api/v1/admin/notices/media` | 管理端 | 按文件头判断类型（PNG/JPG/GIF/WebP ≤10MB，MP4/WebM ≤300MB），不信扩展名 |
| `GET /api/v1/client/notices`、`GET /{id}`、`POST /{id}/read`、`POST /read-all` | 设备令牌 | 只返回已发布、到点、未过期、在范围内的；范围外的详情和标已读都是 404 |
| `GET /api/v1/notices/media/{id}` | 设备令牌 / 管理端令牌 / 后台临时签名 | 支持 Range（视频拖进度条） |

发布、撤回、修改已发布的通知时 `config_events.bump()`，员工端长轮询马上重新拉配置，顺带刷新未读数。

## 3. 富文本安全

正文在员工端 WebView 里用 `v-html` 渲染，所以有两道白名单清洗，规则相同：

1. **服务端保存时**（`notice_service.sanitize_html`，标准库 `HTMLParser`，没有新依赖）；
2. **员工端渲染前**（`features/notices/sanitize.ts`，`DOMParser`）。

规则：

- 只留排版标签（段落、标题、列表、引用、表格、链接、图片、视频、代码……）；`script`、`style`、`iframe`、表单等**连内容**丢掉；不认识的标签去掉标签留文字；
- 事件属性（`onclick` 等）一律不留；行内样式只留 `text-align`、`color`、`background-color`；
- 链接只认 `http`、`https`、`mailto`，加 `target="_blank" rel="noopener noreferrer"`；
- 图片、视频**只认本服务器上传的媒体**，外部地址整个元素丢掉：员工电脑不连外网，也不会被外部图片跟踪谁打开了通知。

## 4. 媒体怎么到员工端

- 正文里存的是正式地址 `/api/v1/notices/media/{id}`；
- 后台编辑器里 `<img>` 带不了 Authorization，所以后台读到的正文里媒体地址带临时签名（`?exp=&sig=`，HMAC，12 小时）；保存时服务端去掉签名，换回正式地址；
- 员工端 Core（`Flyknit.Core/Notices/NoticeFeature`）把正式地址换成 `https://notices.flyknit.local/{id}`，WPF 宿主拦下这个地址，带设备令牌去服务端取，Range 原样转发。界面拿不到令牌，也不直连服务器（服务器常是 http，WebView 里会被当成混合内容拦掉）。ID 不是 32 位小写 hex 的直接 404，不往服务端发。

## 5. 员工端

按扩展架构（`docs/extension-architecture.md`）做成功能模块：

| 层 | 文件 |
| --- | --- |
| Core | `Notices/NoticeFeature.cs`：界面桥模块（`notices.status/list/get/read/readAll`）、未读数刷新、媒体代理入口；`INoticeService` 由 `FlyknitServerClient` 实现 |
| 宿主 | `AgentHost`：构造时注册，拉配置时 `Notices.RefreshAsync()`；`MainWindow`：`notices.flyknit.local` 的资源拦截 |
| Web | `features/notices/`：`store.ts`（`notices.changed` 事件）、`NoticeBell`（侧栏用户卡片上的铃铛）、`NoticesPage`（消息中心）、`NoticeBody`（详情）、`NoticePopup`（重要通知弹窗）、`sanitize.ts`、`meta.ts` |

老服务端没有通知接口（404）时界面不显示铃铛。

## 6. 没做的（以后可以加）

- 手机端消息中心；
- 通知多语言（现在一条通知一种语言；越南语同事多的部门可以单独发一条越南语的）；
- 没被任何通知引用的媒体文件的定期清理。

## 7. 测试

- 服务端 `tests/test_notices.py`：清洗、签名过期、可见性（草稿 / 定时 / 过期 / 部门范围）、已读与撤回、媒体鉴权与 Range、设备不能用管理接口；
- Core `NoticeTests`：媒体地址改走代理、重要通知只弹一次、老服务端、坏的媒体 ID 不外发、刷新出错不抛。
