using System.Text.Json;
using System.Text.Json.Nodes;
using Flyknit.Core.Agent;
using Flyknit.Core.Experts;
using Flyknit.Core.Mcp;
using Flyknit.Core.Security;
using Flyknit.Core.Storage;
using Flyknit.Core.Tools;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>
/// 专家：同一个智能体，换上专家的工作方式、按专家的限制筛工具。
/// 技能、连接器、工具默认全部沿用；以后新加的不用改专家就能用。
/// </summary>
public class ExpertTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flyknit-expert").FullName;

    public void Dispose()
    {
        // Windows 上连接池还攥着 history.db，不清掉删不了目录
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }

    private static McpTool Mcp(string server, string name) =>
        new(server, server.ToUpperInvariant(), new McpToolInfo(name, "", "", new JsonObject(), false, false),
            (_, _, _) => Task.FromResult(new McpCallResult(false, "ok")));

    private static ToolRegistry Registry() => ToolRegistry.CreateDefault()
        .Add(Mcp("mes", "query_orders"))
        .Add(Mcp("erp", "query_stock"));

    [Fact]
    public void ByDefaultAnExpertGetsEveryToolIncludingNewConnectors()
    {
        var all = Registry();
        var tools = ExpertRuntime.FilterTools(all, new Expert { Id = "report" });
        Assert.Equal(all.All.Select(t => t.Name).OrderBy(n => n), tools.All.Select(t => t.Name).OrderBy(n => n));

        // 以后接进来的系统：专家没改，照样能用
        all.Add(Mcp("wms", "query_bins"));
        Assert.NotNull(ExpertRuntime.FilterTools(all, new Expert { Id = "report" }).Get("mcp__wms__query_bins"));
    }

    [Fact]
    public void LimitsOnlyTakeAwayWhatTheAdminNamed()
    {
        var expert = new Expert { Id = "mes_analyst", McpServers = { "mes" }, DisallowedTools = { "run_shell" } };
        var tools = ExpertRuntime.FilterTools(Registry(), expert);
        Assert.NotNull(tools.Get("mcp__mes__query_orders"));
        Assert.Null(tools.Get("mcp__erp__query_stock"));
        Assert.Null(tools.Get("run_shell"));
        Assert.NotNull(tools.Get("read_file"));
        Assert.NotNull(tools.Get("load_skill"));
        // 原来的工具表不动：别的对话还在用
        Assert.NotNull(Registry().Get("run_shell"));
    }

    [Fact]
    public void ReadOnlyExpertsCannotWriteOrDeleteAndRunWithReadOnlyPermission()
    {
        var expert = new Expert { Id = "mes_analyst", ReadOnly = true };
        var tools = ExpertRuntime.FilterTools(Registry(), expert);
        Assert.Null(tools.Get("write_file"));
        Assert.Null(tools.Get("delete_path"));
        Assert.NotNull(tools.Get("run_shell")); // 只读权限下命令策略只放行查询类
        Assert.Equal(PermissionMode.ReadOnly, ExpertRuntime.CapPermission(expert, PermissionMode.Full));
        // 不是只读专家：员工自己选的权限照旧
        Assert.Equal(PermissionMode.Full, ExpertRuntime.CapPermission(new Expert(), PermissionMode.Full));
    }

    [Fact]
    public void PromptCarriesTheExpertsWayOfWorkingAndKnowledge()
    {
        var expert = new Expert
        {
            Id = "mes_analyst",
            Name = "MES生产分析专家",
            Description = "负责：生产分析\n不负责：财务",
            Instructions = "结论先行，给出数据来源。",
            Knowledge = "A 车间 = 1 号厂房",
            PreferredSkills = { "excel-report" },
            ReadOnly = true,
            McpServers = { "mes" },
        };
        var servers = new List<McpPromptInfo> { new("mes", "MES", 3, ""), new("erp", "ERP", 2, "") };
        var prompt = new PromptBuilder(null, null).Build(new PromptContext
        {
            Mode = ConversationMode.Agent,
            Expert = expert,
            McpServers = ExpertRuntime.FilterMcp(expert, servers),
            Permission = ExpertRuntime.CapPermission(expert, PermissionMode.Workspace),
        });
        Assert.Contains("“MES生产分析专家”", prompt);
        Assert.Contains("结论先行，给出数据来源。", prompt);
        Assert.Contains("A 车间 = 1 号厂房", prompt);
        Assert.Contains("不负责：财务", prompt);
        Assert.Contains("excel-report", prompt);
        Assert.Contains("只读专家", prompt);
        Assert.Contains("mcp__mes__", prompt);
        Assert.DoesNotContain("mcp__erp__", prompt);

        // 普通助手的提示词里没有专家这一段
        Assert.DoesNotContain("<专家身份", new PromptBuilder(null, null).Build(new PromptContext { Mode = ConversationMode.Agent }));
    }

    [Fact]
    public void CatalogKeepsTheLastListForOfflineUseAndDropsWithdrawnExperts()
    {
        var file = Path.Combine(_dir, "experts.json");
        var catalog = new ExpertCatalog(file);
        var changes = 0;
        catalog.Changed += () => changes++;

        // 服务端返回的就是这个格式
        var fromServer = JsonSerializer.Deserialize<List<Expert>>("""
            [{"id":"mes_analyst","version":2,"name":"MES生产分析专家","avatar":"🏭","examples":["A 车间产量为什么下降？"],
              "mcp_servers":["mes"],"read_only":true,"model_id":7,"allow_direct":true,"allow_delegate":true,"sort_order":0},
             {"id":"dispatch_only","name":"只给派遣用","allow_direct":false}]
            """)!;
        Assert.True(catalog.Replace(fromServer));
        Assert.False(catalog.Replace(fromServer)); // 没变化不触发
        Assert.Equal(1, changes);
        Assert.Equal(new[] { "mes_analyst" }, catalog.Callable.Select(e => e.Id));
        var mes = catalog.Get("mes_analyst")!;
        Assert.Equal(7, mes.ModelId);
        Assert.True(mes.ReadOnly);
        Assert.Equal("mes", mes.McpServers.Single());

        // 重启后没网：用本机缓存
        Assert.Equal(2, new ExpertCatalog(file).All.Count);

        // 管理员下线了：跟着消失
        catalog.Replace(fromServer.Where(e => e.Id != "mes_analyst"));
        Assert.Null(catalog.Get("mes_analyst"));
        Assert.Null(new ExpertCatalog(file).Get("mes_analyst"));
    }

    [Fact]
    public void ConversationsRememberWhichExpertWasCalled()
    {
        var store = new ConversationStore(Path.Combine(_dir, "history.db"));
        var expertConv = store.Create(ConversationMode.Agent, expertId: "mes_analyst");
        var plain = store.Create(ConversationMode.Agent);
        Assert.Equal("mes_analyst", store.Get(expertConv.Id)!.ExpertId);
        Assert.Null(store.Get(plain.Id)!.ExpertId);
        Assert.Contains(store.List("", false), c => c.ExpertId == "mes_analyst");
    }
}
