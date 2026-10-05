using Flyknit.Core.Agent;
using Flyknit.Core.Chat;
using Flyknit.Core.Context;
using Flyknit.Core.Gateway;
using Flyknit.Core.Memory;
using Flyknit.Core.Security;
using Flyknit.Core.Tools;
using Xunit;

namespace Flyknit.Core.Tests;

public class MemorySystemTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flyknit-memsys").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    /// <summary>记录请求并返回固定内容的假模型。</summary>
    private sealed class FakeGateway : IChatGateway
    {
        private readonly Func<ChatRequest, ChatTurn> _reply;
        public List<ChatRequest> Requests { get; } = new();
        public FakeGateway(Func<ChatRequest, ChatTurn> reply) => _reply = reply;

        public Task<ChatTurn> CompleteAsync(ChatRequest request, IStreamSink? sink, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(_reply(request));
        }
    }

    // ---------- 长期记忆 ----------

    [Fact]
    public void MemoryKindsGoToSectionsAndDeduplicate()
    {
        var store = new MemoryStore(_dir);
        store.EnsureDefaults();
        Assert.True(store.Add(MemoryKind.Preference, "报表默认保存到 D:\\报表"));
        Assert.False(store.Add(MemoryKind.Preference, "报表默认保存到 D:\\报表 "));        // 完全相同
        Assert.False(store.Add(MemoryKind.Preference, "报表默认保存到D:\\报表。"));        // 高度相似
        Assert.True(store.Add(MemoryKind.Fact, "ERP 地址是 http://erp.local"));
        Assert.True(store.Add(MemoryKind.Lesson, "读取 .xls 旧格式要先另存为 .xlsx"));
        Assert.True(store.Add(MemoryKind.Success, "周报用透视表按车间汇总"));

        var items = store.List();
        Assert.Equal(4, items.Count);
        Assert.Equal(MemoryKind.Preference, items.Single(i => i.Text.Contains("报表默认")).Kind);
        Assert.Equal(MemoryKind.Lesson, items.Single(i => i.Text.Contains(".xls")).Kind);
        Assert.Contains("## 偏好与习惯\n- 报表默认保存到", store.Read(MemoryStore.MemoryFile));

        var prompt = store.BuildPromptSection("帮我生成周报");
        Assert.Contains("<用户偏好与习惯>", prompt);
        Assert.Contains("【经验】周报用透视表按车间汇总", prompt);
        Assert.DoesNotContain("<关于用户>", prompt); // role.md 还是空模板

        var id = items.Single(i => i.Text.Contains("ERP")).Id;
        Assert.True(store.Delete(id));
        Assert.Equal(3, store.List().Count);
    }

    [Fact]
    public void LegacyMemoryFileIsStillRead()
    {
        var store = new MemoryStore(_dir);
        File.WriteAllText(Path.Combine(_dir, MemoryStore.MemoryFile), "# 长期记忆\n\n- 日报放在 D:\\日报（2026-09-01）\n");
        var item = Assert.Single(store.List());
        Assert.Equal(MemoryKind.Fact, item.Kind);
        Assert.Equal("日报放在 D:\\日报", item.Text);
        Assert.Equal(new DateOnly(2026, 9, 1), item.Date);

        store.Add(MemoryKind.Preference, "邮件用英文写");
        Assert.Equal(2, store.List().Count);
    }

    [Fact]
    public void LessonsAreOnlyIncludedWhenRelevantOnceThereAreMany()
    {
        var store = new MemoryStore(_dir);
        store.EnsureDefaults();
        for (var i = 0; i < 10; i++)
        {
            store.Add(MemoryKind.Lesson, $"第{i}号设备的 {(char)('A' + i)}{(char)('K' + i)} 报警需要先复位");
        }
        store.Add(MemoryKind.Lesson, "安装 ERP 客户端前要先关闭杀毒软件的实时防护");
        var prompt = store.BuildPromptSection("帮我安装 ERP 客户端");
        Assert.Contains("关闭杀毒软件", prompt);
        Assert.DoesNotContain("第3号设备", prompt);
    }

    // ---------- 历史任务 ----------

    [Fact]
    public void EpisodesAreFoundBySimilarity()
    {
        var episodes = new EpisodeStore(_dir);
        episodes.Add(new Episode { ConversationId = "c1", Title = "生成质检周报", Task = "根据 9 月质检数据生成质检周报 Excel", Procedure = "1. 读取数据 2. 透视表", Outcome = "success" });
        episodes.Add(new Episode { ConversationId = "c2", Title = "安装 ERP 客户端", Task = "从共享盘安装 ERP 客户端", Outcome = "failure", Lessons = { "要先关闭杀毒软件" } });

        var found = new EpisodeStore(_dir).Search("帮我做一下这周的质检周报");
        Assert.Equal("生成质检周报", Assert.Single(found).Episode.Title);
        Assert.Empty(episodes.Search("今天天气怎么样"));

        var section = EpisodeStore.BuildPromptSection(episodes.Search("安装 ERP"));
        Assert.Contains("教训：要先关闭杀毒软件", section);
        Assert.Contains("失败", section);

        episodes.SetFeedback("c1", -1);
        Assert.Equal("partial", episodes.List().Single(e => e.ConversationId == "c1").Outcome);
    }

    // ---------- 复盘 ----------

    private const string ReflectionJson = """
        好的：
        ```json
        {
          "worth_saving": true, // 有复用价值
          "title": "生成质检周报",
          "summary": "读取 D:\\质检\\9月.xlsx，生成周报",
          "outcome": "success",
          "procedure": "1. 读取数据\n2. 按车间汇总\n3. 保存到 D:\\报表",
          "preferences": ["报表默认保存到 D:\\报表"],
          "facts": [],
          "successes": ["按车间汇总后再画图更清楚"],
          "lessons": [],
          "skill": {"name": "QC Weekly Report", "description": "生成质检周报", "body": "1. 读取质检数据\n2. 按车间汇总\n3. 保存到 D:\\报表"}
        }
        ```
        """;

    [Fact]
    public void ReflectionParsesLooseJson()
    {
        var r = Reflector.Parse(ReflectionJson);
        Assert.NotNull(r);
        Assert.True(r!.WorthSaving);
        Assert.Equal("生成质检周报", r.Title);
        Assert.Single(r.Preferences);
        Assert.Equal("QC Weekly Report", r.Skill!.Name);
        Assert.Null(Reflector.Parse("没有 JSON"));
    }

    [Fact]
    public async Task ReflectionWritesMemoryEpisodesAndLearnsSkillAfterRepeatedSuccess()
    {
        var memory = new MemoryStore(Path.Combine(_dir, "memory"));
        memory.EnsureDefaults();
        var episodes = new EpisodeStore(Path.Combine(_dir, "memory"));
        var skills = Path.Combine(_dir, "skills", "learned");
        var gateway = new FakeGateway(_ => new ChatTurn { Content = ReflectionJson });
        var reflector = new Reflector(gateway, memory, episodes, skills);
        var call = new ToolCall("c1", "read_file", "{\"path\":\"D:\\\\质检\\\\9月.xlsx\"}");
        var input = new ReflectionInput
        {
            ConversationId = "conv",
            UserRequest = "生成质检周报",
            Messages = new[] { ChatMessage.Assistant("", new[] { call }), ChatMessage.ToolResult(call, "数据…"), ChatMessage.Assistant("完成") },
        };

        var first = await reflector.ReflectAsync(input, CancellationToken.None);
        Assert.NotNull(first);
        Assert.NotNull(first!.Episode);
        Assert.Equal(new[] { "read_file" }, first.Episode!.Tools);
        Assert.Equal(2, first.Added.Count); // 一条偏好 + 一条经验
        Assert.Null(first.SkillName);        // 第一次成功还不沉淀技能
        Assert.Contains("执行经过", gateway.Requests[0].Messages[1].Content);

        var second = await reflector.ReflectAsync(input, CancellationToken.None);
        Assert.Empty(second!.Added);          // 记忆去重
        Assert.Equal("qc-weekly-report", second.SkillName);
        var skillFile = Path.Combine(skills, "qc-weekly-report", "SKILL.md");
        Assert.Contains("source: learned", File.ReadAllText(skillFile));
        var parsed = Skills.SkillCatalog.TryParse(skillFile, false);
        Assert.Equal("qc-weekly-report", parsed!.Name);
        Assert.Equal(2, episodes.List().Count);
    }

    [Fact]
    public void OnlyToolRunsOrFeedbackTriggerReflection()
    {
        Assert.False(Reflector.ShouldReflect(new[] { ChatMessage.Assistant("你好") }, 0));
        Assert.True(Reflector.ShouldReflect(new[] { ChatMessage.Assistant("你好") }, -1));
        Assert.True(Reflector.ShouldReflect(new[] { ChatMessage.Assistant("", new[] { new ToolCall("a", "list_dir", "{}") }) }, 0));
    }

    // ---------- 上下文压缩 ----------

    [Fact]
    public void OldToolOutputsArePrunedButRecentOnesKept()
    {
        var history = new List<ChatMessage> { ChatMessage.System("sys"), ChatMessage.User("go") };
        var originals = new List<ChatMessage>();
        for (var i = 0; i < 6; i++)
        {
            var call = new ToolCall($"c{i}", "read_file", "{}");
            history.Add(ChatMessage.Assistant("", new[] { call }));
            var result = ChatMessage.ToolResult(call, new string('x', 5000));
            originals.Add(result);
            history.Add(result);
        }
        var pruned = ContextManager.PruneToolOutputs(history, keepRecent: 2, keepChars: 100);
        Assert.Equal(4, pruned);
        var tools = history.Where(m => m.Role == ChatRole.Tool).ToList();
        Assert.Contains("已省略", tools[0].Content);
        Assert.Equal(5000, tools[5].Content.Length);
        Assert.Equal(5000, originals[0].Content.Length); // 原消息对象不被修改（数据库中保存完整内容）
        Assert.Equal(originals[0].Id, tools[0].Id);
    }

    [Fact]
    public async Task LongConversationIsCompactedIntoSummary()
    {
        var gateway = new FakeGateway(_ => new ChatTurn { Content = "<think>x</think>## 用户的目标与要求\n整理日报" });
        var manager = new ContextManager(gateway, "系统提示", contextLength: 20000, options: new ContextOptions { ReserveOutputTokens = 2000 });
        CompactionInfo? raised = null;
        manager.Compacted += i => raised = i;

        var history = new List<ChatMessage> { ChatMessage.System("系统提示") };
        for (var i = 0; i < 30; i++)
        {
            history.Add(ChatMessage.User($"第 {i} 个问题 " + new string('问', 400)));
            history.Add(ChatMessage.Assistant($"第 {i} 个回答 " + new string('答', 400)));
        }
        var last = history[^1];
        var before = manager.Measure(history);
        Assert.True(before > manager.Budget * 0.75);

        var info = await manager.PrepareAsync(history, CancellationToken.None);

        Assert.NotNull(info);
        Assert.Same(info, raised);
        Assert.True(info!.TokensAfter < manager.Budget * 0.75);
        Assert.Equal(ChatRole.System, history[0].Role);
        Assert.Contains("<较早对话的摘要>", history[0].Content);
        Assert.Contains("整理日报", history[0].Content);
        Assert.DoesNotContain("<think>", history[0].Content);
        Assert.Equal(ChatRole.User, history[1].Role);        // 从一条用户消息开始
        Assert.Same(last, history[^1]);                      // 最近的内容保留原文
        Assert.Contains(info.UptoMessageId, history.Select(m => m.Id).Append(info.UptoMessageId)); // ID 有效
        Assert.DoesNotContain(history, m => m.Id == info.UptoMessageId);

        // 摘要请求使用对话的模型、不带工具
        var request = Assert.Single(gateway.Requests);
        Assert.Null(request.Tools);
        Assert.Contains("第 0 个问题", request.Messages[1].Content);
    }

    [Fact]
    public async Task ShortConversationIsUntouched()
    {
        var gateway = new FakeGateway(_ => throw new InvalidOperationException("不应调用"));
        var manager = new ContextManager(gateway, "sys", contextLength: 131072);
        var history = new List<ChatMessage> { ChatMessage.System("sys"), ChatMessage.User("你好") };
        Assert.Null(await manager.PrepareAsync(history, CancellationToken.None));
        Assert.Equal(2, history.Count);
    }

    [Fact]
    public async Task CompactionFailureFallsBackToPruning()
    {
        var gateway = new FakeGateway(_ => throw new GatewayException("模型不可用"));
        var manager = new ContextManager(gateway, "sys", contextLength: 12000, options: new ContextOptions { ReserveOutputTokens = 2000 });
        var history = new List<ChatMessage> { ChatMessage.System("sys"), ChatMessage.User("go") };
        for (var i = 0; i < 8; i++)
        {
            var call = new ToolCall($"c{i}", "read_file", "{}");
            history.Add(ChatMessage.Assistant("", new[] { call }));
            history.Add(ChatMessage.ToolResult(call, new string('数', 3000)));
        }
        await manager.PrepareAsync(history, CancellationToken.None);
        Assert.True(manager.Measure(history) < manager.Budget);
    }

    [Fact]
    public void TokenEstimateTreatsChineseAndEnglishDifferently()
    {
        Assert.InRange(TokenEstimator.Estimate(new string('中', 100)), 70, 80);
        Assert.InRange(TokenEstimator.Estimate(new string('a', 100)), 28, 32);
    }

    // ---------- 用量 ----------

    [Fact]
    public void SseUsageChunkIsParsed()
    {
        var acc = new SseAccumulator();
        acc.FeedLine("data: {\"choices\":[{\"delta\":{\"content\":\"好\"}}]}");
        acc.FeedLine("data: {\"choices\":[],\"usage\":{\"prompt_tokens\":120,\"completion_tokens\":30,\"total_tokens\":150}}");
        acc.FeedLine("data: [DONE]");
        var turn = acc.Build("qwen", 32768);
        Assert.Equal(new TokenUsage(120, 30), turn.Usage);
        Assert.Equal(150, turn.Usage!.Total);
        Assert.Equal(32768, turn.ContextLength);
    }

    [Fact]
    public async Task AgentLoopRecordsModelAndSumsUsage()
    {
        var calls = 0;
        var gateway = new FakeGateway(_ => ++calls == 1
            ? new ChatTurn { ToolCalls = new[] { new ToolCall("a", "update_plan", "{\"steps\":[]}") }, ModelName = "qwen3.8-max", Usage = new TokenUsage(100, 10) }
            : new ChatTurn { Content = "完成", ModelName = "qwen3.8-max", Usage = new TokenUsage(150, 20) });
        var ctx = new ToolContext { Policy = CommandPolicy.Default(), ConversationId = "c", Workspace = _dir };
        var result = await new AgentLoop(gateway, ToolRegistry.CreateDefault(), new NoConfirm())
            .RunAsync(new List<ChatMessage> { ChatMessage.User("x") }, Scenes.Agent, ctx, new NullObserver(), true, CancellationToken.None);

        Assert.Equal(new TokenUsage(250, 30), result.Usage);
        var final = result.NewMessages[^1];
        Assert.Equal("qwen3.8-max", final.ModelName);
        Assert.Equal(150, final.PromptTokens);
        Assert.Equal(20, final.CompletionTokens);
    }

    [Fact]
    public async Task MemorySearchToolFindsMemoriesAndEpisodes()
    {
        var memory = new MemoryStore(_dir);
        memory.EnsureDefaults();
        memory.Add(MemoryKind.Preference, "周报用 Excel 格式，表头加粗");
        var episodes = new EpisodeStore(_dir);
        episodes.Add(new Episode { Title = "生成周报", Task = "生成本周质检周报", Procedure = "1. 读取 2. 汇总" });
        var ctx = new ToolContext { Policy = CommandPolicy.Default(), ConversationId = "c", Memory = memory, Episodes = episodes };
        var args = System.Text.Json.JsonDocument.Parse("{\"query\":\"周报\"}").RootElement;

        var result = await new MemorySearchTool().ExecuteAsync(args, ctx, CancellationToken.None);
        Assert.Contains("[偏好] 周报用 Excel 格式", result.Output);
        Assert.Contains("生成周报", result.Output);
        Assert.Equal(1, episodes.List()[0].Uses);
    }

    [Fact]
    public void PromptIncludesWorkspaceInstructionsAndRelevantEpisodes()
    {
        var ws = Path.Combine(_dir, "ws");
        Directory.CreateDirectory(ws);
        File.WriteAllText(Path.Combine(ws, "AGENTS.md"), "输出文件统一放到 output 子目录");
        var memory = new MemoryStore(Path.Combine(_dir, "m"));
        memory.EnsureDefaults();
        var episodes = new EpisodeStore(Path.Combine(_dir, "m"));
        episodes.Add(new Episode { Title = "生成质检周报", Task = "生成质检周报 Excel", Procedure = "用透视表" });

        var builder = new PromptBuilder(memory, null, episodes);
        var prompt = builder.Build(new PromptContext { Mode = ConversationMode.Agent, Workspace = ws, Query = "生成这周的质检周报" });

        Assert.Contains("<工作区说明 文件=\"AGENTS.md\">", prompt);
        Assert.Contains("output 子目录", prompt);
        Assert.Contains("<相关的历史任务>", prompt);
        Assert.Contains("用透视表", prompt);
        Assert.Equal(1, builder.EpisodesUsed);
        Assert.Contains("<记忆使用说明>", prompt);
    }

    private sealed class NoConfirm : IConfirmationHandler
    {
        public Task<ConfirmChoice> ConfirmAsync(ConfirmRequest request, CancellationToken ct) => Task.FromResult(ConfirmChoice.Reject);
    }

    private sealed class NullObserver : IAgentObserver
    {
        public void OnContent(string delta) { }
        public void OnReasoning(string delta) { }
        public void OnAssistantMessage(ChatMessage message) { }
        public void OnToolStarted(ToolCall call, string summary, PolicyDecision decision) { }
        public void OnToolFinished(ToolCall call, ToolResult result, string decision) { }
        public void OnToolMessage(ChatMessage message) { }
        public void OnPlanUpdated(IReadOnlyList<PlanItem> plan) { }
    }
}
