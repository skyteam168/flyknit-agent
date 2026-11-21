using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Flyknit.Core.Terminal;

/// <summary>
/// Windows 伪控制台（ConPTY，Windows 10 1809+）：起一个真正的交互式 shell（PowerShell），
/// 输出是带颜色、光标控制的 VT 序列（界面用 xterm.js 画），输入原样写回去。和 VS Code、Windows Terminal 用的是同一套系统接口。
///
/// 这是员工自己的终端：员工在里面敲的命令和自己打开 PowerShell 一样，不经过 AI，也不进 AI 的上下文。
/// 只在开发者工具开着时可用（UserTerminalFeature 判断）。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PseudoConsole : IDisposable
{
    private readonly IntPtr _console;
    private readonly SafeFileHandle _input;
    private readonly FileStream _inputStream;
    private readonly FileStream _outputStream;
    private readonly Process? _process;
    private readonly IntPtr _processHandle;
    private readonly IntPtr _threadHandle;
    private readonly object _gate = new();
    private bool _disposed;

    public int ProcessId { get; }

    private PseudoConsole(IntPtr console, SafeFileHandle input, SafeFileHandle output, PROCESS_INFORMATION pi)
    {
        _console = console;
        _input = input;
        _inputStream = new FileStream(input, FileAccess.Write, 1, false);
        _outputStream = new FileStream(output, FileAccess.Read, 4096, false);
        _processHandle = pi.hProcess;
        _threadHandle = pi.hThread;
        ProcessId = pi.dwProcessId;
        try
        {
            _process = Process.GetProcessById(pi.dwProcessId);
        }
        catch (ArgumentException)
        {
            _process = null; // 已经退出了
        }
    }

    /// <summary>在伪控制台里启动一个程序。<paramref name="commandLine"/> 例如 <c>powershell.exe -NoLogo</c>。</summary>
    public static PseudoConsole Start(string commandLine, string workingDirectory, short cols, short rows)
    {
        if (!CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0) || !CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "创建终端管道失败");
        }
        var hr = CreatePseudoConsole(new COORD { X = cols, Y = rows }, inputRead, outputWrite, 0, out var console);
        // 伪控制台那一端已经交给系统了，自己这边关掉（不关的话进程退出后读输出会一直等）
        inputRead.Dispose();
        outputWrite.Dispose();
        if (hr != 0)
        {
            inputWrite.Dispose();
            outputRead.Dispose();
            throw new Win32Exception(hr, "创建伪控制台失败（需要 Windows 10 1809 或更新）");
        }

        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        var attributes = Marshal.AllocHGlobal(size);
        try
        {
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)
                || !UpdateProcThreadAttribute(attributes, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, console, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "设置伪控制台属性失败");
            }
            var startup = new STARTUPINFOEX { lpAttributeList = attributes };
            startup.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            // 明确告诉子进程：标准输入输出不用继承的（全是空），一律走伪控制台。
            // 不设的话，本程序的标准输出被重定向时（从脚本、计划任务启动，或者测试里），子进程会把输出写到那边去，
            // 终端里只剩一个空白画面（Windows CI 上就是这样：只看到启动画面，提示符和命令输出都没了）
            startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
            if (!CreateProcessW(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false,
                    EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT, IntPtr.Zero,
                    Directory.Exists(workingDirectory) ? workingDirectory : null, ref startup, out var pi))
            {
                var error = Marshal.GetLastWin32Error();
                ClosePseudoConsole(console);
                inputWrite.Dispose();
                outputRead.Dispose();
                throw new Win32Exception(error, "启动终端程序失败");
            }
            return new PseudoConsole(console, inputWrite, outputRead, pi);
        }
        finally
        {
            DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes);
        }
    }

    /// <summary>读输出（VT 序列的 UTF-8 字节）。返回 0 表示程序退出、伪控制台关了。</summary>
    public Task<int> ReadAsync(byte[] buffer, CancellationToken ct) => _outputStream.ReadAsync(buffer, 0, buffer.Length, ct);

    public void Write(string text)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            var bytes = Encoding.UTF8.GetBytes(text);
            _inputStream.Write(bytes, 0, bytes.Length);
            _inputStream.Flush();
        }
    }

    public void Resize(short cols, short rows)
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                ResizePseudoConsole(_console, new COORD { X = cols, Y = rows });
            }
        }
    }

    /// <summary>
    /// 等程序退出，返回退出码（拿不到时返回 -1）。
    /// 用 CreateProcess 给的进程句柄等、读退出码：按进程号另找的 Process 对象在命令很快结束时可能拿不到
    /// （GetProcessById 时已经退出，或者读 ExitCode 抛异常），退出码就成了 -1（Windows CI 上真碰到过）。
    /// </summary>
    public async Task<int> WaitForExitAsync(CancellationToken ct)
    {
        // 复制一份句柄自己拿着：等的时候员工关了终端（Dispose 关掉原句柄）也不受影响
        SafeWaitHandle own;
        lock (_gate)
        {
            if (_disposed)
            {
                return -1;
            }
            var self = GetCurrentProcess();
            if (!DuplicateHandle(self, _processHandle, self, out var dup, 0, false, DUPLICATE_SAME_ACCESS))
            {
                return -1;
            }
            own = new SafeWaitHandle(dup, ownsHandle: true);
        }
        using var handle = new ProcessHandleWait(own);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ThreadPool.RegisterWaitForSingleObject(handle, (_, _) => exited.TrySetResult(), null, Timeout.Infinite, executeOnlyOnce: true);
        try
        {
            await exited.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            registration.Unregister(null);
        }
        return GetExitCodeProcess(own.DangerousGetHandle(), out var code) && code != STILL_ACTIVE ? (int)code : -1;
    }

    private sealed class ProcessHandleWait : WaitHandle
    {
        public ProcessHandleWait(SafeWaitHandle handle) => SafeWaitHandle = handle;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }
        // 先关伪控制台（会让里面的程序收到关闭信号），输出管道随之结束；最后才关读写流
        ClosePseudoConsole(_console);
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // 已经退出了
        }
        _inputStream.Dispose();
        _input.Dispose();
        _outputStream.Dispose();
        _process?.Dispose();
        CloseHandle(_threadHandle);
        CloseHandle(_processHandle);
    }

    // ---------- Win32 ----------

    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const int STARTF_USESTDHANDLES = 0x00000100;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? lpApplicationName, StringBuilder lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint STILL_ACTIVE = 259;
    private const uint DUPLICATE_SAME_ACCESS = 0x00000002;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(IntPtr hSourceProcessHandle, IntPtr hSourceHandle, IntPtr hTargetProcessHandle,
        out IntPtr lpTargetHandle, uint dwDesiredAccess, bool bInheritHandle, uint dwOptions);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);
}
