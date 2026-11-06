using System.Text;

namespace Flyknit.Core.Tools;

/// <summary>
/// 工具结果太长时不再直接截掉中间：完整内容存成文件，交给模型的是开头、结尾和文件路径，
/// 要看中间的再用 read_file 按行读（同 Claude Code / Codex 的大结果落盘）。
/// 以前截掉的部分就永远看不到了，AI 只能重跑一遍命令、再被截一次。
/// </summary>
public static class ToolOutputSpill
{
    /// <summary>落盘的文件放多久（下次启动时清掉更早的）。</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(7);

    /// <summary>
    /// 超过 <paramref name="maxChars"/> 的结果：写进 <paramref name="dir"/>，返回开头 + 结尾 + 路径。
    /// 没给目录、写不进去时退回原来的截断。read_file 的结果本身就是从文件读的，按行号继续读原文件即可，不再另存。
    /// </summary>
    public static ToolResult Apply(ToolResult result, int maxChars, string? dir, string toolName, string callId)
    {
        if (result.Output.Length <= maxChars || string.IsNullOrEmpty(dir) || toolName == "read_file")
        {
            return result.Truncate(maxChars);
        }
        string path;
        try
        {
            Directory.CreateDirectory(dir);
            var safeTool = string.Concat(toolName.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_'));
            var safeCall = string.Concat(callId.Where(char.IsLetterOrDigit));
            path = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{safeTool}-{(safeCall.Length > 0 ? safeCall : Guid.NewGuid().ToString("N")[..8])}.txt");
            File.WriteAllText(path, result.Output, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return result.Truncate(maxChars);
        }

        var head = result.Output[..(maxChars * 2 / 3)];
        var tail = result.Output[^(maxChars / 4)..];
        var lines = result.Output.Count(c => c == '\n') + 1;
        var note = $"\n\n…（结果太长，中间省略了 {result.Output.Length - head.Length - tail.Length} 个字符。完整结果共 {lines} 行、{result.Output.Length} 个字符，" +
                   $"已保存到 {path}，需要时用 read_file 按 start_line / max_lines 分段读，或用 search_files 在里面找）…\n\n";
        return result with { Output = head + note + tail };
    }

    /// <summary>清掉放了太久的落盘文件。失败不要紧，下次再清。</summary>
    public static void Prune(string dir)
    {
        try
        {
            if (!Directory.Exists(dir))
            {
                return;
            }
            var cutoff = DateTime.Now - KeepFor;
            foreach (var file in Directory.EnumerateFiles(dir, "*.txt", SearchOption.AllDirectories))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
