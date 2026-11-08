using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Flyknit.Core.Agent;
using Flyknit.Core.Sandbox;
using Flyknit.Core.Storage;
using Flyknit.Core.Tools;

namespace Flyknit.Client.Services;

/// <summary>
/// 沙箱任务（本机 Docker，隔离执行第一期，见 docs/sandbox-execution-design.md）：
/// 员工在开了开发者工具的对话里打开「沙箱」，之后这个对话的读写、命令、git 都在一次性的 Docker 容器里做，不弹确认；
/// 能带出容器的只有出口（推任务分支、建 PR）。一个对话一个容器，程序重启后按容器标签找回来。
/// </summary>
public sealed partial class AgentHost
{
    private readonly DockerSandbox _docker = new();
    private readonly ConcurrentDictionary<string, SandboxSession> _sandboxes = new(StringComparer.Ordinal);
    private string? _dockerProblem = "还没检测";
    private DateTime _dockerCheckedAt = DateTime.MinValue;

    public bool SandboxSwitchOn => SandboxSettings.Enabled(Security, agentMode: true);

    /// <summary>这台电脑现在能不能开沙箱；不能时返回给员工看的原因。检测结果缓存一分钟。</summary>
    public async Task<string?> SandboxProblemAsync()
    {
        if (!SandboxSwitchOn)
        {
            return "IT 没有开启「沙箱任务」";
        }
        if (DateTime.UtcNow - _dockerCheckedAt > TimeSpan.FromMinutes(1))
        {
            _dockerProblem = await _docker.UnavailableReasonAsync(_watchCts.Token);
            _dockerCheckedAt = DateTime.UtcNow;
        }
        return _dockerProblem;
    }

    /// <summary>
    /// 打开 / 关闭这个对话的沙箱。打开时检查：开关、Docker、工作区是 GitHub 仓库；容器等第一次发话时再起。
    /// 关闭时删掉容器——没推出去的改动就没了，界面会先让员工确认。
    /// </summary>
    public async Task<(bool Ok, string Message)> SetSandboxAsync(string conversationId, bool on)
    {
        if (Store.Get(conversationId) is not { } conv)
        {
            return (false, "对话不存在");
        }
        if (IsRunning(conversationId))
        {
            return (false, "任务正在进行，等它结束再切换");
        }
        if (on)
        {
            if (await SandboxProblemAsync() is { } problem)
            {
                return (false, problem);
            }
            if (WorkspaceOf(conversationId) is not { } dir || await Flyknit.Core.Git.GitRepository.InfoAsync(dir, _watchCts.Token) is not { } repo)
            {
                return (false, "沙箱任务要在 git 仓库里做：先把工作区选成一个仓库");
            }
            if (!repo.IsGitHub)
            {
                return (false, "目前只支持 GitHub 上的仓库");
            }
            Store.SetSandbox(conversationId, true);
        }
        else
        {
            Store.SetSandbox(conversationId, false);
            await StopSandboxAsync(conversationId);
        }
        PostSandbox(conversationId, on ? "idle" : "off", "");
        return (true, "");
    }

    /// <summary>这一轮要用的沙箱：已有的接着用（内存里没有就按容器标签找回来），没有就开一个。</summary>
    private async Task<SandboxSession> EnsureSandboxAsync(Conversation conv, IHostEvents events, CancellationToken ct)
    {
        if (_sandboxes.TryGetValue(conv.Id, out var cached))
        {
            return cached;
        }
        if (await SandboxProblemAsync() is { } problem)
        {
            throw new SandboxException(problem);
        }
        var found = await _docker.FindAsync(conv.Id, ct);
        if (found is null)
        {
            var dir = WorkspaceOf(conv.Id) ?? throw new SandboxException("这个对话没有工作区");
            var image = Environment.GetEnvironmentVariable("FLYKNIT_SANDBOX_IMAGE");
            found = await _docker.StartAsync(conv.Id, dir, SandboxSettings.Options(Security, image),
                message => PostSandbox(conv.Id, message.Length > 0 ? "preparing" : "ready", message), ct);
            Log.Info($"对话 {conv.Id} 开了沙箱 {found.Container}（任务分支 {found.TaskBranch}）");
        }
        _sandboxes[conv.Id] = found;
        PostSandbox(conv.Id, "ready", "");
        return found;
    }

