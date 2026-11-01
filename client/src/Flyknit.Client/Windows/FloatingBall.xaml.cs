using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Flyknit.Client.Services;

namespace Flyknit.Client.Windows;

/// <summary>右下角的悬浮球：单击打开 / 收起主窗口，可拖动并吸附屏幕边缘，可把文件拖到上面。</summary>
public partial class FloatingBall : Window
{
    private readonly AppSettings _settings;
    private Point _pressPoint;
    private bool _dragging;

    /// <summary>单击。</summary>
    public event Action? Clicked;

    /// <summary>文件被拖到悬浮球上。</summary>
    public event Action<string[]>? FilesDropped;

    public FloatingBall(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        ToolTip = NativeStrings.T("ball.tip");
        Loaded += (_, _) => RestorePosition();

        // 进度环（有计划时按完成比例画一段弧）和暂停标记：都画在光环那一层上
        Root.Children.Insert(1, _arc);
        Root.Children.Add(_pauseMark);

        MouseEnter += (_, _) =>
        {
            AnimateScale(1.08);
            RememberForeground();
            HoverChanged?.Invoke(true);
        };
        MouseLeave += (_, _) =>
        {
            AnimateScale(1.0);
            HoverChanged?.Invoke(false);
        };
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        DragEnter += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            AnimateScale(1.15);
        };
        DragLeave += (_, _) => AnimateScale(1.0);
        Drop += (_, e) =>
        {
            AnimateScale(1.0);
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                FilesDropped?.Invoke(files);
            }
        };
    }

    /// <summary>鼠标移到悬浮球上 / 移开（有任务在跑时用来弹出任务面板）。</summary>
    public event Action<bool>? HoverChanged;

    private readonly System.Windows.Shapes.Path _arc = new()
    {
        Stroke = new SolidColorBrush(Color.FromRgb(0x0E, 0x9A, 0x8E)),
        StrokeThickness = 3,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
    };

    private readonly System.Windows.Controls.Border _pauseMark = new()
    {
        Width = 18,
        Height = 18,
        CornerRadius = new CornerRadius(9),
        Background = new SolidColorBrush(Color.FromRgb(0x6B, 0x74, 0x80)),
        BorderBrush = Brushes.White,
        BorderThickness = new Thickness(2),
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Bottom,
        Margin = new Thickness(0, 0, 4, 4),
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
        Child = new System.Windows.Controls.TextBlock
        {
            Text = "❚❚",
            FontSize = 6.5,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
    };

    /// <summary>
    /// 任务进度：只有一个任务且有计划时画出完成比例的弧；全部暂停时停下旋转、显示暂停标记；
    /// 提示文字是「标题 · 2/5 · 正在做的事」。
    /// </summary>
    public void SetProgress(System.Collections.Generic.IReadOnlyList<RunSnapshot> runs, bool waiting)
    {
        var allPaused = runs.Count > 0 && runs.All(r => r.Paused);
        _pauseMark.Visibility = allPaused ? Visibility.Visible : Visibility.Collapsed;
        if (allPaused)
        {
            HaloRotate.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
            Halo.Stroke = new SolidColorBrush(Color.FromRgb(0x9A, 0xA1, 0xAC));
        }
        var single = runs.Count == 1 ? runs[0] : null;
        if (single is { Total: > 0 })
        {
            _arc.Data = Arc(32, 32, 29, Math.Clamp((double)single.Done / single.Total, 0, 1));
            _arc.Visibility = single.Done > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            _arc.Visibility = Visibility.Collapsed;
        }
        if (waiting)
        {
            return; // 「有操作等你确认」更要紧，提示文字留给它
        }
        ToolTip = runs.Count switch
        {
            0 => NativeStrings.T("ball.tip"),
            1 => string.Join(" · ", new[]
                {
                    runs[0].Paused ? NativeStrings.T("ball.panel.paused") : "",
                    runs[0].Title,
                    runs[0].Total > 0 ? $"{runs[0].Done}/{runs[0].Total}" : "",
                    runs[0].Step,
                }.Where(x => x.Length > 0)),
            _ => NativeStrings.T("ball.progress.many").Replace("{n}", runs.Count.ToString()),
        };
    }

    /// <summary>从 12 点方向顺时针画占 fraction 的一段圆弧。</summary>
    private static Geometry Arc(double cx, double cy, double r, double fraction)
    {
        if (fraction >= 0.999)
        {
            return new EllipseGeometry(new Point(cx, cy), r, r);
        }
        var angle = fraction * 2 * Math.PI;
        var start = new Point(cx, cy - r);
        var end = new Point(cx + r * Math.Sin(angle), cy - r * Math.Cos(angle));
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(r, r), 0, fraction > 0.5, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    /// <summary>busy：有任务在运行；waiting：有操作等待确认。</summary>
    public void SetState(bool busy, bool waiting)
    {
        Badge.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
        ToolTip = waiting ? NativeStrings.T("ball.waiting") : NativeStrings.T("ball.tip");

        if (busy || waiting)
        {
            Halo.Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(waiting ? "#F0A43A" : "#0E9A8E"));
            Halo.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
            if (busy)
            {
                HaloRotate.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty,
                    new DoubleAnimation(0, 360, TimeSpan.FromSeconds(2.4)) { RepeatBehavior = RepeatBehavior.Forever });
            }
        }
        else
        {
            Halo.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)));
            HaloRotate.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
        }
    }

    public void RefreshText() => ToolTip = NativeStrings.T("ball.tip");

    /// <summary>右键菜单里的一项。gesture 是右侧灰字显示的快捷键。</summary>
    public readonly record struct MenuEntry(string Text, Action OnClick, string Gesture = "");

    /// <summary>
    /// 每次右键时由 App 重新给菜单内容（界面语言、安全中心开关随时会变）。null 表示分隔线。
    /// </summary>
    public Func<System.Collections.Generic.IReadOnlyList<MenuEntry?>>? MenuItems { get; set; }

    /// <summary>
    /// 鼠标移到悬浮球上那一刻在前台的别的程序。从菜单里划词翻译时要把焦点还给它，才取得到选中的字
    /// （一点悬浮球，前台就变成悬浮球了）。
    /// </summary>
    public IntPtr LastForeground { get; private set; }

    private void RememberForeground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return;
        }
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid != (uint)Environment.ProcessId)
        {
            LastForeground = hwnd;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>悬浮球在屏幕上的位置（DIP），对话浮窗据此贴在旁边。</summary>
    public Rect Bounds => new(Left, Top, Width, Height);

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (MenuItems?.Invoke() is not { Count: > 0 } entries)
        {
            return;
        }
        var menu = new System.Windows.Controls.ContextMenu
        {
            Style = (Style)FindResource("BallMenu"),
            PlacementTarget = this,
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
        };
        foreach (var entry in entries)
        {
            if (entry is not { } item)
            {
                menu.Items.Add(new System.Windows.Controls.Separator { Style = (Style)FindResource("BallMenuSeparator") });
                continue;
            }
            var mi = new System.Windows.Controls.MenuItem
            {
                Header = item.Text,
                InputGestureText = item.Gesture,
                Style = (Style)FindResource("BallMenuItem"),
            };
            mi.Click += (_, _) => item.OnClick();
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _pressPoint = e.GetPosition(this);
        _dragging = false;
        CaptureMouse();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || !IsMouseCaptured)
        {
            return;
        }
        var pos = e.GetPosition(this);
        if (!_dragging && (Math.Abs(pos.X - _pressPoint.X) > 4 || Math.Abs(pos.Y - _pressPoint.Y) > 4))
        {
            _dragging = true;
        }
        if (_dragging)
        {
            Left += pos.X - _pressPoint.X;
            Top += pos.Y - _pressPoint.Y;
        }
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        if (_dragging)
        {
            SnapToEdge();
            _dragging = false;
        }
        else
        {
            Clicked?.Invoke();
        }
    }

    /// <summary>松手后吸附到最近的左右边缘，并保存位置。</summary>
    private void SnapToEdge()
    {
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)(Left + Width / 2), (int)(Top + Height / 2)));
        var dpi = VisualTreeHelper.GetDpi(this);
        var area = new Rect(
            screen.WorkingArea.Left / dpi.DpiScaleX,
            screen.WorkingArea.Top / dpi.DpiScaleY,
            screen.WorkingArea.Width / dpi.DpiScaleX,
            screen.WorkingArea.Height / dpi.DpiScaleY);

        var targetLeft = Left + Width / 2 < area.Left + area.Width / 2 ? area.Left + 8 : area.Right - Width - 8;
        var targetTop = Math.Clamp(Top, area.Top + 8, area.Bottom - Height - 8);
        var anim = new DoubleAnimation(targetLeft, TimeSpan.FromMilliseconds(180)) { EasingFunction = new QuadraticEase() };
        anim.Completed += (_, _) =>
        {
            BeginAnimation(LeftProperty, null);
            Left = targetLeft;
        };
        BeginAnimation(LeftProperty, anim);
        Top = targetTop;

        _settings.BallLeft = targetLeft;
        _settings.BallTop = targetTop;
        _settings.Save();
    }

    private void RestorePosition()
    {
        var area = SystemParameters.WorkArea;
        var left = _settings.BallLeft ?? area.Right - Width - 16;
        var top = _settings.BallTop ?? area.Bottom - Height - 24;
        // 显示器配置变化后位置可能在屏幕外（Screen 使用物理像素，需要按 DPI 换算）
        var dpi = VisualTreeHelper.GetDpi(this);
        var px = (int)((left + 10) * dpi.DpiScaleX);
        var py = (int)((top + 10) * dpi.DpiScaleY);
        var visible = System.Windows.Forms.Screen.AllScreens.Any(s => s.WorkingArea.Contains(px, py));
        Left = visible ? left : area.Right - Width - 16;
        Top = visible ? top : area.Bottom - Height - 24;
    }

    private void AnimateScale(double to)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(120));
        Scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        Scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }
}
