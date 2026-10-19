using Flyknit.Core.Gateway;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>定时任务、钉钉/企业微信、IT 指令发起的运行：标记要跟着后台任务走，出了范围就消失。</summary>
public class UsageSourceTests
{
    [Fact]
    public async Task TheSourceFollowsWorkStartedInsideTheScope()
    {
        Task<string?> inner;
        using (UsageSource.Use(UsageSource.Schedule))
        {
            // 和 AgentHost.StartRun 一样：在范围里 Task.Run，真正的模型调用在后台的 await 之后
            inner = Task.Run(async () =>
            {
                await Task.Delay(20);
                return UsageSource.Current;
            });
        }
        Assert.Null(UsageSource.Current);      // 发起处已经出了范围
        Assert.Equal("schedule", await inner);  // 后台那次运行仍然带着
    }

    [Fact]
    public async Task ScopesNestAndRestore()
    {
        using (UsageSource.Use(UsageSource.Channel))
        {
            using (UsageSource.Use(UsageSource.Instruction))
            {
                Assert.Equal("instruction", UsageSource.Current);
            }
            Assert.Equal("channel", UsageSource.Current);
            await Task.Yield();
            Assert.Equal("channel", UsageSource.Current);
        }
        Assert.Null(UsageSource.Current);
    }
}
