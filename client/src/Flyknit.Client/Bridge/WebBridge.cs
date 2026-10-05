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
    private readonly ConcurrentDictionary<string, PendingConfirm> _confirms = new();

    /// <summary>等待用户确认的数量变化（悬浮球提示用）。</summary>
    public event Action<int>? PendingConfirmsChanged;

    /// <summary>新的确认请求（宿主据此弹出系统通知或闪烁窗口）。</summary>
    public event Action<PendingConfirm>? ConfirmRequested;

    /// <summary>确认请求已结束（已回答、取消或超时），对应的系统通知应撤下。</summary>
    public event Action<PendingConfirm>? ConfirmClosed;

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
        var pending = new PendingConfirm
        {
            RequestId = Guid.NewGuid().ToString("N"),
            ConversationId = request.ConversationId,
            CallId = request.Call.Id,
            ToolName = request.Call.Name,
            Detail = ToolDetail.From(request.Call.ArgumentsJson) is { Length: > 0 } d ? d : request.Summary,
            Rationale = request.Rationale,
            Rememberable = request.Decision.Rememberable,
            Title = _host.Store.Get(request.ConversationId)?.Title ?? "",
        };
        _confirms[pending.RequestId] = pending;
        PendingConfirmsChanged?.Invoke(_confirms.Count);
        Post(new
        {
            type = "tool.confirm",
            conversationId = request.ConversationId,
            requestId = pending.RequestId,
            callId = request.Call.Id,
            reason = request.Decision.Reason,
            rationale = request.Rationale,
            rememberable = request.Decision.Rememberable,
        });
        ConfirmRequested?.Invoke(pending);

        await using var registration = ct.Register(() => pending.Completion.TrySetResult(ConfirmChoice.Reject));
        try
        {
            return await pending.Completion.Task;
        }
        finally
        {
            _confirms.TryRemove(pending.RequestId, out _);
            PendingConfirmsChanged?.Invoke(_confirms.Count);
            ConfirmClosed?.Invoke(pending);
        }
    }

    /// <summary>
    /// 在界面以外（例如系统通知的按钮）回答确认请求，并通知页面更新卡片状态。
    /// 返回 false 表示该请求已经结束。
    /// </summary>
    public bool ResolveConfirm(string requestId, ConfirmChoice choice)
    {
        if (!_confirms.TryGetValue(requestId, out var pending) || !pending.Completion.TrySetResult(choice))
        {
            return false;
        }
        Post(new
        {
            type = "tool.confirmResolved",
            conversationId = pending.ConversationId,
            callId = pending.CallId,
            choice = choice == ConfirmChoice.Reject ? "reject" : "allow",
        });
        return true;
    }

    public PendingConfirm? FindConfirm(string requestId) => _confirms.TryGetValue(requestId, out var p) ? p : null;

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

            var watch = System.Diagnostics.Stopwatch.StartNew();
            Log.Info($"页面请求 {method} ({id})");
            var result = await HandleAsync(method, p, droppedPaths);
            Send(new { kind = "response", id, ok = true, result });
            Log.Info($"页面请求 {method} ({id}) 完成，耗时 {watch.ElapsedMilliseconds} ms");
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
        string? OptStr(string name) => Str(name) is { Length: > 0 } v ? v : null;
        int? Int(string name) => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

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
                    defaultModelId = _settings.DefaultModelId,
                    defaultWorkspace = _settings.ResolveWorkspace(_settings.DefaultWorkspace),
                    defaultPermission = _settings.DefaultPermission == "readonly" ? "readonly" : "workspace",
                    workspaces = WorkspaceList(),
                    learning = _settings.EnableLearning,
                    notifications = _settings.EnableNotifications,
                    maximized = _window.IsMaximized,
                };

            case "window.toggleMaximize":
                return _window.ToggleMaximize();

            case "settings.setLearning":
                _settings.EnableLearning = Bool("enabled");
                _settings.Save();
                return null;

            case "settings.setNotifications":
                _settings.EnableNotifications = Bool("enabled");
                _settings.Save();
                return null;

            case "memory.list":
            {
                _host.Skills.Refresh();
                var items = _host.Memory.List();
                return new
                {
                    items = items.Select(i => new
                    {
                        id = i.Id,
                        kind = i.Kind.ToString().ToLowerInvariant(),
                        text = i.Text,
                        date = i.Date?.ToString("yyyy-MM-dd"),
                    }).ToList(),
                    episodes = _host.Episodes.List().Select(e => new
                    {
                        id = e.Id,
                        conversationId = e.ConversationId,
                        title = e.Title,
                        task = e.Task,
                        summary = e.Summary,
                        outcome = e.Outcome,
                        procedure = e.Procedure,
                        lessons = e.Lessons,
                        feedback = e.Feedback,
                        uses = e.Uses,
                        createdAt = e.CreatedAt.ToString("O"),
                    }).ToList(),
                    skills = _host.Skills.Skills.Where(k => k.IsLearned).Select(k => new { name = k.Name, description = k.Description, path = k.Directory }).ToList(),
                    learning = _settings.EnableLearning,
                };
            }

            case "memory.delete":
                _host.Memory.Delete(Str("id"));
                return null;

            case "memory.add":
            {
                var kind = Str("kind") switch
                {
                    "preference" => Flyknit.Core.Memory.MemoryKind.Preference,
                    "success" => Flyknit.Core.Memory.MemoryKind.Success,
                    "lesson" => Flyknit.Core.Memory.MemoryKind.Lesson,
                    _ => Flyknit.Core.Memory.MemoryKind.Fact,
                };
                return _host.Memory.Add(kind, Str("text"));
            }

            case "episodes.delete":
                _host.Episodes.Delete(Str("id"));
                return null;

            case "skills.deleteLearned":
            {
                var skill = _host.Skills.Skills.FirstOrDefault(k => k.IsLearned && k.Name == Str("name"));
                if (skill is not null && Directory.Exists(skill.Directory))
                {
                    Directory.Delete(skill.Directory, recursive: true);
                    _host.Skills.Refresh();
                }
                return null;
            }

            case "workspaces.list":
                return WorkspaceList();

            case "workspaces.add":
            {
                var folder = OptStr("path") ?? _window.PickFolder();
                if (folder is null || !Directory.Exists(folder))
                {
                    return null;
                }
                var full = AppSettings.NormalizeDir(folder);
                _settings.AddWorkspace(full);
                _settings.Save();
                return full;
            }

            case "workspaces.remove":
            {
                var path = Str("path");
                if (!string.Equals(path, AppPaths.DefaultWorkspace, StringComparison.OrdinalIgnoreCase))
                {
                    _settings.Workspaces.RemoveAll(w => w.Equals(path, StringComparison.OrdinalIgnoreCase));
                    if (string.Equals(_settings.DefaultWorkspace, path, StringComparison.OrdinalIgnoreCase))
                    {
                        _settings.DefaultWorkspace = null;
                    }
                    _settings.Save();
                }
                return WorkspaceList();
            }

            case "settings.setDefaultWorkspace":
                _settings.DefaultWorkspace = OptStr("path");
                _settings.Save();
                return null;

            case "settings.setDefaultPermission":
                // 完全权限只对单个任务生效，不保存为默认值
                _settings.DefaultPermission = Str("permission") == "readonly" ? "readonly" : "workspace";
                _settings.Save();
                return null;

            case "approvals.list":
                return _host.Approvals.List().Select(a => new
                {
                    key = a.Key,
                    tool = a.Tool,
                    display = a.Display,
                    approvedAt = a.ApprovedAt.ToString("O"),
                    lastUsedAt = a.LastUsedAt.ToString("O"),
                    uses = a.Uses,
                }).ToList();

            case "approvals.revoke":
                _host.Approvals.Revoke(Str("key"));
                return null;

            case "approvals.clear":
                _host.Approvals.Clear();
                return null;

            case "models.list":
            {
                var models = await _host.GetModelsAsync(Bool("refresh"));
                return models.Select(m => new
                {
                    id = m.Id,
                    name = m.Name,
                    model = m.Model,
                    provider = m.Provider,
                    supportsTools = m.SupportsTools,
                    supportsVision = m.SupportsVision,
                }).ToList();
            }

            case "settings.setDefaultModel":
                _settings.DefaultModelId = Int("modelId");
                _settings.Save();
                return null;

            case "skills.list":
                _host.Skills.Refresh();
                return _host.Skills.Skills.Select(s => new
                {
                    name = s.Name,
                    description = s.Description,
                    organization = s.IsOrganization,
                    enabled = s.Enabled,
                }).ToList();

            case "skills.openFolder":
                Directory.CreateDirectory(AppPaths.Skills);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.Skills}\"") { UseShellExecute = true });
                return null;

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
                var modelId = Int("modelId");
                var workspace = OptStr("workspace") ?? _settings.ResolveWorkspace(_settings.DefaultWorkspace);
                var permission = Flyknit.Core.Security.PermissionModes.Parse(OptStr("permission") ?? _settings.DefaultPermission);
                var conv = await Task.Run(() => _host.Store.Create(mode, "", modelId, workspace, permission));
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

            case "conversation.setModel":
                await Task.Run(() => _host.Store.SetModel(Str("id"), Int("modelId")));
                return null;

            case "conversation.setWorkspace":
                await Task.Run(() => _host.Store.SetWorkspace(Str("id"), OptStr("path")));
                return null;

            case "conversation.setPermission":
                await Task.Run(() => _host.Store.SetPermission(Str("id"), Flyknit.Core.Security.PermissionModes.Parse(Str("permission"))));
                return null;

            case "message.feedback":
            {
                var conversationId = Str("conversationId");
                var messageId = Str("id");
                var value = Int("value");
                await Task.Run(() => _host.Feedback(conversationId, messageId, value, _settings.ResolveUiLanguage(), this));
                return null;
            }

            case "chat.regenerate":
                _host.Regenerate(Str("conversationId"), _settings.ResolveUiLanguage(), this, this);
                return null;

            case "chat.edit":
                _host.EditAndResend(Str("conversationId"), Str("messageId"), Str("text"), OptStr("newMessageId"), _settings.ResolveUiLanguage(), this, this);
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
                    OptStr("messageId"),
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
                    "allowAlways" => ConfirmChoice.AllowAlways,
                    "allowForConversation" => ConfirmChoice.AllowAlways,
                    _ => ConfirmChoice.Reject,
                };
                if (_confirms.TryGetValue(Str("requestId"), out var pending))
                {
                    pending.Completion.TrySetResult(choice);
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

    private object WorkspaceList()
    {
        _settings.EnsureWorkspaces();
        return _settings.Workspaces.Select(w => new
        {
            path = w,
            name = WorkspaceName(w),
            exists = Directory.Exists(w),
            isDefault = string.Equals(w, AppPaths.DefaultWorkspace, StringComparison.OrdinalIgnoreCase),
        }).ToList();
    }

    private static string WorkspaceName(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(name) ? path : name;
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
    string? PickFolder();
    bool IsMaximized { get; }
    bool ToggleMaximize();
}

/// <summary>一个等待用户回答的确认请求。</summary>
public sealed class PendingConfirm
{
    public required string RequestId { get; init; }
    public required string ConversationId { get; init; }
    public required string CallId { get; init; }
    public required string ToolName { get; init; }
    public required string Detail { get; init; }
    public string Rationale { get; init; } = "";
    public bool Rememberable { get; init; }
    public string Title { get; init; } = "";
    public TaskCompletionSource<ConfirmChoice> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
