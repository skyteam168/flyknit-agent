using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Flyknit.Core.Security;

namespace Flyknit.Core.Tools;

/// <summary>
/// 开发者工具（IT 在安全中心打开「开发者工具」后，办事模式才有）：按内容搜代码、一次改多处、结构化的 git。
/// 和 run_shell 跑 git 相比：参数按列表传给 git（不经过 Shell，没有拼接注入），输出固定格式、好读；
/// 安全上不另开口子——提交照样按命令规则判定（git_commit 和 run_shell 里的 git commit 走同一套 PermissionRules）。
/// </summary>
public static class DeveloperToolSet
{
    public static readonly IReadOnlyList<ITool> All = new ITool[]
    {
        new GrepTool(),
        new MultiEditTool(),
        new GitStatusTool(),
        new GitDiffTool(),
        new GitLogTool(),
        new GitCommitTool(),
    };

    public static bool Contains(string name) => All.Any(t => t.Name == name);

    /// <summary>
    /// 这一轮能用的开发者工具：只有办事模式、IT 打开了「开发者工具」（默认关）时才有，再按 allowed（专家的 disallowed_tools）减掉。
    /// 关着时返回空：工具、提示词、场景都和没有这个功能时一样。
    /// </summary>
    public static IReadOnlyList<ITool> For(SecuritySettings? security, bool agentMode, Func<ITool, bool>? allowed = null) =>
        agentMode && (security?.On(SecuritySettings.DeveloperTools, fallback: false) ?? false)
            ? All.Where(t => allowed is null || allowed(t)).ToList()
            : Array.Empty<ITool>();
}

// ---------------------------------------------------------------- grep

public sealed class GrepTool : ITool, IParallelTool
{
    public const int MaxFileBytes = 2 * 1024 * 1024;
    public const int MaxFilesScanned = 20000;
    private const int MaxLineChars = 300;

    public string Name => "grep";

    public string Description =>
        "按内容搜索代码或文本文件（正则表达式，同 ripgrep）。返回「文件:行号: 内容」，可带上下文行。" +
        "可以用 glob 限定文件（如 *.cs、src/**/*.ts）。会跳过 .git、node_modules、bin、obj、dist 等目录和二进制文件。" +
        "找函数定义、调用点、配置项时优先用它，不要用 run_shell 跑 findstr / Select-String。";

    public JsonObject Parameters
    {
        get
        {
            var schema = ToolArgs.Schema(
                ("pattern", "string", "正则表达式；literal=true 时按普通文字找", true),
                ("path", "string", "搜索的目录或单个文件，默认工作区", false),
                ("glob", "string", "只搜这些文件，如 *.py、src/**/*.{ts,vue}", false),
                ("ignore_case", "boolean", "不分大小写，默认 false", false),
                ("literal", "boolean", "pattern 按普通文字处理（不当正则），默认 false", false),
                ("context", "integer", "每处匹配前后各带几行，0-5，默认 0", false),
                ("output", "string", "content（默认，匹配的行）/ files（只列文件）/ count（每个文件几处）", false),
                ("max_results", "integer", "最多返回多少处匹配（files 模式是文件数），默认 100，最多 500", false));
            schema["properties"]!["output"]!["enum"] = new JsonArray("content", "files", "count");
            return schema;
        }
    }

    public bool CanRunInParallel(JsonElement args) => true;

    public PolicyDecision Assess(JsonElement args, ToolContext ctx) => PolicyDecision.Auto();

