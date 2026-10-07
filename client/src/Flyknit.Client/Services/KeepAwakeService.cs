using System;
using System.Runtime.InteropServices;
using System.Threading;
using Flyknit.Core.Settings;

namespace Flyknit.Client.Services;

/// <summary>
/// 锁屏运行：按 <see cref="KeepAwake"/> 算出来的结果，用 SetThreadExecutionState 告诉 Windows 别让电脑睡、要不要让屏幕亮着。
///
/// 这是 Windows 给后台任务、视频播放器用的标准做法：不要管理员权限，不改系统电源设置，程序退出就失效；
/// IT 用 powercfg /requests 能看到是谁在挡着睡眠。这个状态是跟着线程走的，所以专门开一个后台线程一直持有，
/// 状态变了就在那个线程上重新设置。
/// </summary>
public sealed class KeepAwakeService : IDisposable
{
    [Flags]
    private enum ExecutionState : uint
    {
        SystemRequired = 0x00000001,
        DisplayRequired = 0x00000002,
        Continuous = 0x80000000,
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState flags);

    private readonly AutoResetEvent _changed = new(false);
    private readonly Thread _thread;
    private AwakeRequest _wanted = AwakeRequest.None;
    private volatile bool _stopping;

    /// <summary>当前生效的请求（界面、日志用）。</summary>
    public AwakeRequest Current { get; private set; } = AwakeRequest.None;

    public KeepAwakeService()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Flyknit keep-awake" };
        _thread.Start();
    }

    /// <summary>设置想要的状态；和现在一样就什么都不做。</summary>
    public void Set(AwakeRequest request)
    {
        if (request == _wanted)
        {
            return;
        }
        _wanted = request;
        _changed.Set();
    }

    private void Run()
    {
        while (true)
        {
            _changed.WaitOne();
            var request = _stopping ? AwakeRequest.None : _wanted;
            try
            {
                var flags = ExecutionState.Continuous;
                if (request.System) flags |= ExecutionState.SystemRequired;
                if (request.Display) flags |= ExecutionState.DisplayRequired;
                if (SetThreadExecutionState(flags) == 0)
                {
                    Log.Warn($"设置锁屏运行状态失败（错误码 {Marshal.GetLastWin32Error()}）");
                }
                else if (request != Current)
                {
                    Log.Info(request.System
                        ? $"锁屏运行：阻止电脑睡眠{(request.Display ? "，屏幕保持常亮" : "，屏幕可以熄灭")}"
                        : "锁屏运行：恢复按系统设置睡眠");
                    Current = request;
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return; // 不是 Windows（只在跑测试时）
            }
            if (_stopping)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _changed.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
        _changed.Dispose();
    }
}
