using Flyknit.Core.Security;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>钩子（docs/hooks.md）：只能收紧、只问匹配的、连不上按 on_error、参数先打码。</summary>
public sealed class ToolHooksTests
{
    /// <summary>假的服务端：按工具名回答，记下问过什么、通知过什么。</summary>
    public sealed class FakeHookClient : IHookClient
    {
        public Func<HookCall, HookVerdict> Answer { get; set; } = _ => HookVerdict.Allow;
        public bool Fail { get; set; }
        public List<HookCall> Checked { get; } = new();
        public List<(string Event, HookCall Call, bool Ok, string Summary, string Status)> Notified { get; } = new();

        public Task<HookVerdict> CheckAsync(HookCall call, CancellationToken ct)
        {
            Checked.Add(call);
            return Fail ? throw new HttpRequestException("连不上") : Task.FromResult(Answer(call));
        }

        public Task NotifyAsync(string hookEvent, HookCall call, bool ok, string summary, string status, CancellationToken ct)
        {
            lock (Notified)
            {
                Notified.Add((hookEvent, call, ok, summary, status));
            }
            return Task.CompletedTask;
        }
    }

    private static HookCall Call(string tool, string args = "{}") => new(tool, args, "c1", "r1", "t1", "");

    [Fact]
    public void TightenNeverLoosens()
    {
        var auto = PolicyDecision.Auto() with { Effect = CommandEffect.Write };
        var confirm = PolicyDecision.Confirm("要确认") with { Effect = CommandEffect.Write };
        var blocked = PolicyDecision.Blocked("不允许");

        // allow：什么都不变（本来要确认的照样要确认）
        Assert.Equal(auto, ToolHooks.Tighten(auto, HookVerdict.Allow));
        Assert.Equal(confirm, ToolHooks.Tighten(confirm, HookVerdict.Allow));
        // 本来拦下的，钩子说什么都还是拦下
        foreach (var d in Enum.GetValues<HookDecision>())
        {
            Assert.Equal(RiskLevel.Blocked, ToolHooks.Tighten(blocked, new HookVerdict(d)).Level);
        }
        // ask：自动的变成要确认，而且不能被「以后自动」「本次都允许」跳过
        var asked = ToolHooks.Tighten(auto, new HookVerdict(HookDecision.Ask, "建单要主管确认"));
        Assert.Equal(RiskLevel.Confirm, asked.Level);
        Assert.True(asked.AlwaysAsk);
        Assert.False(asked.Rememberable);
        Assert.Equal("建单要主管确认", asked.Reason);
        Assert.Contains("建单要主管确认", ToolHooks.Tighten(confirm, new HookVerdict(HookDecision.Ask, "建单要主管确认")).Reason);
        // deny：拦下
        var denied = ToolHooks.Tighten(confirm, new HookVerdict(HookDecision.Deny, "金额超过授权"));
        Assert.Equal(RiskLevel.Blocked, denied.Level);
        Assert.Equal("金额超过授权", denied.Reason);
    }

    [Theory]
    [InlineData("allow", HookDecision.Allow)]
    [InlineData("DENY", HookDecision.Deny)]
    [InlineData("block", HookDecision.Deny)]
    [InlineData("ask", HookDecision.Ask)]
    [InlineData("approve-everything", HookDecision.Ask)]
    [InlineData(null, HookDecision.Ask)]
    public void UnknownAnswersCountAsAsk(string? text, HookDecision expected) => Assert.Equal(expected, HookVerdict.Parse(text));

    [Fact]
    public async Task OnlyMatchingToolsAskTheServerAndBlockedOnesNeverDo()
    {
        var client = new FakeHookClient { Answer = c => new HookVerdict(HookDecision.Deny, "不行") };
        var hooks = new ToolHooks(new[]
        {
            new HookSpec { Id = 1, Events = new() { ToolHooks.PreTool }, Tools = "mcp__erp__*, run_shell", Match = "" },
            new HookSpec { Id = 2, Events = new() { ToolHooks.PreTool }, Tools = "write_file", Match = "salary" },
            new HookSpec { Id = 3, Events = new() { ToolHooks.PostTool }, Tools = "*" },
        }, client);

        Assert.Equal(PolicyDecision.Auto(), await hooks.BeforeAsync(Call("read_file"), PolicyDecision.Auto(), CancellationToken.None));
        Assert.Equal(PolicyDecision.Auto(), await hooks.BeforeAsync(Call("write_file", "{\"path\":\"a.txt\"}"), PolicyDecision.Auto(), CancellationToken.None));
        Assert.Empty(client.Checked);

        Assert.Equal(RiskLevel.Blocked, (await hooks.BeforeAsync(Call("mcp__erp__create_order"), PolicyDecision.Auto(), CancellationToken.None)).Level);
        Assert.Equal(RiskLevel.Blocked, (await hooks.BeforeAsync(Call("write_file", "{\"path\":\"salary.xlsx\"}"), PolicyDecision.Auto(), CancellationToken.None)).Level);
        Assert.Equal(2, client.Checked.Count);

        // 本来就被拦下的不再去问
        await hooks.BeforeAsync(Call("run_shell"), PolicyDecision.Blocked("策略"), CancellationToken.None);
        Assert.Equal(2, client.Checked.Count);
    }