    public string Describe(JsonElement args) =>
        $"搜索「{args.Str("pattern")}」" + (args.Str("glob").Length > 0 ? $"（{args.Str("glob")}）" : "") + (args.Str("path").Length > 0 ? $" 于 {args.Str("path")}" : "");

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct) => Task.Run(() => Run(args, ctx, ct), ct);

    private static ToolResult Run(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var raw = args.Required("pattern");
        var pathArg = args.Str("path");
        var root = pathArg.Length > 0 ? ctx.ResolvePath(pathArg) : ctx.WorkingDirectory;
        var singleFile = File.Exists(root);
        if (!singleFile && !Directory.Exists(root))
        {
            return ToolResult.Fail($"路径不存在：{root}");
        }
        Regex regex;
        try
        {
            var options = RegexOptions.CultureInvariant | (args.Bool("ignore_case", false) ? RegexOptions.IgnoreCase : RegexOptions.None);
            regex = new Regex(args.Bool("literal", false) ? Regex.Escape(raw) : raw, options, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException ex)
        {
            return ToolResult.Fail($"正则表达式写错了：{ex.Message}。要按普通文字找就加 literal=true");
        }
        var globText = args.Str("glob");
        var glob = globText.Length > 0 ? new Glob(globText) : null;
        var context = Math.Clamp(args.Int("context", 0), 0, 5);
        var mode = args.Str("output", "content").ToLowerInvariant();
        var max = Math.Clamp(args.Int("max_results", 100), 1, 500);

        IEnumerable<string> files = singleFile
            ? new[] { Path.GetFileName(root) }
            : SourceTree.Files(root, ct: ct).Where(f => glob is null || glob.IsMatch(f));
        var baseDir = singleFile ? Path.GetDirectoryName(root)! : root;

        var sb = new StringBuilder();
        var hits = 0;
        var fileHits = 0;
        var scanned = 0;
        var truncated = false;
        var timedOut = 0;
        foreach (var rel in files)
        {
            ct.ThrowIfCancellationRequested();
            if (++scanned > MaxFilesScanned)
            {
                truncated = true;
                break;
            }
            var full = Path.Combine(baseDir, rel);
            string[] lines;
            try
            {
                var info = new FileInfo(full);
                if (info.Length > MaxFileBytes || SourceTree.LooksBinary(full))
                {
                    continue;
                }
                lines = File.ReadAllLines(full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            var matched = new List<int>();
            for (var i = 0; i < lines.Length; i++)
            {
                try
                {
                    if (regex.IsMatch(lines[i]))
                    {
                        matched.Add(i);
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    timedOut++;
                    break;
                }
            }
            if (matched.Count == 0)
            {
                continue;
            }
            fileHits++;
            var display = rel.Replace('\\', '/');
            switch (mode)
            {
                case "files":
                    sb.AppendLine(display);
                    hits++;
                    break;
                case "count":
                    sb.AppendLine($"{display}: {matched.Count}");
                    hits++;
                    break;
                default:
                    AppendMatches(sb, display, lines, matched, context, max - hits, out var used);
                    hits += used;
                    break;
            }
            if (hits >= max)
            {
                truncated = true;
                break;
            }
        }

        if (fileHits == 0)
        {
            var hint = scanned > MaxFilesScanned ? "（文件太多只扫了前 " + MaxFilesScanned + " 个，缩小 path 或加 glob 再试）" : "";
            return ToolResult.Success($"没有找到「{raw}」{hint}");
        }
        var header = mode switch
        {
            "files" => $"{fileHits} 个文件包含「{raw}」",
            "count" => $"{fileHits} 个文件包含「{raw}」",
            _ => $"找到 {hits} 处（{fileHits} 个文件）",
        };
        if (truncated)
        {
            header += "，已达上限，结果不完整：缩小 path、加 glob 或换更精确的 pattern";
        }
        if (timedOut > 0)
        {
            header += $"；{timedOut} 个文件里正则太慢被跳过";
        }
        return ToolResult.Success(header + "\n" + sb.ToString().TrimEnd());
    }

    // ripgrep 的格式：匹配行「文件:行号: 内容」，上下文行「文件-行号- 内容」，不连续的段之间用 --
    private static void AppendMatches(StringBuilder sb, string file, string[] lines, List<int> matched, int context, int budget, out int used)
    {
        used = 0;
        var set = matched.ToHashSet();
        var last = -2;
        foreach (var m in matched)
        {
            if (used >= budget)
            {
                break;
            }
            var from = Math.Max(0, m - context);
            var to = Math.Min(lines.Length - 1, m + context);
            if (context > 0 && last >= 0 && from > last + 1)
            {
                sb.AppendLine("--");
            }
            for (var i = Math.Max(from, last + 1); i <= to; i++)
            {
                var text = lines[i].Length > MaxLineChars ? lines[i][..MaxLineChars] + "…" : lines[i];
                var sep = set.Contains(i) ? ':' : '-';
                sb.Append(file).Append(sep).Append(i + 1).Append(sep).Append(' ').AppendLine(text);
            }
            last = to;
            used++;
        }
    }
}

// ---------------------------------------------------------------- multi_edit

/// <summary>同一个文件一次改多处：按顺序在内存里依次替换，有一处对不上就一处都不改（不会改到一半）。</summary>
public sealed class MultiEditTool : ITool
{
    public const int MaxEdits = 50;

    public string Name => "multi_edit";

    public string Description =>
        "一次修改同一个文件里的多处：edits 按顺序执行，每一项和 edit_file 一样把 old_text 原样换成 new_text。" +
        "后一项看到的是前一项改完的内容。任何一项找不到或不唯一，整个文件一处都不改。改代码时比多次 edit_file 省步骤。";

    public JsonObject Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "文件路径" },
            ["edits"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = $"要做的修改，按顺序执行，最多 {MaxEdits} 项",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["old_text"] = new JsonObject { ["type"] = "string", ["description"] = "要替换的原文（与文件内容完全一致）" },
                        ["new_text"] = new JsonObject { ["type"] = "string", ["description"] = "替换后的内容；为空表示删掉这段" },
                        ["replace_all"] = new JsonObject { ["type"] = "boolean", ["description"] = "替换所有出现的地方，默认只允许出现一次" },
                    },
                    ["required"] = new JsonArray("old_text", "new_text"),
                },
            },
        },
        ["required"] = new JsonArray("path", "edits"),
    };

    public PolicyDecision Assess(JsonElement args, ToolContext ctx) => ctx.AssessWrite(args.Str("path"));

    public string Describe(JsonElement args)
    {
        var n = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("edits", out var e) && e.ValueKind == JsonValueKind.Array ? e.GetArrayLength() : 0;
        return $"修改文件 {args.Str("path")}（{n} 处）";
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var path = ctx.ResolvePath(args.Required("path"));
        if (!File.Exists(path))
        {
            return ToolResult.Fail($"文件不存在：{path}。新建文件请用 write_file");
        }
        if (!args.TryGetProperty("edits", out var edits) || edits.ValueKind != JsonValueKind.Array || edits.GetArrayLength() == 0)
        {
            return ToolResult.Fail("edits 要是一个非空数组：[{\"old_text\": \"…\", \"new_text\": \"…\"}]");
        }
        if (edits.GetArrayLength() > MaxEdits)
        {
            return ToolResult.Fail($"一次最多 {MaxEdits} 处，分几次改");
        }

        string content;
        using (var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            content = await reader.ReadToEndAsync(ct);
        }
        var updated = content;
        var total = 0;
        var k = 0;
        foreach (var edit in edits.EnumerateArray())
        {
            k++;
            var oldText = edit.Str("old_text");
            var newText = edit.Str("new_text");
            if (oldText.Length == 0)
            {
                return ToolResult.Fail($"第 {k} 项的 old_text 是空的，文件没有改动");
            }
            if (oldText == newText)
            {
                return ToolResult.Fail($"第 {k} 项的 old_text 和 new_text 一样，文件没有改动");
            }
            var (find, replace) = EditFileTool.Match(updated, oldText, newText);
            var count = Occurrences(updated, find);
            if (count == 0)
            {
                return ToolResult.Fail($"第 {k} 项在文件里找不到（前面 {k - 1} 项改完之后的内容里）。文件没有改动；先 read_file 看一下当前内容");
            }
            var all = edit.Bool("replace_all", false);
            if (count > 1 && !all)
            {
                return ToolResult.Fail($"第 {k} 项在文件里出现了 {count} 次，不知道改哪一处。文件没有改动；多带些前后文，或 replace_all=true");
            }
            updated = all ? updated.Replace(find, replace, StringComparison.Ordinal) : ReplaceOnce(updated, find, replace);
            total += all ? count : 1;
        }

        var saved = ctx.Backup?.Capture(path);
        ctx.NoteChange(new FileChange(path, FileChange.Modified, saved, Name));
        await File.WriteAllTextAsync(path, updated, new UTF8Encoding(false), ct);
        var note = saved is null ? "" : "（原文件已备份）";
        return ToolResult.Success($"已修改 {path}：{k} 项共替换 {total} 处{note}").WithOutputs(path);
    }

    private static int Occurrences(string content, string find)
    {
        var n = 0;
        for (var i = content.IndexOf(find, StringComparison.Ordinal); i >= 0; i = content.IndexOf(find, i + find.Length, StringComparison.Ordinal))
        {
            n++;
        }
        return n;
    }

    private static string ReplaceOnce(string content, string find, string replace)
    {
        var i = content.IndexOf(find, StringComparison.Ordinal);
        return content[..i] + replace + content[(i + find.Length)..];
    }
}

