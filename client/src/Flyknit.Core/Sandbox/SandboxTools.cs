using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flyknit.Core.Security;
using Flyknit.Core.Tools;

namespace Flyknit.Core.Sandbox;

/// <summary>
/// 在沙箱容器里执行的工具：影响出不了一次性容器，所以不弹确认（Agent 循环的污染模式也不拦它们，见 AgentLoop）。
/// 能把东西带出容器的只有出口（create_pull_request），它不实现这个接口，照常判定。
/// </summary>
public interface ISandboxedTool
{
}

/// <summary>在某个沙箱容器里执行一条命令。</summary>
public delegate Task<CliRunner.Result> SandboxExec(string command, TimeSpan timeout, CancellationToken ct, string? stdin = null);

/// <summary>
/// 沙箱版的文件、命令、git 工具：名字和参数与平常一样（模型不用学新东西），执行落到容器里。
/// 路径：相对路径相对 /workspace（仓库根目录），也可以写容器里的绝对路径。
/// </summary>
public static class SandboxToolSet
{
    public static IReadOnlyList<ITool> Create(SandboxExec exec) => new ITool[]
    {
        new SandboxReadFile(exec),
        new SandboxListDir(exec),
        new SandboxSearchFiles(exec),
        new SandboxGrep(exec),
        new SandboxWriteFile(exec),
        new SandboxEditFile(exec, multi: false),
        new SandboxEditFile(exec, multi: true),
        new SandboxDeletePath(exec),
        new SandboxShell(exec),
        new SandboxGit(exec, "git_status"),
        new SandboxGit(exec, "git_diff"),
        new SandboxGit(exec, "git_log"),
        new SandboxGit(exec, "git_commit"),
    };

    /// <summary>平常版里沙箱版替换掉的工具名（宿主据此从这一轮的工具表里去掉本机版）。</summary>
    public static readonly IReadOnlySet<string> Replaces = new HashSet<string>(StringComparer.Ordinal)
    {
        "read_file", "list_dir", "search_files", "grep", "write_file", "edit_file", "multi_edit", "delete_path", "run_shell",
        "git_status", "git_diff", "git_log", "git_commit", "shell_output", "shell_stop", "open_app",
    };

    internal static string Path(string raw)
    {
        raw = raw.Trim().Replace('\\', '/');
        if (raw.Length == 0 || raw == ".")
        {
            return SandboxSession.Workdir;
        }
        if (raw.StartsWith('/'))
        {
            return raw;
        }
        // 只去掉开头的「./」，不能 TrimStart('.')：那会把 .github、.env 这类点开头的名字也改掉
        while (raw.StartsWith("./", StringComparison.Ordinal))
        {
            raw = raw[2..];
        }
        return raw.Length == 0 ? SandboxSession.Workdir : SandboxSession.Workdir + "/" + raw;
    }

    internal static string Q(string s) => DockerSandbox.ShellQuote(s);

    internal static string Error(CliRunner.Result r) => (r.Stderr.Trim().Length > 0 ? r.Stderr : r.Stdout).Trim();
}

public abstract class SandboxTool : ITool, ISandboxedTool
{
    protected SandboxTool(SandboxExec exec) => Exec = exec;

    protected SandboxExec Exec { get; }
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract JsonObject Parameters { get; }
    public virtual PolicyDecision Assess(JsonElement args, ToolContext ctx) => PolicyDecision.Auto("在沙箱容器里");
    public abstract string Describe(JsonElement args);
    public abstract Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct);

    protected Task<CliRunner.Result> Run(string command, CancellationToken ct, int seconds = 120, string? stdin = null) =>
        Exec(command, TimeSpan.FromSeconds(seconds), ct, stdin);

    protected async Task<string?> ReadAllAsync(string path, CancellationToken ct)
    {
        var r = await Run($"test -f {SandboxToolSet.Q(path)} && cat {SandboxToolSet.Q(path)}", ct);
        return r.ExitCode == 0 ? r.Stdout : null;
    }
}

public sealed class SandboxReadFile : SandboxTool
{
    public SandboxReadFile(SandboxExec exec) : base(exec) { }
    public override string Name => "read_file";
    public override string Description => "读取沙箱容器里的文本文件。路径相对仓库根目录（/workspace）。大文件用 start_line / max_lines 分段读。";
    public override JsonObject Parameters => ToolArgs.Schema(
        ("path", "string", "文件路径", true),
        ("start_line", "integer", "从第几行开始，默认 1", false),
        ("max_lines", "integer", "最多读几行，默认 400", false));
    public override string Describe(JsonElement args) => $"读取 {args.Str("path")}";

