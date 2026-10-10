using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Flyknit.Updater;
using Microsoft.Win32;

namespace Flyknit.Agent;

/// <summary>
/// 替装在 Program Files 里的员工端升级。
///
/// 安装程序（Setup.exe）把员工端装在 C:\Program Files\FlyknitBuddy，员工的普通账号写不了这个目录，
/// 员工端自己的更新器换不了。于是由代理（SYSTEM）来：
///   1. 每 30 分钟问一次服务端有没有新版本；
///   2. 有就下载到只有 SYSTEM/管理员能写的目录，校验 sha256，解开；
///   3. 写 client-update.json 告诉员工端「新版本就绪」（员工端显示更新提示）；
///   4. 等员工端退出（员工点「重启升级」、关掉程序或注销）就整目录替换，失败自动回滚。
///
/// 安全上要紧的一点：替换进 Program Files 的文件只能来自代理自己下载并校验过的包。
/// 所以暂存目录放在代理自己的程序目录（Program Files\FlyknitAgent）下——员工账号在那里建不了、改不了、
/// 也挪不走任何东西；放在 ProgramData 里的话，员工可能事先占好目录，再借代理的手把自己的文件放进所有人都会运行的目录。
/// </summary>
public sealed class ClientUpdater(AgentConfig config, Func<ServerApi> api)
{
    /// <summary>员工端目录里有这个文件，才表示它归代理升级（Setup.exe 写的）。</summary>
    public const string ManagedMarker = "flyknit.managed";

    private static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(30);
    private static readonly string Staging = Path.Combine(AppContext.BaseDirectory, "client-updates");
    /// <summary>员工端读这个文件来显示「新版本就绪」。只是状态，不含任何要执行的东西。</summary>
    public static readonly string StatusFile = Path.Combine(AgentPaths.Root, "client-update.json");

    private string ClientDir => config.ClientDir.Length > 0
        ? config.ClientDir
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "FlyknitBuddy");

    private string ClientExe => Path.Combine(ClientDir, "FlyknitBuddy.exe");

    private bool Managed => File.Exists(Path.Combine(ClientDir, ManagedMarker)) && File.Exists(ClientExe);

    /// <summary>已经下好、校验过、解开的新版本，等员工端退出就换上。</summary>
    private (string Version, string Root)? _ready;

    public async Task RunAsync(CancellationToken ct)
    {
        var nextCheck = DateTime.UtcNow.AddMinutes(2);  // 开机先让别的事跑起来
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (Managed)
                {
                    if (_ready is not null)
                    {
                        TryApply();
                    }
                    else if (DateTime.UtcNow >= nextCheck)
                    {
                        nextCheck = DateTime.UtcNow + CheckEvery;
                        await CheckAsync(ct);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                AgentLog.Warn("员工端升级出错，稍后重试", ex);
                _ready = null;
            }
            // 有版本在等着装时勤看一眼：员工点了「重启升级」，几秒内就该换好
            try { await Task.Delay(TimeSpan.FromSeconds(_ready is null ? 30 : 3), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    public static string InstalledVersion(string exe)
    {
        var v = FileVersionInfo.GetVersionInfo(exe).ProductVersion ?? "";
        return v.Split('+')[0].Trim();
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        var current = InstalledVersion(ClientExe);
        var update = await api().CheckClientUpdateAsync(current, ct);
        if (!update.Available || update.Version.Length == 0)
        {
            return;
        }
        AgentLog.Info($"员工端有新版本 {update.Version}（当前 {current}），开始下载");
        WriteStatus(update.Version, update.Notes, "downloading");

        // 只暂存一个版本：整个清掉重建
        Swap.Delete(Staging);
        var dir = Directory.CreateDirectory(Path.Combine(Staging, update.Version)).FullName;
        var zip = Path.Combine(dir, "update.zip");
        await api().DownloadClientUpdateAsync(update.Version, zip, ct);
        string actual;
        await using (var stream = File.OpenRead(zip))
        {
            actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
        }
        if (!string.Equals(actual, update.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            // 对不上就当没这回事，下个周期再来。宁可停在旧版本
            AgentLog.Warn($"员工端 {update.Version} 校验不通过（期望 {update.Sha256[..Math.Min(12, update.Sha256.Length)]}，实际 {actual[..12]}）");
            Swap.Delete(dir);
            WriteStatus(update.Version, update.Notes, "failed");
            return;
        }
        var files = Path.Combine(dir, "files");
        ZipFile.ExtractToDirectory(zip, files);
        File.Delete(zip);
        var root = RootOf(files) ?? throw new InvalidOperationException("更新包里找不到 FlyknitBuddy.exe");
        _ready = (update.Version, root);
        WriteStatus(update.Version, update.Notes, "ready");
        AgentLog.Info($"员工端 {update.Version} 已就绪，等员工端退出后替换");
    }

    /// <summary>员工端都退出了才换：正在运行的 exe 和 dll 是锁着的，硬换只会换出半个程序。</summary>
    private void TryApply()
    {
        if (_ready is not { } ready || Process.GetProcessesByName("FlyknitBuddy").Length > 0)
        {
            return;
        }
        // 这两个文件是安装时写的，不在更新包里：带到新版本去，不然员工端就不归代理管、也找不到服务器了
        foreach (var keep in new[] { ManagedMarker, "flyknit.provision.json" })
        {
            var from = Path.Combine(ClientDir, keep);
            if (File.Exists(from))
            {
                File.Copy(from, Path.Combine(ready.Root, keep), overwrite: true);
            }
        }
        var backup = ClientDir + ".old";
        var result = Swap.Replace(ready.Root, ClientDir, backup);
        _ready = null;
        Swap.Delete(Path.Combine(Staging, ready.Version));
        if (!result.Ok)
        {
            AgentLog.Error($"替换员工端失败：{result.Message}" + (result.RolledBack ? "（已回滚到原来的版本）" : ""));
            WriteStatus(ready.Version, "", "failed");
            return;
        }
        try { Swap.Delete(backup); }
        catch (Exception ex) { AgentLog.Warn("清理旧版本目录失败", ex); }
        UpdateUninstallVersion(ready.Version);
        WriteStatus(ready.Version, "", "installed");
        AgentLog.Info($"员工端已升级到 {ready.Version}");
    }

    private static string? RootOf(string unpacked)
    {
        if (File.Exists(Path.Combine(unpacked, "FlyknitBuddy.exe")))
        {
            return unpacked;
        }
        return Directory.GetDirectories(unpacked).FirstOrDefault(d => File.Exists(Path.Combine(d, "FlyknitBuddy.exe")));
    }

    private static void WriteStatus(string version, string notes, string stage)
    {
        try
        {
            AgentPaths.EnsureDirectories();
            var json = JsonSerializer.Serialize(new { version, notes, stage, at = DateTimeOffset.Now });
            // 先写在自己的程序目录里再挪过去：ProgramData 里员工能建文件，不往他们可能事先放好的文件里写
            var tmp = Path.Combine(AppContext.BaseDirectory, "client-update.json.tmp");
            File.WriteAllText(tmp, json);
            File.Move(tmp, StatusFile, overwrite: true);
        }
        catch (Exception ex)
        {
            AgentLog.Warn("写员工端升级状态失败", ex);
        }
    }

    private static void UpdateUninstallVersion(string version)
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\FlyknitBuddy", writable: true);
            key?.SetValue("DisplayVersion", version);
        }
        catch (Exception ex)
        {
            AgentLog.Warn("更新「应用和功能」里的版本号失败", ex);
        }
    }
}
