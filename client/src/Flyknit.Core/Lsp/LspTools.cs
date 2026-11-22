using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flyknit.Core.Agent;
using Flyknit.Core.Agent.Extensions;
using Flyknit.Core.Security;
using Flyknit.Core.Storage;
using Flyknit.Core.Tools;
using Flyknit.Core.Workspace;

namespace Flyknit.Core.Lsp;

/// <summary>
/// 给 AI 用的语言服务工具（参照 Claude Code / Cursor 让 AI 直接用 LSP）：都是只读的，可以同时调、不用确认。
/// - code_diagnostics：编译器 / 类型检查报的错和警告（改完代码马上查，自己修掉）；
/// - go_to_definition / find_references：准确的定义和引用（不用 grep 猜，找调用链又快又省 token）；
/// - document_symbols：一个文件里的类、函数、方法大纲。
/// 只在这个对话的工作区里、员工信任了的项目、装了对应语言服务时可用；行号、列号从 1 开始（和编辑器一样）。
/// </summary>
public abstract class LspToolBase : ITool, IParallelTool
{
    protected LspToolBase(LspHub hub) => Hub = hub;

    protected LspHub Hub { get; }

    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract JsonObject Parameters { get; }
    public bool CanRunInParallel(JsonElement args) => true;
    public PolicyDecision Assess(JsonElement args, ToolContext ctx) => PolicyDecision.Auto("语言服务查询（只读）");
    public abstract string Describe(JsonElement args);

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var workspace = ctx.Workspace is { Length: > 0 } ws ? WorkspaceBrowser.Resolve(ws, "") : null;
        if (workspace is null)
        {
            return ToolResult.Fail("这个任务没有工作区，用不了语言服务");
        }
        var raw = args.Str("path");
        var full = Path.IsPathRooted(raw) ? Path.GetFullPath(raw) : WorkspaceBrowser.Resolve(workspace, raw);
        if (full is null || LspFeature.RelativePath(LspPaths.ToUri(full), workspace) is null || !File.Exists(full))
        {
            return ToolResult.Fail($"文件不在工作区里或不存在：{raw}");
        }
        var (client, error) = await Hub.ForFileAsync(workspace, full, ct).ConfigureAwait(false);
        if (client is null)
        {
            return ToolResult.Fail(error switch
            {
                "unsupported" => "这种文件没有语言服务（支持 TS/JS/React、Vue、Python、Go、Java、C#），用 grep / read_file 代替",
                "untrusted" => "员工还没信任这个项目，语言服务没启动。用 grep / read_file 代替（员工在编码模式里点「信任」后就能用）",
                "missing" => "这台电脑没装这种语言的语言服务（由 IT 安装），用 grep / read_file 代替",
                "disabled" => "IT 关闭了这种语言的语言服务，用 grep / read_file 代替",
                _ => error + "。用 grep / read_file 代替",
            });
        }
        try
        {
            // 用磁盘上的内容（AI 刚写进去的就是最新的）
            await client.SyncAsync(full, await File.ReadAllTextAsync(full, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            return await RunAsync(client, workspace, full, args, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or JsonRpcException or TimeoutException)
        {
            return ToolResult.Fail("语言服务出错：" + ex.Message);
        }
    }

    protected abstract Task<ToolResult> RunAsync(LspClient client, string workspace, string full, JsonElement args, CancellationToken ct);

    /// <summary>位置：行号（从 1 开始）+ 列号（从 1 开始）；没给列号时在这一行里找 symbol 第一次出现的位置。</summary>
    protected static (int Line, int Character)? Position(string full, JsonElement args, out string error)
    {
        error = "";
        var line = args.Int("line", 0);
        var lines = File.ReadAllLines(full);
        if (line < 1 || line > lines.Length)
        {
            error = $"行号不对：{line}（文件共 {lines.Length} 行，从 1 开始）";
            return null;
        }
        var column = args.Int("column", 0);
        var symbol = args.Str("symbol");
        if (column < 1 && symbol.Length > 0)
        {
            var i = lines[line - 1].IndexOf(symbol, StringComparison.Ordinal);
            if (i < 0)
            {
                error = $"第 {line} 行里没有「{symbol}」：{lines[line - 1].Trim()}";
                return null;
            }
            column = i + 1;
        }
        if (column < 1)
        {
            error = "要给列号（column，从 1 开始），或者给 symbol（这一行里的名字）";
            return null;
        }
        return (line - 1, column - 1);
    }

    protected static async Task<JsonNode?> RequestAt(LspClient client, string method, string full, (int Line, int Character) pos, JsonObject? extra, CancellationToken ct)
    {
        var p = extra ?? new JsonObject();
        p["textDocument"] = new JsonObject { ["uri"] = LspPaths.ToUri(full) };
        p["position"] = new JsonObject { ["line"] = pos.Line, ["character"] = pos.Character };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        return await client.RequestAsync(method, p, timeout.Token).ConfigureAwait(false);
    }

    /// <summary>Location / LocationLink 的列表 → 「相对路径:行:列  这一行的代码」。</summary>
    protected static string FormatLocations(JsonNode? result, string workspace, int max, out int count)
    {
        var items = result switch
        {
            JsonArray a => a.Where(x => x is not null).Cast<JsonNode>().ToList(),
            JsonObject o => new List<JsonNode> { o },
            _ => new List<JsonNode>(),
        };
        count = items.Count;
        var sb = new StringBuilder();
        var cache = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.Take(max))
        {
            var uri = item["targetUri"]?.GetValue<string>() ?? item["uri"]?.GetValue<string>() ?? "";
            var range = item["targetSelectionRange"] ?? item["targetRange"] ?? item["range"];
            var line = range?["start"]?["line"]?.GetValue<int>() ?? 0;
            var ch = range?["start"]?["character"]?.GetValue<int>() ?? 0;
            var path = LspPaths.FromUri(uri);
            var rel = LspFeature.RelativePath(uri, workspace) ?? path ?? uri;
            var code = "";
            if (path is not null && File.Exists(path))
            {
                if (!cache.TryGetValue(path, out var lines))
                {
                    try
                    {
                        lines = new FileInfo(path).Length < 2_000_000 ? File.ReadAllLines(path) : Array.Empty<string>();
                    }
                    catch (IOException)
                    {
                        lines = Array.Empty<string>();
                    }
                    cache[path] = lines;
                }
                code = line < lines.Length ? lines[line].Trim() : "";
                if (code.Length > 160)
                {
                    code = code[..160] + "…";
                }
            }
            sb.AppendLine($"{rel.Replace('\\', '/')}:{line + 1}:{ch + 1}  {code}");
        }
        if (count > max)
        {
            sb.AppendLine($"…还有 {count - max} 处没列出");
        }
        return sb.ToString().TrimEnd();
    }

    protected static readonly JsonObject PositionSchema = ToolArgs.Schema(
        ("path", "string", "文件路径（工作区里的相对路径）", true),
        ("line", "integer", "行号，从 1 开始", true),
        ("column", "integer", "列号，从 1 开始；不知道就不填，改填 symbol", false),
        ("symbol", "string", "这一行里的名字（函数名、变量名、类名），用来定位列号", false));
}

public sealed class CodeDiagnosticsTool : LspToolBase
{
    public CodeDiagnosticsTool(LspHub hub) : base(hub)
    {
    }

