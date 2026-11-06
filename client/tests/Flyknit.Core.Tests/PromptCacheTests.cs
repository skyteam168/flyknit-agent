using Flyknit.Core.Agent;
using Flyknit.Core.Memory;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>提示词按「多久变一次」排：稳定的在前，按问题挑的记忆和当前时间在后，前缀缓存才命中得了。</summary>
public class PromptCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"flyknit-cache-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void StablePartsComeFirstAndPerTurnPartsLast()
    {
        Directory.CreateDirectory(_dir);
        var memory = new MemoryStore(Path.Combine(_dir, "memory"));
        memory.Remember("用户在质检部，日报保存在 D:\\日报");
        var builder = new PromptBuilder(memory, null);
        PromptContext Ctx(string q) => new()
        {
            Mode = ConversationMode.Agent,
            Workspace = _dir,
            Query = q,
            McpServers = new List<McpPromptInfo> { new("mes", "MES", 3, "查询生产数据") },
        };

        var a = builder.Build(Ctx("写今天的质检日报"));
        var keys = builder.Sections.Select(s => s.Key).ToList();
        var b = builder.Build(Ctx("帮我查一下 MES 里的产量"));

        // 当前时间在最后一行，不在开头
        Assert.StartsWith("当前时间：", a.Split('\n').Last());
        Assert.DoesNotContain("当前时间", string.Join('\n', a.Split('\n').Take(5)));
        // 记忆在连接器、文档说明这些固定段之后
        Assert.True(keys.IndexOf("memory") > keys.IndexOf("mcp"));
        Assert.True(keys.IndexOf("memory") > keys.IndexOf("document"));
        Assert.Equal("base", keys[^1]);

        // 换一个问题：记忆之前的部分一个字都不变
        var cut = a.IndexOf("<记忆使用说明>", StringComparison.Ordinal);
        Assert.True(cut > 0);
        Assert.Equal(a[..cut], b[..cut]);
    }
}
