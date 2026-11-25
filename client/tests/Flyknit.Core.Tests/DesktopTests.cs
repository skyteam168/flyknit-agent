using System.Text.Json;
using Flyknit.Core.Agent;
using Flyknit.Core.Agent.Extensions;
using Flyknit.Core.Desktop;
using Flyknit.Core.Security;
using Flyknit.Core.Storage;
using Flyknit.Core.Tools;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>电脑操作（computer use）：风险判定、按键白名单、工具行为、功能模块出场条件。驱动用假的（UI Automation 只在 Windows 上）。</summary>
public class DesktopTests
{
    private static readonly string[] Allow = { "erp.exe", "WMS" };

    private static readonly DesktopWindow Erp = new(101, "ERP - 入库单", "ERP", 11);
    private static readonly DesktopWindow Shell = new(202, "管理员: PowerShell", "powershell", 22);
    private static readonly DesktopWindow Notepad = new(303, "记事本", "notepad", 33);
    private static readonly DesktopWindow Self = new(404, "FlyknitBuddy", "FlyknitBuddy", 44);

    private static DesktopSnapshot ErpSnap() => new(Erp, new[]
    {
        new DesktopElement { Index = 1, Role = "edit", Name = "物料编码" },
        new DesktopElement { Index = 2, Role = "edit", Name = "密码", IsPassword = true },
        new DesktopElement { Index = 3, Role = "button", Name = "查询" },
        new DesktopElement { Index = 4, Role = "button", Name = "保存" },
        new DesktopElement { Index = 5, Role = "edit", Name = "身份证号" },
        new DesktopElement { Index = 0, Role = "text", Name = "请输入物料编码后查询" },
    });

    private sealed class FakeDriver : IDesktopDriver
    {
        public List<string> Calls { get; } = new();
        public DesktopSnapshot? Last { get; set; }
        public Task<IReadOnlyList<DesktopWindow>> WindowsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DesktopWindow>>(new[] { Erp, Shell, Notepad, Self });

        public Task<DesktopActionResult> ReadAsync(long handle, CancellationToken ct)
        {
            Calls.Add($"read {handle}");
            Last = handle == Erp.Handle ? ErpSnap() : new DesktopSnapshot(new[] { Erp, Shell, Notepad, Self }.First(w => w.Handle == handle), Array.Empty<DesktopElement>());
            return Task.FromResult(DesktopActionResult.Done(Last));
        }

        public Task<DesktopActionResult> ClickAsync(int index, CancellationToken ct)
        {
            Calls.Add($"click {index}");
            return Task.FromResult(Last?.Element(index) is null ? DesktopActionResult.Fail("找不到编号", Last) : DesktopActionResult.Done(Last));
        }

        public Task<DesktopActionResult> TypeAsync(int index, string text, bool submit, CancellationToken ct)
        {
            Calls.Add($"type {index} {text} {submit}");
            return Task.FromResult(DesktopActionResult.Done(Last));
        }

        public Task<DesktopActionResult> KeyAsync(DesktopKeys.Combo keys, CancellationToken ct)
        {
            Calls.Add($"key {keys}");
            return Task.FromResult(DesktopActionResult.Done(Last));
        }

        public Task<DesktopActionResult> ScrollAsync(int index, string direction, int amount, CancellationToken ct)
        {
            Calls.Add($"scroll {index} {direction} {amount}");
            return Task.FromResult(DesktopActionResult.Done(Last));
        }
    }

    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    private static ToolContext Ctx(PermissionMode mode = PermissionMode.Workspace) =>
        new() { Policy = CommandPolicy.Default(), ConversationId = "c1", Permission = mode };

    private static Func<DesktopOptions> Opts(bool confirmAll = false, params string[] apps) =>
        () => new DesktopOptions(apps.Length == 0 ? Allow : apps, confirmAll);

