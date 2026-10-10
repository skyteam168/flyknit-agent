using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Flyknit.Core.Gateway;

namespace Flyknit.Client.Services;

/// <summary>更新到了哪一步。界面照这个显示。</summary>
public enum UpdateStage
{
    /// <summary>没有新版本，或还没查过。</summary>
    None,
    Downloading,
    /// <summary>下好了、校验过了、解开了，等用户点「重启升级」或者等他退出程序。</summary>
    Ready,
    /// <summary>这台电脑装不上（装在 Program Files 而当前用户没有写权限之类）。</summary>
    NeedsIt,
}

/// <summary>用户点「检查更新」的结果。</summary>
public enum UpdateCheckOutcome
{
    UpToDate,
    /// <summary>有新版本，正在后台下载（可能是这次发现的，也可能之前就在下）。</summary>
    Downloading,
    /// <summary>新版本已下好，点「重启升级」就能装。</summary>
    Ready,
    /// <summary>新版本有，但这台电脑装不上，要 IT 协助。</summary>
    NeedsIt,
    /// <summary>连不上服务器之类。Message 里是原因。</summary>
    Failed,
}

public sealed record UpdateCheckResult(UpdateCheckOutcome Outcome, string Version, string Message);

public sealed record UpdateState(UpdateStage Stage, string Version, string Notes, double Progress, string Message)
{
    public static readonly UpdateState Idle = new(UpdateStage.None, "", "", 0, "");
}

/// <summary>
/// 自动更新。
///
/// 节奏按用户的要求来：有新版本就在界面上挂一条，点「重启升级」立刻装；不点也行，
/// 退出程序或者下次开机的时候自动装上。所以**下载和安装是分开的**——下载在后台
/// 悄悄做完，安装只在能重启的那一刻发生。
///
/// 装不上的情况要尽早发现：程序装在 Program Files 而员工不是管理员时，拷贝一定失败。
/// 与其等到他点了「重启升级」、程序退出、再弹一个失败，不如在下载完就探一次写权限，
/// 界面直接显示「需要 IT 协助」。
/// </summary>
public sealed class UpdateService : IDisposable
{
    /// <summary>开机后等一会儿再查：刚启动时网络和界面都在忙，这事不急。</summary>
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly FlyknitServerClient _server;
    private readonly string _currentVersion;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Task? _loop;

    public UpdateState State { get; private set; } = UpdateState.Idle;

    /// <summary>状态变了。界面据此刷新那条提示。</summary>
    public event Action<UpdateState>? Changed;

    public UpdateService(FlyknitServerClient server, string currentVersion)
    {
        _server = server;
        _currentVersion = currentVersion;
    }

