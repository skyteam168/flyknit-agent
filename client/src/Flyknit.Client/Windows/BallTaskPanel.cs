using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Flyknit.Client.Services;

namespace Flyknit.Client.Windows;

/// <summary>
/// 鼠标停在悬浮球上、有任务在跑时弹出的任务面板：每个任务一张小卡片，
/// 显示做到哪一步（有计划时带进度条）、用了多久，可以暂停 / 继续、停止、插话，点标题回到这个任务。
/// </summary>
public sealed class BallTaskPanel : Window
{
    private const double PanelWidth = 320;

    private readonly TranslatePopup.Palette _palette;
    private readonly StackPanel _list = new();
    private readonly Dictionary<string, Row> _rows = new();

    public event Action<string>? OpenRequested;
    public event Action<string>? PauseRequested;
    public event Action<string>? ResumeRequested;
    public event Action<string>? StopRequested;

    /// <summary>插话：任务 ID、说的话、steer（插话）/ queue（排到这一轮之后）。</summary>
    public event Action<string, string, string>? InterjectRequested;

    public BallTaskPanel(string theme)
    {
        _palette = TranslatePopup.Palette.For(theme);
        Title = "FlyknitBuddy";
        Width = PanelWidth + 24;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        // 弹出时不抢焦点（员工正在别的程序里打字）；点「插话」时才激活，让输入框能打字
        ShowActivated = false;

        var card = new Border
        {
            Background = _palette.Background,
            BorderBrush = _palette.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(6),
            Margin = new Thickness(12),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Direction = 270, Opacity = 0.18, Color = Color.FromRgb(0x1A, 0x24, 0x33) },
            Child = _list,
        };
        Content = card;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Hide();
            }
        };
    }

    /// <summary>员工正在面板里打插话：这时鼠标移开也不要收起面板。</summary>
    public bool Editing => _rows.Values.Any(r => r.Input.IsKeyboardFocusWithin || r.Input.Text.Length > 0);

    /// <summary>按最新的任务列表刷新（保留正在打的插话）。没有任务时收起。</summary>
    public void Update(IReadOnlyList<RunSnapshot> runs)
    {
        foreach (var id in _rows.Keys.Where(id => runs.All(r => r.ConversationId != id)).ToList())
        {
            _list.Children.Remove(_rows[id].Root);
            _rows.Remove(id);
        }
        foreach (var run in runs.Take(4))
        {
            if (!_rows.TryGetValue(run.ConversationId, out var row))
            {
                row = new Row(this, run.ConversationId);
                _rows[run.ConversationId] = row;
                _list.Children.Add(row.Root);
            }
            row.Set(run);
        }
        if (runs.Count == 0 && IsVisible)
        {
            Hide();
        }
    }

    /// <summary>贴着悬浮球显示：球在屏幕右半边就放左边，否则放右边；上下不超出工作区。</summary>
    public void ShowNear(Rect ball)
    {
        var area = SystemParameters.WorkArea;
        if (!IsVisible)
        {
            Show();
        }
        UpdateLayout();
        var height = ActualHeight > 0 ? ActualHeight : 160;
        Left = ball.Left + ball.Width / 2 > area.Left + area.Width / 2
            ? ball.Left - Width + 8
            : ball.Right - 8;
        Top = Math.Clamp(ball.Top + ball.Height / 2 - height / 2, area.Top + 4, Math.Max(area.Top + 4, area.Bottom - height - 4));
    }

    // ---------- 一个任务 ----------

    private sealed class Row
    {
        private readonly BallTaskPanel _panel;
        private readonly string _id;
        private readonly TextBlock _title;
        private readonly TextBlock _status;
        private readonly Border _barTrack;
        private readonly Border _barFill;
        private readonly Button _pause;
        private readonly StackPanel _inputBox;
        private bool _paused;

        public Border Root { get; }
        public TextBox Input { get; }

        public Row(BallTaskPanel panel, string id)
        {
            _panel = panel;
            _id = id;
            var p = panel._palette;

            _title = new TextBlock
            {
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = p.Text,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Cursor = Cursors.Hand,
                ToolTip = NativeStrings.T("ball.panel.open"),
            };
            _title.MouseLeftButtonUp += (_, _) => panel.OpenRequested?.Invoke(_id);

            _status = new TextBlock
            {
                FontSize = 12,
                Foreground = p.Subtle,
                Margin = new Thickness(0, 3, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            _barFill = new Border { Background = p.Accent, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
            _barTrack = new Border
            {
                Height = 4,
                Margin = new Thickness(0, 7, 0, 0),
                CornerRadius = new CornerRadius(2),
                Background = p.Surface,
                Child = _barFill,
            };

            _pause = panel.MakeButton("", false);
            _pause.Click += (_, _) =>
            {
                if (_paused)
                {
                    panel.ResumeRequested?.Invoke(_id);
                }
                else
                {
                    panel.PauseRequested?.Invoke(_id);
                }
            };
            var stop = panel.MakeButton(NativeStrings.T("ball.panel.stop"), false);
            stop.Click += (_, _) => panel.StopRequested?.Invoke(_id);
            var talk = panel.MakeButton(NativeStrings.T("ball.panel.interject"), true);
            talk.Click += (_, _) => ToggleInput();

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            buttons.Children.Add(_pause);
            buttons.Children.Add(stop);
            buttons.Children.Add(talk);

            Input = new TextBox
            {
                FontSize = 13,
                MinHeight = 30,
                MaxHeight = 90,
                AcceptsReturn = false,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(6, 4, 6, 4),
                Background = p.Surface,
                Foreground = p.Text,
                CaretBrush = p.Text,
                BorderBrush = p.Border,
                BorderThickness = new Thickness(1),
                ToolTip = NativeStrings.T("ball.panel.placeholder"),
            };
            Input.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
                {
                    e.Handled = true;
                    Send("steer");
                }
            };
            var hint = new TextBlock
            {
                Text = NativeStrings.T("ball.panel.placeholder"),
                FontSize = 11.5,
                Foreground = p.Subtle,
                Margin = new Thickness(0, 0, 0, 4),
                TextWrapping = TextWrapping.Wrap,
            };
            var send = panel.MakeButton(NativeStrings.T("ball.panel.send"), true);
            send.Click += (_, _) => Send("steer");
            var queue = panel.MakeButton(NativeStrings.T("ball.panel.queue"), false);
            queue.Click += (_, _) => Send("queue");
            var sendRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
            sendRow.Children.Add(queue);
            sendRow.Children.Add(send);
            _inputBox = new StackPanel { Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
            _inputBox.Children.Add(hint);
            _inputBox.Children.Add(Input);
            _inputBox.Children.Add(sendRow);

            var stack = new StackPanel();
            stack.Children.Add(_title);
            stack.Children.Add(_status);
            stack.Children.Add(_barTrack);
            stack.Children.Add(buttons);
            stack.Children.Add(_inputBox);
            Root = new Border { Padding = new Thickness(10, 8, 10, 10), CornerRadius = new CornerRadius(8), Child = stack };
            Root.MouseEnter += (_, _) => Root.Background = p.Surface;
            Root.MouseLeave += (_, _) => Root.Background = Brushes.Transparent;
        }

        public void Set(RunSnapshot run)
        {
            _paused = run.Paused;
            _title.Text = (run.Remote ? "📱 " : "") + run.Title;
            var elapsed = DateTimeOffset.Now - run.StartedAt;
            var time = elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"m\:ss");
            var parts = new List<string>();
            if (run.Paused)
            {
                parts.Add(NativeStrings.T("ball.panel.paused"));
            }
            if (run.Total > 0)
            {
                parts.Add($"{run.Done}/{run.Total}");
            }
            parts.Add(time);
            if (run.Step.Length > 0)
            {
                parts.Add(run.Step);
            }
            _status.Text = string.Join(" · ", parts);
            _status.ToolTip = run.Step.Length > 0 ? run.Step : null;
            _barTrack.Visibility = run.Total > 0 ? Visibility.Visible : Visibility.Collapsed;
            _barFill.Width = run.Total > 0 ? (PanelWidth - 32) * Math.Clamp((double)run.Done / run.Total, 0, 1) : 0;
            _barFill.Background = run.Paused ? _panel._palette.Subtle : _panel._palette.Accent;
            _pause.Content = NativeStrings.T(run.Paused ? "ball.panel.resume" : "ball.panel.pause");
        }

        private void ToggleInput()
        {
            var open = _inputBox.Visibility != Visibility.Visible;
            _inputBox.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            if (open)
            {
                // 要打字就得让面板拿到键盘焦点
                _panel.Activate();
                Input.Focus();
            }
        }

        private void Send(string mode)
        {
            var text = Input.Text.Trim();
            if (text.Length == 0)
            {
                return;
            }
            _panel.InterjectRequested?.Invoke(_id, text, mode);
            Input.Clear();
            _inputBox.Visibility = Visibility.Collapsed;
        }
    }

    private Button MakeButton(string text, bool accent)
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.TemplateProperty, TranslatePopup.FlatTemplate(typeof(Button))));
        style.Setters.Add(new Setter(Control.BackgroundProperty, accent ? _palette.Accent : Brushes.Transparent));
        style.Setters.Add(new Setter(Control.ForegroundProperty, accent ? Brushes.White : _palette.Text));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 12.5));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 4, 10, 4)));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(6, 0, 0, 0)));
        style.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, accent ? _palette.AccentHover : _palette.Border));
        style.Triggers.Add(hover);
        return new Button { Content = text, Style = style };
    }
}
