using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Flyknit.Core.Hosting;

namespace Flyknit.Core.Notices;

/// <summary>一条通知（服务端 /api/v1/client/notices）。列表里 BodyHtml 为空，详情里才有。</summary>
public sealed class Notice
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "general";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
    [JsonPropertyName("important")] public bool Important { get; set; }
    [JsonPropertyName("action_label")] public string ActionLabel { get; set; } = "";
    [JsonPropertyName("action_url")] public string ActionUrl { get; set; } = "";
    [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; set; }
    [JsonPropertyName("expire_at")] public DateTimeOffset? ExpireAt { get; set; }
    [JsonPropertyName("read")] public bool Read { get; set; }
    [JsonPropertyName("body_html")] public string BodyHtml { get; set; } = "";
}

public sealed class NoticeList
{
    [JsonPropertyName("items")] public List<Notice> Items { get; set; } = new();
    [JsonPropertyName("unread")] public int Unread { get; set; }
}

/// <summary>代理给界面的媒体响应（图片、视频）。Body 由调用方负责释放。</summary>
public sealed record NoticeMediaResponse(int Status, string ContentType, Stream? Body, long? Length, string? ContentRange);

/// <summary>通知的服务端接口（由 <see cref="Gateway.FlyknitServerClient"/> 实现；范围、发布状态、正文清洗都在服务端）。</summary>
public interface INoticeService
{
    /// <summary>这台电脑能看到的通知。服务端没有这个接口（老版本）时返回 null。</summary>
    Task<NoticeList?> GetNoticesAsync(CancellationToken ct);

    /// <summary>详情（带正文）。不存在、撤回了、不在范围内时返回 null。</summary>
    Task<Notice?> GetNoticeAsync(string id, CancellationToken ct);

    Task MarkNoticeReadAsync(string id, CancellationToken ct);

    Task MarkAllNoticesReadAsync(CancellationToken ct);

    /// <summary>取通知里的图片、视频（带设备令牌）；range 原样转给服务端，视频可以拖进度条。</summary>
    Task<NoticeMediaResponse> OpenNoticeMediaAsync(string mediaId, string? range, CancellationToken ct);
}

/// <summary>
/// 消息通知（docs/notices.md）：界面桥模块 + 未读数刷新。
///
/// - 界面方法：notices.list / notices.get / notices.read / notices.readAll；
/// - 宿主每次拉配置（IT 发布、撤回通知时服务端会推「配置变了」）顺带调 <see cref="RefreshAsync"/>，
///   未读数变了推 notices.changed；有没读过的重要通知时带上它的 ID，界面在打开时弹出来（每条只弹一次）；
/// - 正文里的媒体地址换成 https://notices.flyknit.local/{id}，由宿主代理到服务端（带设备令牌）。
///   界面拿不到令牌，也不直接连服务器（服务器可能是 http，WebView 里会被当成混合内容拦掉）。
/// </summary>
public sealed class NoticeFeature : IBridgeModule
{
    public const string MediaHost = "notices.flyknit.local";

    private static readonly Regex MediaSrc = new(@"(src|poster)=""/api/v1/notices/media/([0-9a-f]{32})""", RegexOptions.Compiled);
    private static readonly Regex MediaId = new("^[0-9a-f]{32}$", RegexOptions.Compiled);

    private readonly INoticeService _server;
    private readonly Action<object> _post;
    private readonly Action<string> _warn;
    private readonly HashSet<string> _popped = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private int _lastUnread = -1;
    private bool _supported = true;

    public NoticeFeature(INoticeService server, Action<object> post, Action<string> warn)
    {
        _server = server;
        _post = post;
        _warn = warn;
    }

    /// <summary>服务端支持通知（老服务端没有这个接口时界面不显示铃铛）。</summary>
    public bool Supported => _supported;

    public int Unread => Math.Max(0, _lastUnread);

    public IReadOnlyCollection<string> Methods { get; } = new[] { "notices.list", "notices.get", "notices.read", "notices.readAll", "notices.status" };

    public async Task<object?> HandleAsync(string method, BridgeArgs args, CancellationToken ct)
    {
        switch (method)
        {
            case "notices.status":
                return new { supported = _supported, unread = Unread };
            case "notices.list":
            {
                var list = await _server.GetNoticesAsync(ct);
                _supported = list is not null;
                list ??= new NoticeList();
                Publish(list);
                return list;
            }
            case "notices.get":
            {
                var notice = await _server.GetNoticeAsync(args.Str("id"), ct);
                if (notice is null)
                {
                    return null;
                }
                notice.BodyHtml = RewriteMedia(notice.BodyHtml);
                return notice;
            }
            case "notices.read":
                await _server.MarkNoticeReadAsync(args.Str("id"), ct);
                await RefreshAsync(ct);
                return true;
            case "notices.readAll":
                await _server.MarkAllNoticesReadAsync(ct);
                await RefreshAsync(ct);
                return true;
            default:
                return null;
        }
    }

    /// <summary>拉一次列表，未读数变了（或有该弹出的重要通知）就推给界面。出错只记日志。</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            var list = await _server.GetNoticesAsync(ct);
            _supported = list is not null;
            Publish(list ?? new NoticeList());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _warn($"刷新通知失败：{ex.Message}");
        }
    }

    private void Publish(NoticeList list)
    {
        string? popup = null;
        bool changed;
        lock (_gate)
        {
            var pop = list.Items.FirstOrDefault(n => n.Important && !n.Read && !_popped.Contains(n.Id));
            if (pop is not null)
            {
                _popped.Add(pop.Id);
                popup = pop.Id;
            }
            changed = list.Unread != _lastUnread || popup is not null;
            _lastUnread = list.Unread;
        }
        if (changed)
        {
            _post(new { type = "notices.changed", unread = list.Unread, supported = _supported, popup });
        }
    }

    /// <summary>正文里的服务端媒体地址 → 宿主代理地址。别的地址服务端已经清洗掉了，这里不再认。</summary>
    public static string RewriteMedia(string html) =>
        MediaSrc.Replace(html ?? "", m => $"{m.Groups[1].Value}=\"https://{MediaHost}/{m.Groups[2].Value}\"");

    /// <summary>宿主收到 https://notices.flyknit.local/{id} 时调用。ID 不对直接 404，不往服务端发。</summary>
    public Task<NoticeMediaResponse> OpenMediaAsync(string mediaId, string? range, CancellationToken ct) =>
        MediaId.IsMatch(mediaId ?? "")
            ? _server.OpenNoticeMediaAsync(mediaId!, range, ct)
            : Task.FromResult(new NoticeMediaResponse(404, "text/plain", null, null, null));
}