// ---------------------------------------------------------------- git

/// <summary>按参数列表启动 git（不经过 Shell），不弹密码框、不进分页器、中文文件名不转义。</summary>
public static class GitRunner
{
    public static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

    public sealed record Result(int ExitCode, string Stdout, string Stderr);

    public static async Task<Result> RunAsync(string workingDirectory, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "--no-pager", "-c", "core.quotepath=false", "-c", "color.ui=never", "-c", "i18n.logOutputEncoding=UTF-8" })
        {
            psi.ArgumentList.Add(a);
        }
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0"; // 要密码时直接失败，不卡住等输入
        psi.Environment["GIT_PAGER"] = "cat";
        psi.Environment["LC_ALL"] = "C.UTF-8";

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("git 没有启动");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new GitMissingException();
        }
        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
                if (ct.IsCancellationRequested)
                {
                    throw;
                }
                return new Result(-1, "", $"git 超过 {Timeout.TotalSeconds:0} 秒没有结束，已停止");
            }
            return new Result(process.ExitCode, await stdout, await stderr);
        }
    }
}

public sealed class GitMissingException : Exception
{
    public GitMissingException() : base("这台电脑没有安装 git（或不在 PATH 里），请联系 IT 安装 Git for Windows") { }
}

/// <summary>git 工具的公共部分：找仓库目录、挡住以 - 开头的参数（防止 --output= 这类写文件的选项混进来）。</summary>
public abstract class GitToolBase : ITool
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract JsonObject Parameters { get; }
    public abstract PolicyDecision Assess(JsonElement args, ToolContext ctx);
    public abstract string Describe(JsonElement args);

    protected static string RepoDir(JsonElement args, ToolContext ctx)
    {
        var raw = args.Str("path");
        return raw.Length > 0 ? ctx.ResolvePath(raw) : ctx.WorkingDirectory;
    }

    /// <summary>引用、文件这类值不能以 - 开头：否则 target="--output=C:/x" 就成了让 git 写文件的选项。</summary>
    protected static string? Unsafe(params string[] values) =>
        values.FirstOrDefault(v => v.Length > 0 && (v.StartsWith('-') || v.Contains('\0') || v.Contains('\n')));

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var dir = RepoDir(args, ctx);
        if (!Directory.Exists(dir))
        {
            return ToolResult.Fail($"目录不存在：{dir}");
        }
        try
        {
            var inside = await GitRunner.RunAsync(dir, new[] { "rev-parse", "--is-inside-work-tree" }, ct);
            if (inside.ExitCode != 0 || !inside.Stdout.Trim().Equals("true", StringComparison.Ordinal))
            {
                return ToolResult.Fail($"{dir} 不在 git 仓库里");
            }
            return await RunAsync(args, ctx, dir, ct);
        }
        catch (GitMissingException ex)
        {
            return ToolResult.Fail(ex.Message);
        }
    }

    protected abstract Task<ToolResult> RunAsync(JsonElement args, ToolContext ctx, string dir, CancellationToken ct);

    protected static ToolResult FromResult(GitRunner.Result r, string empty)
    {
        if (r.ExitCode != 0)
        {
            var err = (r.Stderr.Trim().Length > 0 ? r.Stderr : r.Stdout).Trim();
            return ToolResult.Fail($"git 出错（退出码 {r.ExitCode}）：{err}");
        }
        var text = r.Stdout.TrimEnd();
        return ToolResult.Success(text.Length == 0 ? empty : text);
    }
}

