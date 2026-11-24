using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using Flyknit.Core.Desktop;

namespace Flyknit.Client.Services;

/// <summary>
/// 电脑操作的 Windows 驱动：用系统自带的 UI Automation（读屏软件用的「无障碍树」）读控件、点控件，不截图、不需要视觉模型。
/// - 读：控件视图树，限深度、个数、耗时；看不见的（滚出去的、折叠的）不读；密码框的值不读；
/// - 点：优先用控件自己的操作接口（Invoke / Toggle / 选中 / 展开），没有才把鼠标移过去点；
/// - 填：有 Value 接口就直接设值，没有就聚焦后全选再逐字输入（Unicode，不受输入法影响）；
/// - 操作后等一下再重读；同一个程序弹出了新窗口（对话框）就改读新窗口。
/// 能不能操作由 Core 的 DesktopRisk 先判，这里只负责做（但密码框在这里也再拦一次）。
/// 每个对话一个实例（编号只对这个对话上次读到的内容有效）。UIA 调用都在后台线程做，不占界面线程。
/// </summary>
public sealed class UiaDesktopDriver : IDesktopDriver
{
    private const int MaxIndexed = 300;
    private const int MaxTexts = 150;
    private const int MaxDepth = 25;
    private static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(4);

    private readonly Action<DesktopWindow> _onAction;
    private readonly object _gate = new();
    private Dictionary<int, AutomationElement> _map = new();

    /// <param name="onAction">每次读、点、填之前调用（宿主用它显示「AI 正在操作」提示条）。</param>
    public UiaDesktopDriver(Action<DesktopWindow> onAction) => _onAction = onAction;

    public DesktopSnapshot? Last { get; private set; }

    public Task<IReadOnlyList<DesktopWindow>> WindowsAsync(CancellationToken ct) => Task.Run(() => (IReadOnlyList<DesktopWindow>)ListWindows(), ct);

    private static List<DesktopWindow> ListWindows()
    {
        var self = Environment.ProcessId;
        var list = new List<DesktopWindow>();
        var children = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);
        foreach (AutomationElement w in children)
        {
            try
            {
                var c = w.Current;
                if (c.NativeWindowHandle == 0 || c.ProcessId == self || c.IsOffscreen || string.IsNullOrWhiteSpace(c.Name))
                {
                    continue;
                }
                list.Add(new DesktopWindow(c.NativeWindowHandle, c.Name, ProcessName(c.ProcessId), c.ProcessId));
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
            {
                // 窗口刚好关了
            }
        }
        return list;
    }

