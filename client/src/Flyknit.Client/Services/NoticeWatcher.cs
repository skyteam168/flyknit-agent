using System;
using System.IO;
using System.Text.Json;
using Flyknit.Client.Bridge;

namespace Flyknit.Client.Services;

/// <summary>
/// 监听运维代理投递的通知。代理以 SYSTEM 运行、弹不出当前员工桌面的通知，于是把通知
/// 写成 JSON 文件放进 %ProgramData%\Flyknit\notices；本监听器读到后弹右下角提示，弹完删文件。
/// </summary>
public sealed class NoticeWatcher : IDisposable
{
    private static readonly string NoticesDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Flyknit", "notices");

    private readonly NotificationService _notifications;
    private readonly Action<Action> _toUi;
    private FileSystemWatcher? _watcher;

    /// <param name="toUi">把回调切回 UI 线程执行（通常是 Dispatcher.BeginInvoke）。</param>
    public NoticeWatcher(NotificationService notifications, Action<Action> toUi)
    {
        _notifications = notifications;
        _toUi = toUi;
    }

    public void Start()
    {
        try
        {
            Directory.CreateDirectory(NoticesDir);
            // 启动时先处理积压：代理可能在本客户端没运行时已经写过通知
            foreach (var file in Directory.EnumerateFiles(NoticesDir, "*.json"))
            {
                Handle(file);
            }
            _watcher = new FileSystemWatcher(NoticesDir, "*.json")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };
            _watcher.Created += (_, e) => _toUi(() => Handle(e.FullPath));
        }
        catch (Exception ex)
        {
            Log.Warn("启动运维通知监听失败", ex);
        }
    }

    private void Handle(string path)
    {
        try
        {
            // 文件可能还没写完，稍等一下；代理是先写 .tmp 再改名，正常不会读到半截
            string json;
            try { json = File.ReadAllText(path); }
            catch (IOException) { System.Threading.Thread.Sleep(200); json = File.ReadAllText(path); }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var title = Get(root, "title", "FlyknitBuddy");
            var body = Get(root, "body", "");

            // 太旧的通知（员工长时间没开机）就不弹了，免得登录后被一堆过期提示淹没
            if (root.TryGetProperty("created_at", out var ts)
                && DateTimeOffset.TryParse(ts.GetString(), out var when)
                && DateTimeOffset.Now - when > TimeSpan.FromHours(6))
            {
                SafeDelete(path);
                return;
            }

            if (body.Length > 0)
            {
                _notifications.ShowNotice(title, body);
            }
            SafeDelete(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"处理运维通知失败：{path}", ex);
            SafeDelete(path);
        }
    }

    private static string Get(JsonElement root, string name, string fallback) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    private static void SafeDelete(string path)
    {
        try { File.Delete(path); } catch (Exception) { }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
    }
}
