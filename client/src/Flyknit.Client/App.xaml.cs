using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Flyknit.Client.Bridge;
using Flyknit.Client.Services;
using Flyknit.Client.Windows;
using Flyknit.Core.Translation;

namespace Flyknit.Client;

public partial class App : Application
{
    private const string MutexName = "Local\\Flyknit.Client.SingleInstance";
    private const string PipeName = "Flyknit.Client.Activate";
    private const int HotkeyId = 0xF17;
    private const int TranslateHotkeyId = 0xF18;
    private const int ScreenshotHotkeyId = 0xF19;

    private Mutex? _mutex;
    private AppSettings _settings = new();
    private AgentHost? _host;
    private MainWindow? _main;
    private FloatingBall? _ball;
    private System.Windows.Forms.NotifyIcon? _tray;
    private NotificationService? _notifications;
    private NoticeWatcher? _noticeWatcher;
    private InstructionPoller? _instructionPoller;
    private HwndSource? _hotkeySource;
    private TranslatePopup? _translatePopup;
    private bool _capturingSelection;
    private bool _capturingScreen;
    private CancellationTokenSource _pipeCts = new();
    private bool _cleanedUp;
    private int _activeRuns;
    private int _pendingConfirms;
    private DateTime _lastErrorNotice = DateTime.MinValue;
    private int _crashing;

