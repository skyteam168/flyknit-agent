using System;
using System.Collections.Generic;
using System.Linq;
using Flyknit.Core.Storage;
using Flyknit.Core.Tools;

namespace Flyknit.Client.Services;

/// <summary>
/// 每一轮任务对文件的改动：工具改了文件就记下来（新建 / 修改 / 删除、改之前的备份在哪），
/// 任务结束后员工能看到改了哪些、对比前后、一键还原。
/// </summary>
public sealed partial class AgentHost
{
    /// <summary>给工具的改动记录器：记到这个对话、这一轮（引起这一轮的用户消息）下面。</summary>
    private Action<FileChange> ChangeRecorder(string conversationId, string turnId) => change =>
    {
        try
        {
            Store.AddFileChange(conversationId, turnId, change);
        }
        catch (Exception ex)
        {
            Log.Warn($"记录文件改动失败：{change.Path}", ex);
        }
    };

    public IReadOnlyList<StoredFileChange> FileChanges(string conversationId) => Store.FileChanges(conversationId);

    /// <summary>
    /// 把某一轮对文件的改动退回到这一轮开始之前。paths 为空表示这一轮的全部。
    /// 任务还在跑时不能还原（它可能马上又改）。
    /// </summary>
    public RevertResult RevertTurn(string conversationId, string turnId, IReadOnlyCollection<string>? paths)
    {
        if (IsRunning(conversationId))
        {
            throw new InvalidOperationException("任务还在进行，先停止再还原");
        }
        var changes = Store.FileChanges(conversationId)
            .Where(c => c.TurnId == turnId && c.RevertedAt is null)
            .Select(c => c.Change)
            .ToList();
        var wanted = paths is { Count: > 0 } ? new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase) : null;
        var deleter = Security.On(Flyknit.Core.Security.SecuritySettings.DeleteProtection) ? (IFileDeleter)new Flyknit.Client.Tools.RecycleBinDeleter() : new PermanentDeleter();
        var result = ChangeReverter.Revert(changes, deleter, _backup, wanted is null ? null : p => wanted.Contains(p));
        Store.MarkChangesReverted(conversationId, turnId, result.Reverted);
        Log.Info($"还原对话 {conversationId} 第 {turnId} 轮的改动：还原 {result.Reverted.Count} 个，跳过 {result.Skipped.Count} 个");
        return result;
    }
}
