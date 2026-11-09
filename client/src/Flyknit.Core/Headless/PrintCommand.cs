using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flyknit.Core.Agent;
using Flyknit.Core.Chat;
using Flyknit.Core.Security;
using Flyknit.Core.Tools;

namespace Flyknit.Core.Headless;

/// <summary>flyknit 命令行解析后的参数。</summary>
public sealed class CliOptions
{
    /// <summary>chat（交互对话，默认）、print（-p）、acp、help、version。</summary>
    public string Command { get; init; } = "help";
    public string Prompt { get; init; } = "";

    /// <summary>prompt 从标准输入读（-p - 或者 -p 后面没写、管道里有内容）。</summary>
    public bool PromptFromStdin { get; init; }
    public string Workspace { get; init; } = "";

    /// <summary>readonly / workspace；为空用员工端设置里的默认权限。</summary>
    public string Permission { get; init; } = "";

    /// <summary>json（默认）/ text / stream-json。</summary>
    public string Format { get; init; } = "json";
    public bool AllowWrites { get; init; }

    /// <summary>
    /// 让模型先深度思考再动手（--think）。默认关，不跟员工端的「深度思考」走：命令行和 IDE 里一个任务常常要
    /// 调几十次工具，每一步都先想一大段，慢得多、也费 token；需要时单独打开。
    /// </summary>
    public bool Think { get; init; }

    /// <summary>交互模式打开以前的会话：continue（-c，这个工作区最近的一个）、list（--resume 不带 ID，列出来选）、会话 ID。</summary>
    public string? Resume { get; init; }

    /// <summary>超时秒数，0 表示不限。</summary>
    public int TimeoutSeconds { get; init; }

    /// <summary>
    /// JSON 里的非 ASCII 字符写成 \uXXXX。输出被管道 / 重定向时由程序打开：
    /// 中文 Windows 上 PowerShell 5 按 GBK 解码程序输出，UTF-8 的中文会变乱码；转义后的 JSON 只有 ASCII，按什么编码读都一样。
    /// </summary>
    public bool AsciiJson { get; init; }

    public const string Usage = """
        用法：
          flyknit                          在命令行里和 FlyknitBuddy 对话（输入 /help 看命令）
          flyknit "第一句话"               带着第一句话进入对话
          flyknit -c                       接着这个工作区最近的一次对话
          flyknit --resume [会话 ID]       列出以前的对话选一个接着聊（和 IDE 里的会话共用）
          flyknit -p "要做的事" [选项]     无界面执行一次办事任务，结果输出到标准输出
          flyknit -p - [选项]              要做的事从标准输入读
          flyknit acp [--think]            作为 ACP（Agent Client Protocol）服务运行，给 Zed、JetBrains 等开发工具用
          flyknit --version

        通用选项：
          -w, --workspace <目录>           工作区，默认当前目录
              --permission <档位>          readonly（只读）或 workspace（工作区内修改），默认按员工端设置
              --think                      让模型每一步先深度思考（更慢、更费 token）；默认关，不跟员工端的设置走

        只用于 -p：
              --format <格式>              json（默认）、text（只输出回答）、stream-json（每行一个事件）
              --allow-writes               放行员工端能设成「以后自动执行」的普通写入；删除、覆盖等高危操作任何时候都不放行
              --timeout <秒>               超时就停下，退出码 124

        对话里需要确认的操作在终端里问你；-p 没人可问，默认一律拒绝。

        退出码：0 完成；1 没做完；2 参数不对；3 没登录 / 登录失效；4 IT 没开「命令行 / IDE 接入」；
               5 今日额度用完；6 连不上公司服务器；124 超时；130 被中断

        用的是这台电脑上 FlyknitBuddy 的登录，权限、额度、审计和员工端一样。
        """;