    public override string Name => "code_diagnostics";
    public override string Description => """
        用语言服务（编译器 / 类型检查）查一个文件里的错误和警告，和编辑器里的红色波浪线一样。
        改完代码后马上用它检查，有错就修掉再交付；比跑整个编译快。支持 TS/JS/React、Vue、Python、Go、Java、C#。只读。
        """;
    public override JsonObject Parameters => ToolArgs.Schema(("path", "string", "文件路径（工作区里的相对路径）", true));
    public override string Describe(JsonElement args) => "检查代码错误：" + args.Str("path");

    protected override async Task<ToolResult> RunAsync(LspClient client, string workspace, string full, JsonElement args, CancellationToken ct)
    {
        var list = await client.WaitDiagnosticsAsync(full, TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
        var rel = (LspFeature.RelativePath(LspPaths.ToUri(full), workspace) ?? full).Replace('\\', '/');
        if (list.Count == 0)
        {
            return ToolResult.Success($"{rel}：语言服务没有报错误或警告。");
        }
        var sb = new StringBuilder();
        var errors = list.Count(d => (d?["severity"]?.GetValue<int>() ?? 1) == 1);
        sb.AppendLine($"{rel}：{errors} 个错误，{list.Count - errors} 个警告 / 提示");
        foreach (var d in list.Take(80))
        {
            var start = d?["range"]?["start"];
            var sev = (d?["severity"]?.GetValue<int>() ?? 1) switch { 1 => "错误", 2 => "警告", 3 => "信息", _ => "提示" };
            var code = d?["code"]?.ToString() is { Length: > 0 } c ? $"（{d?["source"]?.GetValue<string>() ?? ""} {c}）".Replace("（ ", "（") : "";
            sb.AppendLine($"{(start?["line"]?.GetValue<int>() ?? 0) + 1}:{(start?["character"]?.GetValue<int>() ?? 0) + 1} {sev} {d?["message"]?.GetValue<string>()?.Replace('\n', ' ')}{code}");
        }
        if (list.Count > 80)
        {
            sb.AppendLine($"…还有 {list.Count - 80} 条没列出");
        }
        return ToolResult.Success(sb.ToString().TrimEnd());
    }
}

public sealed class GoToDefinitionTool : LspToolBase
{
    public GoToDefinitionTool(LspHub hub) : base(hub)
    {
    }

