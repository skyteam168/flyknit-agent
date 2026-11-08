using System.Text.RegularExpressions;
using Flyknit.Core.Tools;

namespace Flyknit.Core.Git;

/// <summary>工作区所在的 git 仓库：根目录、当前分支、远端（GitHub / GitLab / Gitee 等，凭证已去掉）、改动和领先落后。</summary>
public sealed record RepoInfo
{
    public string Root { get; init; } = "";
    public string Branch { get; init; } = "";

    /// <summary>不在任何分支上（检出了某个提交）。</summary>
    public bool Detached { get; init; }

    /// <summary>远端仓库地址（去掉了 https://用户名:令牌@ 这类凭证），没有远端为空。</summary>
    public string RemoteUrl { get; init; } = "";

    /// <summary>远端在网页上的地址，如 https://github.com/acme/app；认不出为空。</summary>
    public string WebUrl { get; init; } = "";

    public string Host { get; init; } = "";
    public string Owner { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>远端的默认分支（main / master），取不到为空。</summary>
    public string DefaultBranch { get; init; } = "";

    /// <summary>未提交的改动（含未跟踪的文件）有几项。</summary>
    public int Changed { get; init; }

    public int Ahead { get; init; }
    public int Behind { get; init; }

    public bool IsGitHub => Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>「owner/repo」，没有远端时用目录名。</summary>
    public string DisplayName => Owner.Length > 0 ? $"{Owner}/{Name}" : Path.GetFileName(Root.TrimEnd('\\', '/'));

    /// <summary>当前分支在网页上的地址。</summary>
    public string BranchUrl => WebUrl.Length == 0 || Branch.Length == 0 || Detached ? ""
        : Host.Contains("gitlab", StringComparison.OrdinalIgnoreCase) ? $"{WebUrl}/-/tree/{Uri.EscapeDataString(Branch)}"
        : $"{WebUrl}/tree/{Uri.EscapeDataString(Branch)}";
}

/// <summary>
/// 读仓库信息、列分支、建分支、切分支。都按参数列表调 git（不经过 Shell）。
/// 这些是员工在界面上点的操作（不是 AI 调的工具）；切分支时有未提交的改动直接拒绝，不会把员工的改动弄丢。
/// </summary>
public static class GitRepository
{
    public static async Task<RepoInfo?> InfoAsync(string directory, CancellationToken ct)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }
        var top = await GitRunner.RunAsync(directory, new[] { "rev-parse", "--show-toplevel" }, ct);
        if (top.ExitCode != 0)
        {
            return null;
        }
        var root = top.Stdout.Trim();
        var status = await GitRunner.RunAsync(directory, new[] { "status", "--porcelain=v1", "--branch" }, ct);
        var (branch, detached, ahead, behind, changed) = ParseStatus(status.Stdout);
        var remote = await GitRunner.RunAsync(directory, new[] { "remote", "get-url", "origin" }, ct);
        if (remote.ExitCode != 0)
        {
            var remotes = await GitRunner.RunAsync(directory, new[] { "remote" }, ct);
            var first = remotes.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            remote = first is null ? remote : await GitRunner.RunAsync(directory, new[] { "remote", "get-url", first }, ct);
        }
        var url = remote.ExitCode == 0 ? StripCredentials(remote.Stdout.Trim()) : "";
        var parsed = ParseRemote(url);
        var head = await GitRunner.RunAsync(directory, new[] { "symbolic-ref", "--short", "-q", "refs/remotes/origin/HEAD" }, ct);
        var defaultBranch = head.ExitCode == 0 ? head.Stdout.Trim().Split('/', 2).Last() : "";
        return new RepoInfo
        {
            Root = root,
            Branch = branch,
            Detached = detached,
            RemoteUrl = url,
            Host = parsed?.Host ?? "",
            Owner = parsed?.Owner ?? "",
            Name = parsed?.Name ?? "",
            WebUrl = parsed is { } p ? $"https://{p.Host}/{p.Owner}/{p.Name}" : "",
            DefaultBranch = defaultBranch,
            Changed = changed,
            Ahead = ahead,
            Behind = behind,
        };
    }

    internal static (string Branch, bool Detached, int Ahead, int Behind, int Changed) ParseStatus(string porcelain)
    {
        var lines = porcelain.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var branch = "";
        var detached = false;
        int ahead = 0, behind = 0;
        if (lines.Length > 0 && lines[0].StartsWith("## ", StringComparison.Ordinal))
        {
            if (lines[0].StartsWith("## HEAD (no branch)", StringComparison.Ordinal))
            {
                detached = true;
            }
            else
            {
                // 「## main...origin/main [ahead 1, behind 2]」「## No commits yet on main」「## feature/x」
                var rest = lines[0][3..].Trim();
                foreach (var prefix in new[] { "No commits yet on ", "Initial commit on " })
                {
                    if (rest.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        rest = rest[prefix.Length..];
                    }
                }
                var track = "";
                var bracket = rest.IndexOf(" [", StringComparison.Ordinal);
                if (bracket >= 0)
                {
                    track = rest[(bracket + 2)..].TrimEnd(']');
                    rest = rest[..bracket];
                }
                var dots = rest.IndexOf("...", StringComparison.Ordinal);
                branch = dots >= 0 ? rest[..dots] : rest;
                foreach (var part in track.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    var bits = part.Split(' ');
                    if (bits.Length == 2 && int.TryParse(bits[1], out var n))
                    {
                        if (bits[0] == "ahead")
                        {
                            ahead = n;
                        }
                        else if (bits[0] == "behind")
                        {
                            behind = n;
                        }
                    }
                }
            }
        }
        var changed = lines.Count(l => !l.StartsWith("## ", StringComparison.Ordinal));
        return (branch, detached, ahead, behind, changed);
    }

    /// <summary>远端地址里的「用户名:令牌@」去掉：这串地址会显示在界面上。</summary>
    public static string StripCredentials(string url) =>
        Regex.Replace(url, @"^(?<scheme>[a-z][a-z0-9+.\-]*://)[^/@\s]+@", "${scheme}", RegexOptions.IgnoreCase);

    /// <summary>认出 https、ssh（git@host:owner/repo）两种写法里的主机、owner、仓库名。</summary>
    public static (string Host, string Owner, string Name)? ParseRemote(string url)
    {
        if (url.Length == 0)
        {
            return null;
        }
        var m = Regex.Match(url, @"^(?:[a-z][a-z0-9+.\-]*://)?(?:[^@/\s]+@)?(?<host>[A-Za-z0-9.\-]+)(?::\d+)?[:/](?<path>[^\s]+?)(?:\.git)?/?$", RegexOptions.IgnoreCase);
        if (!m.Success)
        {
            return null;
        }
        var parts = m.Groups["path"].Value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }
        // GitLab 支持多层组：owner 取除最后一段外的全部
        return (m.Groups["host"].Value.ToLowerInvariant(), string.Join('/', parts[..^1]), parts[^1]);
    }

    /// <summary>本地分支，最近提交过的在前。</summary>
    public static async Task<IReadOnlyList<string>> BranchesAsync(string directory, CancellationToken ct)
    {
        var r = await GitRunner.RunAsync(directory, new[] { "for-each-ref", "--sort=-committerdate", "--format=%(refname:short)", "refs/heads/" }, ct);
        return r.ExitCode == 0
            ? r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(200).ToList()
            : Array.Empty<string>();
    }

    /// <summary>分支名合不合法（按 git 自己的规则）。不允许以 - 开头，免得被当成选项。</summary>
    public static async Task<bool> ValidBranchNameAsync(string directory, string name, CancellationToken ct) =>
        name.Length is > 0 and <= 200 && !name.StartsWith('-')
        && (await GitRunner.RunAsync(directory, new[] { "check-ref-format", "--branch", name }, ct)).ExitCode == 0;

    /// <summary>从当前位置新建分支；checkout=true 时顺便切过去（没提交的改动跟着带过去，不会丢）。</summary>
    public static async Task<(bool Ok, string Message)> CreateBranchAsync(string directory, string name, bool checkout, CancellationToken ct)
    {
        name = name.Trim();
        if (!await ValidBranchNameAsync(directory, name, ct))
        {
            return (false, $"分支名不合法：{name}");
        }
        var r = await GitRunner.RunAsync(directory, checkout ? new[] { "switch", "-c", name } : new[] { "branch", name }, ct);
        return r.ExitCode == 0 ? (true, "") : (false, Error(r));
    }

    /// <summary>切到已有分支。有没提交的改动时拒绝——切过去可能覆盖它们，先提交或暂存。</summary>
    public static async Task<(bool Ok, string Message)> SwitchAsync(string directory, string name, CancellationToken ct)
    {
        name = name.Trim();
        if (!await ValidBranchNameAsync(directory, name, ct))
        {
            return (false, $"分支名不合法：{name}");
        }
        if (await InfoAsync(directory, ct) is { Changed: > 0 } info)
        {
            return (false, $"还有 {info.Changed} 处没提交的改动，先提交或暂存再切换分支");
        }
        var r = await GitRunner.RunAsync(directory, new[] { "switch", name }, ct);
        return r.ExitCode == 0 ? (true, "") : (false, Error(r));
    }

    private static string Error(GitRunner.Result r) => (r.Stderr.Trim().Length > 0 ? r.Stderr : r.Stdout).Trim();
}