public sealed class GitStatusTool : GitToolBase, IParallelTool
{
    public override string Name => "git_status";
    public override string Description => "查看 git 仓库状态：当前分支、和远端差几个提交、哪些文件改了 / 暂存了 / 没跟踪。只读。";
    public override JsonObject Parameters => ToolArgs.Schema(("path", "string", "仓库里的任意目录，默认工作区", false));
    public bool CanRunInParallel(JsonElement args) => true;
    public override PolicyDecision Assess(JsonElement args, ToolContext ctx) => PolicyDecision.Auto();
    public override string Describe(JsonElement args) => "查看 git 状态";

    protected override async Task<ToolResult> RunAsync(JsonElement args, ToolContext ctx, string dir, CancellationToken ct)
    {
        var r = await GitRunner.RunAsync(dir, new[] { "status", "--porcelain=v1", "--branch", "--untracked-files=normal" }, ct);
        var result = FromResult(r, "");
        if (!result.Ok)
        {
            return result;
        }
        var lines = r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var changes = lines.Length - (lines.Length > 0 && lines[0].StartsWith("##", StringComparison.Ordinal) ? 1 : 0);
        var legend = changes == 0
            ? "（工作区干净，没有未提交的修改）"
            : "（每行前两位：第一位是暂存区，第二位是工作区；M 修改、A 新增、D 删除、R 改名、?? 未跟踪）";
        return ToolResult.Success(r.Stdout.TrimEnd() + "\n" + legend);
    }
}

