using System.Diagnostics;
using System.Text;

namespace Flyknit.Agent;

/// <summary>跑一个外部命令，收集输出和退出码。超时会强杀，避免任务卡死线程。</summary>
public static class ProcessRunner
{
    public sealed record Result(int ExitCode, string StdOut, string StdErr)
    {
        public string Combined => (StdOut + (StdErr.Length > 0 ? "\n" + StdErr : "")).Trim();
    }

    public static async Task<Result> RunAsync(
        string fileName, string arguments, CancellationToken ct, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var outBuf = new StringBuilder();
        var errBuf = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) outBuf.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) errBuf.AppendLine(e.Data); };

        AgentLog.Info($"执行命令：{fileName} {arguments}");
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t)
        {
            cts.CancelAfter(t);
        }
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(proc);
            var reason = ct.IsCancellationRequested ? "服务停止" : "执行超时";
            return new Result(-1, outBuf.ToString(), $"命令被中断（{reason}）");
        }
        return new Result(proc.ExitCode, outBuf.ToString(), errBuf.ToString());
    }

    private static void TryKill(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
        catch (Exception) { /* 已经退出了 */ }
    }
}