    /// <summary>解析参数。出错时 Error 不为空。</summary>
    public static (CliOptions? Options, string? Error) Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return (new CliOptions { Command = "chat" }, null);
        }
        if (args[0] is "-h" or "--help" or "help" or "/?")
        {
            return (new CliOptions { Command = "help" }, null);
        }
        if (args[0] is "--version" or "-v" or "version")
        {
            return (new CliOptions { Command = "version" }, null);
        }
        if (args[0] == "acp")
        {
            var acpThink = false;
            foreach (var extra in args.Skip(1))
            {
                if (extra != "--think")
                {
                    return (null, $"acp 只认 --think：{extra}");
                }
                acpThink = true;
            }
            return (new CliOptions { Command = "acp", Think = acpThink }, null);
        }

        string? prompt = null;
        var fromStdin = false;
        var workspace = "";
        var permission = "";
        var format = "json";
        var allowWrites = false;
        var think = false;
        var timeout = 0;
        string? resume = null;
        var positional = new List<string>();
        var printOnly = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            string Next()
            {
                if (i + 1 >= args.Count)
                {
                    throw new ArgumentException($"{a} 后面要跟一个值");
                }
                return args[++i];
            }
            try
            {
                switch (a)
                {
                    case "-p" or "--print":
                        if (i + 1 < args.Count && (!args[i + 1].StartsWith('-') || args[i + 1] == "-"))
                        {
                            var value = args[++i];
                            if (value == "-")
                            {
                                fromStdin = true;
                            }
                            else
                            {
                                prompt = value;
                            }
                        }
                        else
                        {
                            fromStdin = true;
                        }
                        break;
                    case "-w" or "--workspace":
                        workspace = Next();
                        break;
                    case "--permission":
                        permission = Next().ToLowerInvariant();
                        if (permission is not ("readonly" or "workspace"))
                        {
                            return (null, $"--permission 只能是 readonly 或 workspace（命令行不能用完全权限），现在是 {permission}");
                        }
                        break;
                    case "--format" or "--output-format":
                        printOnly.Add(a);
                        format = Next().ToLowerInvariant();
                        if (format is not ("json" or "text" or "stream-json"))
                        {
                            return (null, $"--format 只能是 json、text 或 stream-json，现在是 {format}");
                        }
                        break;
                    case "--allow-writes":
                        printOnly.Add(a);
                        allowWrites = true;
                        break;
                    case "-c" or "--continue":
                        resume = "continue";
                        break;
                    case "--resume" or "-r":
                        resume = i + 1 < args.Count && !args[i + 1].StartsWith('-') ? args[++i] : "list";
                        break;
                    case "--think":
                        think = true;
                        break;
                    case "--timeout":
                        printOnly.Add(a);
                        if (!int.TryParse(Next(), out timeout) || timeout < 0)
                        {
                            return (null, "--timeout 要写秒数（非负整数）");
                        }
                        break;
                    default:
                        if (a.StartsWith('-'))
                        {
                            return (null, $"不认识的参数：{a}");
                        }
                        positional.Add(a);
                        break;
                }
            }
            catch (ArgumentException ex)
            {
                return (null, ex.Message);
            }
        }
        if (prompt is null && !fromStdin)
        {
            // 没有 -p：进入交互对话（flyknit "第一句话" 带着第一句话进去）
            if (printOnly.Count > 0)
            {
                return (null, $"{string.Join("、", printOnly.Distinct())} 只能和 -p 一起用");
            }
            return (new CliOptions
            {
                Command = "chat",
                Prompt = string.Join(" ", positional),
                Workspace = workspace,
                Permission = permission,
                Think = think,
                Resume = resume,
            }, null);
        }
        if (positional.Count > 0)
        {
            return (null, $"-p 之外不认识的参数：{positional[0]}（要做的事写在 -p 后面，有空格要加引号）");
        }
        if (resume is not null)
        {
            return (null, "-c / --resume 只用于对话模式，不能和 -p 一起用");
        }
        if (prompt is not null && prompt.Trim().Length == 0)
        {
            return (null, "-p 后面的内容是空的");
        }
        return (new CliOptions
        {
            Command = "print",
            Prompt = prompt ?? "",
            PromptFromStdin = fromStdin,
            Workspace = workspace,
            Permission = permission,
            Format = format,
            AllowWrites = allowWrites,
            Think = think,
            TimeoutSeconds = timeout,
        }, null);
    }
}

/// <summary>
/// flyknit -p：跑一轮办事任务，按 --format 输出。确认一律走 <see cref="NonInteractiveConfirm"/>（默认全拒）。
/// </summary>
public static class PrintCommand
{
    /// <summary>直接显示在命令行窗口：中文、越南文原样输出，人看得懂。</summary>
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>给程序读（管道 / 重定向）：非 ASCII 转义，和对方用什么编码读无关。</summary>
    private static readonly JsonSerializerOptions AsciiJson = new()
    {
        Encoder = JavaScriptEncoder.Default,
    };

    public static JsonSerializerOptions JsonFor(bool ascii, bool indent) =>
        new(ascii ? AsciiJson : Json) { WriteIndented = indent };

