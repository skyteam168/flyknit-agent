using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Flyknit.Client.Services;
using Flyknit.Core.Agent;
using Flyknit.Core.Storage;
using Microsoft.Web.WebView2.Core;

namespace Flyknit.Client.Bridge;

/// <summary>
/// WebView2 页面与宿主之间的消息通道，协议见 client/web/src/bridge.ts。
/// 同时负责把“需要用户确认”的请求转给页面并等待结果。
/// </summary>
public sealed class WebBridge : IHostEvents, IConfirmationHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly CoreWebView2 _web;
    private readonly Dispatcher _dispatcher;
    private readonly AgentHost _host;
    private readonly AppSettings _settings;
    private readonly IWindowActions _window;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ConfirmChoice>> _confirms = new();

    /// <summary>等待用户确认的数量变化（悬浮球提示用）。</summary>
    public event Action<int>? PendingConfirmsChanged;

    public WebBridge(CoreWebView2 web, Dispatcher dispatcher, AgentHost host, AppSettings settings, IWindowActions window)
    {
        _web = web;
        _dispatcher = dispatcher;
        _host = host;
        _settings = settings;
        _window = window;
        _web.WebMessageReceived += OnMessage;
        _host.StatusChanged += () => Post(new
        {
            type = "app.status",
            connected = _host.Connected,
            serverMessage = _host.ServerMessage,
            modelName = _host.ModelName,
        });
    }

    // ---------- 宿主 → 页面 ----------

    public void Post(object evt) => Send(new { kind = "event", @event = evt });

    private void Send(object message)
    {
        var json = JsonSerializer.Serialize(message, Json);
        _dispatcher.BeginInvoke(() =>
        {
            try
            {
                _web.PostWebMessageAsJson(json);
            }
            catch (Exception ex)
            {
                Log.Warn("PostWebMessage 失败", ex);
            }
        });
    }

    // ---------- 确认 ----------

    public async Task<ConfirmChoice> ConfirmAsync(ConfirmRequest request, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<ConfirmChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        _confirms[requestId] = tcs;
        PendingConfirmsChanged?.Invoke(_confirms.Count);
        Post(new
        {
            type = "tool.confirm",
            conversationId = request.ConversationId,
            requestId,
            callId = request.Call.Id,
            reason = request.Decision.Reason,
            rationale = request.Rationale,
        });
        _dispatcher.BeginInvoke(() => _window.RequestAttention());

        await using var registration = ct.Register(() => tcs.TrySetResult(ConfirmChoice.Reject));
        try
        {
            return await tcs.Task;
        }
        finally
        {
            _confirms.TryRemove(requestId, out _);
            PendingConfirmsChanged?.Invoke(_confirms.Count);
        }
    }

    // ---------- 页面 → 宿主 ----------

    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? id = null;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (root.GetProperty("kind").GetString() != "request")
            {
                return;
            }
            id = root.GetProperty("id").GetString();
            var method = root.GetProperty("method").GetString() ?? "";
            var p = root.TryGetProperty("params", out var pe) ? pe.Clone() : default;

            // 拖入的文件：WebView2 通过 AdditionalObjects 提供真实路径
            var droppedPaths = new List<string>();
            if (e.AdditionalObjects is { } objects)
            {
                foreach (var obj in objects)
                {
                    if (obj is CoreWebView2File file)
                    {
                        droppedPaths.Add(file.Path);
                    }
                }
            }

            var result = await HandleAsync(method, p, droppedPaths);
            Send(new { kind = "response", id, ok = true, result });
        }
        catch (Exception ex)
        {
            Log.Warn("处理页面请求失败", ex);
            if (id is not null)
            {
                Send(new { kind = "response", id, ok = false, error = ex.Message });
            }
        }
    }

    private async Task<object?> HandleAsync(string method, JsonElement p, List<string> droppedPaths)
    {
        string Str(string name) => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        bool Bool(string name) => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        switch (method)
        {
            case "app.init":
                return new
                {
                    version = typeof(WebBridge).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
                    uiLanguage = _settings.ResolveUiLanguage(),
                    theme = _settings.Theme,
                    userName = Environment.UserName,
                    machineName = Environment.MachineName,
                    connected = _host.Connected,
                    serverMessage = _host.ServerMessage,
                    modelName = _host.ModelName,
                };

            case "settings.setLanguage":
                _settings.UiLanguage = Str("language");
                _settings.Save();
                _window.LanguageChanged(_settings.UiLanguage);
                return null;

            case "settings.setTheme":
                _settings.Theme = Str("theme");
                _settings.Save();
                return null;

            case "settings.openMemoryFolder":
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.Memory}\"") { UseShellExecute = true });
                return null;

            case "window.hide":
                _window.HideMain();
                return null;

            case "window.minimize":
                _window.MinimizeMain();
                return null;

            case "window.toggleTopmost":
                return _window.ToggleTopmost();

            case "conversations.list":
            {
                var query = Str("query");
                var trash = Bool("trash");
                var list = await Task.Run(() => _host.Store.List(query, trash));
                return list.Select(ConversationDto.From).ToList();
            }

            case "conversation.create":
            {
                var mode = ConversationStore.TextToMode(Str("mode"));
                var conv = await Task.Run(() => _host.Store.Create(mode));
                return ConversationDto.From(conv);
            }

            case "conversation.rename":
                await Task.Run(() => _host.Store.Rename(Str("id"), Str("title")));
                return null;

            case "conversation.delete":
                _host.Stop(Str("id"));
                await Task.Run(() => _host.Store.Delete(Str("id")));
                return null;

            case "conversation.restore":
                await Task.Run(() => _host.Store.Restore(Str("id")));
                return null;

            case "conversation.purge":
                await Task.Run(() => _host.Store.Purge(Str("id")));
                return null;

            case "conversation.pin":
                await Task.Run(() => _host.Store.SetPinned(Str("id"), Bool("pinned")));
                return null;

            case "conversation.setMode":
                await Task.Run(() => _host.Store.SetMode(Str("id"), ConversationStore.TextToMode(Str("mode"))));
                return null;

            case "conversation.setTranslate":
                await Task.Run(() => _host.Store.SetTranslateLanguages(Str("id"), Str("from"), Str("to")));
                return null;

            case "messages.load":
            {
                var messages = await Task.Run(() => _host.Store.GetMessages(Str("id")));
                return messages.Select(MessageDto.From).ToList();
            }

            case "chat.send":
            {
                var attachments = p.TryGetProperty("attachments", out var a) && a.ValueKind == JsonValueKind.Array
                    ? a.Deserialize<List<AttachmentDto>>(Json) ?? new()
                    : new();
                _host.Send(
                    Str("conversationId"),
                    Str("text"),
                    attachments.Where(x => File.Exists(x.LocalPath)).Select(x => x.ToModel()).ToList(),
                    _settings.ResolveUiLanguage(),
                    this,
                    this);
                return null;
            }

            case "chat.stop":
                _host.Stop(Str("conversationId"));
                return null;

            case "tool.confirm":
            {
                var choice = Str("choice") switch
                {
                    "allowOnce" => ConfirmChoice.AllowOnce,
                    "allowForConversation" => ConfirmChoice.AllowForConversation,
                    _ => ConfirmChoice.Reject,
                };
                if (_confirms.TryGetValue(Str("requestId"), out var tcs))
                {
                    tcs.TrySetResult(choice);
                }
                return null;
            }

            case "files.pick":
                return _window.PickFiles().Select(AttachmentDto.FromPath).ToList();

            case "files.dropped":
                return droppedPaths.Where(File.Exists).Select(AttachmentDto.FromPath).ToList();

            case "files.saveBlob":
            {
                Directory.CreateDirectory(Path.Combine(AppPaths.Temp, "paste"));
                var name = string.Join("_", Str("fileName").Split(Path.GetInvalidFileNameChars()));
                var path = Path.Combine(AppPaths.Temp, "paste", string.IsNullOrWhiteSpace(name) ? $"paste-{Guid.NewGuid():N}.png" : name);
                await File.WriteAllBytesAsync(path, Convert.FromBase64String(Str("base64")));
                return AttachmentDto.FromPath(path);
            }

            case "files.open":
            {
                var path = Str("path");
                if (File.Exists(path))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                }
                else if (Directory.Exists(path))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
                }
                return null;
            }

            default:
                throw new NotSupportedException($"未知方法：{method}");
        }
    }
}

/// <summary>主窗口提供给桥接层的操作。</summary>
public interface IWindowActions
{
    void HideMain();
    void MinimizeMain();
    bool ToggleTopmost();
    void RequestAttention();
    void LanguageChanged(string language);
    IReadOnlyList<string> PickFiles();
}
