using System.Text;

namespace Flyknit.Core.Preview;

/// <summary>预览内容的呈现方式。界面按这个决定怎么渲染，而不是自己再判断一次扩展名。</summary>
public enum PreviewKind
{
    /// <summary>纯文本或代码，带 Language 提示。</summary>
    Text,

    /// <summary>Markdown 源码，界面负责渲染。</summary>
    Markdown,

    /// <summary>表格（CSV / Excel 工作表）。</summary>
    Table,

    /// <summary>分节文档（Word 段落、PPT 每页）。</summary>
    Sections,

    /// <summary>图片，内容在 DataUrl 里。</summary>
    Image,

    /// <summary>PDF，内容在 DataUrl 里，交给浏览器内置阅读器。</summary>
    Pdf,

    /// <summary>流程图源码（Mermaid / DOT），界面负责渲染。</summary>
    Diagram,

    /// <summary>压缩包等，只列出条目。</summary>
    Listing,

    /// <summary>无法预览，只给文件信息。</summary>
    None,
}

/// <summary>文档中的一节：Word 的一段、PPT 的一页、Excel 的一张表。</summary>
public sealed record PreviewSection(string Title, string? Text = null, IReadOnlyList<IReadOnlyList<string>>? Rows = null);

public sealed record PreviewDocument(PreviewKind Kind)
{
    public string? Text { get; init; }

    /// <summary>代码高亮用的语言提示（markdown-it / highlight.js 的名字）。</summary>
    public string? Language { get; init; }

    public string? DataUrl { get; init; }
    public IReadOnlyList<PreviewSection> Sections { get; init; } = Array.Empty<PreviewSection>();

    /// <summary>内容被截断时给用户的说明（大文件只读前面一部分）。</summary>
    public string? Notice { get; init; }

    public string? Error { get; init; }

    public static PreviewDocument Unavailable(string reason) => new(PreviewKind.None) { Error = reason };
}

/// <summary>
/// 一类文件的预览实现。加一种新格式 = 加一个 Provider 并注册，不用改界面也不用改调度逻辑。
/// </summary>
public interface IPreviewProvider
{
    /// <summary>诊断用的名字。</summary>
    string Name { get; }

    /// <summary>认领这个文件吗？按扩展名判断即可，注册顺序决定优先级。</summary>
    bool CanHandle(string path);

    Task<PreviewDocument> LoadAsync(string path, CancellationToken ct);
}

/// <summary>
/// 预览调度：按注册顺序找第一个认领的 Provider。
/// 先注册的优先，所以专用的（Office、图片）放前面，兜底的放最后。
/// </summary>
public sealed class PreviewRegistry
{
    /// <summary>超过这个大小就不整file读进内存。</summary>
    public const long MaxBytes = 25 * 1024 * 1024;

    static PreviewRegistry()
    {
        // GBK / GB18030 不在 .NET Core 默认编码表里，要先注册
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch (Exception) { }
    }

    private readonly List<IPreviewProvider> _providers = new();

    public IReadOnlyList<IPreviewProvider> Providers => _providers;

    public PreviewRegistry Register(IPreviewProvider provider)
    {
        _providers.Add(provider);
        return this;
    }

    /// <summary>默认的一套。顺序即优先级。</summary>
    public static PreviewRegistry CreateDefault() => new PreviewRegistry()
        .Register(new ImagePreviewProvider())
        .Register(new PdfPreviewProvider())
        .Register(new OfficePreviewProvider())
        .Register(new ArchivePreviewProvider())
        .Register(new TablePreviewProvider())
        .Register(new TextPreviewProvider())
        .Register(new FileInfoPreviewProvider());

    /// <summary>这个文件能不能预览出内容（最后的兜底 Provider 不算）。</summary>
    public bool CanPreview(string path) =>
        _providers.Any(p => p is not FileInfoPreviewProvider && p.CanHandle(path));

    public async Task<PreviewDocument> LoadAsync(string path, CancellationToken ct = default)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
        }
        catch (Exception ex)
        {
            return PreviewDocument.Unavailable($"路径无效：{ex.Message}");
        }
        if (!info.Exists)
        {
            return PreviewDocument.Unavailable("文件不存在，可能已被移动或删除");
        }
        if (info.Length > MaxBytes)
        {
            return PreviewDocument.Unavailable($"文件有 {Format(info.Length)}，太大了，请用对应的软件打开");
        }

        foreach (var provider in _providers)
        {
            if (!provider.CanHandle(path))
            {
                continue;
            }
            try
            {
                return await provider.LoadAsync(path, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return PreviewDocument.Unavailable($"{provider.Name} 读取失败：{ex.Message}");
            }
        }
        return PreviewDocument.Unavailable("这种格式还不支持预览");
    }

    public static string Format(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };

    /// <summary>读文本：按 BOM 猜编码，没有 BOM 时按 UTF-8 读，失败退回系统默认编码（中文 Windows 是 GBK）。</summary>
    internal static async Task<(string Text, bool Truncated)> ReadTextAsync(string path, int maxChars, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct);
        var text = Decode(bytes);
        return text.Length > maxChars
            ? (text[..maxChars], true)
            : (text, false);
    }

    internal static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // 不是合法 UTF-8：工厂里不少老文件是 GBK / GB18030 存的，依次试过去
            foreach (var codePage in new[] { 0, 936, 54936, 950 })
            {
                try
                {
                    var encoding = Encoding.GetEncoding(codePage);
                    if (encoding.CodePage == Encoding.UTF8.CodePage)
                    {
                        continue; // 系统默认就是 UTF-8 的话，上面已经试过了
                    }
                    return encoding.GetString(bytes);
                }
                catch (Exception)
                {
                    // 这个代码页不可用，试下一个
                }
            }
            return Encoding.UTF8.GetString(bytes); // 实在不行，带替换字符地读出来
        }
    }
}
