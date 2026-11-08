using Flyknit.Core.Security;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>git / gh 按子命令加关键参数定级：会丢数据、改共享历史、动凭证的一律高危（永不自动）。</summary>
public class DevCommandRulesTests
{
    private static CommandEffect Effect(string command) => CommandAnalyzer.Analyze(command).Effect;

    [Theory]
    [InlineData("git status")]
    [InlineData("git -C D:\\repo status --short")]
    [InlineData("git --no-pager log --oneline -20")]
    [InlineData("git diff HEAD~1 -- src/a.cs")]
    [InlineData("git branch")]
    [InlineData("git branch -a")]
    [InlineData("git branch --list feat*")]
    [InlineData("git tag")]
    [InlineData("git tag -l v1.*")]
    [InlineData("git remote -v")]
    [InlineData("git config --get user.name")]
    [InlineData("git config user.name")]
    [InlineData("git stash list")]
    [InlineData("git reflog")]
    [InlineData("git worktree list")]
    [InlineData("git grep TODO")]
    [InlineData("gh pr list")]
    [InlineData("gh pr view 12 --comments")]
    [InlineData("gh issue list --state open")]
    [InlineData("gh run view 123 --log")]
    [InlineData("gh api repos/acme/app/pulls")]
    [InlineData("gh auth status")]
    public void QueriesAreRead(string command) => Assert.Equal(CommandEffect.Read, Effect(command));

    [Theory]
    [InlineData("git add -A")]
    [InlineData("git commit -m \"fix: 修复登录\"")]
    [InlineData("git commit --amend --no-edit")]
    [InlineData("git push")]
    [InlineData("git push -u origin feature/login")]
    [InlineData("git pull --rebase")]
    [InlineData("git checkout main")]
    [InlineData("git checkout -b feature/x")]
    [InlineData("git switch -c feature/x")]
    [InlineData("git restore --staged a.cs")]
    [InlineData("git stash")]
    [InlineData("git stash pop")]
    [InlineData("git branch feature/y")]
    [InlineData("git branch -m old new")]
    [InlineData("git tag v1.2.0")]
    [InlineData("git config user.email a@b.c")]
    [InlineData("git log --output=log.txt")]
    [InlineData("git status > s.txt")]
    [InlineData("gh pr create --title x --body y")]
    [InlineData("gh pr comment 12 --body ok")]
    [InlineData("gh pr merge 12 --squash")]
    [InlineData("gh issue create --title bug")]
    [InlineData("gh repo clone acme/app")]
    [InlineData("gh api repos/acme/app/issues -f title=x")]
    [InlineData("gh api -X PATCH repos/acme/app")]
    public void EverydayChangesAreWrite(string command) => Assert.Equal(CommandEffect.Write, Effect(command));

    [Theory]
    [InlineData("git push --force")]
    [InlineData("git push -f origin main")]
    [InlineData("git push --force-with-lease")]
    [InlineData("git push origin +main")]
    [InlineData("git push origin :old-branch")]
    [InlineData("git push origin --delete old-branch")]
    [InlineData("git push --mirror")]
    [InlineData("git reset --hard HEAD~3")]
    [InlineData("git clean -fdx")]
    [InlineData("git checkout -- .")]
    [InlineData("git checkout .")]
    [InlineData("git checkout -f main")]
    [InlineData("git checkout -B main origin/main")]
    [InlineData("git switch --discard-changes main")]
    [InlineData("git restore src/a.cs")]
    [InlineData("git restore --staged --worktree a.cs")]
    [InlineData("git stash drop")]
    [InlineData("git stash clear")]
    [InlineData("git branch -D feature/x")]
    [InlineData("git branch --delete feature/x")]
    [InlineData("git tag -d v1.0")]
    [InlineData("git reflog expire --expire=now --all")]
    [InlineData("git config --system core.autocrlf true")]
    [InlineData("git rm -r src")]
    [InlineData("git update-ref -d refs/heads/x")]
    [InlineData("git worktree remove ../wt")]
    [InlineData("gh repo delete acme/app --yes")]
    [InlineData("gh repo edit --visibility public")]
    [InlineData("gh release delete v1")]
    [InlineData("gh auth token")]
    [InlineData("gh auth login")]
    [InlineData("gh secret set TOKEN")]
    [InlineData("gh extension install someone/gh-x")]
    [InlineData("gh api -X DELETE repos/acme/app")]
    public void DataLossHistoryRewriteAndCredentialsAreDestructive(string command)
    {
        var analysis = CommandAnalyzer.Analyze(command);
        Assert.Equal(CommandEffect.Destructive, analysis.Effect);
        Assert.False(analysis.CanBeAutomatic); // 不能记成「以后自动」
    }

    [Theory]
    [InlineData("git -c alias.x=!calc x")]
    [InlineData("git -c core.pager=evil log")]
    [InlineData("git my-alias")]
    [InlineData("gh copilot suggest")]
    public void UnrecognisedIsUnknown(string command) => Assert.Equal(CommandEffect.Unknown, Effect(command));

    [Fact]
    public void CompoundTakesWorstSegment()
    {
        Assert.Equal(CommandEffect.Destructive, Effect("git add . && git commit -m x && git push --force"));
    }

    [Fact]
    public void QuotedMessageDoesNotLookLikeFlags()
    {
        // 提交说明里写了 --force 也只是文字
        Assert.Equal(CommandEffect.Write, Effect("git commit -m \"revert --force push\""));
    }
}
