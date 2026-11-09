using System.Reflection;
using System.Text;
using Flyknit.Core.Headless;

namespace Flyknit.Cli;

/// <summary>
/// flyknit.exe 入口：flyknit（命令行里对话）、flyknit -p（无界面执行一次任务，输出 JSON）和 flyknit acp（给 IDE 用的 ACP 服务）。
/// 逻辑都在 Flyknit.Core.Headless（有单元测试），这里只接标准输入输出、Ctrl+C 和退出码。
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // 不用控制台的代码页：Windows 中文系统默认 GBK，中文和越南文会变成乱码。协议和 JSON 一律 UTF-8（不带 BOM）
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        // 直接显示在命令行窗口时：运行期间把窗口切到 UTF-8（不然 GBK 窗口里中文全是乱码），退出时切回原来的，
        // 不影响这个窗口里之后运行的其他程序。输出被管道 / 重定向时不动窗口，JSON 改用 ASCII 转义（见 CliOptions.AsciiJson）
        Encoding? previous = null;
        if (OperatingSystem.IsWindows() && (!Console.IsOutputRedirected || !Console.IsErrorRedirected))
        {
            try
            {
                previous = Console.OutputEncoding;
                Console.OutputEncoding = utf8;
            }
            catch (IOException)
            {
                previous = null;
            }
        }
        var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = false };
        var stderr = new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true };
        try
        {
            return await RunAsync(args, stdout, stderr, utf8);
        }
        finally
        {
            await stdout.FlushAsync();
            if (previous is not null)
            {
                try
                {
                    Console.OutputEncoding = previous;
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private static string Version =>
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    private static async Task<int> RunAsync(string[] args, StreamWriter stdout, StreamWriter stderr, Encoding utf8)
    {
        var (options, error) = CliOptions.Parse(args);
        if (options is null)
        {
            await stderr.WriteLineAsync($"flyknit：{error}\n");
            await stderr.WriteLineAsync(CliOptions.Usage);
            return ExitCodes.Usage;
        }
        switch (options.Command)
        {
            case "help":
                await stdout.WriteLineAsync(CliOptions.Usage);
                return ExitCodes.Ok;
            case "version":
                await stdout.WriteLineAsync($"flyknit {Version}");
                return ExitCodes.Ok;
        }

        using var cts = new CancellationTokenSource();
        Func<bool>? interactiveCtrlC = null;
        Console.CancelKeyPress += (_, e) =>
        {
            // 对话模式：任务进行中停下这一轮；空闲时第一次只提示，再按一次才退出（见 InteractiveShell.Interrupt）
            if (interactiveCtrlC is { } handle)
            {
                e.Cancel = handle();
                return;
            }
            // 第一次 Ctrl+C：停下这一轮（给发出的工具调用补结果、上报审计）再退出；第二次直接退出
            if (!cts.IsCancellationRequested)
            {
                e.Cancel = true;
                cts.Cancel();
            }
        };
        void Warn(string message) => stderr.WriteLine($"flyknit：{message}");

        var profile = LocalProfile.Load(warn: Warn);
        var acp = options.Command == "acp";
        var connection = await CliHost.ConnectAsync(profile, acp ? "ide" : "cli", handler: null, Warn, cts.Token, think: options.Think);
        var permission = PrintCommand.ResolvePermission(options.Permission, profile.DefaultPermission);
        var asciiJson = Console.IsOutputRedirected;

        if (options.Command == "chat")
        {
            if (!connection.Ok)
            {
                Warn(connection.Error);
                return connection.ExitCode;
            }
            var workspace = Path.GetFullPath(options.Workspace.Length > 0 ? options.Workspace : Environment.CurrentDirectory);
            if (!Directory.Exists(workspace))
            {
                Warn($"工作区不存在：{workspace}");
                return ExitCodes.Usage;
            }
            // 中文 Windows 的控制台默认按 GBK 读输入：对话期间切到 UTF-8，退出时切回去
            Encoding? previousInput = null;
            if (OperatingSystem.IsWindows() && !Console.IsInputRedirected)
            {
                try
                {
                    previousInput = Console.InputEncoding;
                    Console.InputEncoding = utf8;
                }
                catch (IOException)
                {
                    previousInput = null;
                }
            }
            var style = new ConsoleStyle(!Console.IsOutputRedirected
                && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"))
                && Terminal.EnableColors());
            var shell = new InteractiveShell(connection.Environment!, workspace, permission, Console.In, stdout, style,
                store: new AcpSessionStore(Path.Combine(profile.Root, "ide-sessions")),
                showThinking: options.Think, version: Version,
                afterTurn: () => connection.Audit!.FlushAsync(connection.Server!, Warn),
                terminalWidth: Terminal.Width(),
                // 输入行的灰色提示：真的在终端里打字时才用（敲第一个字时擦掉）
                readKey: Console.IsInputRedirected ? null : _ => Task.Run(() => (ConsoleKeyInfo?)Console.ReadKey(intercept: true)));
            interactiveCtrlC = shell.Interrupt;
            try
            {
                return await shell.RunAsync(options.Prompt, options.Resume, CancellationToken.None);
            }
            finally
            {
                interactiveCtrlC = null;
                await connection.Audit!.FlushAsync(connection.Server!, Warn);
                if (previousInput is not null)
                {
                    try
                    {
                        Console.InputEncoding = previousInput;
                    }
                    catch (IOException)
                    {
                    }
                }
            }
        }

        if (acp)
        {
            if (!connection.Ok)
            {
                Warn(connection.Error);
            }
            var input = new StreamReader(Console.OpenStandardInput(), utf8);
            var server = new AcpServer(connection.Environment, permission, input, stdout, Warn,
                afterPrompt: connection.Ok ? () => connection.Audit!.FlushAsync(connection.Server!, Warn) : null,
                version: Version, unavailable: connection.Error,
                // IDE 会话存在本机，关掉 IDE 以后能在会话列表里找回来
                store: new AcpSessionStore(Path.Combine(profile.Root, "ide-sessions")));
            await server.RunAsync(cts.Token);
            return connection.Ok ? ExitCodes.Ok : connection.ExitCode;
        }

        if (!connection.Ok)
        {
            Warn(connection.Error);
            await PrintCommand.WriteErrorAsync(stdout, options.Format, connection.ExitCode, connection.Error, Console.IsOutputRedirected);
            return connection.ExitCode;
        }

        if (options.PromptFromStdin)
        {
            if (!Console.IsInputRedirected)
            {
                Warn("用了 -p - 或 -p 后面没写内容，但没有从管道或文件输入要做的事");
                return ExitCodes.Usage;
            }
            var text = await new StreamReader(Console.OpenStandardInput(), utf8).ReadToEndAsync(cts.Token);
            if (text.Trim().Length == 0)
            {
                Warn("标准输入里没有内容");
                return ExitCodes.Usage;
            }
            options = new CliOptions
            {
                Command = options.Command,
                Prompt = text,
                AsciiJson = asciiJson,
                Workspace = options.Workspace,
                Permission = options.Permission,
                Format = options.Format,
                AllowWrites = options.AllowWrites,
                TimeoutSeconds = options.TimeoutSeconds,
            };
        }

        if (asciiJson && !options.AsciiJson)
        {
            options = new CliOptions
            {
                Command = options.Command,
                Prompt = options.Prompt,
                AsciiJson = true,
                Workspace = options.Workspace,
                Permission = options.Permission,
                Format = options.Format,
                AllowWrites = options.AllowWrites,
                TimeoutSeconds = options.TimeoutSeconds,
            };
        }
        try
        {
            // 标准错误还在终端上：边做边把进度打出来（回答和 JSON 照常最后写到标准输出）
            var progressStyle = new ConsoleStyle(!Console.IsErrorRedirected
                && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"))
                && Terminal.EnableColors());
            var code = await PrintCommand.RunAsync(options, connection.Environment!, connection.Gateway, stdout, permission, cts.Token,
                progress: Console.IsErrorRedirected ? null : stderr, style: progressStyle);
            if (code != ExitCodes.Ok && options.Format == "text")
            {
                Warn($"任务没有完成（退出码 {code}），用 --format json 可以看到原因");
            }
            return code;
        }
        finally
        {
            await connection.Audit!.FlushAsync(connection.Server!, Warn);
        }
    }
}
