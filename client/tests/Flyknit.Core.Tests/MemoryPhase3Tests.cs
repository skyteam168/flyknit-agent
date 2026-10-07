using System.Text.Json;
using Flyknit.Core.Chat;
using Flyknit.Core.Gateway;
using Flyknit.Core.Memory;
using Flyknit.Core.Security;
using Flyknit.Core.Tools;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>记忆第三阶段：偏好槽位、有效期、语义检索、按类别关闭学习。</summary>
public class MemoryPhase3Tests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flyknit-mem3").FullName;
    private DateTime _now = new(2026, 10, 7, 10, 0, 0);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }

    private MemoryStore NewStore() => new(_dir) { Clock = () => _now };

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    // ---------- 偏好槽位 ----------

    [Fact]
    public void ANewValueInTheSameSlotReplacesTheOldOne()
    {
        var store = NewStore();
        var first = store.Save(MemoryKind.Preference, "报表默认保存到 D:\\报表", "reflect", slot: "save_folder");
        Assert.Equal(MemoryWriteOutcome.Added, first.Outcome);

        // 说法完全不同、字面也不相似：靠槽位知道是同一件事
        var second = store.Save(MemoryKind.Preference, "生成的文件一律放到共享盘 \\\\fs01\\质检", "reflect", slot: "save_folder");
        Assert.Equal(MemoryWriteOutcome.Updated, second.Outcome);
        var current = Assert.Single(store.List(), i => i.Slot == "save_folder");
        Assert.Contains("fs01", current.Text);
        Assert.Contains("D:\\报表", Assert.Single(current.History));

        // 同一个值再说一次只加确认次数
        Assert.Equal(MemoryWriteOutcome.Reinforced, store.Save(MemoryKind.Preference, "生成的文件一律放到共享盘 \\\\fs01\\质检", "reflect", slot: "save_folder").Outcome);

        // 不同的槽位互不影响；不认识的槽位、非偏好的槽位都忽略
        store.Save(MemoryKind.Preference, "回答用中文", "reflect", slot: "reply_language");
        store.Save(MemoryKind.Preference, "邮件结尾署名质检部王工", "reflect", slot: "made_up");
        store.Save(MemoryKind.Fact, "MES 地址是 http://mes.local", "reflect", slot: "save_folder");
        var items = store.List();
        Assert.Equal(4, items.Count);
        Assert.Null(items.Single(i => i.Text.StartsWith("邮件结尾")).Slot);
        Assert.Null(items.Single(i => i.Kind == MemoryKind.Fact).Slot);

        // 放进提示词时标出是哪一类
        Assert.Contains("【文件默认保存位置】生成的文件一律放到共享盘", store.BuildPrompt("整理一下质检数据").Text);
    }

    // ---------- 有效期 ----------

    [Fact]
    public void ExpiredFactsAreHeldBackUntilConfirmed()
    {
        var store = NewStore();
        var id = store.Save(MemoryKind.Fact, "ERP 测试环境地址是 http://10.2.0.15:8080", "reflect", validDays: 30).Id!;
        store.Save(MemoryKind.Preference, "报表默认保存到 D:\\报表", "user", pinned: true, validDays: 1);

        var fresh = store.BuildPrompt("登录 ERP 测试环境");
        Assert.Contains("<长期记忆>", fresh.Text);
        Assert.Contains(id, fresh.ItemIds);
        Assert.Equal(new DateOnly(2026, 11, 6), store.List().Single(i => i.Id == id).ValidUntil);

        _now = _now.AddDays(40);
        var stale = store.BuildPrompt("登录 ERP 测试环境");
        Assert.DoesNotContain("<长期记忆>", stale.Text);
        Assert.Contains("<可能已经过时的记忆>", stale.Text);
        Assert.Contains("有效期到 2026-11-06", stale.Text);
        Assert.Contains("报表默认保存到", stale.Text);       // 置顶的是用户的长期要求，不过期
        Assert.Equal(1, store.Metrics().Expired);
        // 和当前任务无关的过期条目不拿出来打扰
        Assert.DoesNotContain("<可能已经过时的记忆>", store.BuildPrompt("写一封请假邮件").Text);

        // 用户确认还对：从今天起重新算
        Assert.True(store.Renew(id));
        var renewed = store.List().Single(i => i.Id == id);
        Assert.False(renewed.IsExpired(DateOnly.FromDateTime(_now)));
        Assert.Contains("<长期记忆>", store.BuildPrompt("登录 ERP 测试环境").Text);

        // AI 原样再记一次也算确认；用 0 改回长期有效
        _now = _now.AddDays(40);
        Assert.Equal(MemoryWriteOutcome.Reinforced, store.Save(MemoryKind.Fact, "ERP 测试环境地址是 http://10.2.0.15:8080", "tool").Outcome);
        Assert.False(store.List().Single(i => i.Id == id).IsExpired(DateOnly.FromDateTime(_now)));
        store.Save(MemoryKind.Fact, "ERP 测试环境地址是 http://10.2.0.15:8080", "user", validDays: 0);
        Assert.Null(store.List().Single(i => i.Id == id).ValidDays);
    }

    [Fact]
    public void UpdatingAnExpiringFactKeepsItsValidity()
    {
        var store = NewStore();
        var old = store.Save(MemoryKind.Fact, "ERP 测试环境地址是 http://10.2.0.15:8080", "reflect", validDays: 90).Id!;
        var updated = store.Save(MemoryKind.Fact, "ERP 测试环境搬到了 http://10.2.0.30", "tool", replaces: old);
        Assert.Equal(90, store.List().Single(i => i.Id == updated.Id).ValidDays);
    }

    // ---------- 语义检索 ----------

    /// <summary>按话题给向量：邮件类、报表类、其他。数一数一共算了几段文字。</summary>
    private sealed class TopicEmbedder : IEmbeddingGateway
    {
        public int Texts { get; private set; }
        public int Calls { get; private set; }
        public Exception? Fail { get; set; }
        public string Model { get; set; } = "text-embedding-v4";

        public Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
        {
            Calls++;
            if (Fail is not null)
            {
                throw Fail;
            }
            Texts += texts.Count;
            static float[] Of(string t) =>
                t.Contains("邮件") || t.Contains("Outlook") || t.Contains("邮箱") ? new[] { 1f, 0.1f, 0f }
                : t.Contains("报表") || t.Contains("周报") ? new[] { 0f, 1f, 0.1f }
                : new[] { 0.1f, 0f, 1f };
            return Task.FromResult(new EmbeddingResult(Model, texts.Select(Of).ToList()));
        }
    }

    [Fact]
    public async Task SemanticRetrievalFindsMemoriesByMeaning()
    {
        var store = NewStore();
        var outlook = store.Save(MemoryKind.Fact, "Outlook 账户是 wang@corp.example", "reflect").Id!;
        store.Save(MemoryKind.Fact, "周报模板在 D:\\模板\\周报.xlsx", "reflect");
        store.Save(MemoryKind.Fact, "车间三号线周五停机检修", "reflect");
        var embedder = new TopicEmbedder();
        var index = new SemanticIndex(store, embedder);
        store.Semantic = index;

        const string query = "帮我给张总发一封邮件";
        // 字面上一个字都对不上：只靠字面找不到
        Assert.DoesNotContain(outlook, store.BuildPrompt(query).ItemIds);

        var scores = await index.ScoreAsync(query, CancellationToken.None);
        Assert.NotNull(scores);
        Assert.True(index.Working);
        Assert.True(scores![outlook] > MemoryStore.SemanticFloor);
        var prompt = store.BuildPrompt(query, semantic: scores);
        Assert.Contains(outlook, prompt.ItemIds);
        Assert.DoesNotContain("三号线", prompt.Text);   // 意思不相关的仍然不放
        Assert.Equal(outlook, store.Search(query, semantic: scores)[0].Item.Id);
        Assert.Equal(4, embedder.Texts);                 // 这句话 + 3 条记忆

        // 记忆的向量存起来了：下一轮只算用户这句话
        await index.ScoreAsync("周报做完发邮件", CancellationToken.None);
        Assert.Equal(5, embedder.Texts);

        // 改了字的重算；删除的连向量一起删
        store.Forget(outlook);
        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM memory_vectors WHERE item_id = '" + outlook + "'"));

        // 换了向量模型：旧向量作废，全部重算
        embedder.Model = "bge-m3";
        await index.ScoreAsync("周报", CancellationToken.None);
        Assert.Equal(5 + 1 + 2, embedder.Texts);
        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM memory_vectors WHERE model <> 'bge-m3'"));
    }

    [Fact]
    public async Task SemanticRetrievalBacksOffWhenTheServerHasNoEmbeddingModel()
    {
        var store = NewStore();
        store.Save(MemoryKind.Fact, "Outlook 账户是 wang@corp.example", "reflect");
        var embedder = new TopicEmbedder { Fail = new GatewayException("场景 embedding 尚未配置可用模型", 503) };
        var clock = DateTime.UtcNow;
        var index = new SemanticIndex(store, embedder) { Clock = () => clock };

        Assert.Null(await index.ScoreAsync("发邮件", CancellationToken.None));
        Assert.False(index.Working);
        Assert.Contains("尚未配置", index.LastError);
        // 停用期内不再去问服务端，不拖慢对话
        Assert.Null(await index.ScoreAsync("发邮件", CancellationToken.None));
        Assert.Equal(1, embedder.Calls);

        clock = clock.AddMinutes(31);
        embedder.Fail = null;
        Assert.NotNull(await index.ScoreAsync("发邮件", CancellationToken.None));
    }

    private string Scalar(string sql)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_dir, MemoryStore.DatabaseFile), Pooling = false }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar()) ?? "";
    }

    // ---------- 按类别关闭学习 ----------

    private sealed class FakeGateway : IChatGateway
    {
        private readonly string _reply;
        public FakeGateway(string reply) => _reply = reply;
        public Task<ChatTurn> CompleteAsync(ChatRequest request, IStreamSink? sink, CancellationToken ct) =>
            Task.FromResult(new ChatTurn { Content = _reply });
    }

    private static SecuritySettings Security(params (string Key, bool Value)[] values) =>
        new(values.ToDictionary(v => v.Key, v => new SecurityItem { Value = JsonDocument.Parse(v.Value ? "true" : "false").RootElement, Locked = true }));

    [Fact]
    public void CompanyPolicyCanTurnOffLearningByCategory()
    {
        var policy = LearningPolicy.From(Security((SecuritySettings.LearnPreferences, false), (SecuritySettings.LearnEpisodes, false)));
        Assert.False(policy.Allows(MemoryKind.Preference));
        Assert.True(policy.Allows(MemoryKind.Fact));
        Assert.True(policy.Allows(MemoryKind.Lesson));
        Assert.False(policy.Episodes);
        Assert.True(policy.Skills);                       // 没下发的项按允许算
        Assert.Equal(LearningPolicy.All, LearningPolicy.From(null));
        Assert.True(LearningPolicy.From(Security(
            (SecuritySettings.LearnPreferences, false), (SecuritySettings.LearnFacts, false), (SecuritySettings.LearnExperience, false),
            (SecuritySettings.LearnEpisodes, false), (SecuritySettings.LearnSkills, false))).Nothing);

        var store = NewStore();
        var episodes = new EpisodeStore(_dir);
        var reply = """
            {"worth_saving": true, "title": "整理周报", "summary": "做好了", "outcome": "success", "procedure": "1. 汇总",
             "preferences": [{"text": "周报用横版", "origin": "user_said", "evidence": "周报用横版", "confidence": 0.9}],
             "facts": [{"text": "周报模板在 D:\\模板", "origin": "inferred", "confidence": 0.9, "valid_days": 180}],
             "successes": [], "lessons": [], "skill": null}
            """;
        var parsed = Reflector.Parse(reply)!;
        Assert.Equal(180, parsed.Facts[0].ValidDays);
        var report = new Reflector(new FakeGateway(reply), store, episodes, null) { Policy = policy }.Apply(new ReflectionInput
        {
            ConversationId = "c",
            UserRequest = "整理周报，周报用横版",
            Messages = new[] { ChatMessage.Assistant("好了") },
        }, parsed);

        Assert.Equal(1, report.Filtered["公司策略关闭了这一类学习"]);
        var fact = Assert.Single(store.List());                  // 偏好没记，信息记了
        Assert.Equal(MemoryKind.Fact, fact.Kind);
        Assert.Equal(180, fact.ValidDays);
        Assert.Null(report.Episode);                             // 历史任务也没记
        Assert.Empty(episodes.List());
    }

    [Fact]
    public async Task MemoryWriteToolRespectsPolicyAndTakesSlotsAndValidity()
    {
        var store = NewStore();
        var tool = new MemoryWriteTool();
        ToolContext Ctx(string user, SecuritySettings? security = null) =>
            new() { Policy = CommandPolicy.Default(), ConversationId = "c", Memory = store, UserRequest = user, Security = security };

        var blocked = await tool.ExecuteAsync(Args("""{"fact":"MES 地址是 http://mes.local","category":"fact"}"""),
            Ctx("打开 MES", Security((SecuritySettings.LearnFacts, false))), CancellationToken.None);
        Assert.False(blocked.Ok);
        Assert.Contains("公司策略", blocked.Output);
        Assert.Empty(store.List());

        var ok = await tool.ExecuteAsync(Args("""{"fact":"回答一律用越南语","category":"preference","evidence":"以后都用越南语回答我","slot":"reply_language"}"""),
            Ctx("以后都用越南语回答我"), CancellationToken.None);
        Assert.True(ok.Ok);
        await tool.ExecuteAsync(Args("""{"fact":"ERP 测试环境地址是 http://10.2.0.15","category":"fact","valid_days":60}"""), Ctx("登录 ERP 测试环境"), CancellationToken.None);
        Assert.Equal("reply_language", store.List().Single(i => i.Kind == MemoryKind.Preference).Slot);
        Assert.Equal(60, store.List().Single(i => i.Kind == MemoryKind.Fact).ValidDays);

        // 换语言：同一个槽位，直接取代
        await tool.ExecuteAsync(Args("""{"fact":"回答用中文","category":"preference","evidence":"还是用中文吧","slot":"reply_language"}"""),
            Ctx("还是用中文吧"), CancellationToken.None);
        Assert.Equal("回答用中文", store.List().Single(i => i.Kind == MemoryKind.Preference).Text);
    }

    [Fact]
    public void TheReflectionPromptListsEverySlot()
    {
        foreach (var slot in MemorySlots.All.Keys)
        {
            Assert.Contains(slot, Reflector.ReflectPrompt);
        }
        Assert.Equal("save_folder", Reflector.Parse("""{"preferences":[{"text":"报表存 D 盘","slot":"SAVE_FOLDER"}]}""")!.Preferences[0].Slot);
        Assert.Null(Reflector.Parse("""{"preferences":[{"text":"报表存 D 盘","slot":"desktop"}]}""")!.Preferences[0].Slot);
    }
}
