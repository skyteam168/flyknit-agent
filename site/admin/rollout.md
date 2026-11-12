# 装到员工电脑

## 打包

在打包用的 Windows 电脑上，进入 `client` 目录：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\publish-client.ps1 -Version 0.3.0
```

生成 `client\dist\FlyknitBuddy-0.3.0.zip`，里面有员工端、命令行 `flyknit`、更新器、运维代理和安装程序。版本号会编进程序，以它为准。

### WebView2 运行时

员工端的界面用的是 Microsoft Edge WebView2 运行时。大部分 Win10 / 11 自带并且会自动更新，但长期不联网、关了 Edge 更新的电脑（常见于 LTSC）可能**没装或版本太旧**，表现是装完一打开就报错。

新版员工端遇到旧运行时会尽量照常打开（只是窗口拖动改由程序处理），实在打不开会提示「WebView2 组件太旧」。要一次性给这类电脑补上：

1. 从微软官网的 WebView2 页面下载 **Evergreen Standalone Installer（x64）**，放到 `client\redist\MicrosoftEdgeWebView2RuntimeInstallerX64.exe`；
2. 打包时加 `-WithWebView2Runtime`：

   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts\publish-client.ps1 -Version 0.3.0 -WithWebView2Runtime
   ```

安装程序和运维代理在电脑上**没装或太旧**时，用包里的离线安装包静默装上（会先校验微软签名，不联网下载）；已经够新的电脑不会重复装。装完离线安装包不会留在 Program Files 里。

::: warning 已经装好的老电脑
运维代理自己目前不会自动升级，靠它补运行时要求代理是这一版以后的。之前装的电脑，用新版安装程序覆盖安装一次即可（对话、记忆、设置都不受影响）。
:::

包会大大约 170 MB，所以只在需要给老电脑补运行时的那个版本里加，之后的版本可以不加。

## 发布

管理后台「**员工端版本**」：上传 zip，版本号填同一个。上传后先是「未发布」，确认无误再点「**发布**」。

## 安装（推荐：安装程序）

1. 同一页点「**下载员工端安装包**」，格式选「安装程序 .exe」，可以起个名字（比如「三车间」）；
2. 把 `FlyknitBuddy-Setup-x.y.z.exe` 拷到员工电脑双击，输入一次管理员密码。安装程序会：
   - 把员工端装到 `C:\Program Files\FlyknitBuddy`，建快捷方式，把命令行 `flyknit` 加入系统 PATH；
   - 装好运维代理（Windows 服务，负责以后的自动升级），用安装包里的凭证注册，不用填注册密钥；
3. 员工登录：加了域的电脑点「授权登录」，没加域的输入 Windows 账号和开机密码。

批量安装：`FlyknitBuddy-Setup-x.y.z.exe /S` 静默安装，可以放进组策略启动脚本或 SCCM。

每下载一次安装包生成一张安装凭证，「已下载的安装包」里能看到每个包登录了几台电脑；包外泄了点「停用」，已登录的电脑不受影响。

## 自动升级

发布新版本后，员工电脑定时检查、后台下载并校验；员工点「重启升级」或关掉程序后自动换好。替换失败会自动回滚到旧版本。

## 协议与隐私

《用户协议》《隐私政策》有一份按本系统实际数据处理方式写的默认文本，在管理后台「协议与隐私」修改。改完后员工下次登录要同意新版本。建议请法务看一遍再发布。
