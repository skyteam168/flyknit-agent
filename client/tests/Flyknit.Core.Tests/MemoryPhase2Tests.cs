using System.Text.Json;
using Flyknit.Core.Agent;
using Flyknit.Core.Chat;
using Flyknit.Core.Context;
using Flyknit.Core.Gateway;
using Flyknit.Core.Memory;
using Flyknit.Core.Skills;
using Flyknit.Core.Storage;
using Flyknit.Core.Tools;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>记忆第二阶段：历史任务入库、教训只存一份、技能生命周期、计划随对话保存、记忆指标。</summary>
public class MemoryPhase2Tests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flyknit-mem2").FullName;
    private DateTime _now = new(2026, 10, 7, 10, 0, 0);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools(); // Windows 上池里的连接会占着数据库文件
        Directory.Delete(_dir, true);
    }

    private string Db => Path.Combine(_dir, MemoryStore.DatabaseFile);

    private sealed class FakeGateway : IChatGateway
    {
        private readonly string _reply;
        public FakeGateway(string reply) => _reply = reply;
        public Task<ChatTurn> CompleteAsync(ChatRequest request, IStreamSink? sink, CancellationToken ct) =>
            Task.FromResult(new ChatTurn { Content = _reply });
    }

    private string Scalar(string sql)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Db, Pooling = false }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar()) ?? "";
    }

    // ---------- 历史任务入库，教训只存一份 ----------

    [Fact]
    public void LegacyEpisodesFileIsImportedOnceAndLessonsBecomeReferences()
    {
        var memory = new MemoryStore(_dir);
        var lesson = memory.Save(MemoryKind.Lesson, "读取 .xls 旧格式要先另存为 .xlsx", "reflect").Id!;
        var legacy = new[]
        {
            new Episode
            {
                Id = "e1", ConversationId = "c1", Title = "汇总销售表", Task = "把三个 xls 汇总", Outcome = "success",
                // 第一条在记忆库里有；第二条以前被用户删掉了，不能借着导入又回来
                Lessons = { "读取 .xls 旧格式要先另存为 .xlsx", "王工的备用钥匙在抽屉第二格" },
                CreatedAt = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.FromHours(8)),
            },
        };
        var file = Path.Combine(_dir, EpisodeStore.LegacyFileName);
        File.WriteAllText(file, JsonSerializer.Serialize(legacy, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        File.WriteAllText(file + ".bak", "old");

        var episodes = new EpisodeStore(_dir);

        Assert.False(File.Exists(file));          // 导入后不留第二份
        Assert.False(File.Exists(file + ".bak"));
        var e = Assert.Single(episodes.List());
        Assert.Equal(new[] { lesson }, e.LessonIds);
        Assert.Equal(new[] { "读取 .xls 旧格式要先另存为 .xlsx" }, e.Lessons);
        Assert.Equal(legacy[0].CreatedAt, e.CreatedAt); // 事件时间保留
        Assert.DoesNotContain("另存为", Scalar("SELECT group_concat(lesson_ids || summary || procedure) FROM episodes"));

        // 再开一次不会重复导入
        Assert.Single(new EpisodeStore(_dir).List());

        // 删掉这条教训：历史任务里也跟着没了（同一个实例马上生效，不能有缓存）
        Assert.Single(episodes.Search("汇总销售表"));
        memory.Forget(lesson);
        Assert.Empty(episodes.List()[0].Lessons);
        Assert.DoesNotContain("另存为", EpisodeStore.BuildPromptSection(episodes.Search("汇总销售表")));
    }

    [Fact]
    public void MergedLessonsFollowTheirSuccessor()
    {
        var memory = new MemoryStore(_dir);
        var a = memory.Save(MemoryKind.Lesson, "python -c 里不要写多行代码", "reflect").Id!;
        var b = memory.Save(MemoryKind.Lesson, "Windows 下 python -c 引号容易出错，改写成脚本文件", "reflect").Id!;
        var episodes = new EpisodeStore(_dir);
        episodes.Add(new Episode { Title = "跑脚本", LessonIds = { a, b } });

        var merged = memory.Merge(new[] { a, b }, "多行或带引号的 Python 代码写成 .py 脚本再运行，不要用 python -c");

        var e = Assert.Single(episodes.List());
        Assert.Equal(new[] { merged }, e.LessonIds);
        Assert.Contains("写成 .py 脚本", Assert.Single(e.Lessons));
    }

    [Fact]
    public void ReflectionStoresTheLessonOnceAndTheEpisodePointsAtIt()
    {
        var memory = new MemoryStore(_dir);
        var episodes = new EpisodeStore(_dir);
        var reply = """
            {"worth_saving": true, "title": "安装 ERP 客户端", "summary": "第一次装失败，关掉杀毒软件后装好了", "outcome": "success",
             "procedure": "1. 关闭杀毒软件 2. 运行安装包",
             "preferences": [], "facts": [], "successes": [],
             "lessons": [{"text": "安装 ERP 客户端前要先关闭杀毒软件", "origin": "inferred", "confidence": 0.9}],
             "skill": null}
            """;
        var call = new ToolCall("c1", "run_command", "{}");
        var report = new Reflector(new FakeGateway(reply), memory, episodes, null).Apply(new ReflectionInput
        {
            ConversationId = "c",
            UserRequest = "帮我装一下 ERP 客户端",
            Messages = new[] { ChatMessage.Assistant("", new[] { call }), ChatMessage.ToolResult(call, "安装失败"), ChatMessage.Assistant("装好了") },
        }, Reflector.Parse(reply)!);

        var lesson = Assert.Single(memory.List(), i => i.Kind == MemoryKind.Lesson);
        Assert.Equal(new[] { lesson.Id }, report.Episode!.LessonIds);
        var stored = Assert.Single(episodes.List());
        Assert.Equal(new[] { lesson.Text }, stored.Lessons);
        // 正文只在记忆表里有
        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM episodes WHERE lesson_ids LIKE '%杀毒%'"));

        // 提示词里同一条教训不重复出现：记忆区块已经放了，历史任务里就不再列
        var found = episodes.Search("安装 ERP 客户端");
        Assert.Contains("教训：安装 ERP", EpisodeStore.BuildPromptSection(found));
        Assert.DoesNotContain("教训：", EpisodeStore.BuildPromptSection(found, new[] { lesson.Id }));
    }

    // ---------- 学习技能的生命周期 ----------

    private const string Body = "1. 打开共享盘的安装包\n2. 先关闭杀毒软件\n3. 一路下一步";

    private (LearnedSkills Ledger, SkillCatalog Catalog, string Root) NewSkills()
    {
        var root = Path.Combine(_dir, "learned");
        Directory.CreateDirectory(root);
        return (new LearnedSkills(root, Db) { Clock = () => _now }, new SkillCatalog().AddRoot(root, SkillSource.Learned), root);
    }

    [Fact]
    public void LearnedSkillsStartAsCandidatesAndGraduateAfterARealSuccess()
    {
        var (ledger, catalog, root) = NewSkills();
        Assert.Equal("erp-install", ledger.Propose("ERP Install", "安装 ERP 客户端", Body));
        catalog.Refresh();
        var skill = catalog.Find("erp-install")!;
        Assert.Equal(LearnedSkillStatus.Candidate, skill.LearnedStatus);
        Assert.Contains("试用中", catalog.BuildPromptSection());

        // 候选再被提出：修订成 v2，旧版本留档
        Assert.Equal("erp-install", ledger.Propose("erp-install", "安装 ERP 客户端", Body + "\n4. 重启电脑"));
        Assert.True(File.Exists(Path.Combine(root, "erp-install", "versions", "v1.md")));
        catalog.Refresh();
        skill = catalog.Find("erp-install")!;
        Assert.Equal(2, LearnedSkillStatus.VersionOf(skill));

        // 实际用了、任务完成 → 转正
        var changed = ledger.RecordRun("c1", "m1", new[] { skill }, ok: true);
        Assert.Equal(new[] { ("erp-install", LearnedSkillStatus.Active) }, changed);
        catalog.Refresh();
        skill = catalog.Find("erp-install")!;
        Assert.Equal(LearnedSkillStatus.Active, skill.LearnedStatus);
        Assert.DoesNotContain("试用中", catalog.BuildPromptSection());
        Assert.Equal(new SkillStats(1, 1, 0), ledger.Stats("erp-install", 2));

        // 已启用、一直好用的：复盘再提也不去改它
        Assert.Null(ledger.Propose("erp-install", "安装 ERP 客户端", "完全不同的步骤，应该被忽略掉的"));
    }

    [Fact]
    public void LearnedSkillsThatKeepFailingAreRetiredAndCanBeRevived()
    {
        var (ledger, catalog, _) = NewSkills();
        ledger.Propose("erp-install", "安装 ERP 客户端", Body);
        catalog.Refresh();
        ledger.RecordRun("c1", "m1", new[] { catalog.FindAny("erp-install")! }, ok: true);
        catalog.Refresh();

        // 一次中途被停、一次被暂停、一次完成了但被点踩：失败 3 次 > 成功 0 次（被踩的那次不算成功）
        ledger.RecordRun("c2", "m2", new[] { catalog.FindAny("erp-install")! }, ok: false);
        ledger.RecordRun("c3", "m3", new[] { catalog.FindAny("erp-install")! }, ok: false);
        Assert.Equal(LearnedSkillStatus.Active, catalog.FindAny("erp-install")!.LearnedStatus);
        Assert.Equal(new[] { "erp-install" }, ledger.ApplyFeedback("m1", -1));
        var review = ledger.Review(catalog.FindAny("erp-install")!);
        Assert.Equal(("erp-install", LearnedSkillStatus.Retired), review);

        catalog.Refresh();
        Assert.Null(catalog.Find("erp-install"));                  // 不再提供给模型
        Assert.False(catalog.FindAny("erp-install")!.Enabled);
        Assert.DoesNotContain("erp-install", catalog.BuildPromptSection());

        // 用户手动重新启用：清掉这个版本的失败记录
        Assert.True(ledger.SetStatus(catalog.FindAny("erp-install")!, LearnedSkillStatus.Active));
        catalog.Refresh();
        Assert.NotNull(catalog.Find("erp-install"));
        Assert.Equal(new SkillStats(0, 0, 0), ledger.Stats("erp-install", 1));

        // 退役的技能被复盘再次提出：写成新版本、重新试用
        ledger.SetStatus(catalog.FindAny("erp-install")!, LearnedSkillStatus.Retired);
        catalog.Refresh();
        Assert.Equal("erp-install", ledger.Propose("erp-install", "安装 ERP 客户端", Body + "\n4. 装完检查版本号"));
        catalog.Refresh();
        Assert.Equal(LearnedSkillStatus.Candidate, catalog.FindAny("erp-install")!.LearnedStatus);
        Assert.Equal(2, LearnedSkillStatus.VersionOf(catalog.FindAny("erp-install")!));
    }

    [Fact]
    public void UnprovenCandidatesRetireAfterTwoFailures()
    {
        var (ledger, catalog, _) = NewSkills();
        ledger.Propose("merge-invoices", "合并发票 PDF", Body);
        catalog.Refresh();
        Assert.Empty(ledger.RecordRun("c1", "m1", new[] { catalog.FindAny("merge-invoices")! }, ok: false));
        Assert.Equal(new[] { ("merge-invoices", LearnedSkillStatus.Retired) },
            ledger.RecordRun("c2", "m2", new[] { catalog.FindAny("merge-invoices")! }, ok: false));
    }

    [Fact]
    public void UserWrittenSkillsAreNeverOverwrittenAndLoadedSkillsAreDetected()
    {
        var (ledger, _, root) = NewSkills();
        Directory.CreateDirectory(Path.Combine(root, "my-report"));
        File.WriteAllText(Path.Combine(root, "my-report", "SKILL.md"), "---\nname: my-report\ndescription: 我自己写的\n---\n内容");
        Assert.Null(ledger.Propose("my-report", "自动总结的", Body));
        Assert.Contains("我自己写的", File.ReadAllText(Path.Combine(root, "my-report", "SKILL.md")));

        var messages = new[]
        {
            ChatMessage.Assistant("", new[] { new ToolCall("1", "load_skill", """{"name":"erp-install"}"""), new ToolCall("2", "read_file", """{"name":"x"}""") }),
            ChatMessage.Assistant("", new[] { new ToolCall("3", "load_skill", """{"name":"ERP-Install"}"""), new ToolCall("4", "load_skill", "坏的 JSON") }),
        };
        Assert.Equal(new[] { "erp-install" }, LearnedSkills.LoadedIn(messages));
    }

    // ---------- 任务计划随对话保存 ----------

    [Fact]
    public void PlanIsSavedWithTheConversationAndSurvivesCompaction()
    {
        var store = new ConversationStore(Path.Combine(_dir, "history.db"));
        var conv = store.Create(ConversationMode.Agent);
        var plan = new List<PlanItem> { new("读取三个车间的数据", "completed"), new("生成汇总表", "in_progress"), new("画趋势图", "pending") };
        store.SetPlan(conv.Id, TaskPlan.Serialize(plan));

        var loaded = TaskPlan.Parse(store.Get(conv.Id)!.Plan);
        Assert.Equal(plan, loaded);
        Assert.Empty(TaskPlan.Parse("不是 JSON"));

        // 没有摘要时不额外放（计划就在对话里）；有摘要时原样附在后面
        var noSummary = new ContextManager(new FakeGateway(""), "系统提示") { Plan = () => loaded };
        Assert.DoesNotContain("<当前任务计划>", noSummary.SystemPrompt);
        var compacted = new ContextManager(new FakeGateway(""), "系统提示", "之前读了三个车间的数据") { Plan = () => loaded };
        Assert.Contains("<当前任务计划>", compacted.SystemPrompt);
        Assert.Contains("2. [>] 生成汇总表", compacted.SystemPrompt);
        Assert.Contains("1. [x] 读取三个车间的数据", compacted.SystemPrompt);

        // 都做完了就不再带
        var done = loaded.Select(p => p with { Status = "completed" }).ToList();
        Assert.DoesNotContain("<当前任务计划>", new ContextManager(new FakeGateway(""), "系统提示", "摘要") { Plan = () => done }.SystemPrompt);
    }

    [Fact]
    public async Task CompactionMidTaskKeepsThePlanVerbatim()
    {
        var plan = new List<PlanItem> { new("读取数据", "completed"), new("生成周报 Excel", "in_progress") };
        var manager = new ContextManager(new FakeGateway("## 进展\n读完了数据"), "系统提示", contextLength: 20000,
            options: new ContextOptions { ReserveOutputTokens = 2000 }) { Plan = () => plan };
        var history = new List<ChatMessage> { ChatMessage.System("系统提示") };
        for (var i = 0; i < 30; i++)
        {
            history.Add(ChatMessage.User($"第 {i} 步 " + new string('问', 400)));
            history.Add(ChatMessage.Assistant($"第 {i} 步完成 " + new string('答', 400)));
        }

        Assert.NotNull(await manager.PrepareAsync(history, CancellationToken.None));

        Assert.Contains("读完了数据", history[0].Content);
        Assert.Contains("2. [>] 生成周报 Excel", history[0].Content);
    }

    // ---------- 记忆指标 ----------

    [Fact]
    public void MetricsReportInjectionUsageAndFreshness()
    {
        var memory = new MemoryStore(_dir) { Clock = () => _now };
        var fresh = memory.Save(MemoryKind.Preference, "报表默认保存到 D:\\报表", "user").Id!;
        _now = _now.AddDays(-200);
        var old = memory.Save(MemoryKind.Fact, "ERP 地址是 erp.example.local", "reflect").Id!;
        _now = _now.AddDays(200);
        memory.Save(MemoryKind.Fact, "质检周报每周一交", "reflect");

        memory.RecordUsage("c1", "m1", new[] { fresh }, tokens: 120, episodes: 1);
        memory.RecordUsage("c1", "m2", new[] { fresh, old }, tokens: 300);
        memory.RecordUsage("c1", "m3", Array.Empty<string>());
        memory.ApplyFeedback("m2", 1);

        var m = memory.Metrics();
        Assert.Equal(3, m.Active);
        Assert.Equal(3, m.Answers);
        Assert.Equal(2, m.AnswersWithMemory);
        Assert.Equal(1.0, m.AvgItemsInjected);
        Assert.Equal(140, m.AvgTokensInjected);
        Assert.Equal(300, m.MaxTokensInjected);
        Assert.Equal(2, m.UsedRecently);
        Assert.Equal(0.67, m.UsedShare);
        Assert.Equal(1, m.NeverUsed);
        Assert.Equal(1, m.Liked);
        Assert.Equal(2, m.Fresh30);
        Assert.Equal(1, m.Stale);

        var episodes = new EpisodeStore(_dir) { Clock = () => _now };
        episodes.Add(new Episode { Id = "e1", Title = "周报", CreatedAt = new DateTimeOffset(_now) });
        episodes.MarkUsed(new[] { "e1" });
        Assert.Equal((1, 1, 1), episodes.Stats());
        Assert.NotNull(episodes.List()[0].LastUsedAt);
    }
}
