using Flyknit.Core.Headless;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>flyknit 对话模式的输入行：退格、Tab 补全、历史、灰色提示、中文宽度、Ctrl+C。</summary>
public class LineEditorTests
{
    private static (LineEditor Editor, StringWriter Output, Queue<ConsoleKeyInfo?> Keys) Make(string typed, int width = 80)
    {
        var keys = InteractiveShellTests.Keys(typed);
        var output = new StringWriter();
        var editor = new LineEditor(_ => Task.FromResult(keys.Count > 0 ? keys.Dequeue() : null), output, new ConsoleStyle(true), width)
        {
            Completions = InteractiveShell.CommandNames,
        };
        return (editor, output, keys);
    }

    private static Task<string?> Read(LineEditor e) => e.ReadLineAsync("> ", "提示", CancellationToken.None);

    [Theory]
    [InlineData("abc\b\r", "ab")]
    [InlineData("a\b\r", "")]                 // 第一个字也能删
    [InlineData("\b\b\bok\r", "ok")]          // 空行上退格什么都不做
    [InlineData("中文\b\r", "中")]
    [InlineData("hello\u001bhi\r", "hi")]     // Esc 清空整行
    public async Task BackspaceAndEscapeEdit(string typed, string expected)
    {
        var (editor, _, _) = Make(typed);
        Assert.Equal(expected, await Read(editor));
    }

    [Fact]
    public async Task EmojiIsOneCharacter()
    {
        var (editor, _, _) = Make("a😀\b\r");
        Assert.Equal("a", await Read(editor));
    }

    [Theory]
    [InlineData("/pl\t\r", "/plan ")]         // 只有一个对得上：补全并加空格
    [InlineData("/p\t\r", "/p")]              // /plan、/permission 都对得上：先不动，再按一次 Tab 列出来
    [InlineData("/th\t\r", "/think ")]
    [InlineData("/E\t\r", "/exit ")]          // 不分大小写
    [InlineData("/zz\t\r", "/zz")]            // 没有对得上的：不动
    [InlineData("hi /p\t\r", "hi /p")]        // 不是以 / 开头：不补全
    [InlineData("/plan x\t\r", "/plan x")]    // 已经在打参数了：不补全
    public async Task TabCompletesCommands(string typed, string expected)
    {
        var (editor, _, _) = Make(typed);
        Assert.Equal(expected, await Read(editor));
    }

    [Fact]
    public async Task SecondTabListsAmbiguousCandidates()
    {
        var (editor, output, _) = Make("/\t\t\r");
        editor.Completions = new[] { "/plan", "/plugins", "/help" };
        Assert.Equal("/", await Read(editor));
        var text = output.ToString();
        Assert.Contains("/plan", text);
        Assert.Contains("/plugins", text);

        // 有公共部分：先补公共部分
        var (e2, _, _) = Make("/p\t\r");
        e2.Completions = new[] { "/plan", "/plugins" };
        Assert.Equal("/pl", await Read(e2));
    }

    [Fact]
    public async Task AmbiguousRealCommandsAreListedOnSecondTab()
    {
        var (editor, output, _) = Make("/p\t\t\r");
        Assert.Equal("/p", await Read(editor));
        var text = output.ToString();
        Assert.Contains("/plan", text);
        Assert.Contains("/permission", text);
    }

    [Fact]
    public async Task UpArrowRecallsEarlierInput()
    {
        var keys = InteractiveShellTests.Keys("第一句\r");
        keys.Enqueue(new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, false, false, false));
        keys.Enqueue(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));
        var editor = new LineEditor(_ => Task.FromResult(keys.Count > 0 ? keys.Dequeue() : null), new StringWriter(), new ConsoleStyle(true), 80);
        Assert.Equal("第一句", await Read(editor));
        Assert.Equal("第一句", await Read(editor));
    }

    [Fact]
    public async Task PlaceholderComesBackWhenTheLineIsEmptyAgain()
    {
        var (editor, output, _) = Make("a\b\r");
        await Read(editor);
        var text = output.ToString();
        Assert.Equal(2, text.Split("提示").Length - 1); // 开始一次，删空后又一次
    }

    [Fact]
    public async Task CtrlDOnEmptyLineEndsInput()
    {
        var keys = new Queue<ConsoleKeyInfo?>(new ConsoleKeyInfo?[] { new ConsoleKeyInfo('\u0004', ConsoleKey.D, false, false, true) });
        var editor = new LineEditor(_ => Task.FromResult(keys.Count > 0 ? keys.Dequeue() : null), new StringWriter(), new ConsoleStyle(true), 80);
        Assert.Null(await Read(editor));
    }

    [Fact]
    public async Task CtrlCWhileIdleStartsAFreshLine()
    {
        // Ctrl+C 由别的线程调 Interrupted：之前打的字作废，之后打的才算
        var keys = InteractiveShellTests.Keys("旧的");
        var output = new StringWriter();
        LineEditor? editor = null;
        var step = 0;
        editor = new LineEditor(_ =>
        {
            if (keys.Count > 0)
            {
                return Task.FromResult(keys.Dequeue());
            }
            if (step++ == 0)
            {
                editor!.Interrupted("（再按一次 Ctrl+C 退出）");
                foreach (var k in InteractiveShellTests.Keys("新的\r"))
                {
                    keys.Enqueue(k);
                }
                return Task.FromResult(keys.Dequeue());
            }
            return Task.FromResult<ConsoleKeyInfo?>(null);
        }, output, new ConsoleStyle(true), 80);
        Assert.Equal("新的", await Read(editor));
        Assert.Contains("再按一次 Ctrl+C 退出", output.ToString());
    }

    [Theory]
    [InlineData(2, 0, 80, 0)]
    [InlineData(2, 77, 80, 0)]     // 正好写满一行：光标还停在这一行
    [InlineData(2, 78, 80, 0)]
    [InlineData(2, 79, 80, 1)]
    [InlineData(2, 158, 80, 1)]
    [InlineData(2, 159, 80, 2)]
    public void CursorRowAccountsForWrapping(int prompt, int total, int width, int row)
    {
        // 「> 」占 2 列，80 列宽：输入 78 列时正好写满第一行
        Assert.Equal(row, LineEditor.CursorRow(prompt, total, width));
    }

    [Fact]
    public async Task BackspaceAcrossALineWrapMovesUp()
    {
        // 10 列宽的终端，「> 」占 2 列：打 9 个字换到第二行，退格后要回到上一行重画
        var (editor, output, _) = Make("123456789\b\r", width: 10);
        Assert.Equal("12345678", await Read(editor));
        Assert.Contains("\u001b[1A", output.ToString(), StringComparison.Ordinal);
    }
}