    public override async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var path = SandboxToolSet.Path(args.Required("path"));
        var start = Math.Max(1, args.Int("start_line", 1));
        var max = Math.Clamp(args.Int("max_lines", 400), 1, 5000);
        var q = SandboxToolSet.Q(path);
        var r = await Run($"test -f {q} || {{ echo '文件不存在' >&2; exit 3; }}; total=$(wc -l < {q}); echo \"__TOTAL__ $total\"; sed -n '{start},{start + max - 1}p' {q}", ct);
        if (r.ExitCode != 0)
        {
            return ToolResult.Fail($"读不了 {path}：{SandboxToolSet.Error(r)}");
        }
        var nl = r.Stdout.IndexOf('\n');
        var header = nl > 0 ? r.Stdout[..nl] : r.Stdout;
        var body = nl > 0 ? r.Stdout[(nl + 1)..] : "";
        var total = int.TryParse(header.Replace("__TOTAL__", "").Trim(), out var t) ? t : 0;
        var end = Math.Min(total, start + max - 1);
        var note = total > max || start > 1 ? $"（{path}，共 {total} 行，显示第 {start}–{end} 行）\n" : "";
        return ToolResult.Success(note + body);
    }
}

public sealed class SandboxListDir : SandboxTool
{
    public SandboxListDir(SandboxExec exec) : base(exec) { }
    public override string Name => "list_dir";
    public override string Description => "列出沙箱容器里一个目录的内容。";
    public override JsonObject Parameters => ToolArgs.Schema(("path", "string", "目录，默认仓库根目录", false));
    public override string Describe(JsonElement args) => $"查看目录 {args.Str("path", ".")}";

    public override async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var path = SandboxToolSet.Path(args.Str("path"));
        var r = await Run($"ls -la {SandboxToolSet.Q(path)} | head -n 500", ct);
        return r.ExitCode == 0 ? ToolResult.Success(r.Stdout.TrimEnd()) : ToolResult.Fail(SandboxToolSet.Error(r));
    }
}

public sealed class SandboxSearchFiles : SandboxTool
{
    public SandboxSearchFiles(SandboxExec exec) : base(exec) { }
    public override string Name => "search_files";
    public override string Description => "在沙箱容器里按文件名通配符找文件，支持 src/**/*.ts、*.{cs,vue}。会跳过 .git、node_modules。";
    public override JsonObject Parameters => ToolArgs.Schema(
        ("path", "string", "从哪个目录找，默认仓库根目录", false),
        ("pattern", "string", "通配符，默认 *", false),
        ("max_results", "integer", "最多返回多少个，默认 200", false));
    public override string Describe(JsonElement args) => $"找文件 {args.Str("pattern", "*")}";

    public override async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var root = SandboxToolSet.Path(args.Str("path"));
        var glob = new Glob(args.Str("pattern", "*") is { Length: > 0 } p ? p : "*");
        var max = Math.Clamp(args.Int("max_results", 200), 1, 2000);
        var prune = string.Join(" -o ", SourceTree.SkippedDirectories.Select(d => $"-name {SandboxToolSet.Q(d)}"));
        var r = await Run($"cd {SandboxToolSet.Q(root)} && find . \\( -type d \\( {prune} \\) -prune \\) -o -type f -print | head -n 50000", ct);
        if (r.ExitCode != 0)
        {
            return ToolResult.Fail(SandboxToolSet.Error(r));
        }
        var hits = r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimStart('.', '/'))
            .Where(glob.IsMatch).Take(max).ToList();
        return ToolResult.Success(hits.Count == 0 ? "没有找到匹配的文件" : $"找到 {hits.Count} 个文件：\n" + string.Join("\n", hits));
    }
}

public sealed class SandboxGrep : SandboxTool
{
    public SandboxGrep(SandboxExec exec) : base(exec) { }
    public override string Name => "grep";
    public override string Description => "在沙箱容器里按内容搜代码（扩展正则，同 grep -E）。返回「文件:行号: 内容」。跳过 .git、node_modules、bin、obj、dist 和二进制文件。";
    public override JsonObject Parameters
    {
        get
        {
            var schema = ToolArgs.Schema(
                ("pattern", "string", "正则表达式；literal=true 时按普通文字找", true),
                ("path", "string", "目录或文件，默认仓库根目录", false),
                ("glob", "string", "只搜这些文件，如 *.py、src/**/*.ts", false),
                ("ignore_case", "boolean", "不分大小写", false),
                ("literal", "boolean", "按普通文字找", false),
                ("context", "integer", "前后各带几行，0-5", false),
                ("output", "string", "content / files / count", false),
                ("max_results", "integer", "最多多少行，默认 200", false));
            return schema;
        }
    }
    public override string Describe(JsonElement args) => $"搜索「{args.Str("pattern")}」";

