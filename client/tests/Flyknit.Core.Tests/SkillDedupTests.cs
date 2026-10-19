using Flyknit.Core.Chat;
using Flyknit.Core.Memory;
using Flyknit.Core.Skills;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>
/// 同一类任务只沉淀一个技能：复盘看得到已有的同类技能、换个名字也建不出第二个，
/// 以前已经重复的清理掉一次，提示词里同类的也只给一份全文。
/// </summary>
public class SkillDedupTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flyknit-skill-dedup").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }

    private string Learned => Path.Combine(_dir, "learned");

    private const string CheckMail = "当用户要检查新邮件、汇总邮件、查看收件箱、查未读邮件时使用，基于经典版 Outlook (win32com) 和 check_mail.py 脚本";
    private const string MailSummary = "当用户要检查新邮件、做邮件汇总、查看收件箱、统计邮件时使用。依赖本机经典版 Outlook 和 pywin32";
    private const string Steps = "1. 打开 Outlook 经典版\n2. 收件箱按时间倒序，只看今天的未读邮件\n3. 按发件人分组，每封一句话总结";

    private LearnedSkills Ledger() => new(Learned, Path.Combine(_dir, "history.db"));

    /// <summary>不经过 Propose 直接写一个学习技能文件（模拟以前的版本已经沉淀出来的）。</summary>
    private void WriteLegacy(string name, string description, string keywords, string status = "active")
    {
        var dir = Path.Combine(Learned, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"),
            $"---\nname: {name}\ndescription: \"{description}\"\nkeywords: {keywords}\nsource: learned\nstatus: {status}\nversion: 1\n---\n\n{Steps}\n");
    }

    [Fact]
    public void ASecondSkillForTheSameTaskIsNotCreated()
    {
        var ledger = Ledger();
        Assert.Equal("check-outlook-mail", ledger.Propose("check-outlook-mail", CheckMail, Steps, "邮件, 收件箱, 未读, Outlook"));

        Assert.Null(ledger.Propose("outlook-mail-summary", MailSummary, Steps, "邮件汇总, 收件箱, Outlook, 统计邮件"));
        Assert.Equal("check-outlook-mail", ledger.LastSkipped);
        Assert.False(Directory.Exists(Path.Combine(Learned, "outlook-mail-summary")));

        // 同一个名字照常写成新版本（复盘修订已有技能走这条路）
        Assert.Equal("check-outlook-mail", ledger.Propose("check-outlook-mail", CheckMail, Steps + "\n4. 汇总写进 Excel"));
        Assert.Null(ledger.LastSkipped);

        // 不同类的任务不受影响
        Assert.Equal("send-outlook-mail", ledger.Propose("send-outlook-mail", "当用户要用 Outlook 发送邮件、给同事发邮件、带附件发邮件时使用",
            "1. 确认收件人\n2. 新建邮件并添加附件\n3. 发送前请用户确认", "发邮件, 发送, 收件人, 抄送, 附件"));
        Assert.Equal("flyknit-ppt-rebuild", ledger.Propose("flyknit-ppt-rebuild", "当需要把内容零散或风格不统一的 PPT 重建为符合 Flyknit 汇报模板规范的成品时使用",
            "1. 复制模板\n2. 按大纲填内容\n3. 检查配色和页码", "PPT, 汇报, 模板"));
    }

    [Fact]
    public void ARetiredSkillDoesNotBlockAFreshAttempt()
    {
        WriteLegacy("check-outlook-mail", CheckMail, "邮件, 收件箱", status: "retired");
        var ledger = Ledger();
        Assert.Equal("outlook-mail-summary", ledger.Propose("outlook-mail-summary", MailSummary, Steps, "邮件汇总, 收件箱"));
    }

    [Fact]
    public void ExistingDuplicatesKeepTheOneThatWorks()
    {
        WriteLegacy("check-outlook-mail", CheckMail, "邮件, 收件箱, 未读, Outlook");
        WriteLegacy("outlook-mail-summary", MailSummary, "邮件汇总, 收件箱, Outlook, 统计邮件");
        WriteLegacy("flyknit-ppt-rebuild", "当需要把内容零散或风格不统一的 PPT 重建为符合 Flyknit 汇报模板规范的成品时使用", "PPT, 汇报, 模板");
        var ledger = Ledger();
        var catalog = new SkillCatalog().AddRoot(Learned, SkillSource.Learned);
        catalog.Refresh();
        // 查邮件那个用成功过 6 次
        for (var i = 0; i < 6; i++)
        {
            ledger.RecordRun("c1", $"m{i}", new[] { catalog.FindAny("check-outlook-mail")! }, ok: true);
        }

        var changed = ledger.RetireDuplicates();

        Assert.Equal(new[] { ("outlook-mail-summary", LearnedSkillStatus.Retired) }, changed);
        catalog.Refresh();
        Assert.Equal(LearnedSkillStatus.Retired, catalog.FindAny("outlook-mail-summary")!.LearnedStatus);
        Assert.Equal(LearnedSkillStatus.Active, catalog.FindAny("check-outlook-mail")!.LearnedStatus);
        Assert.Equal(LearnedSkillStatus.Active, catalog.FindAny("flyknit-ppt-rebuild")!.LearnedStatus);
        Assert.Empty(ledger.RetireDuplicates()); // 再跑一次什么都不变
    }

    [Fact]
    public void DuplicatesThatBothWorkedAreLeftAlone()
    {
        WriteLegacy("check-outlook-mail", CheckMail, "邮件, 收件箱");
        WriteLegacy("outlook-mail-summary", MailSummary, "邮件汇总, 收件箱");
        var ledger = Ledger();
        var catalog = new SkillCatalog().AddRoot(Learned, SkillSource.Learned);
        catalog.Refresh();
        ledger.RecordRun("c1", "m1", new[] { catalog.FindAny("check-outlook-mail")! }, ok: true);
        ledger.RecordRun("c1", "m2", new[] { catalog.FindAny("outlook-mail-summary")! }, ok: true);

        Assert.Empty(ledger.RetireDuplicates()); // 两个都用成功过：不替用户决定，留给人看
    }

    [Fact]
    public void OnlyOneSkillPerKindOfTaskIsGivenInFull()
    {
        WriteLegacy("check-outlook-mail", CheckMail, "邮件, 收件箱, 未读, Outlook, 新邮件");
        WriteLegacy("outlook-mail-summary", MailSummary, "邮件汇总, 收件箱, Outlook, 统计邮件, 新邮件");
        var catalog = new SkillCatalog().AddRoot(Learned, SkillSource.Learned);
        catalog.Refresh();

        var (text, preloaded) = catalog.BuildPrompt("帮我检查一下收件箱的新邮件并汇总", null);

        Assert.Single(preloaded);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "<推荐技能 "));
    }

    [Fact]
    public void TheReviewSeesRelatedSkills()
    {
        var ledger = Ledger();
        ledger.Propose("check-outlook-mail", CheckMail, Steps, "邮件, 收件箱, 未读, Outlook");
        ledger.Propose("flyknit-ppt-rebuild", "当需要把内容零散或风格不统一的 PPT 重建为符合 Flyknit 汇报模板规范的成品时使用",
            "1. 复制模板\n2. 按大纲填内容\n3. 检查配色和页码", "PPT, 汇报, 模板");
        var input = new ReflectionInput
        {
            ConversationId = "c1",
            UserRequest = "看看收件箱有没有新邮件，汇总一下",
            Messages = new[] { ChatMessage.Assistant("今天有 3 封新邮件……") },
        };

        var related = ledger.Related(input.UserRequest);
        Assert.Equal(new[] { "check-outlook-mail" }, related.Select(r => r.Skill.Name));

        var transcript = Reflector.BuildTranscript(input, null, related);
        Assert.Contains("【已有的相关技能】", transcript);
        Assert.Contains("- check-outlook-mail（试用中，用过 0 次，成功 0 次）：", transcript);
        Assert.Contains("不要换个名字为同一类任务再建一个技能", Reflector.ReflectPrompt);
    }
}
