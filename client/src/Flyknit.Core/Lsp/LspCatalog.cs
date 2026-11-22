using System.Text.Json;
using System.Text.Json.Nodes;

namespace Flyknit.Core.Lsp;

/// <summary>
/// 启动一个语言服务的命令（可执行文件 + 参数）。参数里的 {data} 换成这个项目的缓存目录（jdtls 要）。
/// <paramref name="MinMajorVersion"/>：程序得是这个大版本以上才算装了（tsc 只有 7 起才自带语言服务）。
/// </summary>
public sealed record LspCommand(string Exe, IReadOnlyList<string> Args, int MinMajorVersion = 0);

/// <summary>一种语言服务：管哪些扩展名、怎么启动（按顺序找第一个装了的）、项目根的标志文件。</summary>
public sealed record LspServerSpec(
    string Id,
    string Name,
    IReadOnlyList<string> Extensions,
    IReadOnlyList<LspCommand> Commands,
    IReadOnlyList<string> RootMarkers,
    string InstallHint)
{
    /// <summary>LSP 里的 languageId（按扩展名）。</summary>
    public string LanguageIdFor(string extension) => extension.ToLowerInvariant() switch
    {
        ".ts" or ".mts" or ".cts" => "typescript",
        ".tsx" => "typescriptreact",
        ".js" or ".mjs" or ".cjs" => "javascript",
        ".jsx" => "javascriptreact",
        ".vue" => "vue",
        ".py" or ".pyi" => "python",
        ".go" => "go",
        ".java" => "java",
        ".cs" => "csharp",
        _ => Id,
    };
}

/// <summary>某个语言服务在这台电脑上装没装、用哪个命令。</summary>
public sealed record LspAvailability(LspServerSpec Spec, LspCommand? Command, string ResolvedExe, bool Disabled);

/// <summary>
/// 支持的语言服务（公司用的：TS / JS / React、Vue、Python、Go、Java、C#）和在这台电脑上怎么找到它们。
///
/// 由 IT 统一安装（员工电脑不从网上下载）。找的顺序：
/// 1. <c>C:\ProgramData\FlyknitBuddy\lsp\lsp.json</c> 里 IT 写的命令（可以覆盖、可以禁用某种语言）；
/// 2. <c>C:\ProgramData\FlyknitBuddy\lsp\</c> 下（含 <c>bin\</c>、<c>node_modules\.bin\</c>）；
/// 3. 系统 PATH。
/// ProgramData 只有管理员能写，员工和项目改不了这里的命令。
/// </summary>
public sealed class LspCatalog
{
    public static readonly IReadOnlyList<LspServerSpec> Defaults = new[]
    {
        new LspServerSpec("typescript", "TypeScript / JavaScript / React",
            new[] { ".ts", ".tsx", ".mts", ".cts", ".js", ".jsx", ".mjs", ".cjs" },
            new[]
            {
                // TypeScript 5 / 6：typescript-language-server + tsserver
                new LspCommand("typescript-language-server", new[] { "--stdio" }),
                // TypeScript 7（原生版）：tsc 自带语言服务
                new LspCommand("tsc", new[] { "--lsp", "--stdio" }, MinMajorVersion: 7),
            },
            new[] { "tsconfig.json", "jsconfig.json", "package.json" },
            "npm i -g typescript typescript-language-server"),
        new LspServerSpec("vue", "Vue",
            new[] { ".vue" },
            new[] { new LspCommand("vue-language-server", new[] { "--stdio" }) },
            new[] { "package.json", "vite.config.ts", "vite.config.js" },
            "npm i -g typescript @vue/language-server"),
        new LspServerSpec("python", "Python",
            new[] { ".py", ".pyi" },
            new[]
            {
                new LspCommand("pyright-langserver", new[] { "--stdio" }),
                new LspCommand("basedpyright-langserver", new[] { "--stdio" }),
                new LspCommand("pylsp", Array.Empty<string>()),
            },
            new[] { "pyproject.toml", "setup.py", "setup.cfg", "requirements.txt", "pyrightconfig.json" },
            "npm i -g pyright"),
        new LspServerSpec("go", "Go",
            new[] { ".go" },
            new[] { new LspCommand("gopls", Array.Empty<string>()) },
            new[] { "go.mod", "go.work" },
            "go install golang.org/x/tools/gopls@latest"),
        new LspServerSpec("java", "Java",
            new[] { ".java" },
            new[] { new LspCommand("jdtls", new[] { "-data", "{data}" }) },
            new[] { "pom.xml", "build.gradle", "build.gradle.kts", "settings.gradle" },
            "Eclipse JDT Language Server（需要 JDK 17+），把 jdtls 放到 PATH"),
        new LspServerSpec("csharp", "C#",
            new[] { ".cs" },
            new[] { new LspCommand("csharp-ls", Array.Empty<string>()) },
            new[] { "*.sln", "*.csproj" },
            "dotnet tool install -g csharp-ls"),
    };