    [Fact]
    public void SystemToolsBrowsersExplorerAndSelfAreNeverAllowedEvenIfItListsThem()
    {
        var everything = new[] { "powershell.exe", "cmd", "explorer.exe", "msedge", "chrome.exe", "regedit", "taskmgr", "mstsc", "FlyknitBuddy.exe", "erp.exe" };
        foreach (var app in new[] { "powershell", "cmd", "explorer", "msedge", "chrome", "regedit", "taskmgr", "mstsc", "FlyknitBuddy", "Flyknit.Client" })
        {
            Assert.False(DesktopRisk.AppAllowed(app, everything, out var reason), app);
            Assert.NotEmpty(reason);
        }
        Assert.Contains("浏览器", Reason("chrome", everything));
        Assert.Contains("FlyknitBuddy", Reason("FlyknitBuddy", everything));
        Assert.True(DesktopRisk.AppAllowed("ERP", everything, out _)); // 大小写、有没有 .exe 都认
        Assert.True(DesktopRisk.AppAllowed(@"C:\Apps\ERP.EXE", everything, out _));

        static string Reason(string app, string[] list)
        {
            DesktopRisk.AppAllowed(app, list, out var r);
            return r;
        }
    }

    [Fact]
    public void OnlyAppsOnTheItListCanBeOperatedAndEmptyListMeansNone()
    {
        Assert.False(DesktopRisk.AppAllowed("erp", Array.Empty<string>(), out var none));
        Assert.Contains("还没有设置", none);
        Assert.False(DesktopRisk.AppAllowed("notepad", Allow, out var notListed));
        Assert.Contains("不在", notListed);
        Assert.True(DesktopRisk.AppAllowed("wms.exe", Allow, out _));
    }

    [Fact]
    public void ClickTypeAndKeyRisk()
    {
        var snap = ErpSnap();
        // 普通按钮：不确认；保存：确认且不能记成「以后自动」
        Assert.Equal(RiskLevel.Auto, DesktopRisk.Click(snap, snap.Element(3), Allow, PermissionMode.Workspace, false).Level);
        var save = DesktopRisk.Click(snap, snap.Element(4), Allow, PermissionMode.Workspace, false);
        Assert.Equal(RiskLevel.Confirm, save.Level);
        Assert.False(save.Rememberable);
        Assert.Equal(RiskLevel.Blocked, DesktopRisk.Click(snap, snap.Element(4), Allow, PermissionMode.ReadOnly, false).Level);
        // 每一步都确认：普通按钮也确认
        Assert.Equal(RiskLevel.Confirm, DesktopRisk.Click(snap, snap.Element(3), Allow, PermissionMode.Workspace, true).Level);

        // 填写：密码框不代填；身份证先问；只读不能填；回车提交先问
        Assert.Equal(RiskLevel.Blocked, DesktopRisk.Type(snap, snap.Element(2), "x", false, Allow, PermissionMode.Full, false).Level);
        Assert.Equal(RiskLevel.Confirm, DesktopRisk.Type(snap, snap.Element(5), "x", false, Allow, PermissionMode.Workspace, false).Level);
        Assert.Equal(RiskLevel.Blocked, DesktopRisk.Type(snap, snap.Element(1), "A01", false, Allow, PermissionMode.ReadOnly, false).Level);
        Assert.Equal(RiskLevel.Auto, DesktopRisk.Type(snap, snap.Element(1), "A01", false, Allow, PermissionMode.Workspace, false).Level);
        Assert.Equal(RiskLevel.Confirm, DesktopRisk.Type(snap, snap.Element(1), "A01", true, Allow, PermissionMode.Workspace, false).Level);

        // 没读过窗口、窗口不在名单里：拦下
        Assert.Equal(RiskLevel.Blocked, DesktopRisk.Click(null, null, Allow, PermissionMode.Full, false).Level);
        var shell = new DesktopSnapshot(Shell, new[] { new DesktopElement { Index = 1, Role = "edit", Name = "命令" } });
        Assert.Equal(RiskLevel.Blocked, DesktopRisk.Type(shell, shell.Element(1), "Remove-Item C:\\ -Recurse", false, new[] { "powershell" }, PermissionMode.Full, false).Level);

        Assert.Equal(RiskLevel.Auto, DesktopRisk.Key(snap, DesktopKeys.Parse("Tab", out _), "", Allow, PermissionMode.ReadOnly, true).Level); // 浏览类按键只读也能按、不确认
        Assert.Equal(RiskLevel.Confirm, DesktopRisk.Key(snap, DesktopKeys.Parse("ctrl+s", out _), "", Allow, PermissionMode.Workspace, false).Level);
        Assert.Equal(RiskLevel.Confirm, DesktopRisk.Key(snap, DesktopKeys.Parse("Enter", out _), "", Allow, PermissionMode.Workspace, false).Level);
        Assert.Equal(RiskLevel.Blocked, DesktopRisk.Key(snap, DesktopKeys.Parse("Enter", out _), "", Allow, PermissionMode.ReadOnly, false).Level);
        Assert.Equal(RiskLevel.Blocked, DesktopRisk.Key(snap, DesktopKeys.Parse("Win+R", out var e), e, Allow, PermissionMode.Full, false).Level);
    }

