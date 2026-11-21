using System.Diagnostics;
using System.Text.Json;
using Flyknit.Core.Code;
using Flyknit.Core.Hosting;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>编码模式：代码标签只读写这个对话工作区里的文本文件；保存带版本号，AI 期间改过就不覆盖。</summary>
public sealed class CodeFeatureTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("fk-code-").FullName;
    private readonly string _outside = Directory.CreateTempSubdirectory("fk-out-").FullName;
    private readonly List<string> _log = new();
    private readonly CodeFeature _code;

    public CodeFeatureTests()
    {
        _code = new CodeFeature(id => id == "c1" ? _root : null, _log.Add);
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "app.py"), "print('hi')\n");
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "outside");
    }

    public void Dispose()
    {
        Directory.Delete(_root, true);
        Directory.Delete(_outside, true);
    }

    [Fact]
    public void LoadsAndSavesWorkspaceFiles()
    {
        var file = _code.Load("c1", "src/app.py");
        Assert.True(file.Exists);
        Assert.False(file.ReadOnly);
        Assert.Equal("print('hi')\n", file.Text);
        Assert.Equal(Path.Combine("src", "app.py"), file.Path);
        var saved = _code.Save("c1", "src/app.py", "print('bye')\n", file.Version);
        Assert.True(saved.Ok);
        Assert.Equal("print('bye')\n", File.ReadAllText(Path.Combine(_root, "src", "app.py")));
        Assert.Single(_log);
        Assert.DoesNotContain("bye", _log[0]); // 日志里没有内容
    }

    [Fact]
    public void ChangedByAiMeanwhileIsAConflictNotAnOverwrite()
    {
        var file = _code.Load("c1", "src/app.py");
        File.WriteAllText(Path.Combine(_root, "src", "app.py"), "print('ai')\n"); // AI 用 edit_file 改了
        var saved = _code.Save("c1", "src/app.py", "print('mine')\n", file.Version);
        Assert.False(saved.Ok);
        Assert.True(saved.Conflict);
        Assert.Equal("print('ai')\n", saved.CurrentText);
        Assert.Equal("print('ai')\n", File.ReadAllText(Path.Combine(_root, "src", "app.py")));
        // 员工看过以后决定用自己的：带上现在的版本再存
        Assert.True(_code.Save("c1", "src/app.py", "print('mine')\n", saved.Version).Ok);
    }

    [Fact]
    public void ParentPathsAreRefused()
    {
        var escape = "../" + Path.GetFileName(_outside) + "/secret.txt";
        Assert.False(_code.Load("c1", escape).Exists);
        Assert.False(_code.Save("c1", escape, "pwned", "").Ok);
        Assert.Equal("outside", File.ReadAllText(Path.Combine(_outside, "secret.txt")));
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("..\\x.txt")]
    [InlineData("src/../../x.txt")]
    public void WhateverThePathItNeverLeavesTheWorkspace(string path)
    {
        // 开头的 / 当作工作区根（和「文件」标签一样）；Windows 上盘符、反斜杠的 .. 直接不认。不管哪种，都落不到工作区外面
        var file = _code.Load("c1", path);
        Assert.True(file.FullPath.Length == 0 || file.FullPath.StartsWith(_root, StringComparison.OrdinalIgnoreCase), file.FullPath);
        var saved = _code.Save("c1", path, "x", "");
        if (saved.Ok)
        {
            Assert.Contains(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories), f => File.ReadAllText(f) == "x");
        }
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "x.txt")));
    }

    [Fact]
    public void ConversationWithoutWorkspaceCannotReadOrWrite()
    {
        Assert.False(_code.Load("other", "src/app.py").Exists);
        Assert.False(_code.Save("other", "src/app.py", "x", "").Ok);
    }

    [Fact]
    public void LinksPointingOutsideCanBeReadButNotSaved()
    {
        var link = Path.Combine(_root, "linked");
        try
        {
            Directory.CreateSymbolicLink(link, _outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return; // 没有建链接的权限（Windows 普通用户）：跳过
        }
        var file = _code.Load("c1", "linked/secret.txt");
        Assert.True(file.ReadOnly);
        Assert.Equal("outside", file.Reason);
        var saved = _code.Save("c1", "linked/secret.txt", "pwned", file.Version);
        Assert.False(saved.Ok);
        Assert.Equal("outside", File.ReadAllText(Path.Combine(_outside, "secret.txt")));
        // 新建文件也不行（链接目录下）
        Assert.False(_code.Save("c1", "linked/new.txt", "x", "").Ok);
        Assert.False(File.Exists(Path.Combine(_outside, "new.txt")));
    }

    [Fact]
    public void BinaryFilesAreReadOnly()
    {
        File.WriteAllBytes(Path.Combine(_root, "logo.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 1 });
        var file = _code.Load("c1", "logo.png");
        Assert.True(file.ReadOnly);
        Assert.Equal("binary", file.Reason);
        Assert.False(_code.Save("c1", "logo.png", "text", file.Version).Ok);
    }

    [Fact]
    public void NewFilesCanBeCreatedInsideTheWorkspace()
    {
        var file = _code.Load("c1", "src/new_module.py");
        Assert.False(file.Exists);
        Assert.False(file.ReadOnly);
        Assert.True(_code.Save("c1", "src/new_module.py", "x = 1\n", "").Ok);
        Assert.Equal("x = 1\n", File.ReadAllText(Path.Combine(_root, "src", "new_module.py")));
    }

    [Fact]
    public async Task BridgeMethodsReturnCamelCaseFieldsTheUiReads()
    {
        Assert.Equal(new[] { "code.load", "code.save", "code.openInVsCode", "code.focus" }, _code.Methods);
        using var doc = JsonDocument.Parse("""{"conversationId":"c1","path":"src/app.py"}""");
        var result = await _code.HandleAsync("code.load", new BridgeArgs(doc.RootElement), CancellationToken.None);
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"fullPath\":", json);
        Assert.Contains("\"readOnly\":false", json);
        Assert.Contains("\"version\":", json);
    }

    [Fact]
    public void OpensTheWorkspaceInVsCodeWithArgumentsNotAShell()
    {
        ProcessStartInfo? started = null;
        var code = new CodeFeature(id => id == "c1" ? _root : null, _log.Add)
        {
            FindVsCode = () => "C:/VS Code/Code.exe",
            Launch = psi => { started = psi; return true; },
        };
        var r = JsonSerializer.SerializeToElement(code.OpenInVsCode("c1", "src/app.py", 12));
        Assert.True(r.GetProperty("ok").GetBoolean());
        Assert.Equal("C:/VS Code/Code.exe", started!.FileName);
        Assert.False(started.UseShellExecute);
        Assert.Equal(new[] { _root, "--goto", Path.Combine(_root, "src", "app.py") + ":12" }, started.ArgumentList);
        // 工作区外的文件不带上，只开工作区
        code.OpenInVsCode("c1", "../x.txt", 0);
        Assert.Equal(new[] { _root }, started.ArgumentList);
    }

    [Fact]
    public void ReportsWhenVsCodeIsMissingOrThereIsNoWorkspace()
    {
        var code = new CodeFeature(id => id == "c1" ? _root : null, _log.Add) { FindVsCode = () => null, Launch = _ => throw new InvalidOperationException("不该启动") };
        Assert.False(JsonSerializer.SerializeToElement(code.OpenInVsCode("c1", "", 0)).GetProperty("ok").GetBoolean());
        Assert.False(JsonSerializer.SerializeToElement(code.OpenInVsCode("other", "", 0)).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void FindsVsCodeInTheUsualPlacesAndNextToCodeCmdOnPath()
    {
        var sep = Path.PathSeparator;
        var env = new Dictionary<string, string?> { ["LOCALAPPDATA"] = "/u/local", ["ProgramFiles"] = "/pf", ["PATH"] = $"/x/bin{sep}/tools/VSCode/bin" };
        var user = Path.Combine("/u/local", "Programs", "Microsoft VS Code", "Code.exe");
        Assert.Equal(user, VsCode.Find(k => env.GetValueOrDefault(k), f => f == user));
        // Windows 上 GetDirectoryName 会把 / 换成 \\：预期值用同样的路径函数算，两边平台都对
        var custom = Path.Combine(Path.GetDirectoryName("/tools/VSCode/bin")!, "Code.exe");
        var onPath = new HashSet<string> { Path.Combine("/tools/VSCode/bin", "code.cmd"), custom };
        Assert.Equal(custom, VsCode.Find(k => env.GetValueOrDefault(k), onPath.Contains));
        Assert.Null(VsCode.Find(k => env.GetValueOrDefault(k), _ => false));
    }
}
