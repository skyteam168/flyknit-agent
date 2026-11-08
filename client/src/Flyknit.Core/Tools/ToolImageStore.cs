using Flyknit.Core.Chat;

namespace Flyknit.Core.Tools;

/// <summary>
/// 把工具结果里的图片存成文件：结果文字里补上保存位置，文件算进这一步的产出（界面上有卡片能打开），
/// 再作为附件挂到工具结果消息上——模型支持识图时 OpenAiSerializer 会把它们发过去。
/// </summary>
public static class ToolImageStore
{
    public static (ToolResult Result, List<Attachment> Attachments) Save(ToolResult result, string? dir, string toolName, string callId)
    {
        var attachments = new List<Attachment>();
        if (result.Images.Count == 0)
        {
            return (result, attachments);
        }
        dir ??= Path.Combine(Path.GetTempPath(), "Flyknit", "tool-outputs");
        var text = result.Output;
        var outputs = result.Outputs.ToList();
        var safeTool = string.Concat(toolName.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_'));
        var safeCall = string.Concat(callId.Where(char.IsLetterOrDigit));
        var n = 0;
        foreach (var image in result.Images)
        {
            n++;
            try
            {
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{safeTool}-{(safeCall.Length > 0 ? safeCall : Guid.NewGuid().ToString("N")[..8])}-{n}{Extension(image.Mime)}");
                File.WriteAllBytes(path, image.Data);
                var saved = image.Marker.Length > 1 && image.Marker.EndsWith(']')
                    ? image.Marker[..^1] + $"，已保存到 {path}]"
                    : $"[图片已保存到 {path}]";
                text = image.Marker.Length > 0 && text.Contains(image.Marker, StringComparison.Ordinal)
                    ? ReplaceFirst(text, image.Marker, saved)
                    : text + "\n" + saved;
                outputs.Add(path);
                attachments.Add(new Attachment { FileName = Path.GetFileName(path), LocalPath = path, Mime = image.Mime, Size = image.Data.Length });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // 存不下来：文字里的那句说明还在，图片丢掉
            }
        }
        return (result with { Output = text, Outputs = outputs, Images = Array.Empty<ToolImage>() }, attachments);
    }

    private static string Extension(string mime) => mime.ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/bmp" => ".bmp",
        "image/svg+xml" => ".svg",
        _ => ".png",
    };

    private static string ReplaceFirst(string text, string find, string replace)
    {
        var i = text.IndexOf(find, StringComparison.Ordinal);
        return text[..i] + replace + text[(i + find.Length)..];
    }
}
