using Flyknit.Core.Code;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>编码模式的全局搜索：大小写、全字、正则、包含 / 排除的文件；跳过依赖目录和二进制；只在工作区里找。</summary>
public sealed class CodeSearchTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("fk-search-").FullName;

    public CodeSearchTests()
    {
        Write("src/auth.ts", "export function login() {}\nconst relogin = login\n// Login 页面\n");
        Write("src/views/LoginView.vue", "<template><LoginForm /></template>\n");
        Write("api/login.py", "def login():\n    pass\n");
        Write("node_modules/lib/index.js", "login()\n");
        File.WriteAllBytes(Path.Combine(_root, "src", "logo.png"), new byte[] { 0x89, 0x50, 0, 0, 0x6c, 0x6f, 0x67, 0x69, 0x6e });
    }

    public void Dispose() => Directory.Delete(_root, true);

    private void Write(string rel, string text)
    {
        var full = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private SearchResult Run(string q, bool regex = false, bool matchCase = false, bool wholeWord = false, string include = "", string exclude = "") =>
        CodeSearch.Run(_root, q, regex, matchCase, wholeWord, include, exclude, CancellationToken.None);

    private static string[] Paths(SearchResult r) => r.Files.Select(f => f.Path.Replace('\\', '/')).OrderBy(x => x).ToArray();

    [Fact]
    public void FindsTextAndSkipsDependenciesAndBinaries()
    {
        var r = Run("login");
        Assert.Equal(new[] { "api/login.py", "src/auth.ts", "src/views/LoginView.vue" }, Paths(r));
        Assert.Equal(6, r.Total); // 不区分大小写：auth.ts 4 处、LoginView.vue 1 处（文件名不算）、login.py 1 处
        var first = r.Files.Single(f => f.Path.EndsWith("auth.ts")).Hits[0];
        Assert.Equal((1, 17, 5), (first.Line, first.Column, first.Length));
        Assert.Equal("", Run("").Error);
        Assert.Empty(Run("").Files);
    }

    [Fact]
    public void CaseWholeWordAndRegex()
    {
        Assert.Equal(new[] { "src/auth.ts", "src/views/LoginView.vue" }, Paths(Run("Login", matchCase: true)));
        // 全字：relogin、LoginView、LoginForm 不算
        var whole = Run("login", wholeWord: true);
        Assert.Equal(4, whole.Total);
        Assert.Equal(2, Run(@"def\s+\w+|export function", regex: true).Total);
        Assert.Contains("正则", Run("login(", regex: true).Error);
        Assert.Equal(1, Run("login(", regex: false, include: "*.py").Total); // 不是正则时括号照字面找
    }

    [Fact]
    public void IncludeAndExcludeFiles()
    {
        Assert.Equal(new[] { "src/auth.ts" }, Paths(Run("login", include: "*.ts")));
        Assert.Equal(new[] { "src/auth.ts", "src/views/LoginView.vue" }, Paths(Run("login", include: "src")));    // 一个词：文件夹名里有它
        Assert.Equal(new[] { "src/auth.ts", "src/views/LoginView.vue" }, Paths(Run("login", include: "src/**")));
        Assert.Equal(new[] { "api/login.py", "src/auth.ts" }, Paths(Run("login", exclude: "*.vue")));
        Assert.Equal(new[] { "src/auth.ts" }, Paths(Run("login", include: "*.ts, *.py", exclude: "api")));
    }

    [Fact]
    public async Task OnlySearchesTheConversationWorkspace()
    {
        var code = new CodeFeature(id => id == "c1" ? _root : null, _ => { });
        Assert.Equal(6, (await code.SearchAsync("c1", "login", false, false, false, "", "", CancellationToken.None)).Total);
        Assert.Contains("工作区", (await code.SearchAsync("other", "login", false, false, false, "", "", CancellationToken.None)).Error);
    }

    [Fact]
    public void LongLinesAreClippedAroundTheMatch()
    {
        Write("big.txt", new string('x', 1000) + "needle" + new string('y', 1000));
        var hit = Run("needle").Files.Single().Hits.Single();
        Assert.True(hit.Text.Length <= CodeSearch.MaxLineChars);
        Assert.Equal("needle", hit.Text.Substring(hit.Column - 1 - hit.TextOffset, hit.Length));
    }
}
