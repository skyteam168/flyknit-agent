using System.Text;
using Flyknit.Core.Documents;
using Flyknit.Core.Hosting;
using Flyknit.Core.Workspace;

namespace Flyknit.Core.Code;

/// <summary>
/// 编码模式（右侧「代码」标签，Monaco 编辑器）：员工在对话的工作区里看代码、自己改、保存；AI 用 write_file / edit_file
/// 改了文件时界面收到 code.changed，在编辑器里显示改动对比。
///
/// 读写都只在这个对话的工作区里（和「文件」标签一样）：
/// - 相对路径解析后必须还在工作区下（..\、绝对路径、别的盘一律不认）；
/// - 写之前再看一遍真实位置：路径上有符号链接 / 目录联接指到工作区外面的，不写（防借链接改到别处）；
/// - 二进制文件、太大的文件只读不改（编辑器会卡，按文本存回去还会改坏）。
/// 保存带打开时的版本号：期间被 AI 改过就不覆盖，把现在的内容交回界面让员工决定（沿用文档画布的做法）。
/// 这是员工自己在自己的工作区里改文件，和在记事本里改一样，不走 AI 的命令确认；每次保存记日志。
/// </summary>
public sealed class CodeFeature : IBridgeModule
{
    /// <summary>编辑器里最多打开这么大的文件（字符数）。</summary>
    public const int MaxChars = 2_000_000;

    private readonly Func<string, string?> _workspaceOf;
    private readonly Action<string> _log;

    /// <param name="workspaceOf">对话 id → 工作区目录（没有工作区返回 null）。</param>
    /// <param name="log">保存记录（只记路径和大小，不记内容）。</param>
    public CodeFeature(Func<string, string?> workspaceOf, Action<string> log)
    {
        _workspaceOf = workspaceOf;
        _log = log;
    }

    public IReadOnlyCollection<string> Methods { get; } = new[] { "code.load", "code.save" };

    public async Task<object?> HandleAsync(string method, BridgeArgs args, CancellationToken ct)
    {
        var conversation = args.Str("conversationId");
        var path = args.Str("path");
        return method switch
        {
            "code.load" => await Task.Run(() => Load(conversation, path), ct),
            "code.save" => await Task.Run(() => Save(conversation, path, args.Str("text"), args.Str("version")), ct),
            _ => null,
        };
    }

    public CodeFile Load(string conversationId, string relative)
    {
        var root = _workspaceOf(conversationId);
        var full = root is null ? null : WorkspaceBrowser.Resolve(root, relative);
        if (root is null || full is null || Directory.Exists(full))
        {
            return CodeFile.Missing(relative, "不在这个对话的工作区里");
        }
        var rel = Path.GetRelativePath(root, full);
        if (!File.Exists(full))
        {
            return new CodeFile(rel, full, "", "", Exists: false, ReadOnly: false, Reason: "");
        }
        try
        {
            var info = new FileInfo(full);
            if (info.Length > MaxChars * 4L)
            {
                return new CodeFile(rel, full, "", "", true, ReadOnly: true, Reason: "tooLarge");
            }
            if (LooksBinary(full))
            {
                return new CodeFile(rel, full, "", "", true, ReadOnly: true, Reason: "binary");
            }
            var snapshot = DocumentCanvas.Load(full);
            if (snapshot.Text.Length > MaxChars || snapshot.TooLarge)
            {
                return new CodeFile(rel, full, "", snapshot.Version, true, ReadOnly: true, Reason: "tooLarge");
            }
            // 工作区外的链接目标：能看（和「文件」标签一样），不能存
            var escapes = !StaysInside(root, full);
            return new CodeFile(rel, full, snapshot.Text, snapshot.Version, true, ReadOnly: escapes, Reason: escapes ? "outside" : "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CodeFile.Missing(rel, ex.Message);
        }
    }

    public SaveResult Save(string conversationId, string relative, string text, string baseVersion)
    {
        var root = _workspaceOf(conversationId);
        var full = root is null ? null : WorkspaceBrowser.Resolve(root, relative);
        if (root is null || full is null || Directory.Exists(full) || Path.GetFileName(full).Length == 0)
        {
            return new SaveResult(false, false, baseVersion, "", "不在这个对话的工作区里，不能保存");
        }
        if (!StaysInside(root, full))
        {
            return new SaveResult(false, false, baseVersion, "", "这个文件通过链接指到了工作区外面，不能在这里保存");
        }
        if (text.Length > MaxChars)
        {
            return new SaveResult(false, false, baseVersion, "", "文件太大，保存不了");
        }
        if (File.Exists(full) && LooksBinary(full))
        {
            return new SaveResult(false, false, baseVersion, "", "这是二进制文件，不能按文本保存");
        }
        try
        {
            var result = DocumentCanvas.Save(full, text, baseVersion);
            if (result.Ok)
            {
                _log($"员工在代码编辑器里保存了 {full}（{Encoding.UTF8.GetByteCount(text)} 字节）");
            }
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SaveResult(false, false, baseVersion, "", ex.Message);
        }
    }

    /// <summary>
    /// 路径上每一段（从工作区往下）都不能是指到工作区外面的链接。文件本身是链接时看它的最终目标。
    /// 还不存在的部分跳过（新建文件）。
    /// </summary>
    public static bool StaysInside(string root, string full)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var current = rootFull;
        var rest = Path.GetRelativePath(rootFull, full).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in rest)
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (!info.Exists || info.LinkTarget is null)
            {
                continue;
            }
            var target = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            if (target is null || !Inside(rootFull, target))
            {
                return false;
            }
        }
        return true;
    }

    private static bool Inside(string rootFull, string path)
    {
        var rel = Path.GetRelativePath(rootFull, Path.GetFullPath(path));
        return !Path.IsPathRooted(rel) && rel != ".." && !rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>开头 8KB 里有 0 字节就当二进制（图片、压缩包、Office 文件、编译产物）。</summary>
    public static bool LooksBinary(string path)
    {
        using var stream = File.OpenRead(path);
        var buffer = new byte[8192];
        var read = stream.Read(buffer, 0, buffer.Length);
        // UTF-16 文本有 BOM，里面的 0 字节不算
        if (read >= 2 && ((buffer[0] == 0xFF && buffer[1] == 0xFE) || (buffer[0] == 0xFE && buffer[1] == 0xFF)))
        {
            return false;
        }
        return Array.IndexOf(buffer, (byte)0, 0, read) >= 0;
    }
}

/// <param name="Path">工作区里的相对路径。</param>
/// <param name="FullPath">完整路径（界面拿它对 AI 改动的通知）。</param>
/// <param name="ReadOnly">只能看不能存（二进制、太大、链接指到工作区外）。</param>
/// <param name="Reason">只读或打不开的原因：binary / tooLarge / outside，或者一句错误说明。</param>
public sealed record CodeFile(string Path, string FullPath, string Text, string Version, bool Exists, bool ReadOnly, string Reason)
{
    public static CodeFile Missing(string path, string reason) => new(path, "", "", "", false, true, reason);
}
