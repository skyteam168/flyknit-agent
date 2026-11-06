using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Flyknit.Core.Commands;

namespace Flyknit.Client.Services;

/// <summary>
/// 快捷指令：IT 在后台发布的 + 员工自己加的（存本机）。界面在输入框里打 / 时列出来，展开模板后照常发送。
/// 连不上服务器时用上次取到的 IT 指令。
/// </summary>
public sealed partial class AgentHost
{
    private SlashCommandStore? _commandStore;
    private IReadOnlyList<SlashCommand> _itCommands = Array.Empty<SlashCommand>();

    private SlashCommandStore CommandStore => _commandStore ??= new SlashCommandStore(Path.Combine(AppPaths.Root, "commands.json"));

    public async Task<(IReadOnlyList<SlashCommand> It, IReadOnlyList<SlashCommand> Mine)> ListCommandsAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            _itCommands = await Server.GetCommandsAsync(cts.Token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 服务器连不上、旧版服务端没有这个接口：用上次的
        }
        return (_itCommands, CommandStore.Load());
    }

    /// <summary>
    /// 这台电脑范围内的插件（IT 按部门下发）。只读展示：插件里的技能、专家、连接器、快捷指令、工具规则
    /// 已经由服务端合并进各自的接口，这里不再单独生效一遍。连不上时返回 null。
    /// </summary>
    public async Task<System.Text.Json.Nodes.JsonArray?> ListPluginsAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return await Server.GetPluginsAsync(cts.Token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>保存员工自己的指令。不合规时返回原因。</summary>
    public string? SaveMyCommands(IReadOnlyList<SlashCommand> commands) => CommandStore.Save(commands);
}