    public override string Name => "go_to_definition";
    public override string Description => "用语言服务找一个名字（函数、类、变量、类型）是在哪定义的，比 grep 准。给文件、行号和列号（或这一行里的名字）。只读。";
    public override JsonObject Parameters => PositionSchema;
    public override string Describe(JsonElement args) => $"找定义：{args.Str("symbol", args.Str("path"))}";

    protected override async Task<ToolResult> RunAsync(LspClient client, string workspace, string full, JsonElement args, CancellationToken ct)
    {
        if (Position(full, args, out var error) is not { } pos)
        {
            return ToolResult.Fail(error);
        }
        var text = FormatLocations(await RequestAt(client, "textDocument/definition", full, pos, null, ct).ConfigureAwait(false), workspace, 20, out var count);
        return ToolResult.Success(count == 0 ? "语言服务没找到定义（可能是内置的、动态生成的，或者位置不在名字上）" : text);
    }
}

public sealed class FindReferencesTool : LspToolBase
{
    public FindReferencesTool(LspHub hub) : base(hub)
    {
    }

    public override string Name => "find_references";
    public override string Description => "用语言服务找一个名字在整个项目里被用到的所有地方（改函数签名、重命名、查影响面之前用），比 grep 准（不会把同名的别的东西算进来）。只读。";
    public override JsonObject Parameters => PositionSchema;
    public override string Describe(JsonElement args) => $"找引用：{args.Str("symbol", args.Str("path"))}";

    protected override async Task<ToolResult> RunAsync(LspClient client, string workspace, string full, JsonElement args, CancellationToken ct)
    {
        if (Position(full, args, out var error) is not { } pos)
        {
            return ToolResult.Fail(error);
        }
        var result = await RequestAt(client, "textDocument/references", full, pos, new JsonObject { ["context"] = new JsonObject { ["includeDeclaration"] = true } }, ct).ConfigureAwait(false);
        var text = FormatLocations(result, workspace, 100, out var count);
        return ToolResult.Success(count == 0 ? "语言服务没找到引用" : $"共 {count} 处（含定义）：\n{text}");
    }
}

public sealed class DocumentSymbolsTool : LspToolBase
{
    public DocumentSymbolsTool(LspHub hub) : base(hub)
    {
    }

