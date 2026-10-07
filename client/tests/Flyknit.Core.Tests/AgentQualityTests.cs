using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Flyknit.Core.Agent;
using Flyknit.Core.Chat;
using Flyknit.Core.Gateway;
using Flyknit.Core.Security;
using Flyknit.Core.Storage;
using Flyknit.Core.Tools;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>任务质量：产出文件检查、规划提醒（只提醒不拦截）、“只更新计划不干活”的空转、任务效果统计。</summary>
public class AgentQualityTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flyknit-quality").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }

    private string PathOf(string name) => Path.Combine(_dir, name);

    // ---------- 产出文件检查 ----------

    private string Workbook(string name, params (string Sheet, int Rows)[] sheets)
    {
        var path = PathOf(name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Put(string entry, string text)
        {
            using var w = new StreamWriter(zip.CreateEntry(entry).Open(), new UTF8Encoding(false));
            w.Write(text);
        }
        Put("[Content_Types].xml", "<Types/>");
        Put("xl/workbook.xml", "<workbook><sheets>" + string.Concat(sheets.Select((s, i) => $"<sheet name=\"{s.Sheet}\" sheetId=\"{i + 1}\"/>")) + "</sheets></workbook>");
        for (var i = 0; i < sheets.Length; i++)
        {
            Put($"xl/worksheets/sheet{i + 1}.xml", "<worksheet><sheetData>" + string.Concat(Enumerable.Range(1, sheets[i].Rows).Select(r => $"<row r=\"{r}\"><c><v>{r}</v></c></row>")) + "</sheetData></worksheet>");
        }
        return path;
    }

    [Fact]
    public void WorkbooksReportSheetsAndRows()
    {
        var ok = OutputVerifier.Check(Workbook("周报.xlsx", ("汇总", 31), ("明细", 420)));
        Assert.Equal(OutputCheckStatus.Ok, ok.Status);
        Assert.Equal("2 个工作表（汇总 31 行，明细 420 行）", ok.Detail);

        var empty = OutputVerifier.Check(Workbook("空表.xlsx", ("Sheet1", 1)));
        Assert.Equal(OutputCheckStatus.Warning, empty.Status);
        Assert.Contains("没有数据行", empty.Detail);
    }

    [Fact]
    public void BrokenFilesAreProblemsButUncheckableOnesAreNot()
    {
        Assert.Equal(OutputCheckStatus.Problem, OutputVerifier.Check(PathOf("没有.xlsx")).Status);

        File.WriteAllText(PathOf("空.docx"), "");
        Assert.Contains("0 字节", OutputVerifier.Check(PathOf("空.docx")).Detail);

        // 脚本把 CSV 文本存成了 .xlsx：Excel 打不开
        File.WriteAllText(PathOf("假的.xlsx"), "a,b\n1,2\n");
        Assert.Equal(OutputCheckStatus.Problem, OutputVerifier.Check(PathOf("假的.xlsx")).Status);

        // 加密的 Office 文件是 OLE 复合文档：不能当成错误，否则模型会去“修”它
        File.WriteAllBytes(PathOf("加密.xlsx"), new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0 });
        Assert.Equal(OutputCheckStatus.Unverifiable, OutputVerifier.Check(PathOf("加密.xlsx")).Status);

        File.WriteAllText(PathOf("截断.pdf"), "%PDF-1.7\n1 0 obj << /Type /Page >> endobj\n");
        Assert.Contains("不完整", OutputVerifier.Check(PathOf("截断.pdf")).Detail);
        File.WriteAllText(PathOf("好的.pdf"), "%PDF-1.7\n<< /Type /Pages >>\n<< /Type /Page >>\n<< /Type /Page >>\ntrailer\n%%EOF\n");
        Assert.Equal("约 2 页", OutputVerifier.Check(PathOf("好的.pdf")).Detail);

        File.WriteAllText(PathOf("数据.json"), "{\"a\": ");
        Assert.Equal(OutputCheckStatus.Problem, OutputVerifier.Check(PathOf("数据.json")).Status);

        File.WriteAllText(PathOf("只有表头.csv"), "车间,不良率\n");
        Assert.Equal(OutputCheckStatus.Warning, OutputVerifier.Check(PathOf("只有表头.csv")).Status);
        File.WriteAllText(PathOf("明细.csv"), "车间,不良率\n一车间,1.2%\n二车间,0.8%\n");
        Assert.Equal("3 行（含表头）", OutputVerifier.Check(PathOf("明细.csv")).Detail);

        Assert.False(OutputVerifier.IsDocument("build.py"));
        Assert.Null(OutputVerifier.Describe(new[] { new OutputCheck("a.png", OutputCheckStatus.Ok, "") }));
    }

    // ---------- 接进 Agent 循环 ----------

    private sealed class Gateway : IChatGateway
    {
        private readonly Func<int, ChatRequest, ChatTurn> _reply;
        public int Calls { get; private set; }
        public Gateway(Func<int, ChatRequest, ChatTurn> reply) => _reply = reply;

        public Task<ChatTurn> CompleteAsync(ChatRequest request, IStreamSink? sink, CancellationToken ct) =>
            Task.FromResult(_reply(++Calls, request));
    }

    private sealed class Allow : IConfirmationHandler
    {
        public Task<ConfirmChoice> ConfirmAsync(ConfirmRequest request, CancellationToken ct) => Task.FromResult(ConfirmChoice.AllowOnce);
    }

    private sealed class Observer : IAgentObserver
    {
        public void OnContent(string delta) { }
        public void OnReasoning(string delta) { }
        public void OnAssistantMessage(ChatMessage message) { }
        public void OnToolStarted(ToolCall call, string summary, PolicyDecision decision) { }
        public void OnToolFinished(ToolCall call, ToolResult result, string decision) { }
        public void OnToolMessage(ChatMessage message) { }
        public void OnPlanUpdated(IReadOnlyList<PlanItem> plan) { }
    }

    private ToolContext Context() => new()
    {
        Policy = CommandPolicy.Default(),
        ConversationId = "c1",
        Workspace = _dir,
        Permission = PermissionMode.Workspace,
        Security = new SecuritySettings(new Dictionary<string, SecurityItem>
        {
            [SecuritySettings.Sandbox] = new() { Value = false, Locked = true },
        }),
    };

    private static ChatTurn Tools(params (string Name, object Args)[] calls) => new()
    {
        ToolCalls = calls.Select((c, i) => new ToolCall($"call_{Guid.NewGuid():N}"[..14], c.Name, JsonSerializer.Serialize(c.Args))).ToArray(),
    };

    private static ChatTurn Answer(string text) => new() { Content = text };

    private async Task<(AgentRunResult Result, List<ChatMessage> History)> Run(Gateway gateway, AgentOptions? options = null, ToolContext? ctx = null)
    {
        var history = new List<ChatMessage> { ChatMessage.System("sys"), ChatMessage.User("做一份质检汇总") };
        var loop = new AgentLoop(gateway, ToolRegistry.CreateDefault(), new Allow(), options: options ?? new AgentOptions());
        var result = await loop.RunAsync(history, Scenes.Agent, ctx ?? Context(), new Observer(), useTools: true, CancellationToken.None);
        return (result, history);
    }

    private static string ToolText(AgentRunResult r, int index = 0) => r.NewMessages.Where(m => m.Role == ChatRole.Tool).ElementAt(index).Content;

    [Fact]
    public async Task OutputsAreCheckedAsSoonAsTheyAreWritten()
    {
        var csv = PathOf("汇总.csv");
        var gateway = new Gateway((n, _) => n switch
        {
            1 => Tools(("write_file", new { path = csv, content = "车间,不良率\n" })),
            _ => Answer("汇总表已经生成。"),
        });

        var (result, _) = await Run(gateway);

        var tool = ToolText(result);
        Assert.Contains("【系统检查】", tool);
        Assert.Contains("⚠ 汇总.csv：只有表头", tool);
        Assert.Equal(AgentStopReason.Completed, result.StopReason);
        Assert.Equal(0, result.OutputProblems);           // 只是提醒，不算问题
        Assert.Equal(1, result.ToolCalls);
        Assert.Equal(2, result.Steps);
        Assert.Contains(result.Trace!.Steps, s => s.Kind == "verify");
    }

    [Fact]
    public async Task AFileThatIsGoneByDeliveryIsFlaggedInTheAnswer()
    {
        var xlsx = PathOf("周报.xlsx");
        var gateway = new Gateway((n, _) =>
        {
            if (n == 2)
            {
                File.Delete(xlsx); // 比如后面的清理脚本把它删了
                return Answer("周报已保存到工作区。");
            }
            return Tools(("write_file", new { path = xlsx, content = "不是真正的 Excel" }));
        });

        var (result, _) = await Run(gateway);

        Assert.Contains("✗ 周报.xlsx", ToolText(result));   // 当场就提醒了：这不是有效的 xlsx
        Assert.Equal(1, result.OutputProblems);
        var answer = result.NewMessages.Last(m => m.Role == ChatRole.Assistant).Content;
        Assert.StartsWith("周报已保存到工作区。", answer);
        Assert.Contains("⚠ 系统检查：周报.xlsx 文件不存在", answer);
        Assert.Equal(1, result.OutputProblemsAtEnd);
        Assert.Equal(AgentStopReason.Completed, result.StopReason); // 只注明，不当成失败
    }

    [Fact]
    public async Task VerificationCanBeSwitchedOff()
    {
        var csv = PathOf("汇总.csv");
        var gateway = new Gateway((n, _) => n == 1 ? Tools(("write_file", new { path = csv, content = "a\n" })) : Answer("好了"));
        var (result, _) = await Run(gateway, new AgentOptions { VerifyOutputs = false });
        Assert.DoesNotContain("【系统检查】", ToolText(result));
        Assert.DoesNotContain(result.Trace!.Steps, s => s.Kind == "verify");
    }

    [Fact]
    public async Task ANudgeToPlanComesOnceAfterAFewStepsAndNeverBlocks()
    {
        for (var i = 0; i < 5; i++)
        {
            Directory.CreateDirectory(PathOf($"d{i}"));
        }
        var gateway = new Gateway((n, _) => n <= 5 ? Tools(("list_dir", new { path = PathOf($"d{n - 1}") })) : Answer("看完了"));

        var (result, _) = await Run(gateway);

        Assert.Equal(AgentStopReason.Completed, result.StopReason);
        Assert.True(result.PlanNudged);
        var tools = result.NewMessages.Where(m => m.Role == ChatRole.Tool).Select(m => m.Content).ToList();
        Assert.Equal(5, tools.Count);                                    // 每一步都照常执行了
        Assert.Equal(1, tools.Count(t => t.Contains("还没有列计划")));     // 只提醒一次
        Assert.Contains("还没有列计划", tools[2]);                         // 第 3 步之后

        // 简单任务（两步就完）不打扰
        var simple = new Gateway((n, _) => n <= 2 ? Tools(("list_dir", new { path = PathOf($"d{n}") })) : Answer("好了"));
        var (quick, _) = await Run(simple);
        Assert.False(quick.PlanNudged);

        // 一开始就列了计划的不提醒
        var planned = new Gateway((n, _) => n switch
        {
            1 => Tools(("update_plan", new { steps = new[] { new { step = "看目录", status = "in_progress" } } })),
            <= 5 => Tools(("list_dir", new { path = PathOf($"d{n - 2}") })),
            _ => Answer("好了"),
        });
        var (withPlan, _) = await Run(planned);
        Assert.False(withPlan.PlanNudged);

        // 开关关掉就完全不提醒
        var (off, _) = await Run(new Gateway((n, _) => n <= 5 ? Tools(("list_dir", new { path = PathOf($"d{n - 1}") })) : Answer("看完了")),
            new AgentOptions { PlanGuidance = false });
        Assert.False(off.PlanNudged);
        Assert.DoesNotContain(off.NewMessages, m => m.Content.Contains("还没有列计划"));
    }

    [Fact]
    public async Task TwoFailuresInARowWithAPlanSuggestRevisingIt()
    {
        var gateway = new Gateway((n, _) => n switch
        {
            1 => Tools(("update_plan", new { steps = new[] { new { step = "读取三个车间的数据", status = "in_progress" }, new { step = "汇总", status = "pending" } } })),
            2 => Tools(("read_file", new { path = PathOf("一车间.xlsx") })),
            3 => Tools(("read_file", new { path = PathOf("二车间.xlsx") })),
            _ => Answer("找不到数据文件，请告诉我放在哪里。"),
        });

        var (result, _) = await Run(gateway);

        Assert.True(result.ReplanNudged);
        Assert.Contains("先回头看看计划", ToolText(result, 2));
        Assert.DoesNotContain("先回头看看计划", ToolText(result, 1));
        Assert.Equal(AgentStopReason.Completed, result.StopReason);
    }

    [Fact]
    public async Task UpdatingThePlanOverAndOverWithoutWorkingIsStopped()
    {
        var plan = new { steps = new[] { new { step = "读取数据", status = "pending" } } };
        var gateway = new Gateway((n, req) => req.Tools is null ? Answer("我一直在改计划，没有动手。") : Tools(("update_plan", plan)));

        var (result, _) = await Run(gateway);

        Assert.Equal(AgentStopReason.Stuck, result.StopReason);
        Assert.Contains("计划已经更新好几次了", ToolText(result, 2));
        Assert.Equal(5, result.ToolCalls);

        // 每做一步更新一次计划是正常用法，不算
        var guard = new LoopGuard();
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(LoopVerdict.Ok, guard.Observe(new ToolCall("p", "update_plan", "{}"), "ok"));
            Assert.Equal(LoopVerdict.Ok, guard.Observe(new ToolCall("r", "read_file", $"{{\"path\":\"{i}\"}}"), $"内容 {i}"));
        }
    }

    // ---------- 任务效果统计 ----------

    [Fact]
    public void RunStatsSummariseOutcomesAndFeedback()
    {
        var store = new ConversationStore(PathOf("history.db"));
        var conv = store.Create(ConversationMode.Agent);
        var answer = ChatMessage.Assistant("好了");
        store.AddMessages(conv.Id, new[] { ChatMessage.User("做周报"), answer });
        store.SetFeedback(answer.Id, -1);

        store.AddRunStats(new RunStatsRecord(answer.Id, conv.Id, "Completed", 6, 5, 1, 0, true, true, true));
        store.AddRunStats(new RunStatsRecord("m2", conv.Id, "Stuck", 20, 19, 0, 0, true, true, false));
        store.AddRunStats(new RunStatsRecord("m3", conv.Id, "Cancelled", 2, 1, 0, 0, true, true, false));
        store.AddRunStats(new RunStatsRecord("old", conv.Id, "Completed", 3, 2, 0, 0, true, true, false, DateTimeOffset.Now.AddDays(-60)));

        var s = store.RunStats();
        Assert.Equal(3, s.Runs);
        Assert.Equal(1, s.Completed);
        Assert.Equal(1, s.Paused);
        Assert.Equal(1, s.Cancelled);
        Assert.Equal(0.33, s.CompletionRate);
        Assert.Equal(9.3, s.AvgSteps);
        Assert.Equal(1, s.Disliked);
        Assert.Equal(1, s.RunsWithOutputProblems);
        Assert.Equal(1, s.PlanNudges);
    }
}