public sealed class GitDiffTool : GitToolBase, IParallelTool
{
    public override string Name => "git_diff";
    public override string Description =>
        "查看改动内容（统一 diff 格式）。默认是工作区里还没暂存的修改；staged=true 看已暂存的；" +
        "target 可以写提交或范围（HEAD~1、main、main...HEAD）。stat=true 只看每个文件改了几行。只读。";
    public override JsonObject Parameters => ToolArgs.Schema(
        ("path", "string", "仓库里的任意目录，默认工作区", false),
        ("staged", "boolean", "看已暂存（git add 过）的改动，默认 false", false),
        ("target", "string", "对比的提交或范围，如 HEAD~1、main...HEAD；不填就是工作区", false),
        ("file", "string", "只看这个文件或目录（相对仓库）", false),
        ("stat", "boolean", "只列每个文件的增删行数，默认 false", false));
    public bool CanRunInParallel(JsonElement args) => true;
    public override PolicyDecision Assess(JsonElement args, ToolContext ctx) => PolicyDecision.Auto();
    public override string Describe(JsonElement args) =>
        "查看改动" + (args.Str("target").Length > 0 ? $"（{args.Str("target")}）" : args.Bool("staged", false) ? "（已暂存）" : "") + (args.Str("file").Length > 0 ? $"：{args.Str("file")}" : "");

    protected override async Task<ToolResult> RunAsync(JsonElement args, ToolContext ctx, string dir, CancellationToken ct)
    {
        var target = args.Str("target").Trim();
        var file = args.Str("file").Trim();
        if (Unsafe(target, file) is { } bad)
        {
            return ToolResult.Fail($"参数不能以 - 开头或包含换行：{bad}");
        }
        var list = new List<string> { "diff" };
        if (args.Bool("staged", false))
        {
            list.Add("--staged");
        }
        if (args.Bool("stat", false))
        {
            list.Add("--stat");
        }
        if (target.Length > 0)
        {
            list.Add(target);
        }
        list.Add("--");
        if (file.Length > 0)
        {
            list.Add(file);
        }
        return FromResult(await GitRunner.RunAsync(dir, list, ct), "没有改动");
    }
}

public sealed class GitLogTool : GitToolBase, IParallelTool
{
    public override string Name => "git_log";
    public override string Description => "查看提交历史（短哈希、日期、作者、说明）。可以只看某个文件或某个分支 / 范围。只读。";
    public override JsonObject Parameters => ToolArgs.Schema(
        ("path", "string", "仓库里的任意目录，默认工作区", false),
        ("max_count", "integer", "最多几条，默认 20，最多 200", false),
        ("target", "string", "分支、提交或范围，如 main、v1.0..HEAD；默认当前分支", false),
        ("file", "string", "只看改过这个文件或目录的提交", false));
    public bool CanRunInParallel(JsonElement args) => true;
    public override PolicyDecision Assess(JsonElement args, ToolContext ctx) => PolicyDecision.Auto();
    public override string Describe(JsonElement args) => "查看提交历史" + (args.Str("file").Length > 0 ? $"：{args.Str("file")}" : "");

    protected override async Task<ToolResult> RunAsync(JsonElement args, ToolContext ctx, string dir, CancellationToken ct)
    {
        var target = args.Str("target").Trim();
        var file = args.Str("file").Trim();
        if (Unsafe(target, file) is { } bad)
        {
            return ToolResult.Fail($"参数不能以 - 开头或包含换行：{bad}");
        }
        var list = new List<string>
        {
            "log", $"--max-count={Math.Clamp(args.Int("max_count", 20), 1, 200)}", "--date=short", "--pretty=format:%h %ad %an  %s",
        };
        if (target.Length > 0)
        {
            list.Add(target);
        }
        list.Add("--");
        if (file.Length > 0)
        {
            list.Add(file);
        }
        return FromResult(await GitRunner.RunAsync(dir, list, ct), "还没有提交");
    }
}