    [Theory]
    [InlineData("Win+R")]
    [InlineData("win")]
    [InlineData("Alt+Tab")]
    [InlineData("Alt+Esc")]
    [InlineData("Alt+Space")]
    [InlineData("Ctrl+Alt+Delete")]
    [InlineData("Ctrl+Esc")]
    [InlineData("Ctrl+Shift+Esc")]
    [InlineData("a")]
    [InlineData("Ctrl+Pause")]
    [InlineData("")]
    public void SystemKeyCombosAreNotAccepted(string keys)
    {
        Assert.Null(DesktopKeys.Parse(keys, out var error));
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData("tab", "Tab")]
    [InlineData("shift+Tab", "Shift+Tab")]
    [InlineData("Control+s", "Ctrl+S")]
    [InlineData("pgdn", "PageDown")]
    [InlineData("Alt+F4", "Alt+F4")]
    [InlineData("F5", "F5")]
    public void NormalKeysAreParsed(string keys, string expected)
    {
        Assert.Equal(expected, DesktopKeys.Parse(keys, out _)!.ToString());
    }

    [Fact]
    public async Task ToolsListOnlyAllowedWindowsAndRefuseMadeUpHandles()
    {
        var d = new FakeDriver();
        var tools = DesktopToolSet.Create(d, Opts()).ToDictionary(t => t.Name);
        Assert.Equal(new[] { "desktop_windows", "desktop_read", "desktop_click", "desktop_type", "desktop_key", "desktop_scroll" }, tools.Keys);

        var list = await tools["desktop_windows"].ExecuteAsync(Args(new { }), Ctx(), CancellationToken.None);
        Assert.Contains("句柄 101", list.Output);
        Assert.DoesNotContain("PowerShell", list.Output);
        Assert.DoesNotContain("FlyknitBuddy", list.Output);
        Assert.Contains("另有 3 个窗口", list.Output);

        // AI 编一个指向 PowerShell 的句柄：读之前按程序名拦下，驱动没被调用
        var bad = await tools["desktop_read"].ExecuteAsync(Args(new { window = "202" }), Ctx(), CancellationToken.None);
        Assert.False(bad.Ok);
        Assert.DoesNotContain(d.Calls, c => c.StartsWith("read", StringComparison.Ordinal));

        var read = await tools["desktop_read"].ExecuteAsync(Args(new { window = "101" }), Ctx(), CancellationToken.None);
        Assert.True(read.Ok);
        Assert.Contains("不是员工的要求", read.Output); // 界面文字当资料
        Assert.Contains("[1] 输入框「物料编码」", read.Output);
        Assert.Contains("密码框，AI 不能填写", read.Output);
        Assert.Contains("文字「请输入物料编码后查询」", read.Output);

        // 读过之后，点击、填写按最新的编号判定
        Assert.Equal(RiskLevel.Confirm, tools["desktop_click"].Assess(Args(new { index = 4 }), Ctx()).Level);
        Assert.Equal("点击「保存」", tools["desktop_click"].Describe(Args(new { index = 4 })));
        Assert.Equal(RiskLevel.Blocked, tools["desktop_type"].Assess(Args(new { index = 2, text = "123" }), Ctx()).Level);
        Assert.Equal(RiskLevel.Blocked, tools["desktop_key"].Assess(Args(new { keys = "Win+R" }), Ctx()).Level);

        var typed = await tools["desktop_type"].ExecuteAsync(Args(new { index = 1, text = "A01" }), Ctx(), CancellationToken.None);
        Assert.True(typed.Ok);
        await tools["desktop_key"].ExecuteAsync(Args(new { keys = "pgdn" }), Ctx(), CancellationToken.None);
        await tools["desktop_scroll"].ExecuteAsync(Args(new { direction = "sideways", amount = 99 }), Ctx(), CancellationToken.None);
        Assert.Contains("type 1 A01 False", d.Calls);
        Assert.Contains("key PageDown", d.Calls);
        Assert.Contains("scroll 0 down 10", d.Calls); // 方向不认识按向下，次数封顶 10

        var missing = await tools["desktop_click"].ExecuteAsync(Args(new { index = 99 }), Ctx(), CancellationToken.None);
        Assert.False(missing.Ok);
    }