    /// <summary>
    /// 快捷方式的「起始位置」是程序目录，进程的当前目录就停在那里，起的子进程（更新器、MCP 连接器、命令行）也跟着继承。
    /// 谁的当前目录在程序目录里，谁就占着它，升级时整个目录挪不动（「被另一个进程占用」）。开机就挪走。
    /// </summary>
    private static void LeaveInstallDirectory()
    {
        try
        {
            Directory.SetCurrentDirectory(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }
        catch (Exception ex)
        {
            Log.Warn("切换当前目录失败", ex);
        }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        LeaveInstallDirectory();
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("未处理的界面异常", ex.Exception);
            ex.Handled = true;
            NotifyUnexpectedError();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            Log.Error("未处理的异常", ex.ExceptionObject as Exception);
            // 进程马上就没了：至少让员工知道发生了什么。只弹一次，弹框本身出错也不要再进来
            if (Interlocked.Exchange(ref _crashing, 1) == 0)
            {
                try
                {
                    System.Windows.MessageBox.Show(
                        $"FlyknitBuddy 遇到严重错误，即将退出：{(ex.ExceptionObject as Exception)?.Message}\n\n日志在 {AppPaths.Logs}。请重新打开 FlyknitBuddy，如果反复出现请把日志发给 IT。",
                        "FlyknitBuddy", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch (Exception)
                {
                    // 弹不出来就算了，日志已经记下
                }
            }
        };
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log.Error("未观察到的任务异常", ex.Exception);
            ex.SetObserved();
        };

        try
        {
            await StartAsync(e);
        }
        catch (Exception ex)
        {
            FailStartup(ex);
        }
    }

    /// <summary>
    /// 启动中途出错：以前进程会隐身挂着、一直占着单实例锁，之后再怎么双击都没反应。
    /// 现在告诉员工，收拾干净后退出，下次能正常打开。
    /// </summary>
    private void FailStartup(Exception ex)
    {
        Log.Error("启动失败", ex);
        try
        {
            System.Windows.MessageBox.Show(
                $"FlyknitBuddy 启动失败：{ex.Message}\n\n日志在 {AppPaths.Logs}。请把日志发给 IT，或重启电脑后再试。",
                "FlyknitBuddy", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception boxEx)
        {
            Log.Warn("显示启动失败提示出错", boxEx);
        }
        // 已经起来的托盘、悬浮球、后台服务收掉；已经下好的更新顺带装上，修复版本才送得到
        try
        {
            Cleanup(applyUpdate: true);
        }
        catch (Exception cleanupEx)
        {
            Log.Warn("启动失败后清理出错", cleanupEx);
        }
        // Cleanup 中途出错时单实例锁可能还没放
        try { _mutex?.ReleaseMutex(); }
        catch (Exception) { /* 已经放掉了，或者本来就不是我们的 */ }
        _mutex?.Dispose();
        _mutex = null;
        Shutdown(1);
    }

    /// <summary>界面线程上的意外错误：已经兜住了，提醒一下员工。30 秒内最多一次，免得连着弹。</summary>
    private void NotifyUnexpectedError()
    {
        var now = DateTime.UtcNow;
        if (now - _lastErrorNotice < TimeSpan.FromSeconds(30))
        {
            return;
        }
        _lastErrorNotice = now;
        try
        {
            _tray?.ShowBalloonTip(5000, "FlyknitBuddy", "出了点问题，已记录日志。如果功能不正常，请重启 FlyknitBuddy。", System.Windows.Forms.ToolTipIcon.Warning);
        }
        catch (Exception ex)
        {
            Log.Warn("显示错误提示失败", ex);
        }
    }

    /// <summary>Windows 注销、关机：和正常退出一样收拾、保存设置。</summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        if (e.Cancel)
        {
            return;
        }
        Log.Info($"Windows 正在{(e.ReasonSessionEnding == ReasonSessionEnding.Shutdown ? "关机" : "注销")}，退出");
        try
        {
            Cleanup(applyUpdate: false);
        }
        catch (Exception ex)
        {
            Log.Warn("注销/关机时清理出错", ex);
        }
    }

    private async Task StartAsync(StartupEventArgs e)
    {
        // 单实例：已运行时通知原实例显示窗口
        _mutex = new Mutex(true, MutexName, out var isFirst);
        if (!isFirst)
        {
            // 资源管理器右键菜单起的新进程：把参数交给已经在运行的那个，自己退出
            await NotifyExistingInstanceAsync(e.Args);
            Shutdown();
            return;
        }
        ListenForActivation();

        // 先把数据目录标记为加密，再去读设置、开数据库——之后新建的库文件、WAL、
        // journal 都会自动继承这个属性。老用户升上来时目录里已经有明文文件，单独补一遍。
        ProtectDataAtRest();

        _settings = AppSettings.Load();
        NativeStrings.Language = _settings.ResolveUiLanguage();

        if (_settings.IsRegistered && Flyknit.Core.Setup.Provisioning.ServerReplaced(AppContext.BaseDirectory, AppPaths.Root, _settings.ServerUrl))
        {
            Log.Info($"安装程序换了服务器地址（原来 {_settings.ServerUrl}），按新的重新登录");
            _settings.DeviceToken = "";
            _settings.Save();
        }

        if (!_settings.IsRegistered)
        {
            var setup = new LoginWindow(_settings);
            if (setup.ShowDialog() != true)
            {
                Shutdown();
                return;
            }
        }

        _host = new AgentHost(_settings);
        _host.ReregistrationRequired += OnReregistrationRequired;
        _host.ExitRequested += () => Dispatcher.BeginInvoke(() => ExitApp(applyUpdate: false));
        _host.LogoutRequested += () => Dispatcher.BeginInvoke(() =>
        {
            // 清掉令牌再重启：新进程看到「未登录」就会先弹登录窗口
            _settings.DeviceToken = "";
            _settings.Save();
            Restart();
        });
        _main = new MainWindow(_host, _settings);
        _main.UiLanguageChanged += lang =>
        {
            NativeStrings.Language = lang;
            BuildTrayMenu();
            _ball?.RefreshText();
            ExplorerMenuRegistration.Apply(_settings.ExplorerMenu, lang);
        };
        // 资源管理器右键菜单：按现在的程序路径和界面语言重写（装到别处、换了语言都跟着变）
        _ = Task.Run(() => ExplorerMenuRegistration.Apply(_settings.ExplorerMenu, _settings.ResolveUiLanguage()));

        _ball = new FloatingBall(_settings);
        _ball.Clicked += () => _main.Toggle();
        _ball.FilesDropped += OnFilesDropped;
        _ball.MenuItems = BallMenu;
        if (_settings.ShowBall)
        {
            _ball.Show();
        }

        // 技能目录变化（安装、卸载、手动拷入）后通知界面刷新
        _host.SkillsChanged += () => Dispatcher.BeginInvoke(() => _main?.Bridge?.Post(new { type = "skills.changed" }));
        // 管理员发布、下线了专家
        _host.Experts.Changed += () => Dispatcher.BeginInvoke(() => _main?.Bridge?.Post(new { type = "experts.changed" }));
        // MCP 连接器状态变了（连上、断开、要登录），只刷新那一张卡片
        _host.Mcp.Changed += id => Dispatcher.BeginInvoke(() => _main?.Bridge?.Post(new { type = "mcp.changed", id, vendor = _host.Mcp.Get(id) }));

        _host.ActiveRunsChanged += count => Dispatcher.BeginInvoke(() =>
        {
            _activeRuns = count;
            UpdateBall();
        });

        // Windows 系统通知：确认按钮直接生效，点通知回到对应任务
        _notifications = new NotificationService { SoundSetting = () => _host.NotificationSoundChoice() };
        _notifications.ConfirmAnswered += (requestId, choice) => Dispatcher.BeginInvoke(() =>
        {
            if (_main?.Bridge?.ResolveConfirm(requestId, choice) != true)
            {
                Log.Info($"通知中的确认 {requestId} 已失效（可能已在界面中处理）");
            }
        });
        _notifications.OpenRequested += conversationId => Dispatcher.BeginInvoke(() => OpenConversation(conversationId));

        // 监听运维代理投递的通知，弹给当前登录的员工（“IT 正在为你的电脑执行：xxx”）
        _noticeWatcher = new NoticeWatcher(_notifications, action => Dispatcher.BeginInvoke(action));
        _noticeWatcher.Start();
        _host.RunFinished += info => Dispatcher.BeginInvoke(() =>
        {
            // 用户正看着这个任务时不打扰；被用户停止的任务不通知
            if (_settings.EnableNotifications
                && info.StopReason != Flyknit.Core.Agent.AgentStopReason.Cancelled
                && !UserIsWatching(info.ConversationId))
            {
                _notifications.ShowFinished(info);
            }
        });

        CreateTray();
        RegisterHotkey();

        // 先创建主窗口（初始化 WebView2）再隐藏，之后点击悬浮球可以秒开
        _main.HotkeyChanged += () => Dispatcher.BeginInvoke(() => { UnregisterHotkey(); RegisterHotkey(); });
        _main.Loaded += (_, _) => HookBridge();
        _main.Prewarm();

        await _host.InitializeAsync();

        // 后台下发给本机 AI agent 的指令：拉取、执行、回报，开始时给员工弹提示
        _instructionPoller = new InstructionPoller(_host, (title, body) =>
        {
            if (_settings.EnableNotifications)
            {
                Dispatcher.BeginInvoke(() => _notifications?.ShowNotice(title, body));
            }
        });
        _instructionPoller.Start();

        // 员工自己打开的（双击快捷方式、刚登录完、点了「重启升级」）直接把主窗口拿出来，
        // 不要只放一个悬浮球让人再去找。只有开机自启（--silent）才安静地待在悬浮球里，不打断开机
        if (!e.Args.Contains("--silent"))
        {
            _main.ShowAndFocus();
        }
        // 程序没在运行时从右键菜单点进来：启动完再处理那个文件
        if (Flyknit.Core.Setup.ShellRequest.Parse(e.Args) is { } request)
        {
            QueueShellRequest(request);
        }
    }

    private void HookBridge()
    {
        // WebView2 初始化是异步的，轮询等待桥接层就绪
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) =>
        {
            if (_main?.Bridge is { } bridge)
            {
                timer.Stop();
                bridge.PendingConfirmsChanged += count => Dispatcher.BeginInvoke(() =>
                {
                    _pendingConfirms = count;
                    UpdateBall();
                });
                bridge.ConfirmRequested += pending => Dispatcher.BeginInvoke(() =>
                {
                    if (UserIsWatching(pending.ConversationId))
                    {
                        return; // 用户就看着这个任务，界面上的确认条已经够了
                    }
                    if (_settings.EnableNotifications && _notifications is { Available: true })
                    {
                        _notifications.ShowConfirm(pending);
                    }
                    else if (_main.IsVisible)
                    {
                        _main.RequestAttention();
                    }
                    else
                    {
                        _tray?.ShowBalloonTip(4000, "FlyknitBuddy", NativeStrings.T("confirm.notify"), System.Windows.Forms.ToolTipIcon.Info);
                    }
                });
                bridge.ConfirmClosed += pending => Dispatcher.BeginInvoke(() => _notifications?.RemoveConfirm(pending.RequestId));
                // AI 用选项提问：员工没看着这个任务时提醒一下（回答要回界面里点，通知上不放按钮）
                bridge.QuestionRequested += conversationId => Dispatcher.BeginInvoke(() =>
                {
                    if (UserIsWatching(conversationId))
                    {
                        return;
                    }
                    if (_main.IsVisible)
                    {
                        _main.RequestAttention();
                    }
                    else
                    {
                        _tray?.ShowBalloonTip(4000, "FlyknitBuddy", NativeStrings.T("ask.notify"), System.Windows.Forms.ToolTipIcon.Info);
                    }
                });
            }
        };
        timer.Start();
    }

