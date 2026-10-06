using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Flyknit.Core.Security;
using Flyknit.Core.Tools;
using Microsoft.Win32;

namespace Flyknit.Client.Tools;

/// <summary>删除时移入回收站。</summary>
public sealed class RecycleBinDeleter : IFileDeleter
{
    public void Delete(string path)
    {
        if (Directory.Exists(path))
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        else
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
    }
}

/// <summary>按名称打开已安装的软件（Outlook、Foxmail、生产程序等）或打开文件、文件夹、网址。</summary>
public sealed class OpenAppTool : ITool
{
    private readonly Func<IReadOnlyDictionary<string, string>> _aliases;
    private Dictionary<string, string>? _index;
    private DateTime _indexedAt;

    public OpenAppTool(Func<IReadOnlyDictionary<string, string>> aliases)
    {
        _aliases = aliases;
    }

    public string Name => "open_app";

    public string Description =>
        "打开电脑上已安装的软件（如 Outlook、Foxmail、Excel、微信、公司生产程序），或用默认程序打开文件、文件夹、网址。" +
        "name 填软件名称即可，不需要完整路径。找不到时会返回相近的软件名称供选择。";

    public JsonObject Parameters => ToolArgs.Schema(
        ("name", "string", "软件名称，或文件 / 文件夹路径，或 http(s) 网址", true),
        ("arguments", "string", "可选的启动参数", false));

    public PolicyDecision Assess(JsonElement args, ToolContext ctx)
    {
        // 打开软件、文件不修改系统，自动执行；可执行脚本仍走命令检查
        var name = args.Str("name");
        if (ctx.NetworkAllowlist)
        {
            // 网址本身，以及塞在启动参数里的网址或域名（chrome https://...、msedge evil.com）都要过白名单
            var network = IsUrl(name)
                ? ctx.Policy.Network.EvaluateUrl(name)
                : ctx.Policy.Network.EvaluateTargetsIn($"{name} {args.Str("arguments")}");
            if (network is { Level: RiskLevel.Blocked })
            {
                return network;
            }
        }
        if (IsUrl(name))
        {
            return PolicyDecision.Auto();
        }
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (ext is ".ps1" or ".bat" or ".cmd" or ".vbs" or ".js" or ".py")
        {
            return PermissionRules.ForScript(ctx.Policy, ctx.Permission, ctx.ResolvePath(name));
        }
        return PolicyDecision.Auto();
    }

    public string Describe(JsonElement args) => $"打开 {args.Str("name")}";

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var name = args.Required("name").Trim();
        var arguments = args.Str("arguments");

        // 网址
        if (IsUrl(name))
        {
            Launch(name, "");
            return Task.FromResult(ToolResult.Success($"已在浏览器中打开 {name}"));
        }

        // 文件或文件夹
        string? path = null;
        try
        {
            path = ctx.ResolvePath(name);
        }
        catch (Exception)
        {
            // 不是合法路径，按软件名称处理
        }
        if (path is not null && (File.Exists(path) || Directory.Exists(path)))
        {
            Launch(path, arguments);
            return Task.FromResult(ToolResult.Success($"已打开 {path}"));
        }

        // 管理员配置的别名
        foreach (var (alias, target) in _aliases())
        {
            if (alias.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                Launch(target, arguments);
                return Task.FromResult(ToolResult.Success($"已打开 {alias}（{target}）"));
            }
        }

        // 开始菜单与 App Paths
        var index = GetIndex();
        var key = Normalize(name);
        var match = index.Keys.FirstOrDefault(k => k == key)
                    ?? index.Keys.Where(k => k.Contains(key) || key.Contains(k)).OrderBy(k => Math.Abs(k.Length - key.Length)).FirstOrDefault();
        if (match is not null)
        {
            Launch(index[match], arguments);
            return Task.FromResult(ToolResult.Success($"已打开 {Path.GetFileNameWithoutExtension(index[match])}"));
        }

        var suggestions = index.Keys.Where(k => k.Length > 1 && (k.Contains(key[..Math.Min(2, key.Length)]))).Take(8).ToList();
        return Task.FromResult(ToolResult.Fail(
            $"没有找到名为“{name}”的软件。" + (suggestions.Count > 0 ? $"相近的有：{string.Join("、", suggestions)}" : "请确认软件已安装，或提供完整路径。")));
    }

    private static bool IsUrl(string name) =>
        name.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase);

    private static void Launch(string target, string arguments)
    {
        Process.Start(new ProcessStartInfo(target) { Arguments = arguments, UseShellExecute = true });
    }

    private static string Normalize(string s) => s.Trim().ToLowerInvariant().Replace(" ", "").Replace(".exe", "").Replace(".lnk", "");

    /// <summary>索引开始菜单快捷方式和注册表 App Paths，10 分钟内复用。</summary>
    private Dictionary<string, string> GetIndex()
    {
        if (_index is not null && DateTime.Now - _indexedAt < TimeSpan.FromMinutes(10))
        {
            return _index;
        }
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var lnk in Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                var name = Normalize(Path.GetFileNameWithoutExtension(lnk));
                if (name.Contains("uninstall") || name.Contains("卸载"))
                {
                    continue;
                }
                index.TryAdd(name, lnk);
            }
        }
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using var appPaths = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
            if (appPaths is null)
            {
                continue;
            }
            foreach (var exe in appPaths.GetSubKeyNames())
            {
                using var sub = appPaths.OpenSubKey(exe);
                if (sub?.GetValue(null) is string target && target.Length > 0)
                {
                    index.TryAdd(Normalize(exe), target.Trim('"'));
                }
            }
        }
        // 常见办公软件的别名
        AddAlias(index, "outlook", "outlook");
        AddAlias(index, "邮件", "outlook");
        AddAlias(index, "excel", "excel");
        AddAlias(index, "word", "winword");
        AddAlias(index, "ppt", "powerpnt");
        AddAlias(index, "powerpoint", "powerpnt");
        AddAlias(index, "记事本", "notepad");
        AddAlias(index, "计算器", "calc");
        AddAlias(index, "notepad", "notepad");
        AddAlias(index, "calc", "calc");
        index.TryAdd("notepad", "notepad.exe");
        index.TryAdd("calc", "calc.exe");

        _index = index;
        _indexedAt = DateTime.Now;
        return index;
    }

    private static void AddAlias(Dictionary<string, string> index, string alias, string existing)
    {
        if (index.TryGetValue(existing, out var target))
        {
            index.TryAdd(alias, target);
        }
    }
}
