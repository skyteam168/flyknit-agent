using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Flyknit.Core.Security;

namespace Flyknit.Core.Skills;

/// <summary>安装前的检查结果。Ok 为 false 时不会安装。</summary>
public sealed class SkillInspection
{
    public bool Ok => Error is null;
    public string? Error { get; init; }

    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Version { get; init; } = "";
    public IReadOnlyDictionary<string, string> Meta { get; init; } = new Dictionary<string, string>();

    /// <summary>技能内的文件（相对路径）。</summary>
    public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();

    /// <summary>其中的可执行脚本，安装时要让用户看到。</summary>
    public IReadOnlyList<string> Scripts { get; init; } = Array.Empty<string>();

    public long Bytes { get; init; }

    /// <summary>不阻止安装、但需要提醒用户的问题。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>已安装过同名技能时为 true（安装即升级）。</summary>
    public bool Replaces { get; init; }
}

public sealed record SkillInstallResult(bool Ok, string Message, SkillInspection Inspection);

/// <summary>
/// 技能包的校验与安装。来源可以是 zip 文件、文件夹（含共享盘）或下载到本地的 zip。
/// 安装过程：解压到临时目录 → 校验（结构、大小、脚本安全）→ 整体替换目标目录，失败不留残骸。
/// </summary>
public static class SkillPackage
{
    public const long MaxTotalBytes = 64L * 1024 * 1024;
    public const int MaxFiles = 2000;
    public const long MaxSingleFileBytes = 32L * 1024 * 1024;

    /// <summary>需要提醒用户的可执行文件类型。</summary>
    public static readonly string[] ScriptExtensions =
        { ".ps1", ".psm1", ".bat", ".cmd", ".py", ".vbs", ".js", ".sh", ".exe", ".msi", ".dll", ".jar", ".com", ".scr" };

    private static readonly string[] BlockedExtensions = { ".exe", ".msi", ".com", ".scr", ".dll", ".lnk", ".pif", ".cpl", ".sys" };
    private static readonly Regex ValidName = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{1,63}$", RegexOptions.Compiled);

    /// <summary>检查一个已解压的技能目录。</summary>
    public static SkillInspection Inspect(string directory, CommandPolicy? policy, Func<string, bool>? exists = null)
    {
        var skillFile = Path.Combine(directory, "SKILL.md");
        if (!File.Exists(skillFile))
        {
            return new SkillInspection { Error = "压缩包里没有找到 SKILL.md，这不是一个技能包" };
        }

        Dictionary<string, string> meta;
        string body;
        try
        {
            (meta, body) = SkillCatalog.SplitFrontmatter(File.ReadAllText(skillFile));
        }
        catch (Exception ex)
        {
            return new SkillInspection { Error = $"无法读取 SKILL.md：{ex.Message}" };
        }

        var name = meta.TryGetValue("name", out var n) && n.Trim().Length > 0 ? n.Trim() : Path.GetFileName(directory.TrimEnd('\\', '/'));
        name = Sanitize(name);
        if (!ValidName.IsMatch(name))
        {
            return new SkillInspection { Error = $"技能名不合法：{name}（只能是字母、数字、减号、下划线和点，2-64 个字符）" };
        }
        var description = meta.TryGetValue("description", out var d) ? d.Trim() : "";
        if (description.Length == 0)
        {
            return new SkillInspection { Error = "SKILL.md 的 frontmatter 里缺少 description，模型无法判断什么时候该用这个技能" };
        }

        var files = new List<string>();
        long total = 0;
        try
        {
            foreach (var f in new DirectoryInfo(directory).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                files.Add(Path.GetRelativePath(directory, f.FullName).Replace('\\', '/'));
                total += f.Length;
                if (files.Count > MaxFiles)
                {
                    return new SkillInspection { Error = $"技能包里的文件超过 {MaxFiles} 个" };
                }
                if (f.Length > MaxSingleFileBytes)
                {
                    return new SkillInspection { Error = $"文件 {f.Name} 超过 {MaxSingleFileBytes / 1024 / 1024} MB" };
                }
            }
        }
        catch (Exception ex)
        {
            return new SkillInspection { Error = $"读取技能内容失败：{ex.Message}" };
        }
        if (total > MaxTotalBytes)
        {
            return new SkillInspection { Error = $"技能包超过 {MaxTotalBytes / 1024 / 1024} MB" };
        }

        var blocked = files.FirstOrDefault(f => BlockedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));
        if (blocked is not null)
        {
            return new SkillInspection { Error = $"技能包里含有可执行程序（{blocked}），出于安全考虑不允许安装" };
        }