    /// <summary>显示主窗口并打开指定任务。</summary>
    private void OpenConversation(string conversationId)
    {
        if (_main is null)
        {
            return;
        }
        _main.ShowAndFocus();
        _main.Bridge?.Post(new { type = "app.openConversation", conversationId });
        _notifications?.RemoveFinished(conversationId);
    }

    /// <summary>
    /// 用户此刻是不是正看着这个任务？是的话界面上的卡片就够了，不要再弹系统通知。
    ///
    /// 两个条件都要满足：窗口看得见（没最小化、没被别的程序盖住大半、人没离开座位），
    /// 并且当前打开的就是这个任务——在看别的任务时同样是“看不到”，该通知还是要通知。
    /// </summary>
    private bool UserIsWatching(string conversationId)
    {
        if (_main is null)
        {
            return false;
        }
        var hwnd = new WindowInteropHelper(_main).Handle;
        if (!UserPresence.CanSeeWindow(hwnd, _main.IsVisible, _main.WindowState == WindowState.Minimized))
        {
            return false;
        }
        var active = _main.Bridge?.ActiveConversationId;
        return string.IsNullOrEmpty(active) || active == conversationId;
    }

    private void UpdateBall() => _ball?.SetState(_activeRuns > 0, _pendingConfirms > 0);