    public static PermissionMode ResolvePermission(string option, string profileDefault) =>
        (option.Length > 0 ? option : profileDefault) == "readonly" ? PermissionMode.ReadOnly : PermissionMode.Workspace;

    /// <param name="progress">
    /// 在终端里直接运行时（标准错误没有被重定向）传标准错误：边做边把进度（每一步操作、思考中）打出来，
    /// 不然要等整个任务做完才有输出，几分钟里屏幕上什么都没有，像卡住了。回答和 JSON 照常最后写到标准输出，
    /// 脚本、CI 拿到的内容不变。stream-json 本来就是边做边输出，不再另打进度。
    /// </param>
    public static async Task<int> RunAsync(CliOptions options, HeadlessEnvironment env, RecordingGateway? gateway, TextWriter stdout, PermissionMode permission, CancellationToken ct,
        TextWriter? progress = null, ConsoleStyle? style = null)
    {
        var workspace = options.Workspace.Length > 0 ? options.Workspace : Directory.GetCurrentDirectory();
        if (!Directory.Exists(workspace))
        {
            await WriteErrorAsync(stdout, options.Format, ExitCodes.Usage, $"工作区不存在：{workspace}", options.AsciiJson);
            return ExitCodes.Usage;
        }
        var session = new HeadlessSession(env, workspace, permission);
        var confirm = new NonInteractiveConfirm(options.AllowWrites);
        var observer = new PrintObserver(options.Format == "stream-json" ? stdout : null, JsonFor(options.AsciiJson, indent: false));
        var console = progress is not null && options.Format != "stream-json"
            ? new ConsoleObserver(progress, style ?? ConsoleStyle.Plain, showThinking: env.DeepThinking, showContent: false)
            : null;
        IAgentObserver watcher = console is null ? observer : new BothObservers(observer, console);
        var watch = Stopwatch.StartNew();
        if (console is not null)
        {
            progress!.Write((style ?? ConsoleStyle.Plain).Dim($"FlyknitBuddy · 工作区 {Path.GetFullPath(workspace)} · 需要确认的操作{(options.AllowWrites ? "只放行普通写入" : "一律拒绝")}") + "\n");
            console.ShowWaiting();
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (options.TimeoutSeconds > 0)
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        }
        AgentRunResult result;
        try
        {
            result = await session.PromptAsync(options.Prompt, confirm, watcher, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            console?.EndLine();
            var code = ct.IsCancellationRequested ? ExitCodes.Interrupted : ExitCodes.Timeout;
            await WriteErrorAsync(stdout, options.Format, code, code == ExitCodes.Timeout ? $"超过 {options.TimeoutSeconds} 秒，已停止" : "已中断", options.AsciiJson);
            return code;
        }

        var exit = result.StopReason switch
        {
            AgentStopReason.Completed => ExitCodes.Ok,
            // AgentLoop 被取消时自己收尾（给发出的调用补结果）后返回 Cancelled，不抛异常
            AgentStopReason.Cancelled => ct.IsCancellationRequested ? ExitCodes.Interrupted : ExitCodes.Timeout,
            AgentStopReason.Failed when gateway?.LastError is { } error => ExitCodes.ForGatewayError(error),
            _ => ExitCodes.NotCompleted,
        };
        if (console is not null)
        {
            console.EndLine();
            var look = style ?? ConsoleStyle.Plain;
            progress!.Write(look.Dim(InteractiveShell.Footer(result, false, watch.Elapsed)) + "\n");
            progress.Write(DeclinedHint(confirm.Declined, options.AllowWrites, look));
            progress.Flush();
        }
        var answer = result.NewMessages.LastOrDefault(m => m.Role == ChatRole.Assistant && m.Content.Length > 0)?.Content ?? "";
        var summary = new JsonObject
        {
            ["type"] = "result",
            ["ok"] = exit == ExitCodes.Ok,
            ["exit_code"] = exit,
            ["stop_reason"] = StopReasonName(result.StopReason),
            ["answer"] = answer,
            ["error"] = exit == ExitCodes.Ok ? null : ErrorText(result, gateway, exit, options.TimeoutSeconds),
            ["session_id"] = session.Id,
            ["workspace"] = session.Workspace,
            ["permission"] = permission == PermissionMode.ReadOnly ? "readonly" : "workspace",
            ["model"] = result.ModelName,
            ["steps"] = result.Steps,
            ["usage"] = result.Usage is { } u ? new JsonObject { ["prompt_tokens"] = u.PromptTokens, ["completion_tokens"] = u.CompletionTokens } : null,
            ["tool_calls"] = new JsonArray(observer.Calls.Select(c => (JsonNode)new JsonObject
            {
                ["name"] = c.Name,
                ["summary"] = c.Summary,
                ["decision"] = c.Decision,
                ["ok"] = c.Ok,
            }).ToArray()),
            ["declined"] = new JsonArray(confirm.Declined.Select(d => (JsonNode)new JsonObject
            {
                ["name"] = d.Tool,
                ["summary"] = d.Summary,
                ["reason"] = d.Reason,
            }).ToArray()),
            ["duration_ms"] = watch.ElapsedMilliseconds,
        };
        switch (options.Format)
        {
            case "text":
                await stdout.WriteLineAsync(answer);
                break;
            case "stream-json":
                await stdout.WriteLineAsync(summary.ToJsonString(JsonFor(options.AsciiJson, indent: false)));
                break;
            default:
                summary.Remove("type");
                await stdout.WriteLineAsync(summary.ToJsonString(JsonFor(options.AsciiJson, indent: true)));
                break;
        }
        await stdout.FlushAsync();
        return exit;
    }

    /// <summary>启动阶段就失败（没登录、开关没开、参数不对）时也按格式输出，脚本照样能解析。</summary>
    public static async Task WriteErrorAsync(TextWriter stdout, string format, int code, string error, bool asciiJson = false)
    {
        if (format == "text")
        {
            return; // text 格式错误只写 stderr（由调用方写）
        }
        var node = new JsonObject { ["ok"] = false, ["exit_code"] = code, ["error"] = error };
        if (format == "stream-json")
        {
            node["type"] = "result";
        }
        await stdout.WriteLineAsync(node.ToJsonString(JsonFor(asciiJson, indent: format == "json")));
        await stdout.FlushAsync();
    }

    public static string StopReasonName(AgentStopReason reason) => reason switch
    {
        AgentStopReason.Completed => "completed",
        AgentStopReason.MaxSteps => "max_steps",
        AgentStopReason.TooManyFailures => "too_many_failures",
        AgentStopReason.Cancelled => "cancelled",
        AgentStopReason.Stuck => "stuck",
        AgentStopReason.Failed => "failed",
        AgentStopReason.Budget => "budget",
        _ => reason.ToString().ToLowerInvariant(),
    };

    private static string ErrorText(AgentRunResult result, RecordingGateway? gateway, int exit, int timeoutSeconds) => exit switch
    {
        ExitCodes.QuotaExceeded => "今日额度已用完：" + gateway?.LastError?.Message,
        ExitCodes.NotSignedIn => "服务器不认这台电脑的登录，请打开 FlyknitBuddy 重新登录：" + gateway?.LastError?.Message,
        ExitCodes.Unreachable => "连不上公司服务器：" + gateway?.LastError?.Message,
        ExitCodes.Timeout => $"超过 {timeoutSeconds} 秒，已停止",
        ExitCodes.Interrupted => "已中断",
        _ => result.StopReason switch
        {
            AgentStopReason.Failed => "模型服务出错：" + (gateway?.LastError?.Message ?? ""),
            AgentStopReason.Stuck => "反复做同一个操作没有进展，已停下",
            AgentStopReason.Budget => "这次任务的 token 预算用完了，已停下",
            AgentStopReason.MaxSteps => "步数到上限，已停下",
            AgentStopReason.TooManyFailures => "连续失败太多次，已停下",
            _ => "任务没有完成",
        },
    };

    /// <summary>
    /// -p 在终端里跑完、有操作因为要确认被拒绝时，告诉员工为什么、怎么办（不然只能去 JSON 的 declined 里找）。
    /// 没有被拒绝的操作时返回空串。
    /// </summary>
    public static string DeclinedHint(IReadOnlyList<(string Tool, string Summary, string Reason)> declined, bool allowWrites, ConsoleStyle style)
    {
        if (declined.Count == 0)
        {
            return "";
        }
        var sb = new System.Text.StringBuilder();
        sb.Append(style.Yellow($"⚠ 有 {declined.Count} 个操作需要确认，-p 没有人可以问，已拒绝：")).Append('\n');
        foreach (var d in declined.Take(5))
        {
            sb.Append("    · ").Append(InteractiveShell.Clip(d.Summary.Length > 0 ? d.Summary : d.Tool, 100)).Append('\n');
        }
        if (declined.Count > 5)
        {
            sb.Append(style.Dim($"    …还有 {declined.Count - 5} 个")).Append('\n');
        }
        sb.Append(style.Yellow("  需要当场确认的话，去掉 -p 用对话模式：直接输入 flyknit，执行到这一步时会问你 [y] / [n]。")).Append('\n');
        if (!allowWrites && declined.Any(d => d.Reason.Contains("--allow-writes", StringComparison.Ordinal)))
        {
            sb.Append(style.Dim("  （放进脚本里自动跑的话，加 --allow-writes 可以放行新建、修改文件这类普通写入；运行脚本、访问网络、删除等仍然不放行）")).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>同时交给两个观察者：收集结果的和打进度的。</summary>
    private sealed class BothObservers : IAgentObserver
    {
        private readonly IAgentObserver _a;
        private readonly IAgentObserver _b;

        public BothObservers(IAgentObserver a, IAgentObserver b)
        {
            _a = a;
            _b = b;
        }

        public void OnContent(string delta) { _a.OnContent(delta); _b.OnContent(delta); }
        public void OnReasoning(string delta) { _a.OnReasoning(delta); _b.OnReasoning(delta); }
        public void OnAssistantMessage(ChatMessage message) { _a.OnAssistantMessage(message); _b.OnAssistantMessage(message); }
        public void OnToolStarted(ToolCall call, string summary, PolicyDecision decision) { _a.OnToolStarted(call, summary, decision); _b.OnToolStarted(call, summary, decision); }
        public void OnToolFinished(ToolCall call, ToolResult result, string decision) { _a.OnToolFinished(call, result, decision); _b.OnToolFinished(call, result, decision); }
        public void OnToolMessage(ChatMessage message) { _a.OnToolMessage(message); _b.OnToolMessage(message); }
        public void OnPlanUpdated(IReadOnlyList<PlanItem> plan) { _a.OnPlanUpdated(plan); _b.OnPlanUpdated(plan); }
        public void OnContextCompacted(Context.CompactionInfo info) { _a.OnContextCompacted(info); _b.OnContextCompacted(info); }
        public void OnNotice(string text) { _a.OnNotice(text); _b.OnNotice(text); }
    }

    /// <summary>收集工具调用；stream-json 时每个事件写一行。</summary>
    private sealed class PrintObserver : IAgentObserver
    {
        private readonly TextWriter? _stream;
        private readonly JsonSerializerOptions _json;
        private readonly Dictionary<string, string> _summaries = new();

        public PrintObserver(TextWriter? stream, JsonSerializerOptions json)
        {
            _stream = stream;
            _json = json;
        }

        public List<(string Name, string Summary, string Decision, bool Ok)> Calls { get; } = new();

        private void Emit(JsonObject node)
        {
            if (_stream is null)
            {
                return;
            }
            lock (_stream)
            {
                _stream.WriteLine(node.ToJsonString(_json));
                _stream.Flush();
            }
        }

        public void OnContent(string delta) => Emit(new JsonObject { ["type"] = "text", ["text"] = delta });
        public void OnReasoning(string delta) => Emit(new JsonObject { ["type"] = "thinking", ["text"] = delta });
        public void OnAssistantMessage(ChatMessage message)
        {
        }

        public void OnToolStarted(ToolCall call, string summary, PolicyDecision decision)
        {
            lock (_summaries)
            {
                _summaries[call.Id] = summary;
            }
            Emit(new JsonObject { ["type"] = "tool_started", ["id"] = call.Id, ["name"] = call.Name, ["summary"] = summary });
        }

        public void OnToolFinished(ToolCall call, ToolResult result, string decision)
        {
            string summary;
            lock (_summaries)
            {
                summary = _summaries.TryGetValue(call.Id, out var s) ? s : call.Name;
                Calls.Add((call.Name, summary, decision, result.Ok));
            }
            Emit(new JsonObject { ["type"] = "tool_finished", ["id"] = call.Id, ["name"] = call.Name, ["decision"] = decision, ["ok"] = result.Ok });
        }

        public void OnToolMessage(ChatMessage message)
        {
        }

        public void OnPlanUpdated(IReadOnlyList<PlanItem> plan) =>
            Emit(new JsonObject
            {
                ["type"] = "plan",
                ["steps"] = new JsonArray(plan.Select(p => (JsonNode)new JsonObject { ["step"] = p.Step, ["status"] = p.Status }).ToArray()),
            });

        public void OnNotice(string text) => Emit(new JsonObject { ["type"] = "notice", ["text"] = text });
    }
}
