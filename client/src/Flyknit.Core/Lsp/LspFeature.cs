using System.Text;
using System.Text.Json.Nodes;
using Flyknit.Core.Hosting;
using Flyknit.Core.Workspace;

namespace Flyknit.Core.Lsp;

/// <summary>
/// 编码模式的语言服务（界面桥 lsp.*）：编辑器的补全、悬停、跳转、引用、重命名、报错波浪线都从这里问语言服务。
/// - 只在 IT 打开「开发者工具」时可用；只在员工信任了的项目里启动语言服务；
/// - 文件一律是这个对话工作区里的相对路径（解析后必须还在工作区里）；
/// - 界面只能转发白名单里的只读请求；改文件只有「重命名」一种（员工按 F2 确认后，宿主把改动写进工作区里的文本文件）。
/// </summary>
public sealed class LspFeature : IBridgeModule
{
    /// <summary>界面能转发的请求（都是只读的；rename 只是算出改动，不改文件）。</summary>
    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    {
        "textDocument/completion", "completionItem/resolve", "textDocument/hover", "textDocument/signatureHelp",
        "textDocument/definition", "textDocument/typeDefinition", "textDocument/implementation", "textDocument/references",
        "textDocument/documentSymbol", "textDocument/prepareRename", "textDocument/rename",
    };

    private readonly Func<string, string?> _workspaceOf;
    private readonly Func<bool> _enabled;
    private readonly Action<string> _log;

    public LspFeature(LspHub hub, Func<string, string?> workspaceOf, Func<bool> enabled, Action<string> log)
    {
        Hub = hub;
        _workspaceOf = workspaceOf;
        _enabled = enabled;
        _log = log;
    }

    public LspHub Hub { get; }

    public IReadOnlyCollection<string> Methods { get; } = new[] { "lsp.status", "lsp.trust", "lsp.sync", "lsp.close", "lsp.request", "lsp.diagnostics", "lsp.applyEdit" };