    public override string Name => "document_symbols";
    public override string Description => "用语言服务列出一个文件里的类、函数、方法、变量（带行号），先看大纲再决定读哪一段，省得读整个大文件。只读。";
    public override JsonObject Parameters => ToolArgs.Schema(("path", "string", "文件路径（工作区里的相对路径）", true));
    public override string Describe(JsonElement args) => "看文件大纲：" + args.Str("path");

    private static readonly string[] Kinds =
    {
        "", "文件", "模块", "命名空间", "包", "类", "方法", "属性", "字段", "构造函数", "枚举", "接口", "函数", "变量", "常量", "字符串",
        "数字", "布尔", "数组", "对象", "键", "null", "枚举成员", "结构体", "事件", "运算符", "类型参数",
    };

    protected override async Task<ToolResult> RunAsync(LspClient client, string workspace, string full, JsonElement args, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var result = await client.RequestAsync("textDocument/documentSymbol", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = LspPaths.ToUri(full) } }, timeout.Token).ConfigureAwait(false);
        var sb = new StringBuilder();
        var n = 0;
        void Walk(JsonNode? node, int depth)
        {
            if (node is not JsonArray list)
            {
                return;
            }
            foreach (var s in list)
            {
                if (s is null || ++n > 400)
                {
                    return;
                }
                var kind = s["kind"]?.GetValue<int>() ?? 0;
                var start = (s["selectionRange"] ?? s["range"] ?? s["location"]?["range"])?["start"]?["line"]?.GetValue<int>() ?? 0;
                sb.AppendLine($"{new string(' ', depth * 2)}{(kind > 0 && kind < Kinds.Length ? Kinds[kind] : "符号")} {s["name"]?.GetValue<string>()}{(s["detail"]?.GetValue<string>() is { Length: > 0 } d ? "  " + d : "")}  （第 {start + 1} 行）");
                Walk(s["children"], depth + 1);
            }
        }
        Walk(result, 0);
        return ToolResult.Success(n == 0 ? "语言服务没列出符号" : sb.ToString().TrimEnd());
    }
}

/// <summary>
/// 运行扩展：IT 开了「开发者工具」、这一轮是办事、工作区在员工信任了的项目里、这台电脑装了至少一种语言服务时，给 AI 加上语言服务工具。
/// </summary>
public sealed class LspRunExtension : IRunExtension<RunScope>
{
    public const string ExtensionId = "lsp_tools";
    private readonly LspHub _hub;
    private readonly Func<bool> _enabled;

    public LspRunExtension(LspHub hub, Func<bool> enabled)
    {
        _hub = hub;
        _enabled = enabled;
    }

    public string Id => ExtensionId;

    public ValueTask<RunContribution?> PrepareAsync(RunScope scope, CancellationToken ct)
    {
        var conv = scope.Conversation;
        if (!_enabled() || conv.Mode != ConversationMode.Agent || conv.Workspace is not { Length: > 0 } ws
            || WorkspaceBrowser.Resolve(ws, "") is not { } workspace || !_hub.Trust.IsTrusted(workspace)
            || !_hub.Catalog.Availability().Any(a => a.Command is not null))
        {
            return ValueTask.FromResult<RunContribution?>(null);
        }
        return ValueTask.FromResult<RunContribution?>(new RunContribution
        {
            Tools = new ITool[] { new CodeDiagnosticsTool(_hub), new GoToDefinitionTool(_hub), new FindReferencesTool(_hub), new DocumentSymbolsTool(_hub) },
        });
    }
}
