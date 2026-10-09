using Flyknit.Core.Agent;
using Flyknit.Core.Chat;
using Flyknit.Core.Gateway;
using Flyknit.Core.Memory;
using Flyknit.Core.Skills;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>
/// 学到的技能要真的被用上：和任务明显相关的直接把全文放进提示词（不等模型自己想起来 load_skill），
/// 匹配同时看字面、关键词和语义；用上了就记一次。
/// </summary>
public class SkillReuseTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flyknit-skill-reuse").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }

    private string Learned => Path.Combine(_dir, "learned");

    private const string MailSteps = "1. 打开 Outlook 经典版\n2. 收件箱按时间倒序，只看今天的未读邮件\n3. 按发件人分组，每封一句话总结";

    private (LearnedSkills Ledger, SkillCatalog Catalog) Setup()
    {
        var ledger = new LearnedSkills(Learned, Path.Combine(_dir, "history.db"));
        ledger.Propose("outlook-mail-summary", "用户要求检查新邮件或进行邮件汇总时使用", MailSteps);
        ledger.Propose("send-report-via-outlook", "需要把生成的报告文件通过本机 Outlook 经典版发送给指定收件人时使用",
            "1. 确认附件路径存在\n2. 用 Outlook COM 新建邮件，添加附件\n3. 发送前把收件人和主题给用户确认");
        ledger.Propose("template-based-ppt-report", "需要基于固定 PPT 模板生成规范版汇报（如周报）并做结构、配色、页码校验时使用",
            "1. 复制模板\n2. 按大纲填内容\n3. 检查配色和页码");
        var catalog = new SkillCatalog().AddRoot(Learned, SkillSource.Learned);
        catalog.Refresh();
        return (ledger, catalog);
    }

    [Fact]
    public void AClearlyRelatedSkillIsGivenInFull()
    {
        var (_, catalog) = Setup();

        var (text, preloaded) = catalog.BuildPrompt("帮我看看今天有什么新邮件", null);
        Assert.Equal(new[] { "outlook-mail-summary" }, preloaded);
        Assert.Contains("<推荐技能 名称=\"outlook-mail-summary\">", text);
        Assert.Contains("按发件人分组，每封一句话总结", text);          // 正文直接在提示词里，不用等模型 load_skill
        Assert.Contains("还在试用", text);                              // 候选技能带着提醒
        Assert.Contains("- outlook-mail-summary：用户要求检查新邮件或进行邮件汇总时使用（已在上面给出全文）", text);
        Assert.Contains("- send-report-via-outlook：", text);           // 其余的照常列名字
        Assert.DoesNotContain("1. 确认附件路径存在", text);
    }

    [Fact]
    public void UnrelatedRequestsGetOnlyTheList()
    {
        var (_, catalog) = Setup();
        var (text, preloaded) = catalog.BuildPrompt("把车间排班表翻译成越南语", null);
        Assert.Empty(preloaded);
        Assert.DoesNotContain("<推荐技能", text);
        Assert.Contains("<可用技能>", text);
        Assert.Empty(catalog.BuildPrompt(null, null).Preloaded);
    }

    [Fact]
    public void MeaningCountsWhenTheWordsDoNotMatch()
    {
        var (_, catalog) = Setup();
        const string query = "把这份周报发给张经理";
        // 字面上和“通过 Outlook 发送给收件人”对不上
        Assert.DoesNotContain("send-report-via-outlook", catalog.BuildPrompt(query, null).Preloaded);

        var semantic = new Dictionary<string, double>
        {
            [SemanticIndex.SkillPrefix + "send-report-via-outlook"] = 0.71,
            [SemanticIndex.SkillPrefix + "outlook-mail-summary"] = 0.42,
            [SemanticIndex.SkillPrefix + "template-based-ppt-report"] = 0.38,
        };
        var (text, preloaded) = catalog.BuildPrompt(query, semantic);
        Assert.Equal(new[] { "send-report-via-outlook" }, preloaded);
        Assert.Contains("发送前把收件人和主题给用户确认", text);
        Assert.Equal("send-report-via-outlook", catalog.Rank(query, semantic)[0].Skill.Name);
    }

    [Fact]
    public void AtMostTwoSkillsAreGivenInFull()
    {
        var (_, catalog) = Setup();
        var semantic = catalog.Skills.ToDictionary(s => SemanticIndex.SkillPrefix + s.Name, _ => 0.8);
        Assert.Equal(SkillCatalog.PreloadLimit, catalog.BuildPrompt("随便什么", semantic).Preloaded.Count);
    }

    [Fact]
    public void KeywordsAreWrittenAndHelpMatching()
    {
        var ledger = new LearnedSkills(Learned, Path.Combine(_dir, "history.db"));
        ledger.Propose("send-report-via-outlook", "需要把生成的报告文件通过本机 Outlook 经典版发送给指定收件人时使用",
            "1. 确认附件路径存在\n2. 用 Outlook COM 新建邮件，添加附件\n3. 发送前确认",
            "邮件, 发邮件，发给\n\"抄送\", 发送, 发送, Outlook");
        var file = File.ReadAllText(Path.Combine(Learned, "send-report-via-outlook", "SKILL.md"));
        Assert.Contains("keywords: 邮件, 发邮件, 发给, 抄送, 发送, Outlook\n", file);
        Assert.Contains("source: learned", file);

        var catalog = new SkillCatalog().AddRoot(Learned, SkillSource.Learned);
        var skill = catalog.Refresh().Single();
        Assert.Contains("发邮件", skill.Keywords);
        Assert.Equal("candidate", skill.LearnedStatus);                 // frontmatter 没被关键词那一行弄坏
        Assert.Equal(new[] { "send-report-via-outlook" }, catalog.BuildPrompt("把这份周报用邮件发给张经理", null).Preloaded);
    }

    [Fact]
    public void ThePromptBuilderReportsWhatItPreloaded()
    {
        var (_, catalog) = Setup();
        var memory = new MemoryStore(Path.Combine(_dir, "mem"));
        memory.EnsureDefaults();
        var builder = new PromptBuilder(memory, catalog);
        var prompt = builder.Build(new PromptContext { Mode = ConversationMode.Agent, Query = "帮我看看今天有什么新邮件" });
        Assert.Contains("<推荐技能", prompt);
        Assert.Equal(new[] { "outlook-mail-summary" }, builder.SkillsPreloaded);

        // 对话模式没有工具，执行不了技能里的步骤：不放
        var chat = new PromptBuilder(memory, catalog);
        Assert.DoesNotContain("<推荐技能", chat.Build(new PromptContext { Mode = ConversationMode.Chat, Query = "帮我看看今天有什么新邮件" }));
        Assert.Empty(chat.SkillsPreloaded);
    }

    [Fact]
    public void UsingAPreloadedSkillCountsAndIsShownAsLastUsed()
    {
        var (ledger, catalog) = Setup();
        var now = new DateTime(2026, 10, 8, 9, 30, 0);
        ledger = new LearnedSkills(Learned, Path.Combine(_dir, "history.db")) { Clock = () => now };
        Assert.Null(ledger.LastUsed("outlook-mail-summary"));

        var skill = catalog.FindAny("outlook-mail-summary")!;
        var changed = ledger.RecordRun("c1", "m1", new[] { skill }, ok: true);
        Assert.Contains(("outlook-mail-summary", LearnedSkillStatus.Active), changed);   // 用成功一次就转正
        Assert.Equal(1, ledger.Stats("outlook-mail-summary", 1).Uses);
        Assert.Equal(now, ledger.LastUsed("outlook-mail-summary"));
    }

    private sealed class TopicEmbedder : IEmbeddingGateway
    {
        public List<string> Seen { get; } = new();

        public Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
        {
            Seen.AddRange(texts);
            static float[] Of(string t) =>
                t.Contains("发") || t.Contains("Outlook") ? new[] { 1f, 0.1f, 0f }
                : t.Contains("PPT") || t.Contains("幻灯片") ? new[] { 0f, 1f, 0.1f }
                : new[] { 0.1f, 0f, 1f };
            return Task.FromResult(new EmbeddingResult("text-embedding-v4", texts.Select(Of).ToList()));
        }
    }

    [Fact]
    public async Task SkillsShareTheMemoryEmbeddingCall()
    {
        var (_, catalog) = Setup();
        var memory = new MemoryStore(Path.Combine(_dir, "mem"));
        var embedder = new TopicEmbedder();
        var index = new SemanticIndex(memory, embedder);

        var scores = await index.ScoreAsync("把这份周报发给张经理", CancellationToken.None, catalog.SemanticTexts());
        Assert.NotNull(scores);
        Assert.True(scores![SemanticIndex.SkillPrefix + "send-report-via-outlook"] > SkillCatalog.PreloadSemantic);
        Assert.True(scores[SemanticIndex.SkillPrefix + "template-based-ppt-report"] < SkillCatalog.PreloadSemantic);
        Assert.Equal(4, embedder.Seen.Count);              // 用户这句话 + 3 个技能，没有记忆

        // 技能的向量存下来了：下一轮只算用户这句话
        embedder.Seen.Clear();
        await index.ScoreAsync("周报做完发邮件", CancellationToken.None, catalog.SemanticTexts());
        Assert.Equal(new[] { "周报做完发邮件" }, embedder.Seen);
    }
}
