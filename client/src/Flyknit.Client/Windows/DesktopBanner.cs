using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Flyknit.Client.Services;

namespace Flyknit.Client.Windows;

/// <summary>
/// 电脑操作时屏幕顶部的提示条：「FlyknitBuddy 正在操作「xxx」」+「停止」按钮。
/// 置顶、不抢焦点（不然 AI 刚切到目标窗口又被它抢走）、不进任务栏；员工点「停止」立刻停掉这个任务。
/// 自己是 FlyknitBuddy 的窗口，电脑操作本来就读不到也操作不了它（AI 不能替员工点停止或别的按钮）。
/// </summary>
public sealed class DesktopBanner : Window
{
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.White, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 560 };
    private string _conversationId = "";

    /// <summary>员工点了「停止」：对话 ID。</summary>
    public event Action<string>? StopRequested;

    public DesktopBanner()
    {
        Title = "FlyknitBuddy";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;

        var stop = new Button
        {
            Content = NativeStrings.T("desktop.stop"),
            Margin = new Thickness(14, 0, 0, 0),
            Padding = new Thickness(14, 3, 14, 3),
            Background = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            Focusable = false,
        };
        stop.Click += (_, _) =>
        {
            StopRequested?.Invoke(_conversationId);
            Hide();
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Dot());
        row.Children.Add(_text);
        row.Children.Add(stop);
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x1F, 0x3A, 0x8A)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 8, 8, 8),
            Margin = new Thickness(10),
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Direction = 270, Opacity = 0.3 },
            Child = row,
        };
        SizeChanged += (_, _) => Place();
    }

    /// <summary>显示（或更新）：正在操作哪个对话的哪个窗口。</summary>
    public void ShowFor(string conversationId, string windowTitle)
    {
        _conversationId = conversationId;
        _text.Text = NativeStrings.T("desktop.banner").Replace("{title}", windowTitle);
        if (!IsVisible)
        {
            Show();
        }
        Place();
    }

    /// <summary>这个对话的任务结束了：收起（别的对话在用时不收）。</summary>
    public void HideFor(string conversationId)
    {
        if (_conversationId == conversationId && IsVisible)
        {
            Hide();
        }
    }

    private void Place()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Top + 6;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW：点按钮也不抢走目标窗口的焦点，不出现在 Alt+Tab 里
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | 0x08000000 | 0x00000080);
    }

    /// <summary>左边的小圆点（「正在进行」）。</summary>
    private sealed class Dot : Border
    {
        public Dot()
        {
            Width = 8;
            Height = 8;
            CornerRadius = new CornerRadius(4);
            Background = new SolidColorBrush(Color.FromRgb(0x5E, 0xE6, 0x9B));
            Margin = new Thickness(0, 0, 10, 0);
            VerticalAlignment = VerticalAlignment.Center;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
