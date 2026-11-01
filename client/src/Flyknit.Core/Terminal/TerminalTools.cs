using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flyknit.Core.Security;
using Flyknit.Core.Tools;

namespace Flyknit.Core.Terminal;

/// <summary>看后台命令（run_shell background=true 启动的）的新输出。只能看本对话里的命令。</summary>
public sealed class ShellOutputTool : ITool
{
    /// <summary>一次最多给模型这么多字符（只给最后一段）。</summary>
    public const int MaxChars = 8000;

    public string Name => "shell_output";

    public string Description =>
        "查看后台命令的新输出（上次查看之后新增的部分）和是否还在运行。不传编号时列出这个对话里所有后台命令。" +
        "可以等几秒再看（比如刚改完代码，等开发服务器重新编译）。";

    public JsonObject Parameters => ToolArgs.Schema(
        ("id", "string", "run_shell 后台启动时返回的编号，比如 t3", false),
        ("wait_seconds", "integer", "先等几秒再读（命令结束会提前返回），默认 0，最大 30", false));

    public PolicyDecision Assess(JsonElement args, ToolContext ctx) => PolicyDecision.Auto("查看命令输出");

    public string Describe(JsonElement args) => args.Str("id") is { Length: > 0 } id ? $"查看命令 {id} 的输出" : "列出后台命令";

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        if (ctx.Terminal is not { } hub)
        {
            return ToolResult.Fail("这里没有终端");
        }
        var id = args.Str("id").Trim();
        if (id.Length == 0)
        {
            var list = hub.Runs(ctx.ConversationId).Where(r => r.Background).ToList();
            if (list.Count == 0)
            {
                return ToolResult.Success("这个对话没有后台命令");
            }
            var sb = new StringBuilder();
            foreach (var r in list)
            {
                sb.AppendLine($"{r.Id}  {State(r)}  {r.Command}");
            }
            return ToolResult.Success(sb.ToString().TrimEnd());
        }
        if (hub.Find(ctx.ConversationId, id) is not { } run)
        {
            return ToolResult.Fail($"这个对话里没有编号为 {id} 的命令");
        }
        var wait = Math.Clamp(args.Int("wait_seconds", 0), 0, 30);
        if (wait > 0 && run.Running)
        {
            await TerminalHub.WaitAsync(run, TimeSpan.FromSeconds(wait), ct);
        }
        var text = run.ReadNew(MaxChars, out var skipped);
        var output = new StringBuilder();
        output.AppendLine($"{run.Id}：{State(run)}");
        if (skipped > 0)
        {
            output.AppendLine($"…（省略了 {skipped} 个字符）…");
        }
        output.Append(text.Length > 0 ? text.TrimEnd() : "（没有新输出）");
        return ToolResult.Success(output.ToString());
    }

    internal static string State(TerminalRun r) =>
        r.Running ? "运行中" : r.Stopped ? "已停止" : $"已结束（退出码 {r.ExitCode}）";
}

/// <summary>停掉一个后台命令（连同它启动的子进程）。只能停本对话里的命令。</summary>
public sealed class ShellStopTool : ITool
{
    public string Name => "shell_stop";

    public string Description => "停掉一个后台命令（连同它启动的子进程）。调试完、或者要重启开发服务器前用。";

    public JsonObject Parameters => ToolArgs.Schema(("id", "string", "run_shell 后台启动时返回的编号", true));

    public PolicyDecision Assess(JsonElement args, ToolContext ctx) => PolicyDecision.Auto("停止自己启动的命令");

    public string Describe(JsonElement args) => $"停止命令 {args.Str("id")}";

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        if (ctx.Terminal is not { } hub)
        {
            return Task.FromResult(ToolResult.Fail("这里没有终端"));
        }
        var id = args.Required("id").Trim();
        if (hub.Find(ctx.ConversationId, id) is not { } run)
        {
            return Task.FromResult(ToolResult.Fail($"这个对话里没有编号为 {id} 的命令"));
        }
        return Task.FromResult(hub.Stop(run)
            ? ToolResult.Success($"已停止 {run.Id}")
            : ToolResult.Success($"{run.Id} 本来就不在运行（{ShellOutputTool.State(run)}）"));
    }
}

/// <summary>后台命令相关的工具：IT 打开「后台命令」时每一轮加上。</summary>
public static class TerminalToolSet
{
    public static readonly IReadOnlyList<ITool> All = new ITool[] { new ShellOutputTool(), new ShellStopTool() };
}
