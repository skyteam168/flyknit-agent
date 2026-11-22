using Flyknit.Core.Agent;
using Flyknit.Core.Agent.Extensions;
using Flyknit.Core.Projects;
using Flyknit.Core.Storage;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>左侧「项目」：建项目（已有文件夹 / 新建文件夹）、改目标、删清单不碰文件夹；项目里的任务第一次发消息带上目标和说明。</summary>
public sealed class ProjectTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("fk-projects-").FullName;
    private readonly List<string> _log = new();
    private readonly ProjectFeature _feature;

    public ProjectTests() => _feature = new ProjectFeature(new ProjectStore(Path.Combine(_root, "data", "projects.json")), _log.Add);

    public void Dispose() => Directory.Delete(_root, true);

    private static T Prop<T>(object o, string name) => (T)o.GetType().GetProperty(name)!.GetValue(o)!;

    [Fact]
    public void CreatesAFolderOrUsesAnExistingOne()
    {
        var made = _feature.Create("订单系统", "把下单接口 p95 降到 200 毫秒以内", "用 pnpm", "", _root);
        Assert.True(Prop<bool>(made, "ok"));
        Assert.True(Directory.Exists(Path.Combine(_root, "订单系统")));

        var existing = Directory.CreateDirectory(Path.Combine(_root, "vue3-login")).FullName;
        Assert.True(Prop<bool>(_feature.Create("登录页", "", "", existing, ""), "ok"));

        var all = _feature.Store.All();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, p => p.Name == "订单系统" && p.Goal.Contains("p95") && p.Notes == "用 pnpm");
        // 存进文件，换一个实例照样读得到
        Assert.Equal(2, new ProjectStore(Path.Combine(_root, "data", "projects.json")).All().Count);
    }

    [Fact]
    public void RejectsBadNamesAndDuplicates()
    {
        Assert.False(Prop<bool>(_feature.Create("", "", "", "", _root), "ok"));
        Assert.False(Prop<bool>(_feature.Create("a/b", "", "", "", _root), "ok"));
        Assert.False(Prop<bool>(_feature.Create("..", "", "", "", _root), "ok"));
        Assert.False(Prop<bool>(_feature.Create("x", "", "", "", Path.Combine(_root, "不存在")), "ok"));
        Assert.False(Prop<bool>(_feature.Create("x", "", "", Path.Combine(_root, "不存在"), ""), "ok"));
        Assert.True(Prop<bool>(_feature.Create("app", "", "", "", _root), "ok"));
        Assert.False(Prop<bool>(_feature.Create("app", "", "", "", _root), "ok"));                           // 文件夹已经有了
        Assert.False(Prop<bool>(_feature.Create("别名", "", "", Path.Combine(_root, "app"), ""), "ok"));     // 同一个文件夹不能建两个项目
        Assert.False(Directory.Exists(Path.Combine(_root, "a")));
    }

    [Fact]
    public void UpdateOnlyChangesWhatIsGivenAndRemoveKeepsTheFolder()
    {
        _feature.Create("app", "旧目标", "旧说明", "", _root);
        var id = _feature.Store.All()[0].Id;
        Assert.True(Prop<bool>(_feature.Update(id, null, "新目标", null), "ok"));
        var p = _feature.Store.Get(id)!;
        Assert.Equal("新目标", p.Goal);
        Assert.Equal("旧说明", p.Notes);
        Assert.False(Prop<bool>(_feature.Update(id, "  ", null, null), "ok"));

        Assert.True(Prop<bool>(_feature.HandleAsync("project.remove", Args(new { id }), CancellationToken.None).Result!, "ok"));
        Assert.Empty(_feature.Store.All());
        Assert.True(Directory.Exists(Path.Combine(_root, "app"))); // 只删清单
    }

    [Fact]
    public void FindsTheNearestProjectForAFolder()
    {
        var outer = Directory.CreateDirectory(Path.Combine(_root, "mono")).FullName;
        var inner = Directory.CreateDirectory(Path.Combine(outer, "apps", "web")).FullName;
        _feature.Create("整个仓库", "", "", outer, "");
        _feature.Create("前端", "", "", inner, "");
        Assert.Equal("前端", _feature.Store.FindFor(Path.Combine(inner, "src"))!.Name);
        Assert.Equal("整个仓库", _feature.Store.FindFor(Path.Combine(outer, "apps"))!.Name);
        Assert.Null(_feature.Store.FindFor(Path.Combine(_root, "monorepo-other")));
        Assert.Null(_feature.Store.FindFor(null));
    }

    private static Flyknit.Core.Hosting.BridgeArgs Args(object o) =>
        new(System.Text.Json.JsonSerializer.SerializeToElement(o));

    private static RunScope Scope(string id, string? workspace, bool newMessage = true, ConversationMode mode = ConversationMode.Agent) => new()
    {
        Conversation = new Conversation { Id = id, Workspace = workspace, Mode = mode },
        Query = "接着做",
        RunId = "r1",
        Scene = "agent",
        NewUserMessage = newMessage,
        Confirm = new Allow(),
    };

    private sealed class Allow : IConfirmationHandler
    {
        public Task<ConfirmChoice> ConfirmAsync(ConfirmRequest request, CancellationToken ct) => Task.FromResult(ConfirmChoice.AllowOnce);
    }

    [Fact]
    public async Task TasksInAProjectSeeItsGoalOnce()
    {
        _feature.Create("订单系统", "把下单接口 p95 降到 200 毫秒以内", "接口在 api/ 下", "", _root);
        var folder = Path.Combine(_root, "订单系统");
        var ext = new ProjectRunExtension(_feature.Store);

        var first = (await ext.PrepareAsync(Scope("c1", folder), CancellationToken.None))!.TurnContext;
        Assert.Contains("<项目 名称=\"订单系统\"", first);
        Assert.Contains("p95", first);
        Assert.Contains("接口在 api/ 下", first);
        Assert.Null(await ext.PrepareAsync(Scope("c1", folder), CancellationToken.None));             // 同一个对话只给一次
        Assert.NotNull(await ext.PrepareAsync(Scope("c2", Path.Combine(folder, "api")), CancellationToken.None)); // 子文件夹里的任务也算
        Assert.Null(await ext.PrepareAsync(Scope("c3", folder, newMessage: false), CancellationToken.None));
        Assert.Null(await ext.PrepareAsync(Scope("c4", folder, mode: ConversationMode.Translate), CancellationToken.None));
        Assert.Null(await ext.PrepareAsync(Scope("c5", _root), CancellationToken.None));            // 不在项目里

        // 改了目标：再给一次新的
        _feature.Update(_feature.Store.All()[0].Id, null, "先把错误率降下来", null);
        Assert.Contains("先把错误率降下来", (await ext.PrepareAsync(Scope("c1", folder), CancellationToken.None))!.TurnContext);
    }

    [Fact]
    public async Task ProjectWithoutGoalOrNotesAddsNothing()
    {
        _feature.Create("空项目", "", "", "", _root);
        Assert.Null(await new ProjectRunExtension(_feature.Store).PrepareAsync(Scope("c1", Path.Combine(_root, "空项目")), CancellationToken.None));
    }
}