    /// <summary>
    /// 沙箱任务这一轮的工具表：本机的文件、命令、git、终端、浏览器、连接器换掉（连接器连的是公司系统，第一期不进沙箱），
    /// 换成在容器里执行的同名工具，再加上出口 create_pull_request。计划、记忆检索、技能、提问、子代理照旧。
    /// </summary>
    private ToolRegistry SandboxRegistry(ToolRegistry tools, SandboxSession session)
    {
        var registry = new ToolRegistry();
        foreach (var tool in tools.All.Where(t => !SandboxToolSet.Replaces.Contains(t.Name)
            && !t.Name.StartsWith("mcp__", StringComparison.Ordinal)
            && !t.Name.StartsWith("browser_", StringComparison.Ordinal)
            && t.Name != Flyknit.Core.Git.CreatePullRequestTool.ToolName
            && t.Name != LoadToolsTool.ToolName))
        {
            registry.Add(tool);
        }
        SandboxExec exec = (command, timeout, ct, stdin) => _docker.ExecAsync(session.Container, command, timeout, ct, stdin);
        foreach (var tool in SandboxToolSet.Create(exec))
        {
            registry.Add(tool);
        }
        registry.Add(new SandboxCreatePullRequest(_docker, () => session, SandboxSettings.Options(Security), PullRequests));
        return registry;
    }

    public async Task StopSandboxAsync(string conversationId)
    {
        _sandboxes.TryRemove(conversationId, out var session);
        try
        {
            var container = session?.Container ?? (await _docker.FindAsync(conversationId, _watchCts.Token))?.Container;
            if (container is not null)
            {
                await _docker.StopAsync(container, _watchCts.Token);
                Log.Info($"对话 {conversationId} 的沙箱 {container} 已删除");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"删除对话 {conversationId} 的沙箱失败", ex);
        }
    }

    /// <summary>启动时清理：对话已经删了、或者关了沙箱的，容器删掉（不然一直占着磁盘）。</summary>
    private async Task CleanupSandboxesAsync()
    {
        try
        {
            if (await SandboxProblemAsync() is not null)
            {
                return;
            }
            foreach (var (name, conversationId, _) in await _docker.ListAsync(_watchCts.Token))
            {
                if (Store.Get(conversationId) is not { Sandbox: true, DeletedAt: null })
                {
                    await _docker.StopAsync(name, _watchCts.Token);
                    Log.Info($"清理了没人用的沙箱 {name}");
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("清理沙箱失败", ex);
        }
    }

    /// <summary>界面上这个对话的沙箱状态。</summary>
    public async Task<object> SandboxStatusAsync(string conversationId)
    {
        var conv = Store.Get(conversationId);
        var problem = await SandboxProblemAsync();
        _sandboxes.TryGetValue(conversationId, out var session);
        return new
        {
            available = problem is null,
            problem = problem ?? "",
            on = conv?.Sandbox ?? false,
            state = session is not null ? "ready" : conv?.Sandbox == true ? "idle" : "off",
            container = session?.Container ?? "",
            branch = session?.TaskBranch ?? "",
            @base = session?.BaseBranch ?? "",
        };
    }

    private void PostSandbox(string conversationId, string state, string message)
    {
        _sandboxes.TryGetValue(conversationId, out var session);
        Events?.Post(new
        {
            type = "sandbox.state",
            conversationId,
            state,
            message,
            container = session?.Container ?? "",
            branch = session?.TaskBranch ?? "",
            @base = session?.BaseBranch ?? "",
        });
    }
}
