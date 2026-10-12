using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Flyknit.Core.Mcp;

/// <summary>
/// stdio：在这台电脑上起一个进程，一行一条 JSON-RPC 消息走标准输入输出。
/// 标准错误输出留最后一段，连接失败时拿来告诉用户为什么（比如 npx 没装）。
/// </summary>
public sealed class StdioTransport : McpTransport
{
    private const int StderrKeep = 4000;

    private readonly McpServerConfig _config;
    private readonly string? _workingDirectory;
    private readonly McpRuntimeEnvironment? _runtime;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly StringBuilder _stderr = new();
    private Process? _process;
    private Task? _stdoutLoop;

    /// <param name="runtime">公司服务器提供的运行环境（Node.js / uv / Python 和 npm、PyPI 镜像），没有就用这台电脑自己的。</param>
    public StdioTransport(McpServerConfig config, string? workingDirectory = null, McpRuntimeEnvironment? runtime = null)
    {
        _config = config;
        _workingDirectory = workingDirectory;
        _runtime = runtime;
    }

    /// <summary>最近的标准错误输出（截尾）。</summary>
    public string StderrTail
    {
        get
        {
            lock (_stderr)
            {
                return _stderr.ToString().Trim();
            }
        }
    }

    public override Task StartAsync(CancellationToken ct)
    {
        var windows = OperatingSystem.IsWindows();
        var managed = _runtime?.PathDirs ?? Array.Empty<string>();
        string? Find(string f) => FindIn(managed, f) ?? FindOnPath(f);
        if (!CanFind(_config.Command, windows, Find))
        {
            throw new McpException(McpRuntimeManager.MissingMessage(_config.Command));
        }
        var (file, args) = ResolveCommand(_config.Command, _config.Args, windows, Find);
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            // 标准错误按字节原样读进来（Latin1 一个字节对一个字符），每行再判断是 UTF-8 还是 GBK，见 ConsoleText
            StandardErrorEncoding = Encoding.Latin1,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        if (windows && file == "cmd.exe" && args.Count >= 3 && args[0] == "/d" && args[1] == "/s" && args[2] == "/c")
        {
            psi.Arguments = CmdArguments(args.Skip(3).ToList());
        }
        else
        {
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }
        }
        ApplyEnvironment(psi.Environment, _runtime, _config.Env);
        if (!string.IsNullOrEmpty(_workingDirectory) && Directory.Exists(_workingDirectory))
        {
            psi.WorkingDirectory = _workingDirectory;
        }