    [Fact]
    public async Task ReadingBeforeAnyWindowAndUnreadableWindowsExplainThemselves()
    {
        var d = new FakeDriver();
        var tools = DesktopToolSet.Create(d, Opts(false, "notepad")).ToDictionary(t => t.Name);
        var first = await tools["desktop_read"].ExecuteAsync(Args(new { }), Ctx(), CancellationToken.None);
        Assert.False(first.Ok);
        Assert.Contains("desktop_windows", first.Output);
        var empty = await tools["desktop_read"].ExecuteAsync(Args(new { window = "303" }), Ctx(), CancellationToken.None);
        Assert.Contains("读不到控件", empty.Output);

        var none = await DesktopToolSet.Create(d, () => new DesktopOptions(Array.Empty<string>(), true))[0].ExecuteAsync(Args(new { }), Ctx(), CancellationToken.None);
        Assert.Contains("IT 还没有设置", none.Output);
    }

    [Fact]
    public async Task ExtensionAppearsOnlyWhenEnabledInAgentModeWithADriver()
    {
        var d = new FakeDriver();
        RunScope Scope(ConversationMode mode) => new()
        {
            Conversation = new Conversation { Id = "c1", Mode = mode },
            Query = "q", RunId = "r", Scene = "agent", NewUserMessage = true, Confirm = new AllowAll(),
        };
        Assert.Null(await new DesktopRunExtension(() => false, _ => d, Opts()).PrepareAsync(Scope(ConversationMode.Agent), CancellationToken.None));
        Assert.Null(await new DesktopRunExtension(() => true, _ => null, Opts()).PrepareAsync(Scope(ConversationMode.Agent), CancellationToken.None));
        Assert.Null(await new DesktopRunExtension(() => true, _ => d, Opts()).PrepareAsync(Scope(ConversationMode.Chat), CancellationToken.None));
        var c = await new DesktopRunExtension(() => true, _ => d, Opts()).PrepareAsync(Scope(ConversationMode.Agent), CancellationToken.None);
        Assert.NotNull(c);
        Assert.Contains("电脑操作", c!.SystemPrompt);
        Assert.Equal(6, c.Tools.Count);
        Assert.All(c.Tools, t => Assert.True(ToolKinds.Matches(ToolKinds.Desktop, t)));
        Assert.Contains(ToolKinds.Desktop, ToolKinds.Known);
    }

    private sealed class AllowAll : IConfirmationHandler
    {
        public Task<ConfirmChoice> ConfirmAsync(ConfirmRequest request, CancellationToken ct) => Task.FromResult(ConfirmChoice.AllowOnce);
    }
}