    public override async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var pattern = args.Required("pattern");
        var path = SandboxToolSet.Path(args.Str("path"));
        var flags = new StringBuilder("-rnI");
        if (args.Bool("ignore_case", false))
        {
            flags.Append('i');
        }
        flags.Append(args.Bool("literal", false) ? 'F' : 'E');
        var mode = args.Str("output", "content");
        if (mode == "files")
        {
            flags.Append('l');
        }
        else if (mode == "count")
        {
            flags.Append('c');
        }
        var context = Math.Clamp(args.Int("context", 0), 0, 5);
        var max = Math.Clamp(args.Int("max_results", 200), 1, 2000);
        var excludes = string.Join(" ", SourceTree.SkippedDirectories.Select(d => $"--exclude-dir={SandboxToolSet.Q(d)}"));
        var globText = args.Str("glob");
        var include = globText.Length > 0 && !Glob.NeedsWalk(globText) ? $"--include={SandboxToolSet.Q(globText)}" : "";
        var tail = $"{(context > 0 ? $" -C {context}" : "")} -e {SandboxToolSet.Q(pattern)} {SandboxToolSet.Q(path)} 2>&1 | head -n {max * 4}";
        var r = await Run($"set -o pipefail 2>/dev/null; grep {flags}{tail.Replace(" -e ", $" {excludes} {include} -e ")}", ct);
        if (r.ExitCode == 2 && (r.Stdout.Contains("Usage", StringComparison.Ordinal) || r.Stdout.Contains("unrecognized", StringComparison.OrdinalIgnoreCase)))
        {
            // 精简版 grep（busybox）不认 -I、--exclude-dir、--include：只用基本参数，跳过的目录在这边过滤
            r = await Run($"set -o pipefail 2>/dev/null; grep {flags.ToString().Replace("I", "")}{tail}", ct);
        }
        if (r.ExitCode > 1 && !r.Stdout.Contains(':'))
        {
            return ToolResult.Fail("grep 出错：" + SandboxToolSet.Error(r));
        }
        var skipped = SourceTree.SkippedDirectories.Select(d => "/" + d + "/").ToList();
        var lines = r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !l.StartsWith("Binary file", StringComparison.Ordinal) && !l.StartsWith("grep:", StringComparison.Ordinal))
            .Where(l => !skipped.Any(d => l.Contains(d, StringComparison.Ordinal)))
            .Select(l => l.StartsWith(SandboxSession.Workdir + "/", StringComparison.Ordinal) ? l[(SandboxSession.Workdir.Length + 1)..] : l)
            .Where(l => mode != "count" || !l.EndsWith(":0", StringComparison.Ordinal));
        if (globText.Length > 0)
        {
            var glob = new Glob(globText);
            lines = lines.Where(l => glob.IsMatch(System.Text.RegularExpressions.Regex.Match(l, @"^(.+?)[:-]\d+[:-]") is { Success: true } m ? m.Groups[1].Value : l.Split(':')[0]));
        }
        var list = lines.Take(max).ToList();
        return ToolResult.Success(list.Count == 0 ? $"没有找到「{pattern}」" : string.Join("\n", list));
    }
}

public sealed class SandboxWriteFile : SandboxTool
{
    public SandboxWriteFile(SandboxExec exec) : base(exec) { }
    public override string Name => "write_file";
    public override string Description => "在沙箱容器里创建或覆盖文件（也可以追加）。目录不存在时自动创建。";
    public override JsonObject Parameters => ToolArgs.Schema(
        ("path", "string", "文件路径", true),
        ("content", "string", "完整内容", true),
        ("append", "boolean", "追加到末尾，默认覆盖", false));
    public override string Describe(JsonElement args) => $"写入 {args.Str("path")}（{args.Str("content").Length} 个字符）";

    public override async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var path = SandboxToolSet.Path(args.Required("path"));
        var q = SandboxToolSet.Q(path);
        var r = await Run($"mkdir -p \"$(dirname {q})\" && cat {(args.Bool("append", false) ? ">>" : ">")} {q}", ct, stdin: args.Str("content"));
        return r.ExitCode == 0 ? ToolResult.Success($"已写入 {path}") : ToolResult.Fail($"写入失败：{SandboxToolSet.Error(r)}");
    }
}

