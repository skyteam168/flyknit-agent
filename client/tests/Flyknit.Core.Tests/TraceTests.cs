using Flyknit.Core.Agent;
using Flyknit.Core.Chat;
using Flyknit.Core.Gateway;
using Flyknit.Core.Security;
using Flyknit.Core.Tools;
using Xunit;

namespace Flyknit.Core.Tests;

public class TraceTests
{
    [Fact]
    public void StepsAreNumberedInOrderAndCarryTheirDuration()
    {
        var trace = new Trace("c1");
        using (var a = trace.Begin("model", "qwen", "第 1 轮"))
        {
            a.PromptTokens = 1200;
            a.CompletionTokens = 80;
        }
        using (var b = trace.Begin("tool", "read_file"))
        {
            b.Summary = "读取 质检.txt";
            Thread.Sleep(12);
        }

        var steps = trace.Steps;
        Assert.Equal(new[] { 1, 2 }, steps.Select(s => s.Index));
        Assert.Equal(new[] { "model", "tool" }, steps.Select(s => s.Kind));
        Assert.Equal(1280, steps[0].TotalTokens);
        Assert.True(steps[1].DurationMs >= 10, $"耗时应该被记下来，实际 {steps[1].DurationMs}ms");
    }

    [Fact]
    public void SummaryRollsUpWhatAnOperatorNeedsToSee()
    {
        var trace = new Trace("c1");
        using (var a = trace.Begin("model", "qwen")) { a.PromptTokens = 1000; a.CompletionTokens = 100; }
        using (var b = trace.Begin("tool", "run_shell")) { b.Status = "error"; }
        using (var c = trace.Begin("tool", "read_file")) { Thread.Sleep(15); }

        var s = trace.Summarize();
        Assert.Equal(3, s.Steps);
        Assert.Equal(1, s.ModelCalls);
        Assert.Equal(2, s.ToolCalls);
        Assert.Equal(1, s.Errors);
        Assert.Equal(1100, s.TotalTokens);
        Assert.Equal("read_file", s.Slowest!.Name);   // 最慢的那一步直接指出来
    }

    [Fact]
    public void BlockedStepsCountAsErrorsSoTheyShowUpInTheRollup()
    {
        var trace = new Trace("c1");
        using (var a = trace.Begin("model", "qwen")) { a.Status = "blocked"; }
        Assert.Equal(1, trace.Summarize().Errors);
    }

    [Fact]
    public void ALongRunningTaskCannotBlowUpMemory()
    {
        var trace = new Trace("c1");
        for (var i = 0; i < Trace.MaxSteps + 50; i++)
        {
            trace.Begin("tool", "x").Dispose();
        }
        Assert.Equal(Trace.MaxSteps, trace.Count);
    }

    // ---------- 跑完一次真实任务，看链路对不对 ----------

    private sealed class TwoStepGateway : IChatGateway
    {
        private int _calls;

        public Task<ChatTurn> CompleteAsync(ChatRequest request, IStreamSink? sink, CancellationToken ct)
        {
            _calls++;
            if (_calls == 1)
            {
                return Task.FromResult(new ChatTurn
                {
                    Content = "我先看一下目录。",
                    ToolCalls = new[] { new ToolCall("call_1", "list_dir", "{\"path\":\".\"}") },
                    Usage = new TokenUsage(900, 40),
                });
            }
            return Task.FromResult(new ChatTurn { Content = "目录里是空的。", Usage = new TokenUsage(1100, 25) });
        }
    }

    private sealed class Silent : IAgentObserver
    {
        public void OnContent(string delta) { }
        public void OnReasoning(string delta) { }
        public void OnAssistantMessage(ChatMessage message) { }
        public void OnToolStarted(ToolCall call, string summary, PolicyDecision decision) { }
        public void OnToolFinished(ToolCall call, ToolResult result, string decision) { }
        public void OnToolMessage(ChatMessage message) { }
        public void OnPlanUpdated(IReadOnlyList<PlanItem> plan) { }
    }

    [Fact]
    public async Task ARealRunProducesAChainYouCanFollow()
    {
        var dir = Directory.CreateTempSubdirectory("flyknit-trace").FullName;
        try
        {
            var ctx = new ToolContext { Policy = CommandPolicy.Default(), ConversationId = "c1", Workspace = dir, Permission = PermissionMode.Workspace };
            var result = await new AgentLoop(new TwoStepGateway(), ToolRegistry.CreateDefault(), new NeverAsked())
                .RunAsync(new List<ChatMessage> { ChatMessage.User("看下目录") }, Scenes.Agent, ctx, new Silent(), true, CancellationToken.None);

            var trace = result.Trace!;
            var kinds = trace.Steps.Select(s => $"{s.Kind}:{s.Name}").ToList();

            // 模型 → 工具 → 模型，顺序和实际执行一致
            Assert.Equal("model", trace.Steps[0].Kind);
            Assert.Contains("tool:list_dir", kinds);
            Assert.Equal(2, trace.Summarize().ModelCalls);

            // token 按步记录，汇总等于两次调用之和
            Assert.Equal(900 + 40 + 1100 + 25, trace.Summarize().TotalTokens);
            Assert.Equal(0, trace.Summarize().Errors);
            Assert.Equal("c1", trace.ConversationId);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (Exception) { }
        }
    }

    [Fact]
    public async Task AFailedRunStillHasTheChainUpToThePointItBroke()
    {
        var ctx = new ToolContext { Policy = CommandPolicy.Default(), ConversationId = "c2", Permission = PermissionMode.ReadOnly };
        var result = await new AgentLoop(new AlwaysBlocked(), ToolRegistry.CreateDefault(), new NeverAsked())
            .RunAsync(new List<ChatMessage> { ChatMessage.User("问点什么") }, Scenes.Agent, ctx, new Silent(), true, CancellationToken.None);

        Assert.Equal(AgentStopReason.Failed, result.StopReason);
        var summary = result.Trace!.Summarize();
        // 出错的那一步被标出来了，排查时一眼能看到卡在哪
        Assert.True(summary.Errors >= 1);
        Assert.Contains(result.Trace.Steps, s => s.Status == "blocked" || s.Status == "error");
    }

    private sealed class AlwaysBlocked : IChatGateway
    {
        public Task<ChatTurn> CompleteAsync(ChatRequest request, IStreamSink? sink, CancellationToken ct) =>
            throw new GatewayException("Output data may contain inappropriate content.");
    }

    private sealed class NeverAsked : IConfirmationHandler
    {
        public Task<ConfirmChoice> ConfirmAsync(ConfirmRequest request, CancellationToken ct) =>
            Task.FromResult(ConfirmChoice.AllowOnce);
    }
}