    // ---------- 悬浮球右键菜单 ----------

    /// <summary>每次右键时现拼：界面语言、安全中心开关、快捷键随时会变。null 是分隔线。</summary>
    private IReadOnlyList<FloatingBall.MenuEntry?> BallMenu()
    {
        var list = new List<FloatingBall.MenuEntry?>
        {
            new FloatingBall.MenuEntry(NativeStrings.T("ball.menu.open"), () => _main?.ShowAndFocus()),
            new FloatingBall.MenuEntry(NativeStrings.T("ball.menu.compact"), () =>
            {
                if (_ball is not null)
                {
                    _main?.ShowCompact(_ball.Bounds, System.Windows.Media.VisualTreeHelper.GetDpi(_ball));
                }
            }),
        };
        list.Add(null);
        // 划词翻译：选中别的程序里的字，再右键悬浮球点这一项（悬浮球不抢焦点，选中的字还在）
        list.Add(new FloatingBall.MenuEntry(NativeStrings.T("ball.menu.translate"), () => _ = TranslateFromBallAsync(), HotkeyText("selection.translate")));
        if (_host?.Security.On(Flyknit.Core.Security.SecuritySettings.ScreenshotAsk) != false)
        {
            list.Add(new FloatingBall.MenuEntry(NativeStrings.T("ball.menu.screenshot"), () => _ = AskWithScreenshotAsync(), HotkeyText("screenshot.ask")));
        }
        list.Add(null);
        // 只是这次隐藏：设置不变，下次启动照常显示
        list.Add(new FloatingBall.MenuEntry(NativeStrings.T("ball.menu.hide"), () => _ball?.Hide()));
        list.Add(new FloatingBall.MenuEntry(NativeStrings.T("ball.menu.disable"), DisableBall));
        list.Add(null);
        // 和托盘菜单的「退出」一样彻底退出；悬浮球上容易误点，还有任务在跑时先问一句
        list.Add(new FloatingBall.MenuEntry(NativeStrings.T("tray.exit"), ExitFromBall));
        return list;
    }

