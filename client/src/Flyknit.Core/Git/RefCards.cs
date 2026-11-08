using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Flyknit.Core.Git;

/// <summary>
/// 聊天里出现的 PR / Issue 链接（<c>https://github.com/owner/repo/pull/12</c>）。
/// 界面把它显示成「#12」，鼠标停上去弹一张卡片：状态、标题、作者、改了多少行、几个文件。
/// </summary>
/// <param name="Kind">pr 或 issue。</param>
public sealed record RefLink(string Host, string Owner, string Repo, string Kind, int Number)
{
    /// <summary>pull / issues 后面跟编号；允许后面再带 /files、#issuecomment-1 之类。</summary>
    private static readonly Regex Pattern = new(
        @"^https://(?<host>[A-Za-z0-9.-]+)/(?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+)/(?<kind>pull|issues)/(?<num>\d{1,9})(?:[/?#].*)?$",
        RegexOptions.CultureInvariant);

    public string Url => $"https://{Host}/{Owner}/{Repo}/{(Kind == "pr" ? "pull" : "issues")}/{Number}";

    /// <summary>认不出（不是 PR / Issue 地址、不是 https）返回 null。只认 gh 能查的 GitHub 格式。</summary>
    public static RefLink? Parse(string? url)
    {
        var m = Pattern.Match((url ?? "").Trim());
        if (!m.Success || !int.TryParse(m.Groups["num"].Value, out var n) || n <= 0)
        {
            return null;
        }
        return new RefLink(
            m.Groups["host"].Value.ToLowerInvariant(),
            m.Groups["owner"].Value,
            m.Groups["repo"].Value,
            m.Groups["kind"].Value == "pull" ? "pr" : "issue",
            n);
    }
}

/// <summary>悬停卡片上显示的内容。State：open / draft / merged / closed。</summary>
public sealed record RefCard
{
    [JsonPropertyName("kind")] public string Kind { get; init; } = "pr";
    [JsonPropertyName("state")] public string State { get; init; } = "open";
    [JsonPropertyName("repo")] public string Repo { get; init; } = "";
    [JsonPropertyName("number")] public int Number { get; init; }
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("author")] public string Author { get; init; } = "";
    [JsonPropertyName("url")] public string Url { get; init; } = "";

    /// <summary>最近一次状态变化的时间：合并 / 关闭的时间，还开着就是创建时间。</summary>
    [JsonPropertyName("at")] public string At { get; init; } = "";
    [JsonPropertyName("additions")] public int Additions { get; init; }
    [JsonPropertyName("deletions")] public int Deletions { get; init; }
    [JsonPropertyName("files")] public int Files { get; init; }
    [JsonPropertyName("comments")] public int Comments { get; init; }

    public const string PrFields = "number,title,url,state,isDraft,author,additions,deletions,changedFiles,createdAt,mergedAt,closedAt";
    public const string IssueFields = "number,title,url,state,author,createdAt,closedAt,comments";

    /// <summary>gh pr view / gh issue view --json 的结果。</summary>
    public static RefCard Parse(RefLink link, JsonElement e)
    {
        string S(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        int I(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
        var raw = S("state").ToUpperInvariant();
        var draft = e.TryGetProperty("isDraft", out var d) && d.ValueKind == JsonValueKind.True;
        var state = raw switch
        {
            "MERGED" => "merged",
            "CLOSED" => "closed",
            _ => draft ? "draft" : "open",
        };
        var at = state switch
        {
            "merged" => S("mergedAt"),
            "closed" => S("closedAt"),
            _ => "",
        };
        var author = e.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.Object
            && a.TryGetProperty("login", out var login) && login.ValueKind == JsonValueKind.String
            ? login.GetString() ?? ""
            : "";
        var comments = e.TryGetProperty("comments", out var c)
            ? c.ValueKind == JsonValueKind.Array ? c.GetArrayLength() : c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var cn) ? cn : 0
            : 0;
        return new RefCard
        {
            Kind = link.Kind,
            State = state,
            Repo = $"{link.Owner}/{link.Repo}",
            Number = I("number") is > 0 and var num ? num : link.Number,
            Title = S("title"),
            Author = author,
            Url = S("url") is { Length: > 0 } u ? u : link.Url,
            At = at.Length > 0 ? at : S("createdAt"),
            Additions = I("additions"),
            Deletions = I("deletions"),
            Files = I("changedFiles"),
            Comments = comments,
        };
    }
}

/// <summary>
/// 查卡片内容，带缓存：同一个链接一会儿内反复悬停只查一次（查不到也记一会儿，免得反复起 gh）。
/// 查询本身由调用方给（宿主里是员工电脑上的 gh），这里不碰网络、不碰令牌。
/// </summary>
public sealed class RefCardCache
{
    private readonly Func<RefLink, CancellationToken, Task<RefCard?>> _fetch;
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<string, (DateTimeOffset Until, Task<RefCard?> Task)> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public static TimeSpan Fresh { get; set; } = TimeSpan.FromMinutes(2);
    public const int MaxItems = 200;

    public RefCardCache(Func<RefLink, CancellationToken, Task<RefCard?>> fetch, Func<DateTimeOffset>? now = null)
    {
        _fetch = fetch;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public Task<RefCard?> GetAsync(RefLink link, CancellationToken ct)
    {
        var key = link.Url;
        lock (_lock)
        {
            var now = _now();
            if (_items.TryGetValue(key, out var hit) && hit.Until > now && !hit.Task.IsFaulted && !hit.Task.IsCanceled)
            {
                return hit.Task;
            }
            if (_items.Count >= MaxItems)
            {
                foreach (var old in _items.Where(kv => kv.Value.Until <= now).Select(kv => kv.Key).ToList())
                {
                    _items.Remove(old);
                }
                if (_items.Count >= MaxItems)
                {
                    _items.Clear();
                }
            }
            var task = SafeFetch(link, ct);
            _items[key] = (now + Fresh, task);
            return task;
        }
    }

    private async Task<RefCard?> SafeFetch(RefLink link, CancellationToken ct)
    {
        try
        {
            return await _fetch(link, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // 没装 gh、没登录、没权限、仓库不存在：卡片不显示，链接照常能点
            return null;
        }
    }
}
