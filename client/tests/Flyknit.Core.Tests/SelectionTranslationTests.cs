using Flyknit.Core.Agent;
using Flyknit.Core.Chat;
using Flyknit.Core.Gateway;
using Flyknit.Core.Translation;
using Xunit;

namespace Flyknit.Core.Tests;

public class SelectionTranslationTests
{
    [Theory]
    [InlineData("今天下午三点开会", "zh-CN")]
    [InlineData("请把 PO-2024-118 的鞋面数量发给我", "zh-CN")]   // 中文里夹型号和英文
    [InlineData("Hôm nay chúng ta họp lúc ba giờ chiều", "vi")]
    [InlineData("đơn hàng", "vi")]                                  // 一个词也认得出
    [InlineData("The upper material is out of stock", "en")]
    [InlineData("在庫がありません", "ja")]
    [InlineData("재고가 없습니다", "ko")]
    [InlineData("สินค้าหมด", "th")]
    [InlineData("ទំនិញអស់ពីស្តុក", "km")]
    [InlineData("12345 / 678", "")]                                 // 只有数字，认不出
    public void GuessesTheLanguageFromTheScript(string text, string expected)
    {
        Assert.Equal(expected, SelectionTranslation.GuessLanguage(text));
    }

    [Fact]
    public void TranslatesIntoTheUiLanguageByDefault()
    {
        Assert.Equal("zh-CN", SelectionTranslation.PickTarget("The order is late", "auto", "zh-CN").Target);
        Assert.Equal("vi", SelectionTranslation.PickTarget("今天开会", null, "vi-VN").Target);
        Assert.Equal("en", SelectionTranslation.PickTarget("今天开会", "", "en-US").Target);
    }

    [Fact]
    public void TextAlreadyInTheTargetLanguageGoesSomewhereUseful()
    {
        // 中文界面的人划到中文，多半是要发给越南同事
        Assert.Equal("vi", SelectionTranslation.PickTarget("今天下午三点开会", "auto", "zh-CN").Target);
        // 越南语界面的人划到越南语，译成中文
        Assert.Equal("zh-CN", SelectionTranslation.PickTarget("Hôm nay chúng ta họp", "auto", "vi-VN").Target);
        // 英文界面划到英文，译成中文
        Assert.Equal("zh-CN", SelectionTranslation.PickTarget("The order is late", "auto", "en-US").Target);
    }

    [Fact]
    public void AChosenTargetIsKeptUnlessTheTextIsAlreadyInIt()
    {
        Assert.Equal("th", SelectionTranslation.PickTarget("今天开会", "th", "zh-CN").Target);
        // 选了越南语，划到的又是越南语 → 译回界面语言
        Assert.Equal("zh-CN", SelectionTranslation.PickTarget("Hôm nay chúng ta họp", "vi", "zh-CN").Target);
        // 不认识的代码当成没选
        Assert.Equal("zh-CN", SelectionTranslation.PickTarget("The order is late", "xx", "zh-CN").Target);
    }

    [Fact]
    public void TheFallbackIsNeverTheTargetItself()
    {
        foreach (var ui in Languages.UiLanguages)
        {
            foreach (var preferred in Languages.TranslateTargets.Keys.Append("auto"))
            {
                foreach (var text in new[] { "今天开会", "Hôm nay chúng ta họp", "The order is late", "123" })
                {
                    var (target, fallback) = SelectionTranslation.PickTarget(text, preferred, ui);
                    Assert.NotEqual(target, fallback);
                    Assert.True(Languages.TranslateTargets.ContainsKey(target));
                    Assert.True(Languages.TranslateTargets.ContainsKey(fallback));
                }
            }
        }
    }

    [Fact]
    public void CleaningTidiesWhatComesOffTheClipboard()
    {
        // Excel 单元格带结尾换行，网页带一堆空行和行尾空格
        Assert.Equal(("鞋面", false), SelectionTranslation.Clean("鞋面\r\n"));
        Assert.Equal(("第一段\n\n第二段", false), SelectionTranslation.Clean("  第一段   \r\n\r\n\r\n\r\n第二段  \n"));
        Assert.Equal(("a\nb", false), SelectionTranslation.Clean("a\rb"));
        Assert.Equal(("", false), SelectionTranslation.Clean("   \r\n\t "));
        Assert.Equal(("", false), SelectionTranslation.Clean(null));
    }

    [Fact]
    public void LongSelectionsAreCutWithoutSplittingACharacter()
    {
        var (text, truncated) = SelectionTranslation.Clean(new string('字', SelectionTranslation.MaxChars + 50));
        Assert.True(truncated);
        Assert.Equal(SelectionTranslation.MaxChars, text.Length);

        // 截断点正好落在 emoji 中间时往前退一个
        var emoji = new string('a', SelectionTranslation.MaxChars - 1) + "😀" + "tail";
        var (cut, _) = SelectionTranslation.Clean(emoji);
        Assert.False(char.IsHighSurrogate(cut[^1]));
    }

    [Theory]
    [InlineData("鞋面", true)]
    [InlineData("upper", true)]
    [InlineData("out of stock", true)]
    [InlineData("The upper material is out of stock.", false)]
    [InlineData("今天下午三点在二楼会议室开会", false)]
    [InlineData("a\nb", false)]
    public void ShortTermsAreLookedUpLikeADictionary(string text, bool expected)
    {
        Assert.Equal(expected, SelectionTranslation.IsLookup(text));
    }

    [Fact]
    public void TheRequestGoesThroughTheTranslateSceneUnderItsOwnConversation()
    {
        var id = SelectionTranslation.NewConversationId();
        var request = SelectionTranslation.BuildRequest("鞋面", "vi", "zh-CN", id);

        Assert.Equal(Scenes.Translate, request.Scene);
        Assert.True(request.Stream);
        Assert.Equal(id, request.ConversationId);
        Assert.False(request.ExtraBody!["enable_thinking"]!.GetValue<bool>());
        Assert.True(id.Length <= 64); // 服务端只保留前 64 个字符
        Assert.Equal(2, request.Messages.Count);
        Assert.Equal(ChatRole.System, request.Messages[0].Role);
        Assert.Contains("Vietnamese", request.Messages[0].Content);
        Assert.Contains("translate it into Simplified Chinese instead", request.Messages[0].Content);
        Assert.Contains("single word or short term", request.Messages[0].Content); // 查词
        Assert.Equal("鞋面", request.Messages[1].Content);

        var sentence = SelectionTranslation.BuildRequest("今天下午三点在二楼会议室开会", "vi", "zh-CN", id);
        Assert.DoesNotContain("single word or short term", sentence.Messages[0].Content);
    }

    [Fact]
    public void TranslateModePromptIsUnchanged()
    {
        var prompt = new PromptBuilder(null, null).Build(new PromptContext
        {
            Mode = ConversationMode.Translate,
            TranslateFrom = "auto",
            TranslateTo = "vi",
        });
        Assert.Contains("Translate the user's text into Vietnamese; auto-detect the source language.", prompt);
        Assert.Contains("If the text is already in Vietnamese, translate it into Simplified Chinese instead.", prompt);
        Assert.DoesNotContain("single word", prompt);
    }

    [Theory]
    [InlineData("<think>嗯", "")]
    [InlineData("<think>嗯</think>\n\nXin chào", "Xin chào")]
    [InlineData("Xin chào", "Xin chào")]
    public void ThinkingNeverShowsUpInTheTranslation(string raw, string expected)
    {
        Assert.Equal(expected, SelectionTranslation.StripThinking(raw));
    }
}