        var scripts = files.Where(f => ScriptExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)).ToList();
        var warnings = new List<string>();

        // 脚本内容过一遍命令策略，命中危险规则直接拒绝
        if (policy is not null)
        {
            foreach (var rel in scripts)
            {
                var decision = policy.EvaluateScript(Path.Combine(directory, rel.Replace('/', Path.DirectorySeparatorChar)));
                if (decision.Level == RiskLevel.Blocked)
                {
                    return new SkillInspection { Error = $"脚本 {rel} 未通过安全检查：{decision.Reason}" };
                }
            }
            var hit = ScanText(policy, body);
            if (hit is not null)
            {
                warnings.Add($"说明里出现了需要注意的命令：{hit}");
            }
        }
        if (scripts.Count > 0)
        {
            warnings.Add($"包含 {scripts.Count} 个脚本，运行时仍会按权限逐条确认");
        }
        if (description.Length < 15)
        {
            warnings.Add("description 太短，模型可能判断不准什么时候该用它，建议补充“什么情况下使用”");
        }
        if (body.Trim().Length < 40)
        {
            warnings.Add("SKILL.md 正文内容很少");
        }

        return new SkillInspection
        {
            Name = name,
            Description = description,
            Version = meta.TryGetValue("version", out var v) ? v : "",
            Meta = meta,
            Files = files,
            Scripts = scripts,
            Bytes = total,
            Warnings = warnings,
            Replaces = exists?.Invoke(name) ?? false,
        };
    }

    /// <summary>
    /// 从 zip 文件或文件夹安装到 targetRoot/&lt;name&gt;。
    /// zip 里如果只有一层外壳目录会自动进入；路径穿越、绝对路径、软链接都会被拒绝。
    /// </summary>
    public static SkillInstallResult Install(string sourcePath, string targetRoot, CommandPolicy? policy, string origin = "", bool overwrite = true)
    {
        var staging = Path.Combine(Path.GetTempPath(), "Flyknit", "skill-install", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            if (Directory.Exists(sourcePath))
            {
                CopyDirectory(sourcePath, staging);
            }
            else if (File.Exists(sourcePath))
            {
                var error = Extract(sourcePath, staging);
                if (error is not null)
                {
                    return new SkillInstallResult(false, error, new SkillInspection { Error = error });
                }
            }
            else
            {
                return new SkillInstallResult(false, $"找不到 {sourcePath}", new SkillInspection { Error = "来源不存在" });
            }

            var root = FindSkillRoot(staging);
            if (root is null)
            {
                const string msg = "压缩包里没有找到 SKILL.md，这不是一个技能包";
                return new SkillInstallResult(false, msg, new SkillInspection { Error = msg });
            }

            var inspection = Inspect(root, policy, name => Directory.Exists(Path.Combine(targetRoot, name)));
            if (!inspection.Ok)
            {
                return new SkillInstallResult(false, inspection.Error!, inspection);
            }

            var target = Path.Combine(targetRoot, inspection.Name);
            if (Directory.Exists(target) && !overwrite)
            {
                return new SkillInstallResult(false, $"已经安装过 {inspection.Name}", inspection);
            }

            // 写入 origin，界面上能看出技能是从哪里来的
            if (origin.Length > 0)
            {
                WriteOrigin(Path.Combine(root, "SKILL.md"), origin);
            }

            Directory.CreateDirectory(targetRoot);
            var backup = target + ".old-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            if (Directory.Exists(target))
            {
                Directory.Move(target, backup);
            }
            try
            {
                Directory.Move(root, target);
            }
            catch (IOException)
            {
                // 跨卷时 Move 会失败，退回复制
                CopyDirectory(root, target);
            }
            if (Directory.Exists(backup))
            {
                TryDelete(backup);
            }
            return new SkillInstallResult(true, inspection.Replaces ? $"已更新技能 {inspection.Name}" : $"已安装技能 {inspection.Name}", inspection);
        }
        catch (Exception ex)
        {
            return new SkillInstallResult(false, $"安装失败：{ex.Message}", new SkillInspection { Error = ex.Message });
        }
        finally
        {
            TryDelete(staging);
        }
    }

    /// <summary>解压 zip，逐条校验条目路径（防 zip slip）。</summary>
    private static string? Extract(string zipPath, string destination)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        long total = 0;
        var count = 0;
        var full = Path.GetFullPath(destination);
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                continue; // 目录项
            }
            if (++count > MaxFiles)
            {
                return $"压缩包里的文件超过 {MaxFiles} 个";
            }
            total += entry.Length;
            if (entry.Length > MaxSingleFileBytes || total > MaxTotalBytes)
            {
                return "压缩包解压后体积过大";
            }
            var name = entry.FullName.Replace('\\', '/');
            if (name.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(name) || name.Contains(':'))
            {
                return $"压缩包里的路径不安全：{entry.FullName}";
            }
            var targetPath = Path.GetFullPath(Path.Combine(destination, name));
            if (!targetPath.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return $"压缩包里的路径不安全：{entry.FullName}";
            }
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            entry.ExtractToFile(targetPath, overwrite: true);
        }
        return count == 0 ? "压缩包是空的" : null;
    }

    /// <summary>找到含 SKILL.md 的目录（允许 zip 里套一两层外壳目录，GitHub 下载的包就是这样）。</summary>
    public static string? FindSkillRoot(string staging)
    {
        if (File.Exists(Path.Combine(staging, "SKILL.md")))
        {
            return staging;
        }
        var found = Directory
            .EnumerateFiles(staging, "SKILL.md", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 4, MatchCasing = MatchCasing.CaseInsensitive })
            .OrderBy(f => f.Count(c => c is '\\' or '/'))
            .FirstOrDefault();
        return found is null ? null : Path.GetDirectoryName(found);
    }

    /// <summary>一个压缩包里可能有多个技能（社区仓库常见），返回每个技能目录。</summary>
    public static List<string> FindAllSkillRoots(string staging) =>
        Directory
            .EnumerateFiles(staging, "SKILL.md", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 5, MatchCasing = MatchCasing.CaseInsensitive })
            .Select(f => Path.GetDirectoryName(f)!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>在 frontmatter 里记下安装来源。</summary>
    private static void WriteOrigin(string skillFile, string origin)
    {
        try
        {
            var text = File.ReadAllText(skillFile).Replace("\r\n", "\n");
            var line = $"origin: \"{origin.Replace("\"", "'")}\"";
            if (text.StartsWith("---\n", StringComparison.Ordinal))
            {
                var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
                if (end > 0)
                {
                    var header = text[4..end];
                    header = string.Join("\n", header.Split('\n').Where(l => !l.StartsWith("origin:", StringComparison.OrdinalIgnoreCase)));
                    text = "---\n" + header.TrimEnd('\n') + "\n" + line + text[end..];
                    File.WriteAllText(skillFile, text, new UTF8Encoding(false));
                }
            }
        }
        catch (Exception)
        {
            // 记不上来源不影响使用
        }
    }

    public static string Sanitize(string name)
    {
        var cleaned = Regex.Replace(name.Trim().ToLowerInvariant(), @"[^a-z0-9._-]+", "-").Trim('-', '.');
        return cleaned.Length > 64 ? cleaned[..64].Trim('-', '.') : cleaned;
    }

    private static string? ScanText(CommandPolicy policy, string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var decision = policy.EvaluateCommand(line.Trim().TrimStart('`', '$', '>', '#', ' '));
            if (decision.Level == RiskLevel.Blocked && !decision.Reason.Contains("命令为空"))
            {
                return line.Trim().Length > 80 ? line.Trim()[..80] + "…" : line.Trim();
            }
        }
        return null;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            if (info.LinkTarget is not null)
            {
                continue; // 跳过软链接，避免指向目录外
            }
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
        }
    }

    public static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception)
        {
            // 被占用时留在临时目录，不影响主流程
        }
    }
}