    private readonly string _machineDir;
    private readonly Func<string, string?> _env;
    private readonly Func<string, bool> _exists;

    /// <param name="machineDir">IT 放语言服务和 lsp.json 的目录（C:\ProgramData\FlyknitBuddy\lsp）。</param>
    public LspCatalog(string machineDir, Func<string, string?>? env = null, Func<string, bool>? exists = null)
    {
        _machineDir = machineDir;
        _env = env ?? Environment.GetEnvironmentVariable;
        _exists = exists ?? File.Exists;
    }

    public static string DefaultMachineDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FlyknitBuddy", "lsp");

    public LspServerSpec? ForFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Length == 0 ? null : Defaults.FirstOrDefault(s => s.Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>所有语言服务在这台电脑上的情况（每次现查：IT 刚装好不用重启）。</summary>
    public IReadOnlyList<LspAvailability> Availability() => Defaults.Select(Resolve).ToList();

    /// <summary>这台电脑上能用的所有启动方式（按顺序）：第一个起不来就试下一个（比如 TypeScript 7 没有 tsserver 了，换 tsc --lsp）。</summary>
    public IReadOnlyList<(LspCommand Command, string Exe)> Candidates(LspServerSpec spec)
    {
        var config = ReadConfig();
        if (config.Disabled.Contains(spec.Id, StringComparer.OrdinalIgnoreCase))
        {
            return Array.Empty<(LspCommand, string)>();
        }
        var commands = config.Overrides.TryGetValue(spec.Id, out var custom) ? new[] { custom }.Concat(spec.Commands) : spec.Commands;
        return commands.Select(c => (c, FindExe(c.Exe))).Where(x => x.Item2 is not null && VersionOk(x.c, x.Item2)).Select(x => (x.c, x.Item2!)).ToList();
    }

    public LspAvailability Resolve(LspServerSpec spec)
    {
        var config = ReadConfig();
        if (config.Disabled.Contains(spec.Id, StringComparer.OrdinalIgnoreCase))
        {
            return new LspAvailability(spec, null, "", true);
        }
        var commands = config.Overrides.TryGetValue(spec.Id, out var custom) ? new[] { custom }.Concat(spec.Commands) : spec.Commands;
        foreach (var cmd in commands)
        {
            if (FindExe(cmd.Exe) is { } exe && VersionOk(cmd, exe))
            {
                return new LspAvailability(spec, cmd, exe, false);
            }
        }
        return new LspAvailability(spec, null, "", false);
    }

    /// <summary>版本检查的结果（按程序路径 + 修改时间缓存，换了版本会重新查）。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> Majors = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>测试里换掉：程序 → 大版本号（查不到为 0）。</summary>
    public Func<string, int>? VersionProbe { get; init; }

    private bool VersionOk(LspCommand cmd, string exe)
    {
        if (cmd.MinMajorVersion <= 0)
        {
            return true;
        }
        if (VersionProbe is { } probe)
        {
            return probe(exe) >= cmd.MinMajorVersion;
        }
        var key = exe + "|" + (File.Exists(exe) ? File.GetLastWriteTimeUtc(exe).Ticks : 0);
        return Majors.GetOrAdd(key, _ => ProbeMajor(exe)) >= cmd.MinMajorVersion;
    }

    /// <summary>跑一下 <c>程序 --version</c>，取第一个数字（「Version 7.0.2」→ 7）。5 秒没回、出错为 0。</summary>
    private static int ProbeMajor(string exe)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("--version");
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null)
            {
                return 0;
            }
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(5000))
            {
                p.Kill(entireProcessTree: true);
                return 0;
            }
            var m = System.Text.RegularExpressions.Regex.Match(output.Result, @"(\d+)\.\d+");
            return m.Success && int.TryParse(m.Groups[1].Value, out var major) ? major : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>可执行文件：写了完整路径的直接用；否则在 IT 目录、PATH 里找（Windows 上试 .exe / .cmd / .bat）。</summary>
    public string? FindExe(string exe)
    {
        if (Path.IsPathRooted(exe))
        {
            return _exists(exe) ? exe : null;
        }
        var dirs = new List<string> { _machineDir, Path.Combine(_machineDir, "bin"), Path.Combine(_machineDir, "node_modules", ".bin") };
        dirs.AddRange((_env("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var suffixes = OperatingSystem.IsWindows()
            ? (_env("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries).Select(x => x.ToLowerInvariant()).Prepend("").ToArray()
            : new[] { "" };
        foreach (var dir in dirs)
        {
            foreach (var suffix in suffixes)
            {
                var full = Path.Combine(dir, exe + suffix);
                if (_exists(full))
                {
                    return full;
                }
            }
        }
        return null;
    }

    private sealed record Config(Dictionary<string, LspCommand> Overrides, List<string> Disabled);

    /// <summary>
    /// IT 的 lsp.json：<c>{ "servers": { "python": { "command": "D:\\tools\\pyright-langserver.cmd", "args": ["--stdio"] } }, "disabled": ["java"] }</c>。
    /// 读不了、写错了就当没有。
    /// </summary>
    private Config ReadConfig()
    {
        var config = new Config(new Dictionary<string, LspCommand>(StringComparer.OrdinalIgnoreCase), new List<string>());
        var file = Path.Combine(_machineDir, "lsp.json");
        try
        {
            if (!_exists(file))
            {
                return config;
            }
            if (JsonNode.Parse(File.ReadAllText(file), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) is not JsonObject root)
            {
                return config;
            }
            if (root["servers"] is JsonObject servers)
            {
                foreach (var (id, node) in servers)
                {
                    if (node?["command"]?.GetValue<string>() is { Length: > 0 } command)
                    {
                        var args = node["args"] is JsonArray a ? a.Select(x => x?.GetValue<string>() ?? "").ToArray() : Array.Empty<string>();
                        config.Overrides[id] = new LspCommand(command, args);
                    }
                }
            }
            if (root["disabled"] is JsonArray disabled)
            {
                config.Disabled.AddRange(disabled.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
        }
        return config;
    }

    /// <summary>从文件往上找这种语言的项目根（有标志文件的那一层），最远到工作区根；都没有就是工作区根。</summary>
    public static string RootFor(LspServerSpec spec, string file, string workspace)
    {
        var ws = Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var dir = Path.GetDirectoryName(Path.GetFullPath(file));
        while (dir is not null && dir.Length >= ws.Length)
        {
            foreach (var marker in spec.RootMarkers)
            {
                try
                {
                    var hit = marker.Contains('*') ? Directory.EnumerateFiles(dir, marker).Any() : File.Exists(Path.Combine(dir, marker)) || Directory.Exists(Path.Combine(dir, marker));
                    if (hit)
                    {
                        return dir;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
            if (string.Equals(dir.TrimEnd(Path.DirectorySeparatorChar), ws, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            dir = Path.GetDirectoryName(dir);
        }
        return ws;
    }
}
