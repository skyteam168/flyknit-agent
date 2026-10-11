using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Flyknit.Client.Services;

namespace Flyknit.Client.Windows;

/// <summary>
/// 截图提问的框选层。先把整个桌面截下来，再在每块显示器上盖一层「定格的画面 + 半透明遮罩」，
/// 用户拖一个框，裁出那一块存成 PNG。截的是按下快捷键那一刻的画面，所以遮罩本身不会被截进去。
///
/// 每块显示器一个窗口：多显示器缩放比例不同时，一个大窗口横跨几块屏幕坐标会乱。
/// 程序是 PerMonitorV2 感知的，屏幕坐标都按物理像素算。
/// </summary>
public sealed class ScreenshotOverlay : Window
{
    private const double MinSize = 6;

    private readonly System.Drawing.Rectangle _screen;   // 这块显示器（物理像素）
    private readonly Action<System.Drawing.Rectangle?> _done;
    private readonly Canvas _canvas = new();
    private readonly Path _mask;
    private readonly Rectangle _frame;
    private readonly Border _size;
    private Point? _start;
    private Rect _selection = Rect.Empty;

    private ScreenshotOverlay(System.Drawing.Rectangle screen, BitmapSource image, string hint, Action<System.Drawing.Rectangle?> done)
    {
        _screen = screen;
        _done = done;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Cursor = Cursors.Cross;
        Background = new ImageBrush(image) { Stretch = Stretch.Fill };
        // 先放在大概的位置，SourceInitialized 里再按物理像素摆准
        Left = screen.Left;
        Top = screen.Top;
        Width = Math.Max(1, screen.Width);
        Height = Math.Max(1, screen.Height);

        _mask = new Path { Fill = new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)), IsHitTestVisible = false };
        _frame = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0x34, 0x46, 0xC9)),
            StrokeThickness = 2,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        _size = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x1F, 0x23, 0x30)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Child = new TextBlock { Foreground = Brushes.White, FontSize = 12 },
        };
        var tip = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1F, 0x23, 0x30)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 8, 14, 8),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 28, 0, 0),
            IsHitTestVisible = false,
            Child = new TextBlock { Text = hint, Foreground = Brushes.White, FontSize = 14 },
        };
        _canvas.Children.Add(_mask);
        _canvas.Children.Add(_frame);
        _canvas.Children.Add(_size);
        var root = new Grid { Background = Brushes.Transparent };
        root.Children.Add(_canvas);
        root.Children.Add(tip);
        Content = root;

        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowPos(handle, HwndTopmost, screen.Left, screen.Top, screen.Width, screen.Height, SwpNoActivate | SwpShowWindow);
        };
        Loaded += (_, _) => UpdateMask();
        SizeChanged += (_, _) => UpdateMask();
        MouseLeftButtonDown += (_, e) =>
        {
            _start = e.GetPosition(this);
            CaptureMouse();
        };
        MouseMove += (_, e) =>
        {
            if (_start is not { } start || e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }
            var now = e.GetPosition(this);
            _selection = new Rect(start, now);
            UpdateMask();
        };
        MouseLeftButtonUp += (_, _) =>
        {
            ReleaseMouseCapture();
            _start = null;
            if (_selection.Width >= MinSize && _selection.Height >= MinSize)
            {
                _done(ToScreenPixels(_selection));
            }
            else
            {
                _selection = Rect.Empty;
                UpdateMask();
            }
        };
        // 右键、Esc 取消
        MouseRightButtonUp += (_, _) => _done(null);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                _done(null);
            }
        };
    }

    /// <summary>框选区域（窗口内 DIP）→ 屏幕物理像素。</summary>
    private System.Drawing.Rectangle ToScreenPixels(Rect r)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var x = (int)Math.Round(r.X * dpi.DpiScaleX) + _screen.Left;
        var y = (int)Math.Round(r.Y * dpi.DpiScaleY) + _screen.Top;
        var w = (int)Math.Round(r.Width * dpi.DpiScaleX);
        var h = (int)Math.Round(r.Height * dpi.DpiScaleY);
        return System.Drawing.Rectangle.Intersect(new System.Drawing.Rectangle(x, y, w, h), _screen);
    }

    private void UpdateMask()
    {
        var full = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
        if (_selection.IsEmpty || _selection.Width < 1 || _selection.Height < 1)
        {
            _mask.Data = full;
            _frame.Visibility = Visibility.Collapsed;
            _size.Visibility = Visibility.Collapsed;
            return;
        }
        _mask.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(_selection));
        _frame.Visibility = Visibility.Visible;
        Canvas.SetLeft(_frame, _selection.X);
        Canvas.SetTop(_frame, _selection.Y);
        _frame.Width = _selection.Width;
        _frame.Height = _selection.Height;
        var px = ToScreenPixels(_selection);
        ((TextBlock)_size.Child).Text = $"{px.Width} × {px.Height}";
        _size.Visibility = Visibility.Visible;
        Canvas.SetLeft(_size, _selection.X);
        Canvas.SetTop(_size, Math.Max(0, _selection.Y - 24));
    }

    /// <summary>
    /// 让用户框选一块屏幕，返回存好的 PNG 路径；取消返回 null。
    /// 截图存在临时目录：发送时会自动收进资料库（和粘贴的截图一样）。
    /// </summary>
    public static Task<string?> CaptureAsync(string hint, string saveDir)
    {
        var tcs = new TaskCompletionSource<string?>();
        var virtualScreen = System.Windows.Forms.SystemInformation.VirtualScreen;
        System.Drawing.Bitmap desktop;
        try
        {
            desktop = new System.Drawing.Bitmap(virtualScreen.Width, virtualScreen.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = System.Drawing.Graphics.FromImage(desktop);
            g.CopyFromScreen(virtualScreen.Left, virtualScreen.Top, 0, 0, virtualScreen.Size, System.Drawing.CopyPixelOperation.SourceCopy);
        }
        catch (Exception ex)
        {
            Log.Warn("截取屏幕失败", ex);
            return Task.FromResult<string?>(null);
        }

        var windows = new List<ScreenshotOverlay>();
        var finished = false;
        void Finish(System.Drawing.Rectangle? area)
        {
            if (finished)
            {
                return;
            }
            finished = true;
            foreach (var w in windows)
            {
                w.Close();
            }
            string? path = null;
            try
            {
                if (area is { Width: > 0, Height: > 0 } a)
                {
                    var crop = new System.Drawing.Rectangle(a.Left - virtualScreen.Left, a.Top - virtualScreen.Top, a.Width, a.Height);
                    using var part = desktop.Clone(crop, desktop.PixelFormat);
                    System.IO.Directory.CreateDirectory(saveDir);
                    path = System.IO.Path.Combine(saveDir, $"screenshot-{DateTime.Now:yyyyMMdd-HHmmss}.png");
                    part.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("保存截图失败", ex);
                path = null;
            }
            finally
            {
                desktop.Dispose();
            }
            tcs.TrySetResult(path);
        }

        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var b = screen.Bounds;
            var crop = new System.Drawing.Rectangle(b.Left - virtualScreen.Left, b.Top - virtualScreen.Top, b.Width, b.Height);
            using var part = desktop.Clone(crop, desktop.PixelFormat);
            var overlay = new ScreenshotOverlay(b, ToBitmapSource(part), hint, Finish);
            windows.Add(overlay);
            overlay.Show();
        }
        // 鼠标所在的那块屏幕拿到焦点，Esc 才有效
        var cursor = System.Windows.Forms.Cursor.Position;
        var active = windows.Find(w => w._screen.Contains(cursor)) ?? (windows.Count > 0 ? windows[0] : null);
        active?.Activate();
        if (windows.Count == 0)
        {
            Finish(null);
        }
        return tcs.Task;
    }

    private static BitmapSource ToBitmapSource(System.Drawing.Bitmap bitmap)
    {
        var handle = bitmap.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DeleteObject(handle);
        }
    }

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
