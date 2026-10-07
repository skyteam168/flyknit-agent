using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using Flyknit.Core.Translation;

namespace Flyknit.Client.Services;

/// <summary>
/// 读出用户在别的程序里选中的文字（划词翻译用）。
///
/// 先问 UI Automation：记事本、Word、Edge / Chrome 的网页、大多数 WPF / WinForms 程序都能直接给出选区，
/// 不碰剪贴板。问不到再模拟一次 Ctrl+C，读完把剪贴板原样放回去——用户剪贴板里本来的东西不能丢。
///
/// 必须在界面线程（STA）上调用：剪贴板只能在 STA 线程上读写。
/// </summary>
public static class SelectionCapture
{
    /// <summary>UI Automation 最多等这么久。个别程序的 UIA 实现会卡住，不能让热键跟着卡。</summary>
    private static readonly TimeSpan AutomationTimeout = TimeSpan.FromMilliseconds(700);

    /// <summary>
    /// 这些窗口里按 Ctrl+C 在没有选区时是「中断正在运行的程序」，绝不能模拟。
    /// </summary>
    private static readonly HashSet<string> ConsoleClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "ConsoleWindowClass",              // cmd / PowerShell 控制台
        "CASCADIA_HOSTING_WINDOW_CLASS",   // Windows Terminal
        "mintty",                          // Git Bash
        "PuTTY",
        "VirtualConsoleClass",             // ConEmu
    };

    public static async Task<string> CaptureAsync()
    {
        var foreground = GetForegroundWindow();

        var text = await ReadByAutomationAsync();
        if (!string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        if (foreground == IntPtr.Zero || ConsoleClasses.Contains(ClassName(foreground)))
        {
            return "";
        }

        // 热键的修饰键（Ctrl+Alt）还按着的时候发 Ctrl+C，目标程序收到的是 Ctrl+Alt+C
        if (!await WaitForModifiersReleasedAsync())
        {
            Log.Info("划词翻译：修饰键一直按着，跳过模拟复制");
            return "";
        }
        return await ReadByCopyAsync();
    }

    // ---------- UI Automation ----------

    private static async Task<string?> ReadByAutomationAsync()
    {
        // UIA 客户端调用放到线程池（MTA）上：在界面线程上直接调，碰到本进程的窗口会互相等死
        var work = Task.Run(() =>
        {
            try
            {
                var focused = AutomationElement.FocusedElement;
                if (focused is null || !focused.TryGetCurrentPattern(TextPattern.Pattern, out var pattern) || pattern is not TextPattern text)
                {
                    return null;
                }
                var sb = new StringBuilder();
                foreach (var range in text.GetSelection())
                {
                    if (sb.Length > 0)
                    {
                        sb.Append('\n');
                    }
                    sb.Append(range.GetText(SelectionTranslation.MaxChars + 1));
                    if (sb.Length > SelectionTranslation.MaxChars)
                    {
                        break;
                    }
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                // 不支持、元素已消失、跨权限（目标程序以管理员运行）都会走到这里，换剪贴板的办法
                Log.Info($"划词翻译：UI Automation 读不到选区（{ex.GetType().Name}）");
                return null;
            }
        });
        var finished = await Task.WhenAny(work, Task.Delay(AutomationTimeout));
        return finished == work ? work.Result : null;
    }

    // ---------- 模拟复制 ----------

    private static async Task<string> ReadByCopyAsync()
    {
        var saved = Snapshot(out var wasEmpty);
        var before = GetClipboardSequenceNumber();

        keybd_event(VkControl, 0, 0, UIntPtr.Zero);
        keybd_event(VkC, 0, 0, UIntPtr.Zero);
        keybd_event(VkC, 0, KeyEventKeyUp, UIntPtr.Zero);
        keybd_event(VkControl, 0, KeyEventKeyUp, UIntPtr.Zero);

        // 剪贴板序号变了才说明对方真的复制了；没有选区时大多数程序什么都不做
        var deadline = Environment.TickCount64 + 600;
        while (GetClipboardSequenceNumber() == before && Environment.TickCount64 < deadline)
        {
            await Task.Delay(20);
        }
        if (GetClipboardSequenceNumber() == before)
        {
            return "";
        }
        // Excel 这类程序是先清空再一个格式一个格式往里放，稍等它放完
        await Task.Delay(40);

        var text = "";
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
                break;
            }
            catch (COMException)
            {
                await Task.Delay(30); // 剪贴板正被别的程序打开着
            }
        }

        var afterCopy = GetClipboardSequenceNumber();
        await RestoreAsync(saved, wasEmpty, afterCopy);
        return text;
    }

    /// <summary>
    /// 把剪贴板现在的内容逐个格式拷一份。直接留着 GetDataObject() 的结果没用——
    /// 那是对剪贴板的引用，剪贴板一换，它也跟着变。
    /// </summary>
    private static DataObject? Snapshot(out bool wasEmpty)
    {
        wasEmpty = false;
        try
        {
            var current = Clipboard.GetDataObject();
            var formats = current?.GetFormats(false) ?? Array.Empty<string>();
            if (formats.Length == 0)
            {
                wasEmpty = true;
                return null;
            }
            var copy = new DataObject();
            var any = false;
            foreach (var format in formats)
            {
                try
                {
                    if (current!.GetData(format, false) is { } data)
                    {
                        copy.SetData(format, data, false);
                        any = true;
                    }
                }
                catch (Exception)
                {
                    // 个别私有格式读不出来（延迟渲染、进程已退出），跳过它，其余照样恢复
                }
            }
            return any ? copy : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"划词翻译：读取原剪贴板失败，复制后无法恢复：{ex.Message}");
            return null;
        }
    }

    private static async Task RestoreAsync(DataObject? saved, bool wasEmpty, uint afterCopy)
    {
        if (saved is null && !wasEmpty)
        {
            return; // 原来的内容没拿到，也就无从恢复
        }
        for (var attempt = 0; attempt < 5; attempt++)
        {
            // 这一会儿用户自己又复制了别的东西，就别拿旧内容盖掉它
            if (GetClipboardSequenceNumber() != afterCopy)
            {
                return;
            }
            try
            {
                if (saved is null)
                {
                    Clipboard.Clear();
                }
                else
                {
                    Clipboard.SetDataObject(saved, true);
                }
                return;
            }
            catch (Exception ex) when (ex is COMException or ExternalException)
            {
                await Task.Delay(40);
            }
        }
        Log.Warn("划词翻译：剪贴板一直被占用，原内容没能恢复");
    }

    /// <summary>等用户松开 Ctrl / Alt / Shift / Win，最多等一秒。</summary>
    private static async Task<bool> WaitForModifiersReleasedAsync()
    {
        var deadline = Environment.TickCount64 + 1000;
        while (true)
        {
            var held = false;
            foreach (var vk in new[] { VkControl, VkMenu, VkShift, VkLWin, VkRWin })
            {
                if ((GetAsyncKeyState(vk) & 0x8000) != 0)
                {
                    held = true;
                    break;
                }
            }
            if (!held)
            {
                return true;
            }
            if (Environment.TickCount64 > deadline)
            {
                return false;
            }
            await Task.Delay(15);
        }
    }

    private static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    private const byte VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12, VkLWin = 0x5B, VkRWin = 0x5C, VkC = 0x43;
    private const uint KeyEventKeyUp = 0x0002;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
