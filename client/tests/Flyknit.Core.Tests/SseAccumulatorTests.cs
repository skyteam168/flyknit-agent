using Flyknit.Core.Gateway;
using Xunit;

namespace Flyknit.Core.Tests;

public class SseAccumulatorTests
{
    private sealed class Sink : IStreamSink
    {
        public List<string> Content { get; } = new();
        public List<string> Reasoning { get; } = new();
        public void OnContent(string delta) => Content.Add(delta);
        public void OnReasoning(string delta) => Reasoning.Add(delta);
    }

    [Fact]
    public void AccumulatesContentAndReasoning()
    {
        var sink = new Sink();
        var acc = new SseAccumulator(sink);
        acc.FeedLine("data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"想一想\"}}]}");
        acc.FeedLine("");
        acc.FeedLine(": keep-alive");
        acc.FeedLine("data: {\"choices\":[{\"delta\":{\"content\":\"Xin \"}}]}");
        acc.FeedLine("data: {\"choices\":[{\"delta\":{\"content\":\"chào\"},\"finish_reason\":\"stop\"}]}");
        acc.FeedLine("data: [DONE]");

        var turn = acc.Build("Qwen");
        Assert.True(acc.Done);
        Assert.Equal("Xin chào", turn.Content);
        Assert.Equal("想一想", turn.Reasoning);
        Assert.Equal("stop", turn.FinishReason);
        Assert.Equal(new[] { "Xin ", "chào" }, sink.Content);
        Assert.Empty(turn.ToolCalls);
    }

    [Fact]
    public void MergesToolCallFragmentsByIndex()
    {
        var acc = new SseAccumulator();
        acc.FeedLine("data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_a\",\"function\":{\"name\":\"read_file\",\"arguments\":\"\"}}]}}]}");
        acc.FeedLine("data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"{\\\"path\\\":\"}}]}}]}");
        acc.FeedLine("data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":1,\"id\":\"call_b\",\"function\":{\"name\":\"list_dir\",\"arguments\":\"{}\"}}]}}]}");
        acc.FeedLine("data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"\\\"D:/a.txt\\\"}\"}}]}}]}");

        var turn = acc.Build();
        Assert.Equal(2, turn.ToolCalls.Count);
        Assert.Equal("call_a", turn.ToolCalls[0].Id);
        Assert.Equal("read_file", turn.ToolCalls[0].Name);
        Assert.Equal("{\"path\":\"D:/a.txt\"}", turn.ToolCalls[0].ArgumentsJson);
        Assert.Equal("list_dir", turn.ToolCalls[1].Name);
    }

    [Fact]
    public void ParsesNonStreamingResponse()
    {
        var acc = new SseAccumulator();
        acc.FeedChunk("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"工厂周报\"},\"finish_reason\":\"stop\"}]}");
        Assert.Equal("工厂周报", acc.Build().Content);
    }

    [Fact]
    public void ErrorChunkThrows()
    {
        var acc = new SseAccumulator();
        var ex = Assert.Throws<GatewayException>(() => acc.FeedLine("data: {\"error\":{\"message\":\"context too long\"}}"));
        Assert.Equal("context too long", ex.Message);
    }

    [Fact]
    public void MissingToolCallIdGetsGenerated()
    {
        var acc = new SseAccumulator();
        acc.FeedLine("data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"function\":{\"name\":\"list_dir\",\"arguments\":\"{}\"}}]}}]}");
        var call = Assert.Single(acc.Build().ToolCalls);
        Assert.False(string.IsNullOrEmpty(call.Id));
    }
}
