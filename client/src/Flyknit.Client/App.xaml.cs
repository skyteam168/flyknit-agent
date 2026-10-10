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
    private CancellationTokenSource _pipeCts = new();
    private int _activeRuns;
    private int _pendingConfirms;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("未处理的界面异常", ex.Exception);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("未处理的异常", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log.Error("未观察到的任务异常", ex.Exception);
            ex.SetObserved();
        };

        // 单实例：已运行时通知原实例显示窗口
        _mutex = new Mutex(true, MutexName, out var isFirst);
        if (!isFirst)
        {
            await NotifyExistingInstanceAsync();
            Shutdown();
            return;
        }
        ListenForActivation();

        // 先把数据目录标记为加密，再去读设置、开数据库——之后新建的库文件、WAL、
        // journal 都会自动继承这个属性。老用户升上来时目录里已经有明文文件，单独补一遍。
        ProtectDataAtRest();

        _settings = AppSettings.Load();
        NativeStrings.Language = _settings.ResolveUiLanguage();

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
        };

        _ball = new FloatingBall(_settings);
        _ball.Clicked += () => _main.Toggle();
        _ball.FilesDropped += OnFilesDropped;
        if (_settings.ShowBall)
        {
            _ball.Show();
        }

        // 技能目录变化（安装、卸载、手动拷入）后通知界面刷新
        _host.SkillsChanged += () => Dispatcher.BeginInvoke(() => _main?.Bridge?.Post(new { type = "skills.changed" }));
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

        // --silent 是开机自启用的：只放悬浮球，不弹主窗口打断用户登录
        if (e.Args.Contains("--show") && !e.Args.Contains("--silent"))
        {
            _main.ShowAndFocus();
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

    private void ExitApp()
    {
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
        _host?.Updater.ApplyOnExit();
        _host?.Dispose();
        _main?.ExitForReal();
        _ball?.Close();
        _mutex?.ReleaseMutex();
        Shutdown();
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
            }
            return IntPtr.Zero;
        });
        var resolved = Flyknit.Core.Settings.Shortcuts.Resolve(_settings.Shortcuts);
        RegisterOne(HotkeyId, "window.toggle", resolved);
        RegisterOne(TranslateHotkeyId, "selection.translate", resolved);
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
        try
        {
            // 先放掉单实例锁，否则新进程会以为已经有一个在跑然后直接退出
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
            _mutex = null;
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
                    await Dispatcher.InvokeAsync(() => _main?.ShowAndFocus());
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

    private static async Task NotifyExistingInstanceAsync()
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(2000);
        }
        catch (Exception ex)
        {
            Log.Warn("通知已运行的实例失败", ex);
        }
    }
}
