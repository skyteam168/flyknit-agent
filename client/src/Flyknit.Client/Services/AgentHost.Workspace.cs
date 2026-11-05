using System;
using Flyknit.Core.Workspace;

namespace Flyknit.Client.Services;

/// <summary>
/// 右侧「文件」标签：浏览、搜索这个对话的工作区。只读，只在工作区目录里（路径由 WorkspaceBrowser 校验，跳不出去）；
/// 点开文件走现有的预览 / 用默认程序打开，加进输入框走附件。
/// </summary>
public sealed partial class AgentHost
{
    /// <summary>这个对话的工作区；没有（对话、翻译模式，或者没选工作区）返回 null。</summary>
    public string? WorkspaceOf(string conversationId) =>
        Store.Get(conversationId)?.Workspace is { Length: > 0 } w && System.IO.Directory.Exists(w) ? w : null;

    public WorkspaceListing ListWorkspace(string conversationId, string relative) =>
        WorkspaceOf(conversationId) is { } root
            ? WorkspaceBrowser.List(root, relative)
            : new WorkspaceListing(Array.Empty<WorkspaceEntry>(), false, Exists: false);

    public WorkspaceSearch SearchWorkspace(string conversationId, string query) =>
        WorkspaceOf(conversationId) is { } root
            ? WorkspaceBrowser.Search(root, query)
            : new WorkspaceSearch(Array.Empty<WorkspaceEntry>(), false);

    /// <summary>工作区里的一个文件的完整路径（加进输入框用）；不在工作区里、不存在返回 null。</summary>
    public string? WorkspaceFile(string conversationId, string relative) =>
        WorkspaceOf(conversationId) is { } root && WorkspaceBrowser.Resolve(root, relative) is { } full && System.IO.File.Exists(full)
            ? full
            : null;
}
