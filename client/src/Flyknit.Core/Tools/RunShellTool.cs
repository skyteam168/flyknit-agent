using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flyknit.Core.Security;

namespace Flyknit.Core.Tools;

public sealed class RunShellTool : ITool
{
    public string Name => "run_shell";

    public string Description =>
        "在用户电脑上执行 PowerShell（默认）或 cmd 命令并返回输出。只读查询类命令会自动执行，其他命令需用户确认，" +
        "对系统有害的命令（如 rm -rf、format、修改系统注册表）会被直接阻止。优先使用专用工具处理文件。";

    public JsonObject Parameters => ToolArgs.Schema(
        ("command", "string", "要执行的命令", true),
        ("shell", "string", "powershell 或 cmd，默认 powershell", false),
        ("working_directory", "string", "工作目录，默认用户目录", false),
        ("timeout_seconds", "integer", "超时时间（秒），默认 60，最大 600", false));

    public PolicyDecision Assess(JsonElement args, ToolContext ctx)
    {
        var wd = args.Str("working_directory");
        return ctx.Policy.EvaluateCommand(args.Str("command"), wd.Length > 0 ? ctx.ResolvePath(wd) : ctx.WorkingDirectory);
    }

    public string Describe(JsonElement args) => $"执行命令：{args.Str("command")}";

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var command = args.Required("command");
        var shell = args.Str("shell", "powershell").ToLowerInvariant();
        var wd = args.Str("working_directory");
        var workingDirectory = wd.Length > 0 ? ctx.ResolvePath(wd) : ctx.WorkingDirectory;
        if (!Directory.Exists(workingDirectory))
        {
            return ToolResult.Fail($"工作目录不存在：{workingDirectory}");
        }
        var timeout = TimeSpan.FromSeconds(Math.Clamp(args.Int("timeout_seconds", 60), 1, 600));

        var psi = BuildStartInfo(shell, command);
        psi.WorkingDirectory = workingDirectory;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdout) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested)
            {
                throw;
            }
            return ToolResult.Fail($"命令执行超时（{timeout.TotalSeconds:0} 秒），已终止。\n{stdout}");
        }

        var output = new StringBuilder();
        output.AppendLine($"退出码：{process.ExitCode}");
        if (stdout.Length > 0) output.AppendLine(stdout.ToString().TrimEnd());
        if (stderr.Length > 0) output.AppendLine("[错误输出]").AppendLine(stderr.ToString().TrimEnd());
        return new ToolResult(process.ExitCode == 0, output.ToString().TrimEnd());
    }

    private static ProcessStartInfo BuildStartInfo(string shell, string command)
    {
        if (!OperatingSystem.IsWindows())
        {
            // 仅用于在非 Windows 环境下运行单元测试
            var sh = new ProcessStartInfo("/bin/sh");
            sh.ArgumentList.Add("-c");
            sh.ArgumentList.Add(command);
            return sh;
        }
        if (shell == "cmd")
        {
            var cmd = new ProcessStartInfo("cmd.exe");
            cmd.ArgumentList.Add("/d");
            cmd.ArgumentList.Add("/s");
            cmd.ArgumentList.Add("/c");
            cmd.ArgumentList.Add("chcp 65001 >nul & " + command);
            return cmd;
        }
        var ps = new ProcessStartInfo("powershell.exe");
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command" })
        {
            ps.ArgumentList.Add(a);
        }
        ps.ArgumentList.Add("[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; $ProgressPreference='SilentlyContinue'; " + command);
        return ps;
    }
}