    [Fact]
    public async Task ServerUnreachableFollowsTheStrictestOnError()
    {
        var client = new FakeHookClient { Fail = true };
        var ask = new ToolHooks(new[] { new HookSpec { Id = 1, Events = new() { ToolHooks.PreTool }, OnError = "ask" } }, client);
        var r = await ask.BeforeAsync(Call("read_file"), PolicyDecision.Auto(), CancellationToken.None);
        Assert.Equal(RiskLevel.Confirm, r.Level);
        Assert.True(r.AlwaysAsk);

        var strict = new ToolHooks(new[]
        {
            new HookSpec { Id = 1, Events = new() { ToolHooks.PreTool }, OnError = "allow" },
            new HookSpec { Id = 2, Events = new() { ToolHooks.PreTool }, OnError = "deny" },
        }, client);
        Assert.Equal(RiskLevel.Blocked, (await strict.BeforeAsync(Call("read_file"), PolicyDecision.Auto(), CancellationToken.None)).Level);

        var lenient = new ToolHooks(new[] { new HookSpec { Id = 1, Events = new() { ToolHooks.PreTool }, OnError = "allow" } }, client);
        var confirm = PolicyDecision.Confirm("本来就要确认");
        Assert.Equal(confirm, await lenient.BeforeAsync(Call("read_file"), confirm, CancellationToken.None)); // allow 也不放行
    }

    [Fact]
    public async Task ArgumentsAreRedactedBeforeTheyLeave()
    {
        var client = new FakeHookClient();
        var hooks = new ToolHooks(new[] { new HookSpec { Id = 1, Events = new() { ToolHooks.PreTool } } }, client);
        await hooks.BeforeAsync(Call("run_shell", "{\"command\":\"curl -H 'Authorization: Bearer abcdef1234567890abcdef' https://x\"}"), PolicyDecision.Auto(), CancellationToken.None);
        Assert.DoesNotContain("abcdef1234567890abcdef", client.Checked[0].Arguments);

        await hooks.BeforeAsync(Call("write_file", "{\"content\":\"" + new string('x', 50000) + "\"}"), PolicyDecision.Auto(), CancellationToken.None);
        Assert.True(client.Checked[1].Arguments.Length <= ToolHooks.MaxArguments);
    }

    [Fact]
    public async Task StoppingTheTaskIsNotTreatedAsAHookError()
    {
        var client = new BlockingClient();
        var hooks = new ToolHooks(new[] { new HookSpec { Id = 1, Events = new() { ToolHooks.PreTool }, OnError = "allow" } }, client);
        using var cts = new CancellationTokenSource();
        var pending = hooks.BeforeAsync(Call("read_file"), PolicyDecision.Auto(), cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    private sealed class BlockingClient : IHookClient
    {
        public async Task<HookVerdict> CheckAsync(HookCall call, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return HookVerdict.Allow;
        }

        public Task NotifyAsync(string hookEvent, HookCall call, bool ok, string summary, string status, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task PostToolAndStopAreFireAndForget()
    {
        var client = new FakeHookClient();
        var hooks = new ToolHooks(new[]
        {
            new HookSpec { Id = 1, Events = new() { ToolHooks.PostTool }, Tools = "write_file" },
            new HookSpec { Id = 2, Events = new() { ToolHooks.Stop } },
        }, client);
        hooks.After(Call("read_file"), true, "不匹配");
        hooks.After(Call("write_file"), true, "token=sk-abcdefghijklmnopqrstuvwx 已写入");
        hooks.Finished("c1", "r1", "t1", "", "succeeded", "整理完了");
        for (var i = 0; i < 100 && client.Notified.Count < 2; i++)
        {
            await Task.Delay(20);
        }
        Assert.Equal(2, client.Notified.Count);
        var post = client.Notified.Single(n => n.Event == ToolHooks.PostTool);
        Assert.Equal("write_file", post.Call.Tool);
        Assert.DoesNotContain("sk-abcdefghijklmnopqrstuvwx", post.Summary);
        var stop = client.Notified.Single(n => n.Event == ToolHooks.Stop);
        Assert.Equal(("succeeded", "整理完了"), (stop.Status, stop.Summary));
    }
}