/// <summary>edit_file 和 multi_edit 的沙箱版：读出来在这边替换，再整个写回去（规则和平常版一样：原文要唯一）。</summary>
public sealed class SandboxEditFile : SandboxTool
{
    private readonly bool _multi;
    private readonly ITool _shape;

    public SandboxEditFile(SandboxExec exec, bool multi) : base(exec)
    {
        _multi = multi;
        _shape = multi ? new MultiEditTool() : new EditFileTool();
    }

    public override string Name => _shape.Name;
    public override string Description => "（在沙箱容器里）" + _shape.Description;
    public override JsonObject Parameters => _shape.Parameters;
    public override string Describe(JsonElement args) => _shape.Describe(args);

    public override async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var path = SandboxToolSet.Path(args.Required("path"));
        var content = await ReadAllAsync(path, ct);
        if (content is null)
        {
            return ToolResult.Fail($"文件不存在：{path}。新建文件请用 write_file");
        }
        var edits = new List<(string Old, string New, bool All)>();
        if (_multi)
        {
            if (!args.TryGetProperty("edits", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
            {
                return ToolResult.Fail("edits 要是一个非空数组");
            }
            edits.AddRange(list.EnumerateArray().Select(e => (e.Str("old_text"), e.Str("new_text"), e.Bool("replace_all", false))));
        }
        else
        {
            edits.Add((args.Str("old_text"), args.Str("new_text"), args.Bool("replace_all", false)));
        }
        var updated = content;
        var k = 0;
        foreach (var (oldText, newText, all) in edits)
        {
            k++;
            if (oldText.Length == 0 || oldText == newText)
            {
                return ToolResult.Fail($"第 {k} 处的 old_text 为空或和 new_text 一样，文件没有改动");
            }
            var (find, replace) = EditFileTool.Match(updated, oldText, newText);
            var count = 0;
            for (var i = updated.IndexOf(find, StringComparison.Ordinal); i >= 0; i = updated.IndexOf(find, i + find.Length, StringComparison.Ordinal))
            {
                count++;
            }
            if (count == 0)
            {
                return ToolResult.Fail($"第 {k} 处在文件里找不到。文件没有改动；先 read_file 看一下当前内容");
            }
            if (count > 1 && !all)
            {
                return ToolResult.Fail($"第 {k} 处在文件里出现了 {count} 次。文件没有改动；多带些前后文，或 replace_all=true");
            }
            if (all)
            {
                updated = updated.Replace(find, replace, StringComparison.Ordinal);
            }
            else
            {
                var at = updated.IndexOf(find, StringComparison.Ordinal);
                updated = updated[..at] + replace + updated[(at + find.Length)..];
            }
        }
        var w = await Run($"cat > {SandboxToolSet.Q(path)}", ct, stdin: updated);
        return w.ExitCode == 0 ? ToolResult.Success($"已修改 {path}（{k} 处）") : ToolResult.Fail($"写回失败：{SandboxToolSet.Error(w)}");
    }
}

public sealed class SandboxDeletePath : SandboxTool
{
    public SandboxDeletePath(SandboxExec exec) : base(exec) { }
    public override string Name => "delete_path";
    public override string Description => "删除沙箱容器里的文件或目录（只影响容器，不用确认；改动最后在 PR 里由人审核）。";
    public override JsonObject Parameters => ToolArgs.Schema(("path", "string", "要删除的路径", true));
    public override string Describe(JsonElement args) => $"删除 {args.Str("path")}";

    public override async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var path = SandboxToolSet.Path(args.Required("path")).TrimEnd('/');
        if (path.Length == 0 || path == SandboxSession.Workdir || path == SandboxSession.Workdir + "/.git" || !path.StartsWith(SandboxSession.Workdir + "/", StringComparison.Ordinal))
        {
            return ToolResult.Fail("只能删除仓库里的文件或目录，不能删整个仓库或 .git");
        }
        var r = await Run($"rm -rf -- {SandboxToolSet.Q(path)}", ct);
        return r.ExitCode == 0 ? ToolResult.Success($"已删除 {path}") : ToolResult.Fail(SandboxToolSet.Error(r));
    }
}

public sealed class SandboxShell : SandboxTool
{
    public SandboxShell(SandboxExec exec) : base(exec) { }
    public override string Name => "run_shell";
    public override string Description =>
        "在沙箱容器（Linux，bash）里执行命令，默认在仓库根目录 /workspace。可以自由安装依赖、编译、跑测试，不用确认，影响不出容器。" +
        "容器里没有 GitHub 令牌：推送和建 PR 用 create_pull_request，不要用 git push。";
    public override JsonObject Parameters => ToolArgs.Schema(
        ("command", "string", "要执行的命令（bash）", true),
        ("working_directory", "string", "工作目录，默认 /workspace", false),
        ("timeout_seconds", "integer", "超时（秒），默认 300，最大 3600", false));

