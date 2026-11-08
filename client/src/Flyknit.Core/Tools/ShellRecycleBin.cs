using System.Runtime.InteropServices;

namespace Flyknit.Core.Tools;

/// <summary>
/// 删除时移入回收站（Windows 外壳 SHFileOperation）。员工端用 VisualBasic 的实现（要 WPF 运行时）；
/// 命令行 flyknit.exe 不带 WPF，用这一份。安全中心「删除进回收站」开着时命令行和员工端行为一致。
/// 非 Windows 没有回收站：直接拒绝删除，而不是悄悄变成永久删除。
/// </summary>
public sealed class ShellRecycleBin : IFileDeleter
{
    public void Delete(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new IOException("这台电脑没有回收站，按安全设置不做永久删除");
        }
        var full = Path.GetFullPath(path);
        if (!File.Exists(full) && !Directory.Exists(full))
        {
            throw new FileNotFoundException("要删除的文件不存在", full);
        }
        var op = new ShFileOpStruct
        {
            wFunc = FO_DELETE,
            // 路径列表以两个 \0 结尾
            pFrom = full + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        var code = SHFileOperationW(ref op);
        if (code != 0 || op.fAnyOperationsAborted)
        {
            throw new IOException($"移入回收站失败（错误 0x{code:X}）：{full}");
        }
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref ShFileOpStruct fileOp);
}
