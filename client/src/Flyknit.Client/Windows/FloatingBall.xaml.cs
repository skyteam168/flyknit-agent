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

        MouseEnter += (_, _) => AnimateScale(1.08);
        MouseLeave += (_, _) => AnimateScale(1.0);
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
