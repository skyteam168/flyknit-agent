using Flyknit.Core.Preview;

namespace Flyknit.Client.Services;

/// <summary>
/// 视频 / 音频预览的流式地址：https://media.flyknit.local/{令牌}。
///
/// 图片、PDF 预览是读成 data URL 塞进消息的；视频动辄几十上百 MB，这样会把界面卡死。
/// 所以预览时只发一个令牌地址，播放时由主窗口拦截请求、按 Range 分段从磁盘读（见 MainWindow.OnMediaRequest）。
/// 令牌只对应预览过的那个文件，页面拼不出任意路径。
/// </summary>
public static class MediaService
{
    public const string Host = "media.flyknit.local";

    public static readonly MediaTokens Tokens = new();

    public static string UrlFor(string path) => $"https://{Host}/{Tokens.Register(path)}";
}
