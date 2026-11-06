using Flyknit.Core.Commands;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>员工自己的快捷指令：存本机，名字规则和后台一样。</summary>
public sealed class SlashCommandTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flyknit-cmd").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (Exception) { }
    }

    [Fact]
    public void SavesAndLoadsPersonalCommands()
    {
        var store = new SlashCommandStore(Path.Combine(_dir, "commands.json"));
        Assert.Empty(store.Load());

        var problem = store.Save(new[] { new SlashCommand { Name = " /日报 ", Title = "写日报", Template = "  帮我写 {日期} 的日报 " } });

        Assert.Null(problem);
        var c = Assert.Single(store.Load());
        Assert.Equal("日报", c.Name);
        Assert.Equal("帮我写 {日期} 的日报", c.Template);
        Assert.Contains("日报", File.ReadAllText(Path.Combine(_dir, "commands.json"))); // 中文不转义，员工能直接看懂
    }

    [Fact]
    public void BadNamesDuplicatesReservedAndEmptyTemplatesAreRefused()
    {
        var store = new SlashCommandStore(Path.Combine(_dir, "commands.json"));
        Assert.NotNull(store.Save(new[] { new SlashCommand { Name = "有 空格", Template = "x" } }));
        Assert.NotNull(store.Save(new[] { new SlashCommand { Name = "btw", Template = "x" } }));
        Assert.NotNull(store.Save(new[] { new SlashCommand { Name = "a", Template = " " } }));
        Assert.NotNull(store.Save(new[] { new SlashCommand { Name = "Ab", Template = "x" }, new SlashCommand { Name = "ab", Template = "y" } }));
        Assert.Empty(store.Load()); // 不合规的一条也没存

        File.WriteAllText(Path.Combine(_dir, "commands.json"), "{坏掉的 json");
        Assert.Empty(store.Load());
    }
}
