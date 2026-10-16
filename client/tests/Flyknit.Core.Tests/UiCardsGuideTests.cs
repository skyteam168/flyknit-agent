using System.Text.RegularExpressions;
using Flyknit.Core.Agent;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>提示词里教模型的 ```ui 卡片种类，要和界面（client/web/src/render/ui.ts）真正能画的一致。</summary>
public class UiCardsGuideTests
{
    private static string? FindUiTs()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "client", "web", "src", "render", "ui.ts");
            if (File.Exists(path))
            {
                return path;
            }
            path = Path.Combine(dir.FullName, "web", "src", "render", "ui.ts");
            if (File.Exists(path))
            {
                return path;
            }
        }
        return null;
    }

    [Fact]
    public void EveryCardKindTheUiDrawsIsExplainedToTheModelAndNoOthers()
    {
        var ts = FindUiTs();
        Assert.NotNull(ts);
        var line = File.ReadAllLines(ts!).Single(l => l.Contains("export const UiKinds"));
        var kinds = Regex.Matches(line, "'([a-z]+)'").Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(kinds);

        var guide = PromptBuilder.ChartAndDiagramGuide;
        Assert.Contains("```ui", guide);
        foreach (var kind in kinds)
        {
            Assert.Contains($"- {kind} ", guide);
        }
        var taught = Regex.Matches(guide, @"^\s*- ([a-z]+) ", RegexOptions.Multiline).Select(m => m.Groups[1].Value);
        Assert.Equal(kinds.OrderBy(k => k), taught.OrderBy(k => k));
    }
}
