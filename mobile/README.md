# FlyknitBuddy 安卓 App（uni-app x）

员工手机（含 PDA 扫描枪）上的 FlyknitBuddy：

- **对话、翻译**：直接走公司服务器调模型，逐字显示
- **办事**：交给员工自己电脑上的 FlyknitBuddy 执行，执行到哪一步、结果、需要确认的操作，实时推到手机
- **运行中接着说**：电脑在办事时再发一句默认是插话，长按发送可以「打断并优先处理」或「排到这件事之后」；对话、翻译回答到一半再发的话排在本机，回答完按顺序发（能撤回、修改）
- **过程折叠**：办完的任务卡片把执行步骤收成一行「电脑上执行了 N 步」，点「展开」查看
- **扫码绑定**：电脑上「设置 → 手机」显示二维码，手机扫一下就绑定；手机继承那台电脑的身份和额度
- 左侧对话管理（从屏幕左边缘右滑打开），配色、logo 与电脑端一致，支持浅色 / 深色

只在公司 Wi-Fi 下使用（服务器在内网）。后台「手机端」里开启后才能用，默认关闭。

## 目录

```
mobile/
├── manifest.json / pages.json / App.uvue / main.uts
├── common/
│   ├── theme.uts      配色（和 client/web/src/styles/base.css 同一套）、深浅色
│   ├── store.uts      绑定信息、本地对话、待确认的操作
│   ├── api.uts        HTTP（配对、办事、审批）、二维码内容解析
│   ├── socket.uts     WebSocket：聊天流、办事进度、审批请求、自动重连、系统通知
│   ├── markdown.uts   回答的 markdown → 原生段落块
│   └── scan.uts       摄像头扫码（需要插件，见下）
├── components/md-view 原生渲染 markdown（标题、列表、表格、代码、引用；图表显示为提示卡片）
├── pages/pair         绑定页：摄像头扫码 / PDA 扫描枪 / 手动输入
├── pages/chat         主界面：抽屉、对话 / 翻译 / 办事、办事卡片、确认弹层
└── pages/settings     电脑信息、外观、测试通知、解除绑定
```

## 打包

1. 用 **HBuilderX**（4.x 以上）打开 `mobile/` 目录。
2. `manifest.json` →「基础配置」→ 点「重新获取」生成 DCloud AppID（仓库里留空）。
3. **图标**：`manifest.json` →「App 图标配置」用 `static/logo.png` 自动生成各尺寸。
4. **系统通知**（`uni.createPushMessage`）：在「App 模块配置」里勾选 **uni-push**（manifest 已声明 `uni-push` 模块）。办完事、需要确认时，App 在后台会发系统通知；Android 13 以上首次会请求通知权限。
5. **摄像头扫码**：`manifest.json` 须包含 `uni-barcode-scanning` 与 `Camera` 模块（已配置）。绑定页「扫码绑定」打开 `pages/scan`（`camera mode="scanCode"`）。
   - **PDA 扫描枪**：在绑定页点一下输入框，按扫描键即可，不必开摄像头。
   - 改模块配置后需**重新云打包**才会进包。
6. 「发行 → 原生 App-云打包」生成 APK，在内部分发。

## 发版与升级

1. 每次打包前把 `manifest.json` 的「应用版本号」加 1（比如 100 → 101），版本名称按需改（0.1.0 → 0.1.1），**用同一个签名证书**。
2. 后台「手机端 → App 版本」→「上传 APK」：填和 manifest.json 一样的版本名称、版本号，写更新说明，上传后点「发布」。
3. 员工打开 App（或在「设置 → 检查更新」）时，发现新版本会弹窗，点「下载安装」后调起系统安装界面；第一次会要求允许「安装未知应用」。
4. **还没装 App 的手机**：电脑上「设置 → 手机」→「下载 App」显示下载二维码，手机连公司 Wi-Fi 扫码下载。后台也能「复制下载链接」发给员工。
   下载链接不需要登录，靠链接里每个版本自己的随机串；后台撤回发布或关闭手机端后链接失效。

## 联调

1. 后台「手机端」开启（可选：允许在手机上确认操作、确认等待时间）。
2. 电脑客户端「设置 → 手机」→「显示二维码」。
3. 手机连公司 Wi-Fi，打开 App 扫码。二维码内容是 `flyknit://pair?c=配对码&s=服务器地址`，配对码 5 分钟内有效、只能用一次。

接口在 `server/app/routers/mobile.py`：`POST /api/v1/mobile/pair`、`GET /api/v1/mobile/me`、`POST/GET /api/v1/mobile/tasks`、`GET/POST /api/v1/mobile/approvals`、`WS /api/v1/mobile/ws`。

## 已验证 / 未验证

- 服务端接口、WebSocket 协议（聊天流、办事进度、审批）有自动化测试（`server/tests/test_mobile.py`）。
- `common/markdown.uts` 和二维码解析的逻辑，按 TypeScript 在 Node 里跑过用例。
- **App 本身还没有在 HBuilderX 里编译、也没有上真机**（开发环境里没有 HBuilderX）。首次编译如果报 UTS 类型错误，多半是个别 API 的参数类型写法，按提示改即可。
