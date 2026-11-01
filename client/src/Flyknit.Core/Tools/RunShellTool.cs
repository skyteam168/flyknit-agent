using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flyknit.Core.Security;
using Flyknit.Core.Terminal;

namespace Flyknit.Core.Tools;

public sealed class RunShellTool : ITool
{
    public string Name => "run_shell";

    public string Description =>
        "在用户电脑上执行 PowerShell（默认）或 cmd 命令并返回输出，默认在工作区目录下执行。只读查询类命令会自动执行，" +
        "其他命令按用户选择的权限处理；对系统有害的命令（如 rm -rf、format、修改系统注册表）会被直接阻止。优先使用专用工具处理文件。" +
        "命令和输出会实时显示在员工界面右侧的「终端」里。";

    public JsonObject Parameters => ToolArgs.Schema(
        ("command", "string", "要执行的命令", true),
        ("shell", "string", "powershell 或 cmd，默认 powershell", false),
        ("working_directory", "string", "工作目录，默认为工作区", false),
        ("timeout_seconds", "integer", "超时时间（秒），默认 60，最大 600", false),
        ("background", "boolean", "true 表示在后台启动、不等它结束（开发服务器、监听编译这类一直运行的程序），返回编号；之后用 shell_output 看输出、shell_stop 停止。IT 开启「后台命令」才能用", false),
        ("wait_seconds", "integer", "后台启动后先等几秒再返回开头的输出，默认 5，最大 30", false));

    public PolicyDecision Assess(JsonElement args, ToolContext ctx)
    {
        var wd = args.Str("working_directory");
        string dir;
        try
        {
            dir = wd.Length > 0 ? ctx.ResolvePath(wd) : ctx.WorkingDirectory;
        }
        catch (Exception)
        {
            return PolicyDecision.Blocked("工作目录无效");
        }
        var shell = args.Str("shell", "powershell").Equals("cmd", StringComparison.OrdinalIgnoreCase) ? "cmd" : "powershell";
        var command = args.Str("command");
        // 系统级工具先判：改注册表、建服务和计划任务，是在工作区之外留下长期生效的东西，
        // 权限模式和工作区范围都管不着它
        if (SystemToolPolicy.Evaluate(command, ctx.SystemTools) is { } systemTool)
        {
            return systemTool;
        }
        var decision = PermissionRules.ForCommand(ctx.Policy, ctx.EffectivePermission, ctx.Workspace, command, dir, shell);
        return ctx.NetworkAllowlist
            ? PermissionRules.ForNetwork(decision, ctx.Policy.Network.EvaluateCommand(command))
            : decision;
    }

    public string Describe(JsonElement args) => $"执行命令：{args.Str("command")}";

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var command = args.Required("command");
        var shell = args.Str("shell", "powershell").ToLowerInvariant() == "cmd" ? "cmd" : "powershell";
        var wd = args.Str("working_directory");
        var workingDirectory = wd.Length > 0 ? ctx.ResolvePath(wd) : ctx.WorkingDirectory;
        if (!Directory.Exists(workingDirectory))
        {
            return ToolResult.Fail($"工作目录不存在：{workingDirectory}");
        }
        var background = args.Bool("background", false);
        if (background && (ctx.Terminal is null || !ctx.BackgroundCommands))
        {
            return ToolResult.Fail("后台命令没有开启（IT 在安全中心的「后台命令」关着）。请改成普通命令执行（会等它结束），不要启动一直运行的程序。");
        }
        // 没有界面（单测、专家子任务）时用一个只给这条命令用的终端
        var hub = ctx.Terminal ?? new TerminalHub();
        var psi = BuildStartInfo(shell, command);
        psi.WorkingDirectory = workingDirectory;

        if (background)
        {
            return await StartBackgroundAsync(hub, psi, command, shell, args, ctx, ct);
        }

        var timeout = TimeSpan.FromSeconds(Math.Clamp(args.Int("timeout_seconds", 60), 1, 600));

        // 命令跑的可能是任意程序，产出什么事先不知道，所以前后各扫一次工作目录比对出来
        var tracker = OutputTracker.Snapshot(workingDirectory);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var run = hub.Start(ctx.ConversationId, psi, command, shell, background: false, (line, error) =>
        {
            var sb = error ? stderr : stdout;
            lock (sb) sb.AppendLine(line);
        });

        bool finished;
        try
        {
            finished = await TerminalHub.WaitAsync(run, timeout, ct);
        }
        catch (OperationCanceledException)
        {
            hub.Stop(run);
            throw;
        }
        if (!finished)
        {
            hub.Stop(run);
            return ToolResult.Fail($"命令执行超时（{timeout.TotalSeconds:0} 秒），已终止。" +
                (ctx.BackgroundCommands && ctx.Terminal is not null ? "一直运行的程序（开发服务器、监听编译）请用 background=true 在后台启动。" : "") +
                $"\n{stdout}");
        }

        var output = new StringBuilder();
        output.AppendLine($"退出码：{run.ExitCode}");
        if (stdout.Length > 0) output.AppendLine(stdout.ToString().TrimEnd());
        if (stderr.Length > 0) output.AppendLine("[错误输出]").AppendLine(stderr.ToString().TrimEnd());
        var produced = tracker?.Changed() ?? Array.Empty<OutputFile>();
        // 命令改了哪些文件：新建的能还原（移到回收站）；改动的事先没备份，只能列出来
        foreach (var f in produced)
        {
            ctx.NoteChange(new FileChange(f.Path, tracker!.Existed(f.Path) ? FileChange.Modified : FileChange.Created, null, Name));
        }
        return new ToolResult(run.ExitCode == 0, output.ToString().TrimEnd())
        {
            Outputs = produced.Select(f => f.Path).ToList(),
        };
    }

    /// <summary>后台启动：等几秒看看开头的输出（启动报错一般这时候就出来了），然后交回去，命令接着跑。</summary>
    private static async Task<ToolResult> StartBackgroundAsync(TerminalHub hub, ProcessStartInfo psi, string command, string shell,
        JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        TerminalRun run;
        try
        {
            run = hub.Start(ctx.ConversationId, psi, command, shell, background: true);
        }
        catch (InvalidOperationException ex)
        {
            return ToolResult.Fail(ex.Message);
        }
        var wait = TimeSpan.FromSeconds(Math.Clamp(args.Int("wait_seconds", 5), 0, 30));
        // 这里取消（员工停了任务）不停后台命令：它本来就该接着跑，员工可以在终端里停
        var exited = await TerminalHub.WaitAsync(run, wait, ct);
        var text = run.ReadNew(ShellOutputTool.MaxChars, out var skipped);
        var sb = new StringBuilder();
        if (exited)
        {
            sb.AppendLine($"命令 {run.Id} 已经结束（退出码：{run.ExitCode?.ToString() ?? "被停止"}），没有留在后台。");
        }
        else
        {
            sb.AppendLine($"已在后台启动，编号 {run.Id}，还在运行。用 shell_output 查看后续输出，用完用 shell_stop 停掉。");
        }
        if (skipped > 0)
        {
            sb.AppendLine($"…（前面省略 {skipped} 个字符）…");
        }
        sb.Append(text.Length > 0 ? text.TrimEnd() : "（暂时还没有输出）");
        return new ToolResult(!exited || run.ExitCode == 0, sb.ToString());
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
