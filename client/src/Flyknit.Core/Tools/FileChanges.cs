namespace Flyknit.Core.Tools;

/// <summary>
/// 任务对一个文件做的一次改动：新建、修改、删除。BackupPath 是改动之前那一份的备份（有才能还原）；
/// EmptyBefore 表示改之前是个空文件（没东西可备份，但能还原成空文件）。
/// </summary>
public sealed record FileChange(string Path, string Kind, string? BackupPath, string Source, bool EmptyBefore = false)
{
    public const string Created = "created";
    public const string Modified = "modified";
    public const string Deleted = "deleted";

    /// <summary>能不能自动还原：新建的删掉即可；修改、删除的要有备份（或原来就是空文件）。文件夹删掉了不能自动还原（在回收站里）。</summary>
    public bool Restorable => Kind == Created || BackupPath is not null || EmptyBefore;
}

/// <summary>还原一次任务做的改动的结果。</summary>
public sealed record RevertResult(IReadOnlyList<string> Reverted, IReadOnlyList<(string Path, string Reason)> Skipped);

/// <summary>
/// 把一次任务对文件做的改动退回到任务开始之前。同一个文件改了好几次，以最早那次之前的样子为准。
/// 还原前把现在的样子也备份一份——还原本身也能反悔。新建的文件移到回收站，不直接删除。
/// </summary>
public static class ChangeReverter
{
    public static RevertResult Revert(IEnumerable<FileChange> changes, IFileDeleter deleter, FileBackup? backup, Func<string, bool>? only = null)
    {
        var reverted = new List<string>();
        var skipped = new List<(string, string)>();
        var firsts = changes
            .GroupBy(c => System.IO.Path.GetFullPath(c.Path), StringComparer.OrdinalIgnoreCase)
            .Select(g => (Path: g.Key, First: g.First()));
        foreach (var (path, first) in firsts)
        {
            if (only is not null && !only(path))
            {
                continue;
            }
            try
            {
                if (first.Kind == FileChange.Created)
                {
                    // 任务之前没有这个文件：现在有就移到回收站
                    if (File.Exists(path) || Directory.Exists(path))
                    {
                        deleter.Delete(path);
                    }
                    reverted.Add(path);
                    continue;
                }
                if (first.BackupPath is { } saved)
                {
                    if (!File.Exists(saved))
                    {
                        skipped.Add((path, "备份已经被清理（超过备份容量后最旧的会被删掉）"));
                        continue;
                    }
                    backup?.Capture(path);
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                    File.Copy(saved, path, overwrite: true);
                    reverted.Add(path);
                    continue;
                }
                if (first.EmptyBefore)
                {
                    backup?.Capture(path);
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                    File.WriteAllBytes(path, Array.Empty<byte>());
                    reverted.Add(path);
                    continue;
                }
                skipped.Add((path, first.Kind == FileChange.Deleted
                    ? "删掉的是文件夹或没有备份，请到回收站里还原"
                    : first.Source == "run_shell"
                        ? "这个文件是命令改的，改之前没有备份，无法自动还原"
                        : "文件太大或备份失败，改之前没有备份"));
            }
            catch (Exception ex)
            {
                skipped.Add((path, ex.Message));
            }
        }
        return new RevertResult(reverted, skipped);
    }
}
