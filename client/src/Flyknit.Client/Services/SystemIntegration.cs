using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using Microsoft.Win32;

namespace Flyknit.Client.Services;

/// <summary>
/// 开机自启。
///
/// 写在当前用户的 Run 项里（HKCU），不碰 HKLM——工厂电脑上普通员工没有管理员权限，
/// 写 HKLM 会直接失败；而且自启本来就该是每个用户自己的选择。
/// </summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FlyknitBuddy";

    /// <summary>注册表里实际是不是开着。以这个为准，而不是设置文件里的记录——
    /// 用户可能用别的工具（任务管理器的启动项）关掉过。</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (Exception ex)
        {
            Log.Warn("读取开机自启状态失败", ex);
            return false;
        }
    }

    /// <summary>返回是否设置成功。失败时把原因带出来显示给用户，而不是悄悄不生效。</summary>
    public static (bool Ok, string Message) Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null)
            {
                return (false, "打不开注册表启动项，可能被组策略限制了");
            }
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
                {
                    return (false, "找不到程序路径");
                }
                // --silent：开机启动时只放悬浮球，不弹主窗口
                key.SetValue(ValueName, $"\"{exe}\" --silent");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            Log.Info($"开机自启已{(enabled ? "开启" : "关闭")}");
            return (true, "");
        }
        catch (Exception ex)
        {
            Log.Warn("设置开机自启失败", ex);
            return (false, $"设置失败：{ex.Message}");
        }
    }
}

/// <summary>
/// 网络代理。
///
/// 工厂里不少电脑要走代理才能出网，而且代理地址会变。改完立即生效的做法是：
/// 换代理时重建 HttpClient（HttpClientHandler 的代理设置创建后不能改），
/// 调用方拿到的是同一个 FlyknitServerClient 实例，所以上层无感。
/// </summary>
public static class ProxyFactory
{
    public const string Direct = "direct";
    public const string System = "system";
    public const string Manual = "manual";

    public static HttpClient CreateHttpClient(AppSettings settings) => new(CreateHandler(settings));

    /// <summary>按代理设置建好的 handler。MCP 要在外面再包一层自己处理重定向，所以单独拿出来。</summary>
    public static HttpClientHandler CreateHandler(AppSettings settings)
    {
        var handler = new HttpClientHandler();
        switch ((settings.ProxyMode ?? System).ToLowerInvariant())
        {
            case Direct:
                handler.UseProxy = false;
                break;

            case Manual:
                if (Uri.TryCreate(settings.ProxyUrl, UriKind.Absolute, out var uri))
                {
                    var proxy = new WebProxy(uri);
                    if (!string.IsNullOrWhiteSpace(settings.ProxyUser))
                    {
                        proxy.Credentials = new NetworkCredential(settings.ProxyUser, settings.ProxyPassword);
                    }
                    handler.Proxy = proxy;
                    handler.UseProxy = true;
                }
                else
                {
                    Log.Warn($"代理地址无效，按直连处理：{settings.ProxyUrl}");
                    handler.UseProxy = false;
                }
                break;

            default: // system
                handler.UseProxy = true;
                handler.Proxy = WebRequest.GetSystemWebProxy();
                handler.DefaultProxyCredentials = CredentialCache.DefaultCredentials;
                break;
        }
        return handler;
    }

    /// <summary>校验用户填的代理地址，填错了当场告诉他，而不是等到下次请求失败。</summary>
    public static (bool Ok, string Message) Validate(string mode, string url)
    {
        if (!string.Equals(mode, Manual, StringComparison.OrdinalIgnoreCase))
        {
            return (true, "");
        }
        if (string.IsNullOrWhiteSpace(url))
        {
            return (false, "请填写代理地址");
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return (false, "代理地址格式不对，例如 http://10.0.0.8:8080");
        }
        if (uri.Scheme is not ("http" or "https" or "socks5"))
        {
            return (false, $"不支持 {uri.Scheme} 代理，请用 http、https 或 socks5");
        }
        return (true, "");
    }
}

/// <summary>某个目录占了多少空间，以及它所在磁盘的余量。</summary>
public sealed record StorageInfo(string Path, long Bytes, int Files, long DiskTotal, long DiskUsed, long DiskFree);

/// <summary>
/// 统计数据目录的大小。目录可能很大（技能、缓存、历史库），所以扫描有上限，
/// 宁可报一个「至少这么大」，也不要让设置界面卡住。
/// </summary>
public static class StorageUsage
{
    public const int MaxEntries = 200_000;

    public static StorageInfo Measure(string path)
    {
        long bytes = 0;
        var files = 0;
        try
        {
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    if (files >= MaxEntries)
                    {
                        break;
                    }
                    try
                    {
                        bytes += new FileInfo(file).Length;
                        files++;
                    }
                    catch (Exception)
                    {
                        // 文件被占用或没权限：跳过，不影响整体统计
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"统计目录大小失败：{path}", ex);
        }

        long total = 0, free = 0;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(root))
            {
                var drive = new DriveInfo(root);
                total = drive.TotalSize;
                free = drive.AvailableFreeSpace;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("读取磁盘信息失败", ex);
        }
        return new StorageInfo(path, bytes, files, total, total - free, free);
    }

    /// <summary>在资源管理器里打开目录；不存在就先建出来，免得用户点了没反应。</summary>
    public static void Open(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"打开目录失败：{path}", ex);
        }
    }
}
