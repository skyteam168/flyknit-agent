using System;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Flyknit.Client.Services;
using Flyknit.Core.Agent;
using Flyknit.Core.Gateway;
using Flyknit.Core.Translation;

namespace Flyknit.Client.Windows;

/// <summary>
/// 划词翻译的小浮窗：出现在鼠标旁边，上面是原文（可以改），下面是边出边显示的译文。
///
/// 点别处就关（钉住后不关），Esc 关，Enter 用改过的原文重新翻译。
/// 「在主窗口继续」把这段文字带到翻译模式里，开一个正式的会话。
///
/// 界面用代码搭而不是 XAML：它很小，而且这样整个窗口在一个文件里就能看全。
/// </summary>
public sealed class TranslatePopup : Window
{
    private const double PopupWidth = 400;

    private readonly IChatGateway _gateway;
    private readonly AppSettings _settings;
    private readonly string _uiLanguage;
    private readonly NativePoint _anchor;
    private readonly Palette _palette;

    private readonly TextBox _source;
    private readonly TextBox _result;
    private readonly ComboBox _target;
    private readonly TextBlock _status;
    private readonly Button _copy;
    private readonly Button _continue;
    private readonly ToggleButton _pin;

    private CancellationTokenSource? _cts;
    private bool _updatingTarget;
    private bool _userMoved;
    private bool _placed;
    private bool _closing;

    /// <summary>用户点了「在主窗口继续」：原文、目标语言。</summary>
    public event Action<string, string>? ContinueRequested;

    public TranslatePopup(IChatGateway gateway, AppSettings settings, string uiLanguage, string text, NativePoint anchor)
    {
        _gateway = gateway;
        _settings = settings;
        _uiLanguage = uiLanguage;
        _anchor = anchor;
        _palette = Palette.For(settings.Theme);

        Title = NativeStrings.T("sel.title");
        Width = PopupWidth + 24; // 两边各 12 留给阴影
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -10000; // 量好尺寸、摆到鼠标旁边之前先放在屏幕外，免得闪一下
        Top = -10000;
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI, Leelawadee UI, Khmer UI");
        FontSize = 13 * Math.Clamp(settings.FontScale, 0.85, 1.25);
        UseLayoutRounding = true;

        // ---- 标题行：名字、目标语言、钉住、关闭 ----
        var heading = new TextBlock
        {
            Text = NativeStrings.T("sel.title"),
            FontWeight = FontWeights.SemiBold,
            Foreground = _palette.Text,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var arrow = new TextBlock
        {
            Text = "→",
            Margin = new Thickness(10, 0, 6, 0),
            Foreground = _palette.Subtle,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _target = new ComboBox
        {
            MinWidth = 110,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = NativeStrings.T("sel.target"),
        };
        foreach (var code in Languages.TranslateTargets.Keys)
        {
            _target.Items.Add(new ComboBoxItem { Content = NativeStrings.T("lang." + code), Tag = code });
        }
        _target.SelectionChanged += (_, _) =>
        {
            if (_updatingTarget || _target.SelectedItem is not ComboBoxItem { Tag: string code })
            {
                return;
            }
            // 用户自己选的语言记下来，下次划词默认就译成它
            _settings.SelectionTranslateTo = code;
            SaveSettings();
            _ = TranslateAsync(code);
        };
        _pin = new ToggleButton
        {
            Content = "📌",
            ToolTip = NativeStrings.T("sel.pin"),
            Style = FlatToggle(),
        };
        var close = new Button
        {
            Content = "✕",
            ToolTip = NativeStrings.T("sel.close"),
            Style = Flat(),
        };
        close.Click += (_, _) => SafeClose();

        var header = new DockPanel { LastChildFill = false, Background = Brushes.Transparent, Cursor = Cursors.SizeAll };
        header.Children.Add(heading);
        header.Children.Add(arrow);
        header.Children.Add(_target);
        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(_pin, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(_pin);
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && IsInside<ComboBox>(d))
            {
                return;
            }
            _userMoved = true;
            DragMove();
        };

        // ---- 原文 ----
        _source = new TextBox
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 110,
            MinHeight = 22,
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(0, 10, 0, 0),
            BorderThickness = new Thickness(0),
            Background = _palette.Surface,
            Foreground = _palette.Subtle,
            CaretBrush = _palette.Text,
            ToolTip = NativeStrings.T("sel.sourceHint"),
        };
        _source.PreviewKeyDown += (_, e) =>
        {
            // Enter 重新翻译，Shift+Enter 换行——和主窗口输入框一样
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                _ = TranslateAsync(null);
            }
        };

