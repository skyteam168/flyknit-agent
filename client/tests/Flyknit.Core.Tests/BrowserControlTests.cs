using Flyknit.Core.Browser;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>AI 浏览器：员工的地址栏只开 http / https；AI 操作时员工「接管」/「交还给 AI」的控制权。</summary>
public class BrowserControlTests
{
    [Theory]
    [InlineData("https://erp.corp.cn/report", "https://erp.corp.cn/report")]
    [InlineData("http://10.1.2.3:8080/a", "http://10.1.2.3:8080/a")]
    [InlineData("www.example.com", "https://www.example.com/")]
    [InlineData("erp.company.local/daily?d=1", "https://erp.company.local/daily?d=1")]
    [InlineData("erp", "http://erp/")]                       // 内网主机名
    [InlineData("localhost:5173", "http://localhost:5173/")]
    [InlineData("192.168.1.20", "http://192.168.1.20/")]
    [InlineData("  https://a.cn  ", "https://a.cn/")]
    public void AddressIsCompletedToHttpOrHttps(string input, string expected) =>
        Assert.Equal(expected, BrowserAddress.Normalize(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("file:C:/a.txt")]
    [InlineData("ftp://x.cn/")]
    [InlineData("mailto:a@b.cn")]
    [InlineData("data:text/html,<b>x</b>")]
    [InlineData("怎么 打开 报表")]                          // 不是网址：不拿去外部搜索
    public void NonHttpAddressesAreRefused(string input) =>
        Assert.Null(BrowserAddress.Normalize(input));

    [Fact]
    public async Task AiTakesControlAndReleasesItWhenTheRunEnds()
    {
        var control = new BrowserControl();
        var seen = new List<BrowserController>();
        control.Changed += seen.Add;
        await control.AcquireAsync(CancellationToken.None);
        Assert.Equal(BrowserController.Ai, control.State);
        await control.AcquireAsync(CancellationToken.None); // 下一步：还是 AI，不重复通知
        control.Release();
        Assert.Equal(BrowserController.Idle, control.State);
        Assert.Equal(new[] { BrowserController.Ai, BrowserController.Idle }, seen);
    }

    [Fact]
    public async Task AfterTakeOverTheAiWaitsUntilHandedBack()
    {
        var control = new BrowserControl();
        await control.AcquireAsync(CancellationToken.None);
        control.TakeOver();
        Assert.Equal(BrowserController.User, control.State);
        var waited = false;
        var next = control.AcquireAsync(CancellationToken.None, () => waited = true);
        await Task.Delay(50);
        Assert.False(next.IsCompleted);      // 员工在操作：AI 这一步等着
        Assert.True(waited);
        control.Release();                    // 任务里别的地方喊结束也不会把员工的控制权抢走
        Assert.Equal(BrowserController.User, control.State);
        control.HandBack();
        await next.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(BrowserController.Ai, control.State);
    }

    [Fact]
    public async Task StoppingTheTaskCancelsTheWait()
    {
        var control = new BrowserControl();
        control.TakeOver();
        using var cts = new CancellationTokenSource();
        var next = control.AcquireAsync(cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
        Assert.Equal(BrowserController.User, control.State);
        control.HandBack();                   // 没人在等：交还后就是没人在用
        Assert.Equal(BrowserController.Idle, control.State);
    }
}
