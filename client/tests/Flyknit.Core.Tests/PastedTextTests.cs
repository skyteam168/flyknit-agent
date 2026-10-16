using Flyknit.Core.Chat;
using Flyknit.Core.Context;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>粘贴的长文本：界面上是卡片，发给模型时整段放进消息。</summary>
public class PastedTextTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "flyknit-paste-" + Guid.NewGuid().ToString("N"));

    public PastedTextTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private Attachment Paste(string text, bool pasted = true)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, text);
        return new Attachment { FileName = "粘贴文本.txt", LocalPath = path, Mime = "text/plain", Size = new FileInfo(path).Length, Pasted = pasted };
    }

    private static string ContentOf(ChatMessage m) => OpenAiSerializer.ToJson(m)["content"]!.GetValue<string>();

    [Fact]
    public void PastedTextGoesIntoTheMessageAheadOfWhatTheEmployeeTyped()
    {
        var m = ChatMessage.User("以上是我的知识库架构，帮我查缺补漏", new[] { Paste("# 项目：企业知识库平台\r\n## 技术栈\r\n- 前端：Vue 3") });

        var content = ContentOf(m);

        Assert.StartsWith("<粘贴的文本 名称=\"粘贴文本.txt\">\n# 项目：企业知识库平台\n## 技术栈", content);
        Assert.EndsWith("</粘贴的文本>\n\n以上是我的知识库架构，帮我查缺补漏", content);
        Assert.DoesNotContain("[用户附加的文件]", content); // 不再只给路径
    }

    [Fact]
    public void OrdinaryTextFilesStillOnlySendTheirPath()
    {
        var file = Paste("机密内容", pasted: false);

        var content = ContentOf(ChatMessage.User("看看这个文件", new[] { file }));

        Assert.DoesNotContain("机密内容", content);
        Assert.Contains("[用户附加的文件]", content);
        Assert.Contains(file.LocalPath, content);
    }

    [Fact]
    public void VeryLongPastesAreCutAndTheModelIsToldWhereTheRestIs()
    {
        var a = Paste(new string('字', PastedText.MaxInlineChars + 500));

        var content = ContentOf(ChatMessage.User("", new[] { a }));

        Assert.Contains($"只放了前 {PastedText.MaxInlineChars} 字", content);
        Assert.Contains(a.LocalPath, content);
        Assert.True(content.Length < PastedText.MaxInlineChars + 500);
    }

    [Fact]
    public void AMissingPasteFileFallsBackToAnOrdinaryAttachment()
    {
        var a = Paste("内容");
        File.Delete(a.LocalPath);

        var content = ContentOf(ChatMessage.User("问题", new[] { a }));

        Assert.StartsWith("问题", content);
        Assert.Contains("[用户附加的文件]", content);
    }

    [Fact]
    public void TokenEstimateCountsThePastedText()
    {
        var text = string.Concat(Enumerable.Repeat("企业知识库平台的技术栈与架构。", 200));
        var pasted = TokenEstimator.Estimate(ChatMessage.User("问题", new[] { Paste(text) }));
        var plain = TokenEstimator.Estimate(ChatMessage.User("问题", new[] { Paste(text, pasted: false) }));

        Assert.True(pasted > plain + TokenEstimator.Estimate(text) / 2);
    }

    [Fact]
    public void PastedFlagSurvivesStorage()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new List<Attachment> { Paste("x") });

        Assert.True(System.Text.Json.JsonSerializer.Deserialize<List<Attachment>>(json)![0].Pasted);
        // 旧数据没有这个字段：默认不是粘贴文本
        Assert.False(System.Text.Json.JsonSerializer.Deserialize<Attachment>("""{"FileName":"a.txt"}""")!.Pasted);
    }
}
