using System;
using System.Runtime.InteropServices;

namespace Flyknit.Client.Services;

/// <summary>“用户现在能不能看到这个任务”的判断依据，便于排查为什么弹了（或没弹）通知。</summary>
public sealed record PresenceState(
    bool WindowVisible,
    bool Minimized,
    bool Cloaked,
    bool IsForeground,
    double CoveredRatio,
    TimeSpan IdleFor)
{
    public override string ToString() =>
        $"visible={WindowVisible} min={Minimized} cloaked={Cloaked} fg={IsForeground} covered={CoveredRatio:P0} idle={IdleFor.TotalSeconds:0}s";
}

/// <summary>
/// 判断用户此刻是否看得见主窗口。
///
/// 只靠 WPF 的 IsActive 不够：用户把鼠标点到别的窗口、或者焦点短暂跑到通知上，IsActive 就变 false，
/// 但窗口还好好地摆在眼前，这时候再弹系统通知就是打扰。所以这里按「看得见」而不是「有焦点」判断：
/// 窗口可见、没最小化、没被 DWM 隐藏（虚拟桌面切走会是 cloaked），并且没有被前台窗口盖住大半。
///
/// 另外加一条：用户离开座位（一段时间没有任何键鼠输入）时，哪怕窗口就在最前面也要通知，
/// 否则任务跑完没人知道。
/// </summary>
public static class UserPresence
{
    /// <summary>被前台窗口盖住这个比例以上，就当作用户看不见了。</summary>
    public const double CoverThreshold = 0.6;

    /// <summary>这么久没有键鼠输入就认为人不在座位上。</summary>
    public static TimeSpan AwayAfter { get; set; } = TimeSpan.FromMinutes(2);

    /// <param name="hwnd">主窗口句柄。</param>
    /// <param name="visible">WPF 层面的可见性（Hide() 之后为 false）。</param>
    /// <param name="minimized">是否最小化。</param>
    public static PresenceState Inspect(IntPtr hwnd, bool visible, bool minimized)
    {
        if (hwnd == IntPtr.Zero || !visible || minimized)
        {
            return new PresenceState(visible, minimized, false, false, 1, IdleTime());
        }

        var cloaked = IsCloaked(hwnd);
        var foreground = GetForegroundWindow();
        var isForeground = foreground == hwnd;
        var covered = isForeground || cloaked ? (cloaked ? 1 : 0) : CoveredRatio(hwnd, foreground);

        return new PresenceState(true, false, cloaked, isForeground, covered, IdleTime());
    }

    /// <summary>用户此刻看得见主窗口吗？</summary>
    public static bool CanSeeWindow(IntPtr hwnd, bool visible, bool minimized) =>
        CanSee(Inspect(hwnd, visible, minimized));

    public static bool CanSee(PresenceState s) =>
        s.WindowVisible
        && !s.Minimized
        && !s.Cloaked
        && s.CoveredRatio < CoverThreshold
        && s.IdleFor < AwayAfter;

    // ---------- Win32 ----------

    /// <summary>前台窗口盖住了我们多大比例。</summary>
    private static double CoveredRatio(IntPtr mine, IntPtr foreground)
    {
        if (foreground == IntPtr.Zero || foreground == mine)
        {
            return 0;
        }
        if (!GetWindowRect(mine, out var a) || !GetWindowRect(foreground, out var b))
        {
            return 0;
        }
        var myArea = (double)Math.Max(0, a.Right - a.Left) * Math.Max(0, a.Bottom - a.Top);
        if (myArea <= 0)
        {
            return 1;
        }
        // 前台窗口若是最小化或零尺寸（比如某些浮窗），不算遮挡
        var overlapW = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left));
        var overlapH = Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
        return overlapW * (double)overlapH / myArea;
    }

    /// <summary>窗口在别的虚拟桌面、或被 DWM 隐藏。</summary>
    private static bool IsCloaked(IntPtr hwnd)
    {
        try
        {
            return DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;
        }
        catch (Exception)
        {
            return false; // 取不到就当作没被隐藏，宁可少弹一次通知
        }
    }

    /// <summary>距离上一次键鼠输入过了多久（整个系统，不限本进程）。</summary>
    public static TimeSpan IdleTime()
    {
        try
        {
            var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
            if (!GetLastInputInfo(ref info))
            {
                return TimeSpan.Zero;
            }
            var idleMs = unchecked((uint)Environment.TickCount - info.dwTime);
            return TimeSpan.FromMilliseconds(idleMs);
        }
        catch (Exception)
        {
            return TimeSpan.Zero;
        }
    }

    private const int DwmwaCloaked = 14;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
