using System.Text.Json;
using System.Text.Json.Nodes;
using Flyknit.Core.Agent;
using Flyknit.Core.Agent.Extensions;
using Flyknit.Core.Security;
using Flyknit.Core.Storage;
using Flyknit.Core.Tools;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>运行扩展管线（docs/extension-architecture.md）：出场、合并、互斥、出错隔离、收尾。</summary>
public class RunExtensionTests
{
    private sealed class Ext(string id, Func<RunContribution?> prepare, params string[] excludes) : IRunExtension<RunScope>
    {
        public int Calls;
        public string Id => id;
        public IReadOnlyCollection<string> Excludes => excludes;

        public ValueTask<RunContribution?> PrepareAsync(RunScope scope, CancellationToken ct)
        {
            Calls++;
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(prepare());
        }
    }

    private sealed class Named(string name) : ITool
    {
        public string Name => name;
        public string Description => "";
        public JsonObject Parameters => new();
        public PolicyDecision Assess(JsonElement args, ToolContext ctx) => PolicyDecision.Auto();
        public string Describe(JsonElement args) => name;
        public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct) => Task.FromResult(ToolResult.Success(""));
    }

    private sealed class Allow : IConfirmationHandler
    {
        public Task<ConfirmChoice> ConfirmAsync(ConfirmRequest request, CancellationToken ct) => Task.FromResult(ConfirmChoice.AllowOnce);
    }

    private static RunScope Scope() => new()
    {
        Conversation = new Conversation(),
        Query = "查一下 10 月延期",
        RunId = "r1",
        Scene = "agent",
        Confirm = new Allow(),
    };

    [Fact]
    public async Task NoActiveExtensionAddsNothing()
    {
        var pipeline = new RunExtensionPipeline<RunScope>(new[] { new Ext("a", () => null) });
        using var assembly = await pipeline.PrepareAsync(Scope(), CancellationToken.None);
        Assert.Empty(assembly.ActiveIds);
        Assert.Equal("", assembly.SystemPrompt);
        Assert.Equal("", assembly.TurnContext);
        Assert.Empty(assembly.Tools);
        Assert.Null(assembly.ModelId);
        Assert.Null(assembly.MaxRunTokens);
    }

    [Fact]
    public async Task ContributionsAreMergedInOrder()
    {
        var pipeline = new RunExtensionPipeline<RunScope>(new[]
        {
            new Ext("a", () => new RunContribution { SystemPrompt = "A", Tools = new[] { new Named("t1") }, MaxRunTokens = 500 }),
            new Ext("b", () => new RunContribution { SystemPrompt = "B", TurnContext = "ctx", Tools = new[] { new Named("t2") }, ModelId = 7, MaxRunTokens = 300 }),
            new Ext("c", () => new RunContribution { ModelId = 9 }),
        });
        using var assembly = await pipeline.PrepareAsync(Scope(), CancellationToken.None);
        Assert.Equal(new[] { "a", "b", "c" }, assembly.ActiveIds);
        Assert.Equal("A\n\nB", assembly.SystemPrompt);
        Assert.Equal("ctx", assembly.TurnContext);
        Assert.Equal(new[] { "t1", "t2" }, assembly.Tools.Select(t => t.Name));
        Assert.Equal(7, assembly.ModelId);             // 第一个给了模型的
        Assert.Equal(300, assembly.MaxRunTokens);      // 最严的上限
    }

    [Fact]
    public async Task AnActiveExtensionKeepsExcludedOnesFromBeingAsked()
    {
        var later = new Ext("delegation", () => new RunContribution { SystemPrompt = "派遣" });
        var pipeline = new RunExtensionPipeline<RunScope>(new[]
        {
            new Ext("team", () => new RunContribution { SystemPrompt = "队长" }, "delegation"),
            later,
        });
        using var assembly = await pipeline.PrepareAsync(Scope(), CancellationToken.None);
        Assert.Equal(new[] { "team" }, assembly.ActiveIds);
        Assert.Equal(0, later.Calls);
    }

    [Fact]
    public async Task AnInactiveExtensionExcludesNothing()
    {
        var pipeline = new RunExtensionPipeline<RunScope>(new[]
        {
            new Ext("team", () => null, "delegation"),
            new Ext("delegation", () => new RunContribution { SystemPrompt = "派遣" }),
        });
        using var assembly = await pipeline.PrepareAsync(Scope(), CancellationToken.None);
        Assert.Equal(new[] { "delegation" }, assembly.ActiveIds);
    }

    [Fact]
    public async Task ALaterExtensionCanDropAnEarlierOneAndTheDroppedOneIsCleanedUp()
    {
        var ended = 0;
        var pipeline = new RunExtensionPipeline<RunScope>(new[]
        {
            new Ext("subagents", () => new RunContribution { Tools = new[] { new Named("spawn_agent") }, OnEnd = () => ended++ }),
            new Ext("team", () => new RunContribution { SystemPrompt = "队长" }, "subagents"),
        });
        using var assembly = await pipeline.PrepareAsync(Scope(), CancellationToken.None);
        Assert.Equal(new[] { "team" }, assembly.ActiveIds);
        Assert.Empty(assembly.Tools);
        Assert.Equal(1, ended);
    }

    [Fact]
    public async Task AFailingExtensionIsSkippedAndReported()
    {
        var errors = new List<string>();
        var pipeline = new RunExtensionPipeline<RunScope>(new[]
        {
            new Ext("broken", () => throw new InvalidOperationException("坏了")),
            new Ext("ok", () => new RunContribution { SystemPrompt = "OK" }),
        }, (id, ex) => errors.Add($"{id}:{ex.Message}"));
        using var assembly = await pipeline.PrepareAsync(Scope(), CancellationToken.None);
        Assert.Equal(new[] { "ok" }, assembly.ActiveIds);
        Assert.Equal(new[] { "broken:坏了" }, errors);
    }

    [Fact]
    public async Task StoppingTheTaskPropagatesAndCleansUpWhatWasPrepared()
    {
        var ended = 0;
        using var cts = new CancellationTokenSource();
        var pipeline = new RunExtensionPipeline<RunScope>(new IRunExtension<RunScope>[]
        {
            new Ext("first", () =>
            {
                cts.Cancel();
                return new RunContribution { OnEnd = () => ended++ };
            }),
            new Ext("second", () => new RunContribution()),
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipeline.PrepareAsync(Scope(), cts.Token));
        Assert.Equal(1, ended);
    }

    [Fact]
    public async Task EndRunsEveryCleanupOnceEvenIfOneThrows()
    {
        var ended = new List<string>();
        var errors = 0;
        var pipeline = new RunExtensionPipeline<RunScope>(new[]
        {
            new Ext("a", () => new RunContribution { OnEnd = () => throw new InvalidOperationException() }),
            new Ext("b", () => new RunContribution { OnEnd = () => ended.Add("b") }),
        }, (_, _) => errors++);
        var assembly = await pipeline.PrepareAsync(Scope(), CancellationToken.None);
        assembly.Dispose();
        assembly.Dispose();
        Assert.Equal(new[] { "b" }, ended);
        Assert.Equal(1, errors);
    }

    [Fact]
    public void DuplicateIdsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new RunExtensionPipeline<RunScope>(new[]
        {
            new Ext("team", () => null),
            new Ext("team", () => null),
        }));
    }

    [Fact]
    public async Task ExtensionsCanOnlyNarrowToolsAndPermission()
    {
        var pipeline = new RunExtensionPipeline<RunScope>(new[]
        {
            new Ext("a", () => new RunContribution { AllowTool = t => t.Name != "run_shell", CapPermission = _ => Security.PermissionMode.Full }),
            new Ext("b", () => new RunContribution { CapPermission = _ => Security.PermissionMode.Workspace }),
        });
        using var assembly = await pipeline.PrepareAsync(Scope(), CancellationToken.None);
        Assert.True(assembly.FiltersTools);
        Assert.False(assembly.AllowsTool(new Named("run_shell")));
        Assert.True(assembly.AllowsTool(new Named("read_file")));
        // 扩展给了更宽的权限也不认：只读还是只读
        Assert.Equal(Security.PermissionMode.ReadOnly, assembly.CapPermission(Security.PermissionMode.ReadOnly));
        Assert.Equal(Security.PermissionMode.Workspace, assembly.CapPermission(Security.PermissionMode.Full));

        using var none = await new RunExtensionPipeline<RunScope>(new[] { new Ext("c", () => new RunContribution()) })
            .PrepareAsync(Scope(), CancellationToken.None);
        Assert.False(none.FiltersTools);
        Assert.Equal(Security.PermissionMode.Full, none.CapPermission(Security.PermissionMode.Full));
    }
}
