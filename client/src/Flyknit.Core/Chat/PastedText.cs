using System.Text;

namespace Flyknit.Core.Chat;

/// <summary>
/// 员工粘贴的长文本（<see cref="Attachment.Pasted"/>）：发给模型时整段放进消息，而不是只给一个路径。
/// 太长的只放前面一部分，告诉模型全文在哪，「办事」模式下需要时可以用工具读。
/// </summary>
public static class PastedText
{
    /// <summary>一段粘贴文本最多放进消息的字数，再多就截断。</summary>
    public const int MaxInlineChars = 60_000;

    public sealed record Content(string Text, int Lines, bool Truncated);

    /// <summary>读出粘贴的文本；文件不在了或读不了返回 null。</summary>
    public static Content? Read(Attachment a)
    {
        try
        {
            if (!File.Exists(a.LocalPath))
            {
                return null;
            }
            // 只读前面够用的部分，避免几十 MB 的文本整个读进内存
            using var reader = new StreamReader(a.LocalPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[MaxInlineChars + 1];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            var truncated = read > MaxInlineChars;
            var text = new string(buffer, 0, Math.Min(read, MaxInlineChars)).Replace("\r\n", "\n");
            return new Content(text, text.Count(c => c == '\n') + 1, truncated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>放进消息里的那一段；读不到返回 null，由调用方当普通附件处理。</summary>
    public static string? Block(Attachment a)
    {
        if (Read(a) is not { } c)
        {
            return null;
        }
        var sb = new StringBuilder();
        sb.Append($"<粘贴的文本 名称=\"{a.FileName}\">\n");
        sb.Append(c.Text.TrimEnd('\n'));
        sb.Append("\n</粘贴的文本>");
        if (c.Truncated)
        {
            sb.Append($"\n（这段文本太长，上面只放了前 {MaxInlineChars} 字；全文在 {a.LocalPath}，需要时用工具读取）");
        }
        return sb.ToString();
    }
}