    private static string ProcessName(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return "";
        }
    }

    public Task<DesktopActionResult> ReadAsync(long handle, CancellationToken ct) => Task.Run(() =>
    {
        if (handle == 0 && Last is not null)
        {
            handle = Last.Window.Handle;
        }
        var window = ListWindows().FirstOrDefault(w => w.Handle == handle);
        if (window is null)
        {
            return DesktopActionResult.Fail($"找不到句柄为 {handle} 的窗口（可能已经关了），请用 desktop_windows 重新查看");
        }
        _onAction(window);
        return Snapshot(window);
    }, ct);

    public Task<DesktopActionResult> ClickAsync(int index, CancellationToken ct) => Act(index, (el, window) =>
    {
        Foreground(window.Handle);
        if (TryPattern<InvokePattern>(el, InvokePattern.Pattern, out var invoke))
        {
            invoke.Invoke();
        }
        else if (TryPattern<TogglePattern>(el, TogglePattern.Pattern, out var toggle))
        {
            toggle.Toggle();
        }
        else if (TryPattern<SelectionItemPattern>(el, SelectionItemPattern.Pattern, out var select))
        {
            select.Select();
        }
        else if (TryPattern<ExpandCollapsePattern>(el, ExpandCollapsePattern.Pattern, out var expand))
        {
            if (expand.Current.ExpandCollapseState == ExpandCollapseState.Expanded)
            {
                expand.Collapse();
            }
            else
            {
                expand.Expand();
            }
        }
        else if (!MouseClick(el))
        {
            return "这个控件点不了（没有可点击的位置，可能被挡住或在屏幕外），可以先滚动或重新读取";
        }
        return null;
    }, ct);

    public Task<DesktopActionResult> TypeAsync(int index, string text, bool submit, CancellationToken ct) => Act(index, (el, window) =>
    {
        if (el.Current.IsPassword)
        {
            return "密码框不能由 AI 填写";
        }
        Foreground(window.Handle);
        if (TryPattern<ValuePattern>(el, ValuePattern.Pattern, out var value) && !value.Current.IsReadOnly)
        {
            value.SetValue(text);
        }
        else
        {
            if (!el.Current.IsKeyboardFocusable)
            {
                return "这个控件不能输入文字";
            }
            el.SetFocus();
            Thread.Sleep(80);
            Keyboard.Press(new DesktopKeys.Combo(true, false, false, "A"));
            Keyboard.Text(text);
        }
        if (submit)
        {
            el.SetFocus();
            Keyboard.Press(new DesktopKeys.Combo(false, false, false, "Enter"));
        }
        return null;
    }, ct);

    public Task<DesktopActionResult> KeyAsync(DesktopKeys.Combo keys, CancellationToken ct) => Task.Run(() =>
    {
        if (Last is null)
        {
            return DesktopActionResult.Fail("还没有读过窗口：先用 desktop_read 读取");
        }
        _onAction(Last.Window);
        if (!Foreground(Last.Window.Handle))
        {
            return DesktopActionResult.Fail("没能切到这个窗口（可能已经关了或被别的窗口挡住），请重新读取", Last);
        }
        Keyboard.Press(keys);
        return After(Last.Window);
    }, ct);

    public Task<DesktopActionResult> ScrollAsync(int index, string direction, int amount, CancellationToken ct) => Task.Run(() =>
    {
        if (Last is null)
        {
            return DesktopActionResult.Fail("还没有读过窗口：先用 desktop_read 读取");
        }
        _onAction(Last.Window);
        AutomationElement? target;
        lock (_gate)
        {
            target = index > 0 && _map.TryGetValue(index, out var el) ? el : SafeFromHandle(Last.Window.Handle);
        }
        if (target is null)
        {
            return DesktopActionResult.Fail("找不到要滚动的控件，请重新读取", Last);
        }
        // 往上找能滚动的容器
        var walker = TreeWalker.ControlViewWalker;
        for (var e = target; e is not null; e = SafeParent(walker, e))
        {
            if (TryPattern<ScrollPattern>(e, ScrollPattern.Pattern, out var scroll))
            {
                var vertical = direction is "up" or "down";
                var step = direction is "up" or "left" ? ScrollAmount.LargeDecrement : ScrollAmount.LargeIncrement;
                for (var i = 0; i < amount; i++)
                {
                    if (vertical && scroll.Current.VerticallyScrollable)
                    {
                        scroll.ScrollVertical(step);
                    }
                    else if (!vertical && scroll.Current.HorizontallyScrollable)
                    {
                        scroll.ScrollHorizontal(step);
                    }
                }
                return After(Last.Window);
            }
        }
        // 没有滚动接口：鼠标移到上面滚滚轮
        Foreground(Last.Window.Handle);
        if (!Mouse.Wheel(target, direction, amount))
        {
            return DesktopActionResult.Fail("这里滚动不了", Last);
        }
        return After(Last.Window);
    }, ct);

    private Task<DesktopActionResult> Act(int index, Func<AutomationElement, DesktopWindow, string?> action, CancellationToken ct) => Task.Run(() =>
    {
        if (Last is null)
        {
            return DesktopActionResult.Fail("还没有读过窗口：先用 desktop_read 读取");
        }
        AutomationElement? el;
        lock (_gate)
        {
            _map.TryGetValue(index, out el);
        }
        if (el is null)
        {
            return DesktopActionResult.Fail($"找不到编号 [{index}] 的控件，请重新读取窗口", Last);
        }
        _onAction(Last.Window);
        try
        {
            if (!el.Current.IsEnabled)
            {
                return DesktopActionResult.Fail($"[{index}] 现在不可用（灰色），可能要先完成前面的步骤", Last);
            }
            if (action(el, Last.Window) is { } error)
            {
                return DesktopActionResult.Fail(error, Last);
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException or ElementNotEnabledException)
        {
            return DesktopActionResult.Fail($"操作没成功（界面可能变了）：{ex.Message}。请重新读取窗口", Last);
        }
        return After(Last.Window);
    }, ct);

    /// <summary>操作后等界面反应，再读：同一个程序弹出了新窗口（对话框）就读新窗口。</summary>
    private DesktopActionResult After(DesktopWindow window)
    {
        Thread.Sleep(450);
        var fg = (long)GetForegroundWindow();
        var windows = ListWindows();
        var next = fg != 0 && fg != window.Handle ? windows.FirstOrDefault(w => w.Handle == fg && w.ProcessId == window.ProcessId) : null;
        if (next is not null)
        {
            return Snapshot(next);
        }
        if (windows.All(w => w.Handle != window.Handle))
        {
            var sibling = windows.FirstOrDefault(w => w.ProcessId == window.ProcessId);
            return sibling is null
                ? DesktopActionResult.Done(null)
                : Snapshot(sibling);
        }
        return Snapshot(window);
    }

    private DesktopActionResult Snapshot(DesktopWindow window)
    {
        var root = SafeFromHandle(window.Handle);
        if (root is null)
        {
            return DesktopActionResult.Fail("这个窗口读不了（可能已经关了，或是用管理员权限运行的程序）");
        }
        var elements = new List<DesktopElement>();
        var map = new Dictionary<int, AutomationElement>();
        var watch = Stopwatch.StartNew();
        var truncated = false;
        var texts = 0;
        var walker = TreeWalker.ControlViewWalker;

        void Walk(AutomationElement parent, int depth)
        {
            if (depth > MaxDepth)
            {
                return;
            }
            for (var child = SafeFirstChild(walker, parent); child is not null; child = SafeNext(walker, child))
            {
                if (map.Count >= MaxIndexed || watch.Elapsed > ReadBudget)
                {
                    truncated = true;
                    return;
                }
                AutomationElement.AutomationElementInformation c;
                try
                {
                    c = child.Current;
                }
                catch (Exception ex) when (ex is ElementNotAvailableException or COMException)
                {
                    continue;
                }
                if (c.IsOffscreen)
                {
                    continue; // 滚出去、折叠起来的：连同子控件都不读
                }
                var role = Role(child, c.ControlType);
                var name = (c.Name ?? "").Trim();
                if (role is not null)
                {
                    var index = map.Count + 1;
                    map[index] = child;
                    elements.Add(new DesktopElement
                    {
                        Index = index,
                        Role = role,
                        Name = name.Length > 0 ? name : (c.HelpText ?? "").Trim(),
                        Value = c.IsPassword ? "" : ValueOf(child),
                        Enabled = c.IsEnabled,
                        IsPassword = c.IsPassword,
                        Checked = CheckedOf(child),
                        Depth = depth,
                    });
                }
                else if (c.ControlType == ControlType.Text && name.Length > 0)
                {
                    if (texts++ < MaxTexts)
                    {
                        elements.Add(new DesktopElement { Index = 0, Role = "text", Name = name, Depth = depth });
                    }
                    else
                    {
                        truncated = true;
                    }
                }
                // 表格、列表、树、菜单的子项也要读；按钮、输入框里面一般没有要操作的东西
                if (role is null or "combobox" or "tree_item" or "menu_item" or "tab" or "split_button" or "cell")
                {
                    Walk(child, depth + 1);
                }
            }
        }

        try
        {
            Walk(root, 0);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException)
        {
            truncated = true;
        }
        var snap = new DesktopSnapshot(window, elements, truncated);
        lock (_gate)
        {
            _map = map;
            Last = snap;
        }
        return DesktopActionResult.Done(snap);
    }

    /// <summary>能操作的控件类型 → 角色；null = 不编号（纯文字、容器）。</summary>
    private static string? Role(AutomationElement el, ControlType t)
    {
        if (t == ControlType.Button) return "button";
        if (t == ControlType.Edit) return "edit";
        if (t == ControlType.CheckBox) return "checkbox";
        if (t == ControlType.RadioButton) return "radio";
        if (t == ControlType.ComboBox) return "combobox";
        if (t == ControlType.ListItem) return "list_item";
        if (t == ControlType.MenuItem) return "menu_item";
        if (t == ControlType.TabItem) return "tab";
        if (t == ControlType.TreeItem) return "tree_item";
        if (t == ControlType.DataItem) return "cell";
        if (t == ControlType.Hyperlink) return "link";
        if (t == ControlType.SplitButton) return "split_button";
        if (t == ControlType.Spinner) return "spinner";
        if (t == ControlType.Slider) return "slider";
        if (t == ControlType.Document && Has(el, ValuePattern.Pattern)) return "document";
        // 自绘控件：能 Invoke 的当按钮
        if ((t == ControlType.Custom || t == ControlType.Pane || t == ControlType.Image) && Has(el, InvokePattern.Pattern)) return "button";
        return null;
    }

    private static bool Has(AutomationElement el, AutomationPattern pattern)
    {
        try
        {
            return el.TryGetCurrentPattern(pattern, out _);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException)
        {
            return false;
        }
    }

    private static bool TryPattern<T>(AutomationElement el, AutomationPattern pattern, out T value) where T : class
    {
        value = null!;
        try
        {
            if (el.TryGetCurrentPattern(pattern, out var p) && p is T t)
            {
                value = t;
                return true;
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException)
        {
        }
        return false;
    }

    private static string ValueOf(AutomationElement el)
    {
        if (TryPattern<ValuePattern>(el, ValuePattern.Pattern, out var v))
        {
            try
            {
                var s = v.Current.Value ?? "";
                return s.Length > 200 ? s[..200] + "…" : s;
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
            {
            }
        }
        if (TryPattern<SelectionPattern>(el, SelectionPattern.Pattern, out var sel))
        {
            try
            {
                return string.Join("、", sel.Current.GetSelection().Cast<AutomationElement>().Select(x => x.Current.Name).Take(5));
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
            {
            }
        }
        return "";
    }

    private static bool? CheckedOf(AutomationElement el)
    {
        if (TryPattern<TogglePattern>(el, TogglePattern.Pattern, out var t))
        {
            try
            {
                return t.Current.ToggleState == ToggleState.On;
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or COMException)
            {
            }
        }
        if (TryPattern<SelectionItemPattern>(el, SelectionItemPattern.Pattern, out var s) && el.Current.ControlType == ControlType.RadioButton)
        {
            try
            {
                return s.Current.IsSelected;
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or COMException)
            {
            }
        }
        return null;
    }

    private static AutomationElement? SafeFromHandle(long handle)
    {
        try
        {
            return AutomationElement.FromHandle(new IntPtr(handle));
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or ArgumentException or COMException)
        {
            return null;
        }
    }

    private static AutomationElement? SafeFirstChild(TreeWalker w, AutomationElement e)
    {
        try { return w.GetFirstChild(e); }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException) { return null; }
    }

    private static AutomationElement? SafeNext(TreeWalker w, AutomationElement e)
    {
        try { return w.GetNextSibling(e); }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException) { return null; }
    }

    private static AutomationElement? SafeParent(TreeWalker w, AutomationElement e)
    {
        try
        {
            var p = w.GetParent(e);
            return p == AutomationElement.RootElement ? null : p;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException) { return null; }
    }

    private static bool MouseClick(AutomationElement el)
    {
        try
        {
            if (!el.TryGetClickablePoint(out var pt))
            {
                var r = el.Current.BoundingRectangle;
                if (r.IsEmpty || r.Width <= 0 || r.Height <= 0)
                {
                    return false;
                }
                pt = new System.Windows.Point(r.Left + r.Width / 2, r.Top + r.Height / 2);
            }
            Mouse.Click((int)pt.X, (int)pt.Y);
            return true;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or NoClickablePointException)
        {
            return false;
        }
    }

    private static bool Foreground(long handle)
    {
        var h = new IntPtr(handle);
        if (IsIconic(h))
        {
            ShowWindow(h, 9); // SW_RESTORE
        }
        SetForegroundWindow(h);
        Thread.Sleep(60);
        return GetForegroundWindow() == h;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>SendInput 键盘：组合键、Unicode 文字。</summary>
    private static class Keyboard
    {
        private static readonly Dictionary<string, ushort> Vk = new(StringComparer.Ordinal)
        {
            ["Enter"] = 0x0D, ["Tab"] = 0x09, ["Esc"] = 0x1B, ["Space"] = 0x20, ["Backspace"] = 0x08, ["Delete"] = 0x2E,
            ["Up"] = 0x26, ["Down"] = 0x28, ["Left"] = 0x25, ["Right"] = 0x27, ["Home"] = 0x24, ["End"] = 0x23,
            ["PageUp"] = 0x21, ["PageDown"] = 0x22,
        };

        private static ushort Code(string key)
        {
            if (Vk.TryGetValue(key, out var v))
            {
                return v;
            }
            if (key.Length >= 2 && key[0] == 'F' && int.TryParse(key[1..], out var f) && f is >= 1 and <= 12)
            {
                return (ushort)(0x70 + f - 1);
            }
            return key.Length == 1 ? (ushort)char.ToUpperInvariant(key[0]) : (ushort)0;
        }

        public static void Press(DesktopKeys.Combo combo)
        {
            var inputs = new List<Native.INPUT>();
            var mods = new List<ushort>();
            if (combo.Ctrl) mods.Add(0x11);
            if (combo.Shift) mods.Add(0x10);
            if (combo.Alt) mods.Add(0x12);
            foreach (var m in mods) inputs.Add(Native.Key(m, false));
            var key = Code(combo.Key);
            if (key == 0)
            {
                return;
            }
            inputs.Add(Native.Key(key, false));
            inputs.Add(Native.Key(key, true));
            for (var i = mods.Count - 1; i >= 0; i--) inputs.Add(Native.Key(mods[i], true));
            Native.Send(inputs);
            Thread.Sleep(60);
        }

        public static void Text(string text)
        {
            var inputs = new List<Native.INPUT>();
            foreach (var ch in text.Replace("\r\n", "\n"))
            {
                if (ch == '\n')
                {
                    inputs.Add(Native.Key(0x0D, false));
                    inputs.Add(Native.Key(0x0D, true));
                    continue;
                }
                inputs.Add(Native.Unicode(ch, false));
                inputs.Add(Native.Unicode(ch, true));
            }
            Native.Send(inputs);
            Thread.Sleep(60);
        }
    }

    private static class Mouse
    {
        public static void Click(int x, int y)
        {
            Native.SetCursorPos(x, y);
            Thread.Sleep(40);
            Native.Send(new List<Native.INPUT> { Native.MouseButton(0x0002), Native.MouseButton(0x0004) }); // LEFTDOWN / LEFTUP
            Thread.Sleep(60);
        }

        public static bool Wheel(AutomationElement el, string direction, int amount)
        {
            try
            {
                var r = el.Current.BoundingRectangle;
                if (r.IsEmpty)
                {
                    return false;
                }
                Native.SetCursorPos((int)(r.Left + r.Width / 2), (int)(r.Top + r.Height / 2));
                var vertical = direction is "up" or "down";
                var delta = (direction is "up" or "right" ? 120 : -120) * 3;
                for (var i = 0; i < amount; i++)
                {
                    Native.Send(new List<Native.INPUT> { Native.MouseWheel(delta, vertical) });
                    Thread.Sleep(80);
                }
                return true;
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or COMException)
            {
                return false;
            }
        }
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint Type;
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT Mi;
            [FieldOffset(0)] public KEYBDINPUT Ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int Dx;
            public int Dy;
            public int MouseData;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort Vk;
            public ushort Scan;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        private const uint InputMouse = 0;
        private const uint InputKeyboard = 1;
        private const uint KeyUp = 0x0002;
        private const uint KeyUnicode = 0x0004;

        public static INPUT Key(ushort vk, bool up) => new()
        {
            Type = InputKeyboard,
            U = new InputUnion { Ki = new KEYBDINPUT { Vk = vk, Flags = up ? KeyUp : 0 } },
        };

        public static INPUT Unicode(char ch, bool up) => new()
        {
            Type = InputKeyboard,
            U = new InputUnion { Ki = new KEYBDINPUT { Scan = ch, Flags = KeyUnicode | (up ? KeyUp : 0) } },
        };

        public static INPUT MouseButton(uint flags) => new() { Type = InputMouse, U = new InputUnion { Mi = new MOUSEINPUT { Flags = flags } } };

        public static INPUT MouseWheel(int delta, bool vertical) => new()
        {
            Type = InputMouse,
            U = new InputUnion { Mi = new MOUSEINPUT { MouseData = delta, Flags = vertical ? 0x0800u : 0x01000u } }, // WHEEL / HWHEEL
        };

        public static void Send(List<INPUT> inputs)
        {
            if (inputs.Count > 0)
            {
                SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
            }
        }

        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    }
}