        // ---- 译文 ----
        _result = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 300,
            MinHeight = 22,
            Padding = new Thickness(6, 6, 6, 2),
            Margin = new Thickness(0, 6, 0, 0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = _palette.Text,
            FontSize = FontSize + 1,
        };

        // ---- 底部：状态、复制、在主窗口继续 ----
        _status = new TextBlock
        {
            Foreground = _palette.Subtle,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = FontSize - 1,
        };
        _copy = new Button { Content = NativeStrings.T("sel.copy"), Style = Flat(), IsEnabled = false };
        _copy.Click += (_, _) => CopyResult();
        _continue = new Button { Content = NativeStrings.T("sel.continue"), Style = Flat(accent: true) };
        _continue.Click += (_, _) => ContinueInMainWindow();

        var footer = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(_copy);
        buttons.Children.Add(_continue);
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        footer.Children.Add(_status);

        var body = new StackPanel();
        body.Children.Add(header);
        body.Children.Add(_source);
        body.Children.Add(new Border { Height = 1, Background = _palette.Border, Margin = new Thickness(0, 8, 0, 0) });
        body.Children.Add(_result);
        body.Children.Add(footer);

        Content = new Border
        {
            Margin = new Thickness(12),
            Padding = new Thickness(14, 10, 14, 12),
            CornerRadius = new CornerRadius(10),
            Background = _palette.Background,
            BorderBrush = _palette.Border,
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Direction = 270, Opacity = 0.22, Color = Color.FromRgb(0x1A, 0x24, 0x33) },
            Child = body,
        };

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                SafeClose();
            }
        };
        Deactivated += (_, _) =>
        {
            // 点到别处就收起；钉住了就留着，方便边看译文边在原程序里干活
            if (_pin.IsChecked != true)
            {
                SafeClose();
            }
        };
        SizeChanged += (_, _) => Place();
        Loaded += (_, _) =>
        {
            Place();
            Activate();
            if (_source.Text.Length == 0)
            {
                _source.Focus();
                _status.Text = NativeStrings.T("sel.empty");
                _continue.IsEnabled = false;
            }
            else
            {
                _ = TranslateAsync(null);
            }
        };
        Closing += (_, _) => _closing = true; // 外面（App）直接调 Close 时也要记上
        Closed += (_, _) => _cts?.Cancel();
        _source.TextChanged += (_, _) => _continue.IsEnabled = _source.Text.Trim().Length > 0;
    }

    /// <summary>选区太长被截过：告诉用户只翻了前面一段。</summary>
    public bool Truncated { get; init; }

    // ---------- 翻译 ----------

    /// <param name="chosen">用户在下拉框里明确选的目标语言；null 表示按设置自动挑。</param>
    private async Task TranslateAsync(string? chosen)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;

        var (text, truncated) = SelectionTranslation.Clean(_source.Text);
        truncated |= Truncated && _source.Text.Length >= SelectionTranslation.MaxChars;
        if (text.Length == 0)
        {
            _result.Text = "";
            _status.Text = NativeStrings.T("sel.empty");
            _copy.IsEnabled = false;
            return;
        }

        string target, fallback;
        if (chosen is not null)
        {
            // 下拉框里选了什么就译成什么，不再替用户「换一个」
            target = chosen;
            fallback = SelectionTranslation.Alternative(chosen, _uiLanguage);
        }
        else
        {
            (target, fallback) = SelectionTranslation.PickTarget(text, _settings.SelectionTranslateTo, _uiLanguage);
        }
        ShowTarget(target);

        _result.Text = "";
        _result.Foreground = _palette.Text;
        _status.Text = NativeStrings.T("sel.working");
        _copy.IsEnabled = false;

        var sink = new Sink(this, ct);
        try
        {
            var request = SelectionTranslation.BuildRequest(text, target, fallback, SelectionTranslation.NewConversationId());
            var turn = await Task.Run(() => _gateway.CompleteAsync(request, sink, ct), ct);
            if (ct.IsCancellationRequested)
            {
                return;
            }
            var translated = SelectionTranslation.StripThinking(turn.Content).Trim();
            _result.Text = translated;
            _copy.IsEnabled = translated.Length > 0;
            _status.Text = translated.Length == 0 ? NativeStrings.T("sel.noResult")
                : truncated ? string.Format(NativeStrings.T("sel.truncated"), SelectionTranslation.MaxChars)
                : "";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 改了语言或关掉了窗口
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }
            Log.Warn($"划词翻译失败：{ex.Message}");
            _status.Text = ex switch
            {
                GatewayException { StatusCode: 401 or 403 } => NativeStrings.T("sel.error.auth"),
                GatewayException { StatusCode: 429 } => NativeStrings.T("sel.error.busy"),
                HttpRequestException or TaskCanceledException => NativeStrings.T("sel.error.network"),
                _ => string.Format(NativeStrings.T("sel.error.failed"), ex.Message),
            };
            _result.Foreground = _palette.Subtle;
        }
    }

    private void ShowTarget(string code)
    {
        _updatingTarget = true;
        _target.SelectedItem = _target.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == code);
        _updatingTarget = false;
    }

    private void CopyResult()
    {
        try
        {
            Clipboard.SetText(_result.Text);
            _status.Text = NativeStrings.T("sel.copied");
        }
        catch (Exception ex)
        {
            Log.Warn($"复制译文失败：{ex.Message}");
        }
    }

    private void ContinueInMainWindow()
    {
        var (text, _) = SelectionTranslation.Clean(_source.Text);
        if (text.Length == 0)
        {
            return;
        }
        var target = _target.SelectedItem is ComboBoxItem { Tag: string code }
            ? code
            : SelectionTranslation.PickTarget(text, _settings.SelectionTranslateTo, _uiLanguage).Target;
        // 先关自己再唤起主窗口：主窗口一激活，这里就会收到 Deactivated
        SafeClose();
        ContinueRequested?.Invoke(text, target);
    }

    /// <summary>
    /// 关窗口的几条路（Esc、✕、点别处、转到主窗口）可能前后脚到；
    /// 窗口正在关的时候再调 Close 会抛异常。
    /// </summary>
    private void SafeClose()
    {
        if (!_closing)
        {
            _closing = true;
            Close();
        }
    }

    private void SaveSettings()
    {
        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            Log.Warn($"保存划词翻译语言失败：{ex.Message}");
        }
    }

    /// <summary>边收边显示。回调可能在任意线程上来，统一切回界面线程。</summary>
    private sealed class Sink : IStreamSink
    {
        private readonly TranslatePopup _popup;
        private readonly CancellationToken _ct;
        private readonly StringBuilder _content = new();
        private readonly object _gate = new();

        public Sink(TranslatePopup popup, CancellationToken ct)
        {
            _popup = popup;
            _ct = ct;
        }

        public void OnContent(string delta)
        {
            string snapshot;
            lock (_gate)
            {
                _content.Append(delta);
                snapshot = _content.ToString();
            }
            _popup.Dispatcher.BeginInvoke(() =>
            {
                if (!_ct.IsCancellationRequested)
                {
                    _popup._result.Text = SelectionTranslation.StripThinking(snapshot).TrimStart();
                }
            });
        }

        public void OnReasoning(string delta)
        {
            // 思考过程不显示
        }
    }

    // ---------- 摆放位置 ----------

    /// <summary>
    /// 放在鼠标右下方；下面放不下就放到上面；超出屏幕就往里挪。
    /// 用物理像素算（GetWindowRect / 显示器工作区），多显示器、不同缩放比例下都不会错位。
    /// </summary>
    private void Place()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || _userMoved || !GetWindowRect(hwnd, out var rect))
        {
            return;
        }
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var monitor = MonitorFromPoint(_anchor, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return;
        }
        var work = info.Work;
        var scale = GetDpiForWindow(hwnd) / 96.0;
        if (scale <= 0)
        {
            scale = 1;
        }
        var gap = (int)(8 * scale);

        int x, y;
        if (!_placed)
        {
            // 窗口外圈有 12 的阴影边，让可见的卡片左上角贴着鼠标右下方
            x = _anchor.X - (int)(4 * scale);
            y = _anchor.Y + gap;
            if (y + height > work.Bottom && _anchor.Y - gap - height >= work.Top)
            {
                y = _anchor.Y - gap - height;
            }
            _placed = true;
        }
        else
        {
            // 译文越来越长时窗口往下长，到底了就整体往上推
            x = rect.Left;
            y = rect.Top;
        }
        x = Math.Max(work.Left, Math.Min(x, work.Right - width));
        y = Math.Max(work.Top, Math.Min(y, work.Bottom - height));
        if (x != rect.Left || y != rect.Top)
        {
            SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
        }
    }

    // ---------- 外观 ----------

    private static bool IsInside<T>(DependencyObject d) where T : DependencyObject
    {
        for (var node = d; node is not null; node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is T)
            {
                return true;
            }
        }
        return false;
    }

    private Style Flat(bool accent = false)
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.TemplateProperty, FlatTemplate(typeof(Button))));
        style.Setters.Add(new Setter(Control.BackgroundProperty, accent ? _palette.Accent : Brushes.Transparent));
        style.Setters.Add(new Setter(Control.ForegroundProperty, accent ? Brushes.White : _palette.Subtle));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 4, 10, 4)));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(6, 0, 0, 0)));
        style.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, accent ? _palette.AccentHover : _palette.Surface));
        style.Triggers.Add(hover);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
        style.Triggers.Add(disabled);
        return style;
    }

    private Style FlatToggle()
    {
        var style = new Style(typeof(ToggleButton));
        style.Setters.Add(new Setter(Control.TemplateProperty, FlatTemplate(typeof(ToggleButton))));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.ForegroundProperty, _palette.Subtle));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 2, 6, 2)));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(4, 0, 0, 0)));
        style.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
        style.Setters.Add(new Setter(UIElement.OpacityProperty, 0.5));
        var on = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        on.Setters.Add(new Setter(Control.BackgroundProperty, _palette.Surface));
        on.Setters.Add(new Setter(UIElement.OpacityProperty, 1.0));
        style.Triggers.Add(on);
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, 1.0));
        style.Triggers.Add(hover);
        return style;
    }

    /// <summary>去掉系统按钮那一圈 3D 边框，只留圆角背景和内容。</summary>
    private static ControlTemplate FlatTemplate(Type target)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        return new ControlTemplate(target) { VisualTree = border };
    }

    private sealed record Palette(Brush Background, Brush Surface, Brush Border, Brush Text, Brush Subtle, Brush Accent, Brush AccentHover)
    {
        public static Palette For(string theme)
        {
            var dark = theme == "dark" || (theme != "light" && SystemUsesDarkTheme());
            return dark
                ? new Palette(Hex("#1F2329"), Hex("#2A2F37"), Hex("#383E48"), Hex("#E8EAED"), Hex("#9AA1AC"), Hex("#5568E0"), Hex("#6878E8"))
                : new Palette(Hex("#FFFFFF"), Hex("#F3F5F8"), Hex("#E2E6EC"), Hex("#1A2433"), Hex("#6B7480"), Hex("#3446C9"), Hex("#2B3BB0"));
        }

        private static bool SystemUsesDarkTheme()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static SolidColorBrush Hex(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
    }

    // ---------- Win32 ----------

    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoSize = 0x0001, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint pt, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    /// <summary>鼠标现在的位置（物理像素）。</summary>
    public static NativePoint CursorPosition() => GetCursorPos(out var p) ? p : default;
}

/// <summary>Win32 POINT，物理像素。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct NativePoint
{
    public int X;
    public int Y;
}
