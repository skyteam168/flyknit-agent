using Flyknit.Core.Settings;
using Xunit;

namespace Flyknit.Core.Tests;

public class ShortcutTests
{
    [Theory]
    [InlineData("ctrl+n", "Ctrl+N")]
    [InlineData("Shift+Ctrl+B", "Ctrl+Shift+B")]      // 修饰键顺序统一
    [InlineData("CTRL + SHIFT + b", "Ctrl+Shift+B")]  // 大小写和空格都忽略
    [InlineData("esc", "Escape")]
    [InlineData("f11", "F11")]
    [InlineData("ctrl+alt+space", "Ctrl+Alt+Space")]
    [InlineData("control+,", "Ctrl+,")]
    [InlineData("ctrl+plus", "Ctrl+=")]
    [InlineData("arrowup", "Up")]
    public void BindingsAreNormalisedSoTheSameKeyAlwaysLooksTheSame(string input, string expected)
    {
        Assert.Equal(expected, Shortcuts.Normalize(input));
    }

    [Fact]
    public void ModifiersOnlyIsNotAShortcut()
    {
        Assert.Equal("", Shortcuts.Normalize("Ctrl"));
        Assert.Equal("", Shortcuts.Normalize("Ctrl+Shift"));
        Assert.Equal("", Shortcuts.Normalize(""));
        Assert.Equal("", Shortcuts.Normalize(null));
    }

    [Fact]
    public void EveryCommandHasAUniqueIdAndAUsableDefault()
    {
        var ids = Shortcuts.All.Select(c => c.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());

        foreach (var cmd in Shortcuts.All)
        {
            Assert.NotEqual("", Shortcuts.Normalize(cmd.Default));
        }
    }

    [Fact]
    public void DefaultBindingsDoNotCollideWithEachOther()
    {
        var resolved = Shortcuts.Resolve(null);
        var duplicates = resolved.Values
            .Where(v => v.Length > 0)
            .GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        Assert.Empty(duplicates);
    }

    [Fact]
    public void OnlyChangedBindingsAreStoredSoNewDefaultsStillReachUsers()
    {
        var resolved = Shortcuts.Resolve(new Dictionary<string, string> { ["conversation.new"] = "ctrl+shift+n" });

        Assert.Equal("Ctrl+Shift+N", resolved["conversation.new"]);   // 改过的用新的
        Assert.Equal("Ctrl+B", resolved["sidebar.toggle"]);           // 没改过的跟默认
    }

    [Fact]
    public void FixedCommandsIgnoreAttemptsToRebindThem()
    {
        // Enter 发送是写死的，改了也不生效，否则用户可能把自己锁在外面
        var resolved = Shortcuts.Resolve(new Dictionary<string, string> { ["chat.send"] = "Ctrl+Q" });
        Assert.Equal("Enter", resolved["chat.send"]);
    }

    [Fact]
    public void ABindingCanBeClearedToDisableThatCommand()
    {
        var resolved = Shortcuts.Resolve(new Dictionary<string, string> { ["chat.search"] = "" });
        Assert.Equal("", resolved["chat.search"]);
    }

    [Theory]
    [InlineData("B", "needsModifier")]        // 单个字母会把正常打字吃掉
    [InlineData("1", "needsModifier")]
    [InlineData("Ctrl+C", "reserved")]        // 复制粘贴这些让给系统
    [InlineData("Alt+F4", "reserved")]
    [InlineData("", "empty")]
    public void UnusableBindingsAreRejectedWithAReason(string binding, string reason)
    {
        var (ok, why) = Shortcuts.Validate(binding);
        Assert.False(ok);
        Assert.Equal(reason, why);
    }

    [Theory]
    [InlineData("Ctrl+N")]
    [InlineData("F11")]        // 功能键不需要修饰键
    [InlineData("Escape")]
    [InlineData("Ctrl+Shift+B")]
    public void NormalBindingsAreAccepted(string binding)
    {
        Assert.True(Shortcuts.Validate(binding).Ok);
    }

    [Fact]
    public void ConflictsArePointedAtTheCommandAlreadyUsingTheKey()
    {
        var resolved = Shortcuts.Resolve(null);

        // Ctrl+B 已经是「切换左侧栏」
        Assert.Equal("sidebar.toggle", Shortcuts.Conflict("chat.search", "ctrl+b", resolved));
        // 自己原来的键不算冲突
        Assert.Null(Shortcuts.Conflict("sidebar.toggle", "Ctrl+B", resolved));
        // 没人用的键没问题
        Assert.Null(Shortcuts.Conflict("chat.search", "Ctrl+Shift+K", resolved));
    }

    [Fact]
    public void OnlyTheWindowToggleAndSelectionTranslateAreGlobalHotkeys()
    {
        // 全局热键会抢占整个系统的按键，多了会和别的软件打架。
        // 划词翻译必须是全局的：它就是在别的程序里用的。
        var global = Shortcuts.All.Where(c => c.Global).Select(c => c.Id).ToList();
        Assert.Equal(new[] { "window.toggle", "selection.translate" }, global);
    }
}