/// <summary>
/// 提交。判定和 run_shell 里的「git add + git commit」完全一样（同一套 PermissionRules：拦截规则、权限模式、工作区、记住的规则），
/// 不会因为换了个工具名就松一点。不推送——推送用 run_shell 的 git push，那里 push --force 按高危确认。
/// </summary>
public sealed class GitCommitTool : GitToolBase
{
    public override string Name => "git_commit";
    public override string Description =>
        "提交改动：先把 files 里的文件（或 all=true 时全部改动，包括新文件）加入暂存区，再用 message 提交。" +
        "不填 files 也不开 all 时只提交已经暂存的。提交前先用 git_status / git_diff 确认要提交的内容。不会推送到远端。";
    public override JsonObject Parameters
    {
        get
        {
            var schema = ToolArgs.Schema(
                ("message", "string", "提交说明（说清楚改了什么、为什么）", true),
                ("path", "string", "仓库里的任意目录，默认工作区", false),
                ("all", "boolean", "把所有改动（含新文件、删除）都加进来，默认 false", false));
            schema["properties"]!["files"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["description"] = "要提交的文件（相对仓库根目录或绝对路径）",
            };
            return schema;
        }
    }

    public override PolicyDecision Assess(JsonElement args, ToolContext ctx)
    {
        string dir;
        try
        {
            dir = RepoDir(args, ctx);
        }
        catch (Exception)
        {
            return PolicyDecision.Blocked("目录无效");
        }
        // 用等价的命令去判（不带说明文字：说明里的标点不该影响判定）
        var stage = Files(args).Count > 0 ? "git add -- <files> && " : args.Bool("all", false) ? "git add -A && " : "";
        return PermissionRules.ForCommand(ctx.Policy, ctx.EffectivePermission, ctx.Workspace, stage + "git commit", dir);
    }

    public override string Describe(JsonElement args)
    {
        var files = Files(args);
        var what = files.Count > 0 ? $"{files.Count} 个文件" : args.Bool("all", false) ? "全部改动" : "已暂存的改动";
        var msg = args.Str("message");
        return $"提交{what}：「{(msg.Length > 40 ? msg[..40] + "…" : msg)}」";
    }

    private static List<string> Files(JsonElement args) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty("files", out var f) && f.ValueKind == JsonValueKind.Array
            ? f.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!.Trim()).Where(s => s.Length > 0).ToList()
            : new List<string>();

    protected override async Task<ToolResult> RunAsync(JsonElement args, ToolContext ctx, string dir, CancellationToken ct)
    {
        var message = args.Required("message").Trim();
        var files = Files(args);
        if (Unsafe(files.ToArray()) is { } bad)
        {
            return ToolResult.Fail($"文件名不能以 - 开头或包含换行：{bad}");
        }
        if (files.Count > 0)
        {
            var add = await GitRunner.RunAsync(dir, new[] { "add", "--" }.Concat(files), ct);
            if (add.ExitCode != 0)
            {
                return FromResult(add, "");
            }
        }
        else if (args.Bool("all", false))
        {
            var add = await GitRunner.RunAsync(dir, new[] { "add", "-A" }, ct);
            if (add.ExitCode != 0)
            {
                return FromResult(add, "");
            }
        }
        // 说明走 -F -（标准输入）也行，但 -m 按参数列表传本来就不会被 Shell 解析
        var commit = await GitRunner.RunAsync(dir, new[] { "commit", "-m", message }, ct);
        if (commit.ExitCode != 0)
        {
            var text = (commit.Stdout + "\n" + commit.Stderr).Trim();
            if (text.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase) || text.Contains("nothing added to commit", StringComparison.OrdinalIgnoreCase) || text.Contains("no changes added", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Fail("没有可提交的改动（文件没加进暂存区？用 files 指定文件或 all=true）");
            }
            if (text.Contains("user.email", StringComparison.OrdinalIgnoreCase) || text.Contains("Please tell me who you are", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Fail("这台电脑的 git 还没设置提交人（user.name / user.email），请员工先设置，不要替员工编一个");
            }
            return FromResult(commit, "");
        }
        var head = await GitRunner.RunAsync(dir, new[] { "log", "-1", "--pretty=format:%h %s" }, ct);
        return ToolResult.Success($"已提交：{head.Stdout.Trim()}\n{commit.Stdout.Trim()}");
    }
}
