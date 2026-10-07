using System.Diagnostics;
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
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly StringBuilder _stderr = new();
    private Process? _process;
    private Task? _stdoutLoop;

    public StdioTransport(McpServerConfig config, string? workingDirectory = null)
    {
        _config = config;
        _workingDirectory = workingDirectory;
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
        var (file, args) = ResolveCommand(_config.Command, _config.Args, OperatingSystem.IsWindows(), FindOnPath);
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        foreach (var (k, v) in _config.Env)
        {
            psi.Environment[k] = v;
        }
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
                _stderr.AppendLine(e.Data);
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
    /// </summary>
    public static (string File, IReadOnlyList<string> Args) ResolveCommand(
        string command, IReadOnlyList<string> args, bool windows, Func<string, string?> findOnPath)
    {
        if (!windows)
        {
            return (command, args);
        }
        var ext = Path.GetExtension(command).ToLowerInvariant();
        if (ext is ".cmd" or ".bat")
        {
            return ("cmd.exe", new[] { "/d", "/s", "/c", command }.Concat(args).ToList());
        }
        if (ext == ".exe" || command.Equals("cmd", StringComparison.OrdinalIgnoreCase))
        {
            return (command, args);
        }
        if (ext.Length == 0 && findOnPath(command + ".exe") is { } exe)
        {
            return (exe, args);
        }
        return ("cmd.exe", new[] { "/d", "/s", "/c", command }.Concat(args).ToList());
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
