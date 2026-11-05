using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using Flyknit.Core.Documents;

namespace Flyknit.Client.Services;

/// <summary>
/// 文档画布：AI 写出来的 Markdown 文稿在右侧「文档」标签里打开，员工可以直接改、导出 Word。
///
/// 能在画布里保存的只有「这个对话打开过的文档」：AI 在这个对话里写过 / 改过的，或者员工从这个对话的文件里点开的
/// （AI 改过的文件，或者对话工作区里的文件）。界面不能借保存去写别处的文件。
/// </summary>
public sealed partial class AgentHost
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _documents = new(StringComparer.Ordinal);

    /// <summary>AI 用 write_file / edit_file 写了 Markdown 文稿：记进这个对话的文档，界面在画布里打开或刷新。</summary>
    internal void NoteDocumentWritten(string conversationId, string path, IHostEvents events)
    {
        if (!DocumentCanvas.IsDocument(path) || !File.Exists(path))
        {
            return;
        }
        Register(conversationId, path);
        events.Post(new { type = "document.changed", conversationId, path, name = Path.GetFileName(path), by = "ai" });
    }

    /// <summary>员工从对话的文件里点「在画布中打开」。只认这个对话 AI 改过的文件、或者对话工作区里的文件。</summary>
    public bool OpenDocument(string conversationId, string path)
    {
        if (!DocumentCanvas.IsDocument(path) || !File.Exists(path) || !BelongsTo(conversationId, path))
        {
            return false;
        }
        Register(conversationId, path);
        return true;
    }

    public DocumentSnapshot? LoadDocument(string conversationId, string path) =>
        IsOpen(conversationId, path) ? DocumentCanvas.Load(path) : null;

    public SaveResult SaveDocument(string conversationId, string path, string text, string baseVersion)
    {
        if (!IsOpen(conversationId, path))
        {
            return new SaveResult(false, false, baseVersion, "", "这个文档没有在画布里打开");
        }
        try
        {
            var result = DocumentCanvas.Save(path, text, baseVersion);
            if (result.Ok)
            {
                Log.Info($"员工在画布里保存了文档：{path}");
            }
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"保存文档失败：{path}", ex);
            return new SaveResult(false, false, baseVersion, "", ex.Message);
        }
    }

    public bool IsOpen(string conversationId, string path) =>
        _documents.TryGetValue(conversationId, out var set) && set.ContainsKey(Normalize(path));

    private void Register(string conversationId, string path) =>
        _documents.GetOrAdd(conversationId, _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase))[Normalize(path)] = 0;

    private bool BelongsTo(string conversationId, string path)
    {
        var full = Normalize(path);
        if (FileChanges(conversationId).Any(c => string.Equals(Normalize(c.Change.Path), full, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        var workspace = Store.Get(conversationId)?.Workspace;
        if (string.IsNullOrWhiteSpace(workspace))
        {
            return false;
        }
        var root = Normalize(workspace).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return path;
        }
    }
}
