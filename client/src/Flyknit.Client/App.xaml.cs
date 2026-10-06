using System;
using System.Collections.Generic;
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

namespace Flyknit.Client;

public partial class App : Application
{
    private const string MutexName = "Local\\Flyknit.Client.SingleInstance";
    private const string PipeName = "Flyknit.Client.Activate";
    private const int HotkeyId = 0xF17;

    private Mutex? _mutex;
    private AppSettings _settings = new();
    private AgentHost? _host;
    private MainWindow? _main;
    private FloatingBall? _ball;
    private System.Windows.Forms.NotifyIcon? _tray;
    private NotificationService? _notifications;
    private HwndSource? _hotkeySource;
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

        _settings = AppSettings.Load();
        NativeStrings.Language = _settings.ResolveUiLanguage();

        if (!_settings.IsRegistered)
        {
            var setup = new SetupWindow(_settings);
            if (setup.ShowDialog() != true)
            {
                Shutdown();
                return;
            }
        }

        _host = new AgentHost(_settings);
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

        _host.ActiveRunsChanged += count => Dispatcher.BeginInvoke(() =>
        {
            _activeRuns = count;
            UpdateBall();
        });

        // Windows 系统通知：确认按钮直接生效，点通知回到对应任务
        _notifications = new NotificationService { SoundSetting = () => _settings.NotificationSound };
        _notifications.ConfirmAnswered += (requestId, choice) => Dispatcher.BeginInvoke(() =>
        {
            if (_main?.Bridge?.ResolveConfirm(requestId, choice) != true)
            {
                Log.Info($"通知中的确认 {requestId} 已失效（可能已在界面中处理）");
            }
        });
        _notifications.OpenRequested += conversationId => Dispatcher.BeginInvoke(() => OpenConversation(conversationId));
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
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _settings.Save();
        _notifications?.ClearAll();
        _host?.Dispose();
        _main?.ExitForReal();
        _ball?.Close();
        _mutex?.ReleaseMutex();
        Shutdown();
    }

    // ---------- 全局热键 Ctrl+Alt+Space ----------

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
            if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
            {
                _main?.Toggle();
                handled = true;
            }
            return IntPtr.Zero;
        });
        var resolved = Flyknit.Core.Settings.Shortcuts.Resolve(_settings.Shortcuts);
        var binding = resolved.TryGetValue("window.toggle", out var configured) ? configured : "Ctrl+Alt+Space";
        if (ParseHotkey(binding) is not { } hk)
        {
            Log.Info($"全局热键已停用（{binding}）");
            return;
        }
        if (!RegisterHotKey(_hotkeySource.Handle, HotkeyId, hk.Modifiers, hk.Key))
        {
            Log.Warn($"注册全局热键 {binding} 失败（可能被其他程序占用）");
        }
        else
        {
            Log.Info($"全局热键已注册：{binding}");
        }
    }

    private void UnregisterHotkey()
    {
        if (_hotkeySource is not null)
        {
            UnregisterHotKey(_hotkeySource.Handle, HotkeyId);
            _hotkeySource.Dispose();
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

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