    // 拦截规则照样生效（不弹确认，直接拦下）：防止借沙箱做明显的坏事
    public override PolicyDecision Assess(JsonElement args, ToolContext ctx)
    {
        var baseline = ctx.Policy.EvaluateCommand(args.Str("command"), SandboxSession.Workdir);
        return baseline.Level == RiskLevel.Blocked ? baseline : PolicyDecision.Auto("在沙箱容器里");
    }

    public override string Describe(JsonElement args) => $"（沙箱）{args.Str("command")}";

    public override async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var command = args.Required("command");
        var wd = args.Str("working_directory");
        var full = wd.Length > 0 ? $"cd {SandboxToolSet.Q(SandboxToolSet.Path(wd))} && {command}" : command;
        var r = await Run(full, ct, Math.Clamp(args.Int("timeout_seconds", 300), 5, 3600));
        var sb = new StringBuilder();
        sb.AppendLine($"退出码：{r.ExitCode}");
        if (r.Stdout.Length > 0)
        {
            sb.AppendLine(r.Stdout.TrimEnd());
        }
        if (r.Stderr.Trim().Length > 0)
        {
            sb.AppendLine("[stderr]");
            sb.AppendLine(r.Stderr.TrimEnd());
        }
        return r.ExitCode == 0 ? ToolResult.Success(sb.ToString().TrimEnd()) : ToolResult.Fail(sb.ToString().TrimEnd());
    }
}

/// <summary>git_status / git_diff / git_log / git_commit 的沙箱版。参数和平常版一样，引用、文件名不能以 - 开头。</summary>
public sealed class SandboxGit : SandboxTool
{
    private readonly string _name;
    private readonly ITool _shape;

    public SandboxGit(SandboxExec exec, string name) : base(exec)
    {
        _name = name;
        _shape = name switch
        {
            "git_status" => new GitStatusTool(),
            "git_diff" => new GitDiffTool(),
            "git_log" => new GitLogTool(),
            _ => new GitCommitTool(),
        };
    }

    public override string Name => _name;
    public override string Description => "（在沙箱容器里）" + _shape.Description;
    public override JsonObject Parameters => _shape.Parameters;
    public override string Describe(JsonElement args) => _shape.Describe(args);

    public override async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var target = args.Str("target").Trim();
        var file = args.Str("file").Trim();
        var files = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("files", out var f) && f.ValueKind == JsonValueKind.Array
            ? f.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!.Trim()).Where(s => s.Length > 0).ToList()
            : new List<string>();
        if (new[] { target, file }.Concat(files).Any(v => v.StartsWith('-') || v.Contains('\n')))
        {
            return ToolResult.Fail("参数不能以 - 开头或包含换行");
        }
        string Q(string s) => SandboxToolSet.Q(s);
        var command = _name switch
        {
            "git_status" => "git status --porcelain=v1 --branch --untracked-files=normal",
            "git_diff" => "git --no-pager diff" + (args.Bool("staged", false) ? " --staged" : "") + (args.Bool("stat", false) ? " --stat" : "")
                + (target.Length > 0 ? " " + Q(target) : "") + " --" + (file.Length > 0 ? " " + Q(file) : ""),
            "git_log" => $"git --no-pager log --max-count={Math.Clamp(args.Int("max_count", 20), 1, 200)} --date=short --pretty=format:'%h %ad %an  %s'"
                + (target.Length > 0 ? " " + Q(target) : "") + " --" + (file.Length > 0 ? " " + Q(file) : ""),
            _ => (files.Count > 0 ? "git add -- " + string.Join(" ", files.Select(Q)) + " && " : args.Bool("all", false) ? "git add -A && " : "")
                + "git commit -m " + Q(args.Required("message").Trim()) + " && git --no-pager log -1 --pretty=format:'已提交：%h %s'",
        };
        var r = await Run(command, ct);
        if (r.ExitCode != 0)
        {
            var text = SandboxToolSet.Error(r);
            return ToolResult.Fail(text.Contains("nothing", StringComparison.OrdinalIgnoreCase) ? "没有可提交的改动（用 files 指定文件或 all=true）" : $"git 出错：{text}");
        }
        var output = r.Stdout.TrimEnd();
        return ToolResult.Success(output.Length > 0 ? output : _name == "git_diff" ? "没有改动" : "（没有输出）");
    }
}