    public async Task<object?> HandleAsync(string method, BridgeArgs args, CancellationToken ct)
    {
        var conversation = args.Str("conversationId");
        var workspace = _workspaceOf(conversation) is { Length: > 0 } ws ? WorkspaceBrowser.Resolve(ws, "") : null;
        if (method == "lsp.status")
        {
            return Status(workspace);
        }
        if (!_enabled() || workspace is null)
        {
            return new { ok = false, error = _enabled() ? "noWorkspace" : "disabled" };
        }
        switch (method)
        {
            case "lsp.trust":
                Hub.Trust.Set(workspace, args.Bool("trust"));
                _log($"语言服务：{(args.Bool("trust") ? "信任" : "不再信任")}项目 {workspace}");
                return new { ok = true };
            case "lsp.applyEdit":
                return ApplyEdit(workspace, args.Raw.TryGetProperty("edits", out var e) ? JsonNode.Parse(e.GetRawText()) as JsonArray : null);
        }
        if (WorkspaceBrowser.Resolve(workspace, args.Str("path")) is not { } full || !File.Exists(full) && method != "lsp.close")
        {
            return new { ok = false, error = "badPath" };
        }
        var (client, error) = await Hub.ForFileAsync(workspace, full, ct).ConfigureAwait(false);
        if (client is null)
        {
            return new { ok = false, error };
        }
        try
        {
            switch (method)
            {
                case "lsp.sync":
                    await client.SyncAsync(full, args.Str("text"), ct).ConfigureAwait(false);
                    return new { ok = true };
                case "lsp.close":
                    await client.CloseAsync(full, ct).ConfigureAwait(false);
                    return new { ok = true };
                case "lsp.diagnostics":
                    var list = args.Bool("wait") ? await client.WaitDiagnosticsAsync(full, TimeSpan.FromSeconds(3), ct).ConfigureAwait(false) : client.DiagnosticsFor(full);
                    return new { ok = true, diagnostics = list };
                case "lsp.request":
                    return await RequestAsync(client, workspace, full, args, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonRpcException or TimeoutException)
        {
            return new { ok = false, error = ex.Message };
        }
        return null;
    }

    private object Status(string? workspace) => new
    {
        enabled = _enabled(),
        trusted = workspace is not null && Hub.Trust.IsTrusted(workspace),
        servers = Hub.Catalog.Availability().Select(a => new
        {
            id = a.Spec.Id,
            name = a.Spec.Name,
            extensions = a.Spec.Extensions,
            available = a.Command is not null,
            disabled = a.Disabled,
            exe = a.ResolvedExe,
            installHint = a.Spec.InstallHint,
            running = Hub.Running.Any(c => c.Spec.Id == a.Spec.Id),
        }).ToList(),
    };

    private async Task<object> RequestAsync(LspClient client, string workspace, string full, BridgeArgs args, CancellationToken ct)
    {
        var method = args.Str("method");
        if (!Allowed.Contains(method))
        {
            return new { ok = false, error = "notAllowed" };
        }
        // 先同步编辑器里现在的内容（界面带上了就用它，没带就读磁盘）
        if (args.Raw.TryGetProperty("text", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            await client.SyncAsync(full, t.GetString() ?? "", ct).ConfigureAwait(false);
        }
        else if (!client.IsOpen(full))
        {
            await client.SyncAsync(full, await File.ReadAllTextAsync(full, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        }
        var @params = args.Raw.TryGetProperty("params", out var p) ? JsonNode.Parse(p.GetRawText()) as JsonObject ?? new JsonObject() : new JsonObject();
        if (method != "completionItem/resolve")
        {
            @params["textDocument"] = new JsonObject { ["uri"] = LspPaths.ToUri(full) };
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var result = await client.RequestAsync(method, @params, timeout.Token).ConfigureAwait(false);
        return new { ok = true, result = AddPaths(result, workspace) };
    }

    /// <summary>
    /// 结果里的 file:// 地址旁边加上工作区里的相对路径（path / targetPath；不在工作区里为 null），界面靠它打开文件。
    /// WorkspaceEdit 的 changes（按地址分组）转成 changesByPath。
    /// </summary>
    public static JsonNode? AddPaths(JsonNode? node, string workspace)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (var item in array)
                {
                    AddPaths(item, workspace);
                }
                break;
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    if (key is "uri" or "targetUri" && obj[key]?.GetValueKind() == System.Text.Json.JsonValueKind.String)
                    {
                        obj[key == "uri" ? "path" : "targetPath"] = RelativePath(obj[key]!.GetValue<string>(), workspace);
                    }
                    else if (key == "changes" && obj[key] is JsonObject changes)
                    {
                        var byPath = new JsonArray();
                        foreach (var (uri, edits) in changes)
                        {
                            byPath.Add(new JsonObject { ["path"] = RelativePath(uri, workspace), ["edits"] = edits?.DeepClone() });
                        }
                        obj["changesByPath"] = byPath;
                    }
                    else
                    {
                        AddPaths(obj[key], workspace);
                    }
                }
                break;
        }
        return node;
    }

    public static string? RelativePath(string uri, string workspace)
    {
        if (LspPaths.FromUri(uri) is not { } path)
        {
            return null;
        }
        var rel = Path.GetRelativePath(workspace, Path.GetFullPath(path));
        return Path.IsPathRooted(rel) || rel == ".." || rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? null : rel;
    }

    /// <summary>
    /// 重命名在没打开的文件里的改动写进磁盘：只改这个工作区里的文本文件；任何一个文件不对整批不改。
    /// <paramref name="edits"/>：[{ path, edits: [{ range, newText }] }]。
    /// </summary>
    public object ApplyEdit(string workspace, JsonArray? edits)
    {
        if (edits is null || edits.Count == 0 || edits.Count > 500)
        {
            return new { ok = false, error = "badEdit", changed = Array.Empty<string>() };
        }
        var plan = new List<(string Full, string Rel, string Text)>();
        foreach (var file in edits)
        {
            var rel = file?["path"]?.GetValue<string>() ?? "";
            if (WorkspaceBrowser.Resolve(workspace, rel) is not { } full || !File.Exists(full) || Code.CodeFeature.LooksBinary(full)
                || !Code.CodeFeature.StaysInside(workspace, full))
            {
                return new { ok = false, error = $"badPath:{rel}", changed = Array.Empty<string>() };
            }
            var text = File.ReadAllText(full);
            try
            {
                plan.Add((full, rel, LspEdits.Apply(text, file!["edits"] as JsonArray ?? new JsonArray())));
            }
            catch (ArgumentException ex)
            {
                return new { ok = false, error = ex.Message, changed = Array.Empty<string>() };
            }
        }
        foreach (var (full, _, text) in plan)
        {
            File.WriteAllText(full, text, new UTF8Encoding(false));
        }
        _log($"语言服务重命名：{workspace}，改了 {plan.Count} 个文件");
        return new { ok = true, error = "", changed = plan.Select(x => x.Rel).ToList() };
    }
}

/// <summary>把 LSP 的 TextEdit（行、列按 UTF-16，从 0 开始）用到文本上。</summary>
public static class LspEdits
{
    public static string Apply(string text, JsonArray edits)
    {
        var lineStarts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                lineStarts.Add(i + 1);
            }
        }
        int Offset(JsonNode? pos)
        {
            var line = pos?["line"]?.GetValue<int>() ?? throw new ArgumentException("改动的位置不对");
            var ch = pos["character"]?.GetValue<int>() ?? 0;
            if (line < 0 || line >= lineStarts.Count)
            {
                return line < 0 ? throw new ArgumentException("改动的位置不对") : text.Length;
            }
            var end = line + 1 < lineStarts.Count ? lineStarts[line + 1] - 1 : text.Length;
            if (end > lineStarts[line] && end - 1 < text.Length && end > 0 && text[end - 1] == '\r')
            {
                end--;
            }
            return Math.Min(lineStarts[line] + Math.Max(0, ch), end);
        }
        var ordered = edits
            .Select(e => (Start: Offset(e?["range"]?["start"]), End: Offset(e?["range"]?["end"]), New: e?["newText"]?.GetValue<string>() ?? ""))
            .OrderByDescending(e => e.Start).ThenByDescending(e => e.End)
            .ToList();
        var sb = new StringBuilder(text);
        var last = int.MaxValue;
        foreach (var (start, end, replacement) in ordered)
        {
            if (end < start || end > last)
            {
                throw new ArgumentException("改动互相重叠");
            }
            sb.Remove(start, end - start).Insert(start, replacement);
            last = start;
        }
        return sb.ToString();
    }
}