        try
        {
            _process = Process.Start(psi) ?? throw new McpException($"启动 {_config.Command} 失败");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new McpException($"找不到或启动不了 {_config.Command}：{ex.Message}。请联系 IT 在这台电脑上安装它", ex);
        }
        _process.EnableRaisingEvents = true;
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }
            lock (_stderr)
            {
                _stderr.AppendLine(ConsoleText.FromLatin1(e.Data));
                if (_stderr.Length > StderrKeep * 2)
                {
                    _stderr.Remove(0, _stderr.Length - StderrKeep);
                }
            }
        };
        _process.BeginErrorReadLine();
        _stdoutLoop = Task.Run(ReadLoopAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task ReadLoopAsync()
    {
        var reader = _process!.StandardOutput;
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }
                JsonNode? node;
                try
                {
                    node = JsonNode.Parse(line);
                }
                catch (System.Text.Json.JsonException)
                {
                    continue; // 有些服务会往标准输出打日志，跳过不是 JSON 的行
                }
                Dispatch(node);
            }
        }
        catch (Exception)
        {
            // 进程被关掉时读会抛异常，下面统一报告
        }
        var tail = StderrTail;
        var code = _process.HasExited ? _process.ExitCode.ToString() : "?";
        FailAll(tail.Length > 0
            ? $"{_config.Command} 已退出（退出码 {code}）：{Last(tail, 500)}"
            : $"{_config.Command} 已退出（退出码 {code}）");
    }

    protected override async Task SendAsync(JsonObject message, long? requestId, CancellationToken ct)
    {
        var process = _process ?? throw new McpException("进程还没启动");
        if (process.HasExited)
        {
            throw new McpException($"{_config.Command} 已经退出了：{Last(StderrTail, 500)}");
        }
        await _writeLock.WaitAsync(ct);
        try
        {
            // 规范要求消息里不能有换行；ToJsonString 默认就是单行
            await process.StandardInput.WriteLineAsync(message.ToJsonString().AsMemory(), ct);
            await process.StandardInput.FlushAsync(ct);
        }
        catch (IOException ex)
        {
            throw new McpException($"{_config.Command} 不再接收消息：{Last(StderrTail, 500)}", ex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        var process = _process;
        if (process is null)
        {
            return;
        }
        try
        {
            // 先关输入让它自己退出，等一会儿不退再整棵进程树杀掉（npx 会再起一个 node）
            process.StandardInput.Close();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await process.WaitForExitAsync(cts.Token);
        }
        catch (Exception)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // 已经退出了
            }
        }
        if (_stdoutLoop is not null)
        {
            try
            {
                await _stdoutLoop.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
                // 不等了
            }
        }
        process.Dispose();
        _writeLock.Dispose();
    }

    private static string Last(string s, int n) => s.Length <= n ? s : "…" + s[^n..];

    /// <summary>
    /// Windows 上 npx、uvx 这些其实是 .cmd 批处理，不经过 cmd.exe 起不来——
    /// Claude Code 文档里让用户手写 `cmd /c npx`，这里替用户做掉。
    /// 已经写了扩展名的 .exe、或者找到的是 .exe 的，直接起。
    /// findOnPath 先找托管运行环境的目录再找 PATH；找到的用完整路径，因为子进程的 PATH 起进程时不算数。
    /// </summary>
    public static (string File, IReadOnlyList<string> Args) ResolveCommand(
        string command, IReadOnlyList<string> args, bool windows, Func<string, string?> findOnPath)
    {
        if (!windows)
        {
            return (findOnPath(command) is { } found && !Path.IsPathRooted(command) && !command.Contains('/') ? found : command, args);
        }
        IReadOnlyList<string> ViaCmd(string target) => new[] { "/d", "/s", "/c", target }.Concat(args).ToList();
        var ext = Path.GetExtension(command).ToLowerInvariant();
        if (ext is ".cmd" or ".bat")
        {
            return ("cmd.exe", ViaCmd(findOnPath(command) ?? command));
        }
        if (command.Equals("cmd", StringComparison.OrdinalIgnoreCase))
        {
            return (command, args);
        }
        if (ext == ".exe")
        {
            return (findOnPath(command) ?? command, args);
        }
        if (findOnPath(command + ".exe") is { } exe)
        {
            return (exe, args);
        }
        if ((findOnPath(command + ".cmd") ?? findOnPath(command + ".bat")) is { } shim)
        {
            return ("cmd.exe", ViaCmd(shim));
        }
        return ("cmd.exe", ViaCmd(command));
    }

    /// <summary>起进程之前先看看命令在不在（托管目录 + PATH，Windows 上带 .exe/.cmd/.bat），不在就直接说清楚。</summary>
    public static bool CanFind(string command, bool windows, Func<string, string?> findOnPath)
    {
        if (command.Length == 0)
        {
            return false;
        }
        if (Path.IsPathRooted(command) || command.Contains('/') || command.Contains('\\'))
        {
            return true; // 写了路径的交给 Process.Start 报错
        }
        if (!windows)
        {
            return findOnPath(command) is not null;
        }
        if (command.Equals("cmd", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        var ext = Path.GetExtension(command).ToLowerInvariant();
        if (ext is ".exe" or ".cmd" or ".bat" or ".com")
        {
            return findOnPath(command) is not null;
        }
        return new[] { ".exe", ".cmd", ".bat", ".com" }.Any(e => findOnPath(command + e) is not null);
    }

    /// <summary>
    /// cmd.exe /d /s /c 的参数：整条命令再包一层引号，/s 会去掉最外面这层——
    /// 否则 C:\Users\John Smith\...\npx.cmd 这种带空格的路径会被 cmd 拆错。
    /// </summary>
    public static string CmdArguments(IReadOnlyList<string> command) =>
        "/d /s /c \"" + string.Join(" ", command.Select(QuoteArgument)) + "\"";

    /// <summary>按 Windows 命令行规则给一个参数加引号（和 .NET ArgumentList 的规则一样）。</summary>
    public static string QuoteArgument(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
        {
            return arg;
        }
        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            sb.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
            backslashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// 子进程的环境变量：托管运行环境的目录加到 PATH 最前面，再设它的变量，最后是连接器自己的 env（优先）。
    /// </summary>
    public static void ApplyEnvironment(IDictionary<string, string?> env, McpRuntimeEnvironment? runtime, IReadOnlyDictionary<string, string> vendorEnv)
    {
        if (runtime is not null)
        {
            if (runtime.PathDirs.Count > 0)
            {
                var key = env.Keys.FirstOrDefault(k => k.Equals("PATH", StringComparison.OrdinalIgnoreCase)) ?? "PATH";
                var old = env.TryGetValue(key, out var v) ? v : null;
                env[key] = string.Join(Path.PathSeparator, runtime.PathDirs) + (string.IsNullOrEmpty(old) ? "" : Path.PathSeparator + old);
            }
            foreach (var (k, v) in runtime.Env)
            {
                env[k] = v;
            }
        }
        foreach (var (k, v) in vendorEnv)
        {
            env[k] = v;
        }
    }

    private static string? FindIn(IReadOnlyList<string> dirs, string file)
    {
        foreach (var dir in dirs)
        {
            var candidate = Path.Combine(dir, file);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private static string? FindOnPath(string file)
    {
        if (Path.IsPathRooted(file))
        {
            return File.Exists(file) ? file : null;
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), file);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // PATH 里有乱七八糟的项
            }
        }
        return null;
    }
}

/// <summary>
/// 命令行程序标准错误的编码：node、python 写 UTF-8，cmd.exe 和 Windows 自带的工具写 OEM 代码页（中文系统是 GBK 936）。
/// 按字节读进来，每行先按严格 UTF-8 解，解不了就当 OEM 代码页，「'npx' 不是内部或外部命令」就不会变成乱码。
/// </summary>
public static class ConsoleText
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    static ConsoleText()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>这台电脑控制台用的 OEM 代码页。</summary>
    public static int OemCodePage
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    return (int)GetOEMCP();
                }
                catch (Exception)
                {
                    // 退回按区域设置
                }
            }
            return CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
        }
    }

    /// <summary>一行按 Latin1 读进来的文字（每个字符就是一个字节）还原成正确的文字。</summary>
    public static string FromLatin1(string line, int? oemCodePage = null) =>
        Decode(Encoding.Latin1.GetBytes(line), oemCodePage ?? OemCodePage);

    public static string Decode(byte[] bytes, int oemCodePage)
    {
        if (bytes.All(b => b < 0x80))
        {
            return Encoding.ASCII.GetString(bytes);
        }
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // 不是 UTF-8
        }
        try
        {
            return Encoding.GetEncoding(oemCodePage).GetString(bytes);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.UTF8.GetString(bytes);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();
}
