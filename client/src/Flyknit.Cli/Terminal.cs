using System.Runtime.InteropServices;

namespace Flyknit.Cli;

/// <summary>终端能力。</summary>
internal static class Terminal
{
    private const int StdOutputHandle = -11;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr handle, out uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr handle, uint mode);

    /// <summary>
    /// 打开颜色（ANSI 转义）。Windows Terminal 本来就支持；老的控制台窗口要打开「虚拟终端处理」才认，
    /// 打不开（太老的 Windows）就不用颜色，原样输出文字。
    /// </summary>
    public static bool EnableColors()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }
        try
        {
            var handle = GetStdHandle(StdOutputHandle);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1) || !GetConsoleMode(handle, out var mode))
            {
                return false;
            }
            return (mode & EnableVirtualTerminalProcessing) != 0 || SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>终端有多宽（列）；拿不到（重定向等）返回 0。</summary>
    public static int Width()
    {
        try
        {
            return Console.IsOutputRedirected ? 0 : Console.WindowWidth;
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
        {
            return 0;
        }
    }
}