    /// <summary>程序目录，也就是要被替换掉的那个目录。</summary>
    public static string InstallDirectory =>
        Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory)!.TrimEnd(Path.DirectorySeparatorChar);

    /// <summary>
    /// 用安装程序（Setup.exe）装在 Program Files 里的：员工账号换不了程序目录，升级由运维代理（SYSTEM）负责。
    /// 安装程序在程序目录里放了这个标记文件。
    /// </summary>
    public static bool Managed => File.Exists(Path.Combine(InstallDirectory, "flyknit.managed"));

    /// <summary>代理写的升级状态：version、notes、stage（downloading / ready / installed / failed）。</summary>
    private static string AgentStatusFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Flyknit", "client-update.json");

    private static readonly TimeSpan ManagedPoll = TimeSpan.FromMinutes(1);

    private static string StagingRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Flyknit", "updates");

    public void Start()
    {
        _loop ??= Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        if (Managed)
        {
            await ManagedLoopAsync(ct);
            return;
        }
        // 上次下好了没装上的，开机先装——用户说的「下次开机自动更新」就是这里
        TryApplyStaged(silent: false);

        try
        {
            await Task.Delay(FirstDelay, ct);
            while (!ct.IsCancellationRequested)
            {
                await CheckAsync(ct);
                await Task.Delay(Interval, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn("检查更新的循环退出了", ex);
        }
    }

    /// <summary>代理管升级：只看代理写的状态文件，新版本下好了就显示「新版本就绪」。</summary>
    private async Task ManagedLoopAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            while (!ct.IsCancellationRequested)
            {
                RefreshFromAgent();
                await Task.Delay(ManagedPoll, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void RefreshFromAgent()
    {
        var status = ReadAgentStatus();
        if (status is null || !IsNewer(status.Value.Version, _currentVersion))
        {
            if (State.Stage != UpdateStage.None)
            {
                Publish(UpdateState.Idle);
            }
            return;
        }
        var next = status.Value.Stage switch
        {
            "ready" => new UpdateState(UpdateStage.Ready, status.Value.Version, status.Value.Notes, 1, ""),
            "downloading" => new UpdateState(UpdateStage.Downloading, status.Value.Version, status.Value.Notes, 0, ""),
            _ => UpdateState.Idle,
        };
        if (next != State)
        {
            Publish(next);
        }
    }

    private static (string Version, string Notes, string Stage)? ReadAgentStatus()
    {
        try
        {
            if (!File.Exists(AgentStatusFile))
            {
                return null;
            }
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(AgentStatusFile));
            string Str(string name) => doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : "";
            return (Str("version"), Str("notes"), Str("stage"));
        }
        catch (Exception)
        {
            return null;  // 代理正在写，下一分钟再看
        }
    }

    /// <summary>只比较数字段（0.10.0 比 0.9.0 新）。认不出来就当不是更新。</summary>
    public static bool IsNewer(string candidate, string current)
    {
        static Version? Parse(string v) => Version.TryParse(v.Split('-', '+')[0].Trim().TrimStart('v', 'V'), out var r) ? r : null;
        var (a, b) = (Parse(candidate), Parse(current));
        return a is not null && b is not null && a > b;
    }

    /// <summary>查一次。有新版本就在后台下好、校验、解开，然后把状态置为 Ready。</summary>
    public async Task CheckAsync(CancellationToken ct)
    {
        if (Managed)
        {
            RefreshFromAgent();
            return;
        }
        if (!await _gate.WaitAsync(0, ct))
        {
            return;  // 上一次还在跑
        }
        try
        {
            if (State.Stage is UpdateStage.Downloading or UpdateStage.Ready)
            {
                return;
            }
            var update = await _server.CheckUpdateAsync(_currentVersion, ct);
            if (!update.Available || update.Version.Length == 0)
            {
                return;
            }
            Log.Info($"发现新版本 {update.Version}（当前 {_currentVersion}）");
            await DownloadAsync(update, ct);
        }
        catch (Exception ex)
        {
            // 查不到就下次再查。这事不该打扰用户
            Log.Warn("检查更新失败", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 用户在设置里点「检查更新」：马上问服务器，把结果告诉他（已是最新 / 正在下载 / 已就绪 / 失败）。
    /// 发现新版本就交给后台下载，不让界面等下载完。
    /// </summary>
    public async Task<UpdateCheckResult> CheckNowAsync(CancellationToken ct)
    {
        if (Managed)
        {
            // 代理管升级：已经下好就说就绪；没有就问服务端，有新版本告诉员工代理会在后台装
            RefreshFromAgent();
            if (Known() is { } ready)
            {
                return ready;
            }
            try
            {
                var update = await _server.CheckUpdateAsync(_currentVersion, ct);
                return update.Available && update.Version.Length > 0
                    ? new UpdateCheckResult(UpdateCheckOutcome.Downloading, update.Version, "")
                    : new UpdateCheckResult(UpdateCheckOutcome.UpToDate, _currentVersion, "");
            }
            catch (Exception ex)
            {
                Log.Warn("手动检查更新失败", ex);
                return new UpdateCheckResult(UpdateCheckOutcome.Failed, "", ex is OperationCanceledException ? "timeout" : ex.Message);
            }
        }
        if (Known() is { } known)
        {
            return known;
        }
        await _gate.WaitAsync(ct);  // 后台那次正好在查，等它查完
        var handedOff = false;
        try
        {
            if (Known() is { } after)
            {
                return after;
            }
            var update = await _server.CheckUpdateAsync(_currentVersion, ct);
            if (!update.Available || update.Version.Length == 0)
            {
                return new UpdateCheckResult(UpdateCheckOutcome.UpToDate, _currentVersion, "");
            }
            Log.Info($"手动检查：发现新版本 {update.Version}（当前 {_currentVersion}）");
            Publish(new UpdateState(UpdateStage.Downloading, update.Version, update.Notes, 0, ""));
            handedOff = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    await DownloadAsync(update, _cts.Token);
                }
                finally
                {
                    _gate.Release();
                }
            });
            return new UpdateCheckResult(UpdateCheckOutcome.Downloading, update.Version, "");
        }
        catch (Exception ex)
        {
            Log.Warn("手动检查更新失败", ex);
            return new UpdateCheckResult(UpdateCheckOutcome.Failed, "", ex is OperationCanceledException ? "timeout" : ex.Message);
        }
        finally
        {
            if (!handedOff)
            {
                _gate.Release();
            }
        }
    }

    /// <summary>已经在下载、已就绪、装不上：不用再问服务器，直接告诉用户现在的情况。</summary>
    private UpdateCheckResult? Known() => State.Stage switch
    {
        UpdateStage.Downloading => new UpdateCheckResult(UpdateCheckOutcome.Downloading, State.Version, ""),
        UpdateStage.Ready => new UpdateCheckResult(UpdateCheckOutcome.Ready, State.Version, ""),
        UpdateStage.NeedsIt => new UpdateCheckResult(UpdateCheckOutcome.NeedsIt, State.Version, State.Message),
        _ => null,
    };

    private async Task DownloadAsync(ClientUpdate update, CancellationToken ct)
    {
        var dir = Path.Combine(StagingRoot, update.Version);
        var zip = Path.Combine(dir, "update.zip");
        var unpacked = Path.Combine(dir, "files");
        Directory.CreateDirectory(dir);

        Publish(new UpdateState(UpdateStage.Downloading, update.Version, update.Notes, 0, ""));
        try
        {
            await _server.DownloadUpdateAsync(update.Version, zip,
                p => Publish(State with { Progress = p }), ct);

            var actual = Sha256Of(zip);
            if (!string.Equals(actual, update.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                // 对不上就当没发生过。宁可停在旧版本，也不能把来路不明的文件铺上去
                Log.Warn($"安装包校验不通过：期望 {update.Sha256[..12]}，实际 {actual[..12]}");
                Cleanup(dir);
                Publish(UpdateState.Idle);
                return;
            }

            if (Directory.Exists(unpacked))
            {
                Directory.Delete(unpacked, recursive: true);
            }
            ZipFile.ExtractToDirectory(zip, unpacked);
            var root = RootOf(unpacked);

            if (!CanWriteInstallDirectory())
            {
                Log.Warn($"{InstallDirectory} 不可写，这台电脑装不了更新");
                Publish(new UpdateState(UpdateStage.NeedsIt, update.Version, update.Notes, 1, InstallDirectory));
                return;
            }

            StagedUpdate.Write(update.Version, root, zip);
            Publish(new UpdateState(UpdateStage.Ready, update.Version, update.Notes, 1, ""));
            Log.Info($"新版本 {update.Version} 已就绪，等待重启安装");
        }
        catch (Exception ex)
        {
            Log.Warn($"下载 {update.Version} 失败", ex);
            Cleanup(dir);
            Publish(UpdateState.Idle);
        }
    }

    /// <summary>
    /// zip 里可能是 FlyknitBuddy.exe 直接摊在根上，也可能外面还包了一层目录
    /// （右键「压缩」出来的就是后者）。两种都要认。
    /// </summary>
    private static string RootOf(string unpacked)
    {
        if (File.Exists(Path.Combine(unpacked, "FlyknitBuddy.exe")))
        {
            return unpacked;
        }
        var dirs = Directory.GetDirectories(unpacked);
        if (dirs.Length == 1 && File.Exists(Path.Combine(dirs[0], "FlyknitBuddy.exe")))
        {
            return dirs[0];
        }
        return unpacked;
    }

    /// <summary>
    /// 能不能写程序目录。装在 Program Files 而员工不是管理员时写不了——
    /// 这种情况要在界面上说清楚，而不是等退出时才失败。
    /// </summary>
    public static bool CanWriteInstallDirectory()
    {
        try
        {
            var probe = Path.Combine(InstallDirectory, $".write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>立刻装：启动更新器，然后调用方让程序退出。</summary>
    public bool ApplyNow() => Managed ? WaitForAgent() : Launch(silent: false);

    /// <summary>退出时装。用户点了「稍后」，那就在他关掉程序的时候悄悄换上。代理管升级的不用管：程序一退代理就换。</summary>
    public bool ApplyOnExit() => !Managed && State.Stage == UpdateStage.Ready && Launch(silent: true);

    /// <summary>
    /// 代理管升级时点「重启升级」：程序退出后代理几秒内就换好。起一个更新器在旁边等，
    /// 看到程序目录里换成了新版本就把它重新打开（等不到也打开旧的，不让员工以为程序没了）。
    /// </summary>
    private bool WaitForAgent()
    {
        if (State.Stage != UpdateStage.Ready)
        {
            return false;
        }
        try
        {
            var source = Path.Combine(InstallDirectory, "FlyknitUpdater.exe");
            if (!File.Exists(source))
            {
                return false;
            }
            var runner = Path.Combine(Path.GetTempPath(), $"FlyknitUpdater-{Guid.NewGuid():N}.exe");
            File.Copy(source, runner, overwrite: true);
            var info = new ProcessStartInfo(runner) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[]
            {
                "--wait-version", State.Version,
                "--exe", Environment.ProcessPath ?? Path.Combine(InstallDirectory, "FlyknitBuddy.exe"),
                "--log", Path.Combine(AppPaths.Logs, "update.log"),
            })
            {
                info.ArgumentList.Add(a);
            }
            Process.Start(info);
            Log.Info($"等运维代理装上 {State.Version}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("启动等待升级的程序失败", ex);
            return false;
        }
    }

    /// <summary>开机时发现上次下好没装的，直接装上。</summary>
    private void TryApplyStaged(bool silent)
    {
        if (Managed)
        {
            return;
        }
        var staged = StagedUpdate.Read();
        if (staged is null)
        {
            return;
        }
        if (staged.Version == _currentVersion || !Directory.Exists(staged.Source))
        {
            // 已经是这个版本了（上次装成功了），或者文件没了：清掉记录
            StagedUpdate.Clear();
            return;
        }
        State = new UpdateState(UpdateStage.Ready, staged.Version, "", 1, "");
        Launch(silent);
    }

    private bool Launch(bool silent)
    {
        var staged = StagedUpdate.Read();
        if (staged is null || !Directory.Exists(staged.Source))
        {
            return false;
        }
        try
        {
            // 更新器不能待在要被替换的目录里，否则替换到一半把自己删了
            var runner = Path.Combine(Path.GetTempPath(), $"FlyknitUpdater-{Guid.NewGuid():N}.exe");
            var source = Path.Combine(InstallDirectory, "FlyknitUpdater.exe");
            if (!File.Exists(source))
            {
                Log.Warn("找不到 FlyknitUpdater.exe，无法自动更新");
                return false;
            }
            File.Copy(source, runner, overwrite: true);

            var exe = Environment.ProcessPath ?? Path.Combine(InstallDirectory, "FlyknitBuddy.exe");
            var args = new[]
            {
                "--pid", Environment.ProcessId.ToString(),
                "--source", staged.Source,
                "--target", InstallDirectory,
                "--exe", exe,
                "--zip", staged.Zip,
                "--log", Path.Combine(AppPaths.Logs, "update.log"),
            }.ToList();
            if (silent)
            {
                args.Add("--silent");
            }

            var info = new ProcessStartInfo(runner) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args)
            {
                info.ArgumentList.Add(a);
            }
            Process.Start(info);
            StagedUpdate.Clear();
            Log.Info($"已启动更新器，准备装上 {staged.Version}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("启动更新器失败", ex);
            return false;
        }
    }

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void Cleanup(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception)
        {
            // 清理失败不影响下一次
        }
    }

    private void Publish(UpdateState state)
    {
        State = state;
        Changed?.Invoke(state);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _gate.Dispose();
    }

    /// <summary>
    /// 下好但还没装的那一份，记在磁盘上——程序退出后这条记录还要用，所以不能只存在内存里。
    /// </summary>
    internal sealed record StagedUpdate(string Version, string Source, string Zip)
    {
        private static string File_ => Path.Combine(AppPaths.Root, "pending-update.json");

        public static void Write(string version, string source, string zip)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Root);
                System.IO.File.WriteAllText(File_,
                    System.Text.Json.JsonSerializer.Serialize(new StagedUpdate(version, source, zip)));
            }
            catch (Exception ex)
            {
                Log.Warn("记录待安装版本失败", ex);
            }
        }

        public static StagedUpdate? Read()
        {
            try
            {
                return System.IO.File.Exists(File_)
                    ? System.Text.Json.JsonSerializer.Deserialize<StagedUpdate>(System.IO.File.ReadAllText(File_))
                    : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static void Clear()
        {
            try
            {
                System.IO.File.Delete(File_);
            }
            catch (Exception)
            {
            }
        }
    }
}