    /// <summary>
    /// 从悬浮球菜单划词翻译：把焦点还给右键之前的那个程序（菜单弹出时焦点可能被抢走），再去取选中的字。
    /// </summary>
    private async Task TranslateFromBallAsync()
    {
        if (_ball?.LastForeground is { } target && target != IntPtr.Zero)
        {
            SetForegroundWindow(target);
            await Task.Delay(150);
        }
        await TranslateSelectionAsync();
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private string HotkeyText(string commandId)
    {
        var resolved = Flyknit.Core.Settings.Shortcuts.Resolve(_settings.Shortcuts);
        return resolved.TryGetValue(commandId, out var binding)
            ? binding
            : Flyknit.Core.Settings.Shortcuts.Find(commandId)?.Default ?? "";
    }

    /// <summary>停用悬浮球：和托盘菜单里取消勾选「显示悬浮球」一样，记进设置。</summary>
    private void ExitFromBall()
    {
        if (_activeRuns > 0 && System.Windows.MessageBox.Show(
                NativeStrings.T("ball.exitBusy").Replace("{n}", _activeRuns.ToString()),
                "FlyknitBuddy",
                System.Windows.MessageBoxButton.OKCancel,
                System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.OK)
        {
            return;
        }
        ExitApp();
    }

    private void DisableBall()
    {
        _settings.ShowBall = false;
        _settings.Save();
        _ball?.Hide();
        BuildTrayMenu();
        _tray?.ShowBalloonTip(4000, "FlyknitBuddy", NativeStrings.T("ball.disabled"), System.Windows.Forms.ToolTipIcon.Info);
    }

    // ---------- 资源管理器右键菜单 ----------

    private readonly Flyknit.Core.Setup.ShellRequestBatcher _shellBatch = new();
    private System.Windows.Threading.DispatcherTimer? _shellTimer;

    /// <summary>
    /// 多选文件时每个文件各来一次：等 0.6 秒没有新的再一起处理，几个文件进同一个对话。
    /// 界面还没加载好（刚被右键菜单拉起来）就接着等。
    /// </summary>
    private void QueueShellRequest(Flyknit.Core.Setup.ShellRequest request)
    {
        _shellBatch.Add(request);
        _shellTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _shellTimer.Stop();
        _shellTimer.Tick -= OnShellTimer;
        _shellTimer.Tick += OnShellTimer;
        _shellTimer.Start();
    }

    private void OnShellTimer(object? sender, EventArgs e)
    {
        if (_main?.Bridge is not { UiReady: true } bridge)
        {
            return; // 界面还没好，下一次再看
        }
        _shellTimer?.Stop();
        _main.ShowAndFocus();
        foreach (var (action, paths) in _shellBatch.Drain())
        {
            if (action == Flyknit.Core.Setup.ShellAction.Workspace)
            {
                var folder = paths[0];
                if (!Directory.Exists(folder))
                {
                    continue;
                }
                var full = AppSettings.NormalizeDir(folder);
                _settings.AddWorkspace(full);
                _settings.Save();
                bridge.Post(new { type = "shell.request", action = "workspace", path = full });
                continue;
            }
            var attachments = paths.Where(File.Exists).Select(AttachmentDto.FromPath).ToList();
            if (attachments.Count > 0)
            {
                bridge.Post(new { type = "shell.request", action = action.ToString().ToLowerInvariant(), attachments });
            }
        }
    }

    // ---------- 截图提问 ----------

    /// <summary>
    /// 框选屏幕的一块，作为附件开一个新对话，员工接着打字问就行。
    /// 截之前先把悬浮球和主窗口藏起来，截的是员工正在看的东西。
    /// </summary>
    private async Task AskWithScreenshotAsync()
    {
        if (_capturingScreen || _main is null || _host is null)
        {
            return;
        }
        if (!_host.Security.On(Flyknit.Core.Security.SecuritySettings.ScreenshotAsk))
        {
            _tray?.ShowBalloonTip(4000, "FlyknitBuddy", NativeStrings.T("screenshot.blocked"), System.Windows.Forms.ToolTipIcon.Info);
            return;
        }
        _capturingScreen = true;
        var ballVisible = _ball?.IsVisible == true;
        var mainVisible = _main.IsVisible && _main.WindowState != WindowState.Minimized;
        try
        {
            if (ballVisible)
            {
                _ball!.Hide();
            }
            if (mainVisible)
            {
                _main.HideMain();
            }
            await Task.Delay(200); // 等窗口真正消失、桌面重绘完
            var path = await ScreenshotOverlay.CaptureAsync(NativeStrings.T("screenshot.hint"), AppPaths.Temp);
            if (path is null)
            {
                if (mainVisible)
                {
                    _main.ShowAndFocus();
                }
                return;
            }
            Log.Info("截图提问：已截取一块屏幕");
            _main.ShowAndFocus();
            _main.Bridge?.Post(new { type = "screenshot.taken", attachment = AttachmentDto.FromPath(path) });
        }
        catch (Exception ex)
        {
            Log.Warn("截图提问失败", ex);
            // 截之前把主窗口藏起来了，出错也要拿回来，不然员工以为程序没了
            if (mainVisible)
            {
                try { _main.ShowAndFocus(); }
                catch (Exception showEx) { Log.Warn("恢复主窗口失败", showEx); }
            }
        }
        finally
        {
            if (ballVisible)
            {
                _ball?.Show();
            }
            _capturingScreen = false;
        }
    }

    private void OnFilesDropped(string[] files)
    {
        if (_main is null)
        {
            return;
        }
        _main.ShowAndFocus();
        var attachments = files.Where(File.Exists).Select(AttachmentDto.FromPath).ToList();
        if (attachments.Count > 0)
        {
            _main.Bridge?.Post(new { type = "files.added", attachments });
        }
    }

    // ---------- 托盘 ----------

    private void CreateTray()
    {
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "flyknitbuddy.ico")),
            Text = "FlyknitBuddy",
            Visible = true,
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
            {
                _main?.ShowAndFocus();
            }
        };
        BuildTrayMenu();
    }

    private void BuildTrayMenu()
    {
        if (_tray is null)
        {
            return;
        }
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(NativeStrings.T("tray.open"), null, (_, _) => _main?.ShowAndFocus());
        menu.Items.Add(NativeStrings.T("tray.newChat"), null, (_, _) =>
        {
            _main?.ShowAndFocus();
            _main?.Bridge?.Post(new { type = "app.focusInput" });
        });
        var ballItem = new System.Windows.Forms.ToolStripMenuItem(NativeStrings.T("tray.ball")) { Checked = _settings.ShowBall, CheckOnClick = true };
        ballItem.CheckedChanged += (_, _) =>
        {
            _settings.ShowBall = ballItem.Checked;
            _settings.Save();
            if (ballItem.Checked) _ball?.Show();
            else _ball?.Hide();
        };
        menu.Items.Add(ballItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(NativeStrings.T("tray.exit"), null, (_, _) => ExitApp());
        _tray.ContextMenuStrip?.Dispose();
        _tray.ContextMenuStrip = menu;
    }

    private void ExitApp() => ExitApp(applyUpdate: true);

    /// <param name="applyUpdate">用户点「稍后」的更新在退出时装上。点「重启升级」走到这里时更新器已经起了，别再起一个。</param>
    private void ExitApp(bool applyUpdate)
    {
        Cleanup(applyUpdate);
        Shutdown();
    }

    /// <summary>
    /// 退出前收拾干净：托盘图标、悬浮球、后台服务都要停掉。只调 Shutdown 的话悬浮球和托盘图标会留在屏幕上、
    /// 后台线程让进程一直不退——运维代理看到程序还在跑，新版本就一直换不上。
    /// </summary>
    private void Cleanup(bool applyUpdate)
    {
        if (_cleanedUp)
        {
            return;
        }
        _cleanedUp = true;
        // 停后台服务可能要好几秒：先把窗口收起来，别让员工对着一个不动的窗口等
        _main?.Hide();
        _ball?.Hide();
        _pipeCts.Cancel();
        UnregisterHotkey();
        _translatePopup?.Close();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _settings.Save();
        _notifications?.ClearAll();
        _noticeWatcher?.Dispose();
        _instructionPoller?.Dispose();
        // 下好了没装的，就在这会儿装上——用户说「稍后」，指的就是等他关掉程序的时候
        if (applyUpdate)
        {
            _host?.Updater.ApplyOnExit();
        }
        try { _host?.Dispose(); }
        catch (Exception ex) { Log.Warn("退出时停止后台服务出错", ex); }
        _main?.ExitForReal();
        _ball?.Close();
        try { _mutex?.ReleaseMutex(); }
        catch (Exception) { /* 已经放掉了 */ }
        _mutex?.Dispose();
        _mutex = null;
        // 兜底：哪个后台线程卡住不退，5 秒后强制结束，别让进程半死不活地挂着
        new Thread(() =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(5));
            Log.Warn("退出 5 秒后进程还在，强制结束");
            Environment.Exit(0);
        }) { IsBackground = true }.Start();
    }

    // ---------- 全局热键：唤起主窗口（Ctrl+Alt+Space）、划词翻译（Ctrl+Alt+T） ----------

    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;

    /// <summary>把 "Ctrl+Alt+Space" 这样的写法翻成 RegisterHotKey 要的修饰位和虚拟键码。</summary>
    private static (uint Modifiers, uint Key)? ParseHotkey(string binding)
    {
        var normalized = Flyknit.Core.Settings.Shortcuts.Normalize(binding);
        if (normalized.Length == 0)
        {
            return null;
        }
        uint mods = ModNoRepeat;
        var parts = normalized.Split('+');
        foreach (var part in parts[..^1])
        {
            mods |= part switch
            {
                "Ctrl" => ModControl,
                "Alt" => ModAlt,
                "Shift" => ModShift,
                "Meta" => ModWin,
                _ => 0u,
            };
        }
        var key = parts[^1];
        var vk = key switch
        {
            "Space" => 0x20u,
            "Escape" => 0x1Bu,
            "Enter" => 0x0Du,
            "Up" => 0x26u, "Down" => 0x28u, "Left" => 0x25u, "Right" => 0x27u,
            _ when key.Length == 1 && char.IsLetterOrDigit(key[0]) => (uint)char.ToUpperInvariant(key[0]),
            _ when key.Length >= 2 && key[0] == 'F' && int.TryParse(key[1..], out var n) && n is >= 1 and <= 24 => (uint)(0x70 + n - 1),
            _ => 0u,
        };
        return vk == 0 ? null : (mods, vk);
    }

    private void RegisterHotkey()
    {
        var parameters = new HwndSourceParameters("FlyknitHotkey") { Width = 0, Height = 0, WindowStyle = 0 };
        _hotkeySource = new HwndSource(parameters);
        _hotkeySource.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg != WmHotkey)
            {
                return IntPtr.Zero;
            }
            switch (wParam.ToInt32())
            {
                case HotkeyId:
                    _main?.Toggle();
                    handled = true;
                    break;
                case TranslateHotkeyId:
                    _ = TranslateSelectionAsync();
                    handled = true;
                    break;
                case ScreenshotHotkeyId:
                    _ = AskWithScreenshotAsync();
                    handled = true;
                    break;
            }
            return IntPtr.Zero;
        });
        var resolved = Flyknit.Core.Settings.Shortcuts.Resolve(_settings.Shortcuts);
        RegisterOne(HotkeyId, "window.toggle", resolved);
        RegisterOne(TranslateHotkeyId, "selection.translate", resolved);
        RegisterOne(ScreenshotHotkeyId, "screenshot.ask", resolved);
    }

    private void RegisterOne(int hotkeyId, string commandId, IReadOnlyDictionary<string, string> resolved)
    {
        var binding = resolved.TryGetValue(commandId, out var configured)
            ? configured
            : Flyknit.Core.Settings.Shortcuts.Find(commandId)?.Default ?? "";
        if (ParseHotkey(binding) is not { } hk)
        {
            Log.Info($"全局热键 {commandId} 已停用（{binding}）");
            return;
        }
        if (!RegisterHotKey(_hotkeySource!.Handle, hotkeyId, hk.Modifiers, hk.Key))
        {
            Log.Warn($"注册全局热键 {commandId} = {binding} 失败（可能被其他程序占用）");
        }
        else
        {
            Log.Info($"全局热键已注册：{commandId} = {binding}");
        }
    }

    private void UnregisterHotkey()
    {
        if (_hotkeySource is not null)
        {
            UnregisterHotKey(_hotkeySource.Handle, HotkeyId);
            UnregisterHotKey(_hotkeySource.Handle, TranslateHotkeyId);
            UnregisterHotKey(_hotkeySource.Handle, ScreenshotHotkeyId);
            _hotkeySource.Dispose();
        }
    }

    // ---------- 划词翻译 ----------

    /// <summary>
    /// 读出前台程序里选中的文字，在鼠标旁边弹出译文。
    /// 什么都没选中也照样弹出来，用户可以直接在里面输入或粘贴。
    /// </summary>
    private async Task TranslateSelectionAsync()
    {
        if (_host is null || _capturingSelection)
        {
            return; // 上一次还在取词，连按热键不重复弹
        }
        _capturingSelection = true;
        try
        {
            var anchor = TranslatePopup.CursorPosition();
            string raw;
            try
            {
                raw = await SelectionCapture.CaptureAsync();
            }
            catch (Exception ex)
            {
                Log.Warn("划词翻译：读取选中文字失败", ex);
                raw = "";
            }
            var (text, truncated) = SelectionTranslation.Clean(raw);

            // 取完词再关旧的：热键可能就是在旧浮窗里选了字按的
            _translatePopup?.Close();
            var popup = new TranslatePopup(_host.Server, _settings, NativeStrings.Language, text, anchor) { Truncated = truncated };
            popup.ContinueRequested += ContinueTranslation;
            popup.Closed += (_, _) =>
            {
                if (_translatePopup == popup)
                {
                    _translatePopup = null;
                }
            };
            _translatePopup = popup;
            popup.Show();
        }
        catch (Exception ex)
        {
            Log.Error("划词翻译出错", ex);
        }
        finally
        {
            _capturingSelection = false;
        }
    }

    /// <summary>把这段文字带到主窗口的翻译模式里，开一个正式的会话接着聊。</summary>
    private void ContinueTranslation(string text, string to)
    {
        if (_main is null)
        {
            return;
        }
        _main.ShowAndFocus();
        _main.Bridge?.Post(new { type = "app.translate", text, from = "auto", to });
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ---------- 重新注册 ----------

    private bool _reregistering;

    /// <summary>
    /// 服务端查无此设备（401）。常见于换了服务器，或者服务端的数据库被重建过。
    /// 以前用户只能自己去删 settings.json，这里直接把注册窗口弹出来。
    /// </summary>
    private void OnReregistrationRequired()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_reregistering || _settings is null)
            {
                return;
            }
            _reregistering = true;
            try
            {
                // 清掉服务端已经不认的令牌，注册窗口才会以「未注册」的姿态出现
                _settings.DeviceToken = "";
                _settings.Save();

                if (new LoginWindow(_settings).ShowDialog() == true)
                {
                    // 重启而不是热切换：换服务器时连接的基地址是构造时定的，
                    // 就地改要动一串状态，重启是确定正确的那条路。
                    Restart();
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"重新注册流程出错：{ex.Message}");
            }
            finally
            {
                _reregistering = false;
            }
        });
    }

    private void Restart()
    {
        var exe = Environment.ProcessPath;
        // 先收拾干净（也放掉单实例锁，否则新进程会以为已经有一个在跑然后直接退出）
        Cleanup(applyUpdate: false);
        try
        {
            if (exe is not null)
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"自动重启失败，请手动重新打开：{ex.Message}");
        }
        Shutdown();
    }

    // ---------- 本地数据保护 ----------

    /// <summary>
    /// 把本地数据目录标记为加密。对手是「拿本机管理员账号把 history.db 拷走，
    /// 用 SQLite 工具读完所有对话」——加密之后拷出去的是密文，换个账号也解不开。
    /// 加不上就算了（家庭版、FAT32、组策略禁用），功能照常，只是不加密。
    /// </summary>
    private static void ProtectDataAtRest()
    {
        try
        {
            var dataDir = Path.GetDirectoryName(AppPaths.Database)!;
            if (!DataProtection.ProtectDirectory(dataDir))
            {
                return;
            }
            DataProtection.ProtectDirectory(AppPaths.Memory);
            // 资料库里是上传文件和截图的副本、缩略图，和聊天记录一样敏感。
            // 已经建好的子目录（按月的 files\2026-10、thumbs）不会继承上层的加密属性，要逐个标记
            DataProtection.ProtectDirectory(AppPaths.Library);
            foreach (var sub in Directory.EnumerateDirectories(AppPaths.Library, "*", SearchOption.AllDirectories))
            {
                DataProtection.ProtectDirectory(sub);
            }
            // 升级场景：目录属性不会追溯已有文件，把库和记忆补加密一遍
            DataProtection.ProtectExisting(dataDir);
            DataProtection.ProtectExisting(AppPaths.Memory);
            DataProtection.ProtectExisting(AppPaths.Library);
            DataProtection.ProtectExisting(AppPaths.Root, "settings.json", "approval-rules.json");
        }
        catch (Exception ex)
        {
            // 这一步失败绝不能挡住启动
            Log.Warn($"本地数据加密未生效：{ex.Message}");
        }
    }

    // ---------- 单实例激活 ----------

    private void ListenForActivation()
    {
        var token = _pipeCts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(token);
                    // 新进程把自己的命令行参数（JSON 数组）写过来；普通双击没有参数，就是把窗口拿出来
                    using var reader = new StreamReader(server);
                    var payload = await reader.ReadToEndAsync(token);
                    string[] args;
                    try
                    {
                        args = payload.Length > 0 ? System.Text.Json.JsonSerializer.Deserialize<string[]>(payload) ?? Array.Empty<string>() : Array.Empty<string>();
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        args = Array.Empty<string>();
                    }
                    var request = Flyknit.Core.Setup.ShellRequest.Parse(args);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (request is not null)
                        {
                            QueueShellRequest(request);
                        }
                        else
                        {
                            _main?.ShowAndFocus();
                        }
                    });
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log.Warn("单实例管道异常", ex);
                    await Task.Delay(1000, token).ContinueWith(_ => { });
                }
            }
        });
    }

    private static async Task NotifyExistingInstanceAsync(string[] args)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            // 多选文件时资源管理器同时起好几个进程，管道一次只接一个：多等一会儿
            await client.ConnectAsync(5000);
            var bytes = System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(args));
            await client.WriteAsync(bytes);
        }
        catch (Exception ex)
        {
            Log.Warn("通知已运行的实例失败", ex);
        }
    }
}
