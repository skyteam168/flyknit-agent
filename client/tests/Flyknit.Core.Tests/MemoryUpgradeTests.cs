using System.Text.RegularExpressions;
using Flyknit.Core.Chat;
using Flyknit.Core.Context;
using Flyknit.Core.Gateway;
using Flyknit.Core.Memory;
using Xunit;
using Xunit.Abstractions;

namespace Flyknit.Core.Tests;

/// <summary>
/// 记忆升级：SQLite 条目库、md 双向同步、合并与取代、敏感信息、价值淘汰、评价、时间范围。
/// Fixtures 里的 memory.md / lessons.md 仿照真实使用中积累的记忆写成（同样的重复方式、同样的话题），内容是虚构的。
/// </summary>
public class MemoryUpgradeTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 7, 10, 0, 0);

    private readonly string _dir = Directory.CreateTempSubdirectory("flyknit-memup").FullName;
    private readonly ITestOutputHelper _out;

    public MemoryUpgradeTests(ITestOutputHelper output) => _out = output;

    public void Dispose() => Directory.Delete(_dir, true);

    private MemoryStore NewStore(DateTime? now = null)
    {
        var at = now ?? Now;
        return new MemoryStore(_dir) { Clock = () => at };
    }

    private sealed class FakeGateway : IChatGateway
    {
        private readonly Func<ChatRequest, string> _reply;
        public List<ChatRequest> Requests { get; } = new();
        public FakeGateway(Func<ChatRequest, string> reply) => _reply = reply;

        public Task<ChatTurn> CompleteAsync(ChatRequest request, IStreamSink? sink, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(new ChatTurn { Content = _reply(request) });
        }
    }

    private void CopyFixtures()
    {
        var src = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        File.Copy(Path.Combine(src, "memory.md"), Path.Combine(_dir, MemoryStore.MemoryFile));
        File.Copy(Path.Combine(src, "lessons.md"), Path.Combine(_dir, MemoryStore.LessonsFile));
    }

    private static int Bullets(string text) => text.Split('\n').Count(l => l.StartsWith("- "));

    // ---------- 从旧文件导入（真实数据） ----------

    [Fact]
    public void ImportsRealMemoryFilesMergingDuplicatesAndKeepingABackup()
    {
        CopyFixtures();
        var before = Bullets(File.ReadAllText(Path.Combine(_dir, MemoryStore.MemoryFile))) + Bullets(File.ReadAllText(Path.Combine(_dir, MemoryStore.LessonsFile)));
        var store = NewStore();

        var items = store.List();
        _out.WriteLine($"导入前 {before} 条，导入后 {items.Count} 条；确认 2 次以上的 {items.Count(i => i.ProofCount > 1)} 条");
        foreach (var g in items.GroupBy(i => i.Kind))
        {
            _out.WriteLine($"  {g.Key}: {g.Count()}");
        }

        // 只差一个句号的那两条合成了一条；换了说法的重复（python -c 那几条）字面差得多，要靠“整理记忆”让模型合并
        Assert.Equal(before - 1, items.Count);
        Assert.Equal(2, items.Single(i => i.Text.StartsWith("报表默认保存为 xlsx")).ProofCount);
        Assert.True(items.Count(i => i.Text.Contains("python -c")) >= 2);
        Assert.Equal(before, items.Sum(i => i.ProofCount)); // 合并掉的都算进了确认次数
        Assert.True(File.Exists(Path.Combine(_dir, MemoryStore.MemoryFile + ".bak")));
        Assert.True(File.Exists(Path.Combine(_dir, MemoryStore.LessonsFile + ".bak")));

        // 文件被重新导出：小标题齐全，条目数和库里一致，开头说明保留
        var memory = store.Read(MemoryStore.MemoryFile);
        Assert.StartsWith("# 长期记忆", memory);
        Assert.Contains("## 偏好与习惯\n- ", memory);
        Assert.Contains("## 常用信息\n- ", memory);
        Assert.Equal(items.Count, Bullets(memory) + Bullets(store.Read(MemoryStore.LessonsFile)));

        // 第二次打开不会重复导入
        Assert.Equal(items.Count, NewStore().List().Count);
    }

    [Fact]
    public void PromptFromRealMemoryPicksRelevantItemsWithinBudget()
    {
        CopyFixtures();
        var store = NewStore();
        var cases = new (string Query, string Expected)[]
        {
            ("帮我做一份季度汇报 PPT", "微软雅黑"),
            ("用 python 处理一下这个 Excel 表", ".py"),
            ("调用设备管家的接口导出设备台账", "8081"),
            ("发一封邮件给供应商", "Outlook"),
        };
        foreach (var (query, expected) in cases)
        {
            var prompt = store.BuildPrompt(query);
            var tokens = TokenEstimator.Estimate(prompt.Text);
            _out.WriteLine($"{query} → {prompt.ItemIds.Count} 条，约 {tokens} tokens");
            Assert.Contains(expected, prompt.Text);
            Assert.InRange(prompt.ItemIds.Count, 1, 120);
        }
    }

    // ---------- 写入：重复、取代、删除 ----------

    [Fact]
    public void DuplicatesReinforceAndReplacementsKeepHistory()
    {
        var store = NewStore();
        store.EnsureDefaults();
        var first = store.Save(MemoryKind.Preference, "报表保存到桌面");
        Assert.Equal(MemoryWriteOutcome.Added, first.Outcome);
        Assert.Equal(MemoryWriteOutcome.Reinforced, store.Save(MemoryKind.Preference, "报表保存到桌面。").Outcome);
        Assert.Equal(2, store.List().Single().ProofCount);

        var updated = store.Save(MemoryKind.Preference, "报表默认保存到 D:\\报表", replaces: first.Id);
        Assert.Equal(MemoryWriteOutcome.Updated, updated.Outcome);
        var item = Assert.Single(store.List());
        Assert.Equal("报表默认保存到 D:\\报表", item.Text);
        Assert.Equal(3, item.ProofCount);                       // 继承了旧条目的确认次数
        Assert.Equal(new[] { "报表保存到桌面" }, item.History);
        Assert.DoesNotContain("桌面", store.BuildPromptSection("做报表"));
        Assert.DoesNotContain("桌面", store.Read(MemoryStore.MemoryFile));

        // 旧说法不会被 AI 再翻回来
        Assert.Equal(MemoryWriteOutcome.Reinforced, store.Save(MemoryKind.Preference, "报表保存到桌面", "reflect").Outcome);
        Assert.Single(store.List());
    }

    [Fact]
    public void DeletedMemoriesAreNotRelearnedUnlessTheUserAddsThemBack()
    {
        var store = NewStore();
        store.EnsureDefaults();
        var id = store.Save(MemoryKind.Fact, "ERP 地址是 http://erp.local").Id!;
        Assert.True(store.Delete(id));
        Assert.Empty(store.List());

        Assert.Equal(MemoryWriteOutcome.Rejected, store.Save(MemoryKind.Fact, "ERP 地址是 http://erp.local", "reflect").Outcome);
        Assert.Empty(store.List());
        Assert.Equal(MemoryWriteOutcome.Added, store.Save(MemoryKind.Fact, "ERP 地址是 http://erp.local", "user").Outcome);
        Assert.Equal("user", store.List().Single().Source);
    }

    [Fact]
    public void EditsMadeInTheMarkdownFileAreReadBack()
    {
        var store = NewStore();
        store.EnsureDefaults();
        store.Save(MemoryKind.Preference, "周报用 Excel 格式");
        store.Save(MemoryKind.Preference, "邮件用英文写");
        store.Save(MemoryKind.Fact, "日报放在 D:\\日报");
        store.Save(MemoryKind.Fact, "日报放在 D:\\日报"); // 确认 2 次

        var path = Path.Combine(_dir, MemoryStore.MemoryFile);
        var text = File.ReadAllText(path)
            .Replace("- 邮件用英文写（2026-10-07）\n", "")                       // 删除
            .Replace("日报放在 D:\\日报", "日报放在 D:\\日报\\2026")              // 改措辞
            + "- 同事阿明负责仓库\n";                                             // 新增（写在常用信息下面）
        File.WriteAllText(path, text.Replace("\n", "\r\n"));                     // 记事本可能存成 CRLF

        var items = store.List();
        Assert.DoesNotContain(items, i => i.Text.Contains("英文"));
        var edited = items.Single(i => i.Text.StartsWith("日报放在"));
        Assert.Equal("日报放在 D:\\日报\\2026", edited.Text);
        Assert.Equal(2, edited.ProofCount);
        Assert.Equal(new[] { "日报放在 D:\\日报" }, edited.History);
        var added = items.Single(i => i.Text.Contains("阿明"));
        Assert.Equal(MemoryKind.Fact, added.Kind);
        Assert.Equal("user", added.Source);

        // 删掉的不会被 AI 再记回来
        Assert.False(store.Add(MemoryKind.Preference, "邮件用英文写"));
        // 文件被规范化后再读不会产生变化
        Assert.Equal(items.Count, store.List().Count);
    }

    [Fact]
    public void DeletedFileIsRegeneratedInsteadOfWipingMemory()
    {
        var store = NewStore();
        store.EnsureDefaults();
        store.Save(MemoryKind.Lesson, "读取 .xls 旧格式要先另存为 .xlsx");
        File.Delete(Path.Combine(_dir, MemoryStore.LessonsFile));
        Assert.Single(store.List());
        Assert.Contains(".xlsx", store.Read(MemoryStore.LessonsFile));
    }

    // ---------- 敏感信息 ----------

    [Theory]
    [InlineData("邮箱密码是 Abc12345")]
    [InlineData("ERP 账号 admin，password: hunter2!")]
    [InlineData("OpenAI key 是 sk-proj-abcdefghijklmnopqrstuvwx")]
    [InlineData("请求头 Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N")]
    [InlineData("验证码 482913")]
    [InlineData("-----BEGIN RSA PRIVATE KEY----- MIIEpAIBAAKCAQEA")]
    public void SecretsAreNeverStored(string text)
    {
        var store = NewStore();
        store.EnsureDefaults();
        var rejected = new List<string>();
        store.SensitiveRejected += (_, f) => rejected.AddRange(f);
        var result = store.Save(MemoryKind.Fact, text, "reflect");
        Assert.Equal(MemoryWriteOutcome.Rejected, result.Outcome);
        Assert.Empty(store.List());
        Assert.NotEmpty(rejected);
        Assert.DoesNotContain(text, store.Read(MemoryStore.MemoryFile));
    }

    [Theory]
    [InlineData("设备管家登录时密码需先用 /auth/public-key 公钥加密")]
    [InlineData("登录页表单字段 id 为 #user_name 和 #password")]
    [InlineData("token 在响应 data.list.token，有效期 12 小时")]
    [InlineData("物料号 202610070000123456 的库存放在 B 仓")]
    public void TechnicalNotesAboutPasswordsAreKept(string text)
    {
        var check = SensitiveScanner.Check(text);
        Assert.False(check.Rejected);
        Assert.Equal(text, check.Text);
    }

    [Fact]
    public void PersonalNumbersAreMasked()
    {
        var check = SensitiveScanner.Check("王工手机 13812345678，身份证 11010519491231002X，工资卡 6222 0212 3456 7890 128");
        Assert.False(check.Rejected);
        Assert.Contains("138****5678", check.Text);
        Assert.Contains("1101**********002X", check.Text);
        Assert.DoesNotContain("13812345678", check.Text);
        Assert.DoesNotContain("11010519491231002X", check.Text);
        Assert.Contains(check.Findings, f => f == "手机号");
        Assert.Contains(check.Findings, f => f == "身份证号");
        Assert.True(SensitiveScanner.Luhn("4111111111111111"));
        Assert.False(SensitiveScanner.Luhn("4111111111111112"));
    }

    // ---------- 价值淘汰 ----------

    [Fact]
    public void EvictionDropsLowValueItemsInsteadOfTheOldest()
    {
        var old = NewStore(Now.AddDays(-90));
        old.EnsureDefaults();
        old.Save(MemoryKind.Preference, "报表默认保存到 D:\\报表", "user");          // 用户亲手加的
        for (var i = 0; i < 4; i++) old.Save(MemoryKind.Preference, "邮件一律用英文写");  // 确认多次

        var store = NewStore();
        for (var i = 0; i < MemoryStore.MaxPerKind; i++)
        {
            store.Save(MemoryKind.Preference, $"偏好编号 {i:000} {Guid.NewGuid():N}");
        }

        var items = store.List().Where(i => i.Kind == MemoryKind.Preference).ToList();
        Assert.Equal(MemoryStore.MaxPerKind, items.Count);
        Assert.Contains(items, i => i.Text.Contains("D:\\报表"));
        Assert.Contains(items, i => i.Text.Contains("英文"));
    }

    // ---------- 使用与评价 ----------

    [Fact]
    public void FeedbackOnAnswersFlowsToTheMemoriesTheyUsed()
    {
        var store = NewStore();
        store.EnsureDefaults();
        store.Save(MemoryKind.Preference, "周报用 Excel 格式");
        var prompt = store.BuildPrompt("做周报");
        var id = Assert.Single(prompt.ItemIds);

        store.RecordUsage("c1", "msg1", prompt.ItemIds);
        store.RecordUsage("c1", "msg1", prompt.ItemIds); // 同一条回答只算一次
        Assert.Equal(1, store.List().Single().Uses);

        store.ApplyFeedback("msg1", -1);
        store.ApplyFeedback("msg1", -1);
        Assert.Equal(-1, store.List().Single(i => i.Id == id).Feedback);
        store.ApplyFeedback("msg1", null);
        Assert.Equal(0, store.List().Single().Feedback);
        store.ApplyFeedback("msg1", 1);
        Assert.Equal(1, store.List().Single().Feedback);
    }

    // ---------- 时间 ----------

    [Theory]
    [InlineData("昨天做的报表", "2026-10-06", "2026-10-07")]
    [InlineData("上周的周报", "2026-09-28", "2026-10-05")]
    [InlineData("这周", "2026-10-05", "2026-10-12")]
    [InlineData("上个月的数据", "2026-09-01", "2026-10-01")]
    [InlineData("九月那次安装", "2026-09-01", "2026-10-01")]
    [InlineData("12月的盘点", "2025-12-01", "2026-01-01")]
    [InlineData("9月15日的会议纪要", "2026-09-15", "2026-09-16")]
    [InlineData("最近3天", "2026-10-04", "2026-10-08")]
    [InlineData("两周前", "2026-09-22", "2026-09-25")]
    public void ParsesCommonTimePhrases(string text, string from, string to)
    {
        var range = TimeRange.Parse(text, Now);
        Assert.NotNull(range);
        Assert.Equal(DateTime.Parse(from), range!.From);
        Assert.Equal(DateTime.Parse(to), range.To);
    }

    [Theory]
    [InlineData("统一月报的格式")]
    [InlineData("生成质检周报")]
    public void IgnoresTextWithoutTimePhrases(string text) => Assert.Null(TimeRange.Parse(text, Now));

    [Fact]
    public void EpisodesFromTheMentionedTimeRankFirst()
    {
        var episodes = new EpisodeStore(_dir) { Clock = () => Now };
        episodes.Add(new Episode { Id = "old", Title = "生成质检周报", Task = "生成质检周报", CreatedAt = Now.AddDays(-30) });
        episodes.Add(new Episode { Id = "last-week", Title = "生成质检周报", Task = "生成质检周报", CreatedAt = Now.AddDays(-7) });
        episodes.Add(new Episode { Id = "yesterday", Title = "整理供应商名单", Task = "把供应商名单去重", CreatedAt = Now.AddDays(-1) });

        Assert.Equal("last-week", episodes.Search("把上周做的质检周报再做一遍")[0].Episode.Id);
        Assert.Equal("yesterday", episodes.Search("昨天做了什么")[0].Episode.Id);
    }

    // ---------- 复盘时合并 ----------

    [Fact]
    public async Task ReflectionCanReinforceOrReplaceExistingMemories()
    {
        var store = NewStore();
        store.EnsureDefaults();
        store.Save(MemoryKind.Preference, "质检周报保存到桌面");
        store.Save(MemoryKind.Lesson, "PowerShell 里跑 python -c 容易被引号坑，应先写成 .py 文件再执行");

        var gateway = new FakeGateway(req =>
        {
            var user = req.Messages[1].Content;
            string Alias(string contains) => Regex.Match(user, @"\[(m\d+)\][^\n]*" + Regex.Escape(contains)).Groups[1].Value;
            return $$"""
                {"worth_saving": true, "title": "生成质检周报", "summary": "", "outcome": "success", "procedure": "",
                 "preferences": [{"text": "质检周报保存到 D:\\报表", "replaces": "{{Alias("桌面")}}"}],
                 "facts": [],
                 "successes": [],
                 "lessons": [{"text": "PowerShell 中 python -c 多行脚本引号转义易出错", "same": "{{Alias("python -c")}}"}],
                 "skill": null}
                """;
        });
        var reflector = new Reflector(gateway, store, new EpisodeStore(_dir), null);
        var call = new ToolCall("c1", "run_shell", "{}");
        var report = await reflector.ReflectAsync(new ReflectionInput
        {
            ConversationId = "conv",
            UserRequest = "生成质检周报，用 python 处理",
            Messages = new[] { ChatMessage.Assistant("", new[] { call }), ChatMessage.ToolResult(call, "ok"), ChatMessage.Assistant("质检周报已保存") },
        }, CancellationToken.None);

        Assert.Contains("【已有的相关记忆】", gateway.Requests[0].Messages[1].Content);
        Assert.NotNull(report);
        Assert.Equal(1, report!.Updated);
        Assert.Equal(1, report.Reinforced);
        var items = store.List();
        Assert.Equal(2, items.Count);
        var pref = items.Single(i => i.Kind == MemoryKind.Preference);
        Assert.Equal("质检周报保存到 D:\\报表", pref.Text);
        Assert.Equal(new[] { "质检周报保存到桌面" }, pref.History);
        Assert.Equal(2, items.Single(i => i.Kind == MemoryKind.Lesson).ProofCount);
    }

    [Fact]
    public void ReflectionParsesStringAndObjectItems()
    {
        var r = Reflector.Parse("""{"worth_saving": false, "preferences": ["a 偏好", {"text": "b 偏好", "replaces": "[m2]"}, {"text": ""}], "lessons": [{"text": "c", "same": "m1"}]}""");
        Assert.NotNull(r);
        Assert.Equal(2, r!.Preferences.Count);
        Assert.Equal("m2", r.Preferences[1].Replaces);
        Assert.Equal("m1", r.Lessons[0].Same);
    }

    // ---------- 整理记忆 ----------

    [Fact]
    public async Task ConsolidatorMergesRephrasedDuplicatesFromRealData()
    {
        CopyFixtures();
        var store = NewStore();
        var pythonLessons = store.List().Where(i => i.Kind == MemoryKind.Lesson && i.Text.Contains("python -c")).ToList();
        _out.WriteLine($"导入后仍有 {pythonLessons.Count} 条讲 python -c 的教训");
        foreach (var g in store.List().GroupBy(i => i.Kind))
        {
            _out.WriteLine($"  {g.Key}: {g.Count()} 条，其中可能重复的 {MemoryConsolidator.Candidates(g.ToList(), 0.3).Count} 条");
        }
        Assert.True(pythonLessons.Count >= 2);

        // 假模型：把讲 python -c 的几条合成一条
        var gateway = new FakeGateway(req =>
        {
            var lines = Regex.Matches(req.Messages[1].Content, @"^\[(m\d+)\] (.+)$", RegexOptions.Multiline)
                .Where(m => m.Groups[2].Value.Contains("python -c")).Select(m => $"\"{m.Groups[1].Value}\"").ToList();
            return lines.Count < 2
                ? """{"merge": []}"""
                : $$"""{"merge": [{"ids": [{{string.Join(",", lines)}}], "text": "PowerShell 里不要用 python -c 跑内联脚本（引号、正则、中文都会出错），先写成 .py 文件再执行"}]}""";
        });
        var report = await new MemoryConsolidator(gateway, store).RunAsync(CancellationToken.None);

        Assert.Empty(report.Errors);
        Assert.True(report.Groups >= 1);
        var merged = store.List().Where(i => i.Text.Contains("python -c")).ToList();
        var lesson = Assert.Single(merged, i => i.Kind == MemoryKind.Lesson);
        Assert.Equal(pythonLessons.Sum(i => i.ProofCount), lesson.ProofCount);
        Assert.NotEmpty(lesson.History);
        Assert.Contains("python -c", store.Read(MemoryStore.LessonsFile));
    }

    [Fact]
    public void ConsolidatorOnlySendsLikelyDuplicates()
    {
        var items = new List<MemoryItem>
        {
            new("1", MemoryKind.Lesson, "PowerShell 里跑 python -c 容易被引号坑，应先写成 .py 文件", null),
            new("2", MemoryKind.Lesson, "读取 .xls 旧格式要先另存为 .xlsx", null),
            new("3", MemoryKind.Lesson, "PowerShell 中 python -c 多行脚本引号转义易出错，应写成 .py 文件再执行", null),
        };
        var picked = MemoryConsolidator.Candidates(items, 0.3);
        Assert.Equal(new[] { "1", "3" }, picked.Select(i => i.Id));
    }
}
