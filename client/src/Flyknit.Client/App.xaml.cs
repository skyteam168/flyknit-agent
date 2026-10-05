using System;
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

        _host.ActiveRunsChanged += count => Dispatcher.BeginInvoke(() =>
        {
            _activeRuns = count;
            UpdateBall();
        });

        CreateTray();
        RegisterHotkey();

        // 先创建主窗口（初始化 WebView2）再隐藏，之后点击悬浮球可以秒开
        _main.Loaded += (_, _) => HookBridge();
        _main.Prewarm();

        await _host.InitializeAsync();

        if (e.Args.Contains("--show"))
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
                    if (count > 0 && _main is { IsVisible: false })
                    {
                        _tray?.ShowBalloonTip(4000, "Flyknit", NativeStrings.T("confirm.notify"), System.Windows.Forms.ToolTipIcon.Info);
                    }
                });
            }
        };
        timer.Start();
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
            Icon = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "flyknit.ico")),
            Text = "Flyknit",
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
        _host?.Dispose();
        _main?.ExitForReal();
        _ball?.Close();
        _mutex?.ReleaseMutex();
        Shutdown();
    }

    // ---------- 全局热键 Ctrl+Alt+Space ----------

    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModNoRepeat = 0x4000;
    private const uint VkSpace = 0x20;

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
        if (!RegisterHotKey(_hotkeySource.Handle, HotkeyId, ModControl | ModAlt | ModNoRepeat, VkSpace))
        {
            Log.Warn("注册全局热键 Ctrl+Alt+Space 失败（可能被其他程序占用）");
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
