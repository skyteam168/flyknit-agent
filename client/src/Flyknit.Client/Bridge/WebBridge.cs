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
using Flyknit.Core.Library;
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

    /// <summary>最近一次网络检测（设置 → 网络 再打开时直接显示，不用重测）。</summary>
    private (Flyknit.Core.Diagnostics.NetworkReport Report, string Path)? _lastNetwork;
    private readonly ConcurrentDictionary<string, PendingConfirm> _confirms = new();

    /// <summary>界面当前打开的任务。用户在看别的任务时，这个任务的通知照常弹。</summary>
    public string? ActiveConversationId { get; private set; }

    /// <summary>按文件类型分派的预览实现。加一种格式只要注册一个 Provider。</summary>
    private readonly Flyknit.Core.Preview.PreviewRegistry _preview = Flyknit.Core.Preview.PreviewRegistry.CreateDefault();

    /// <summary>语音输入。录音在本机，转写在服务端。</summary>
    private readonly SpeechService _speech;

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
        _host.AttachUi(this, this);
        _host.Scheduler.Changed += () => Post(new { type = "schedules.changed" });
        _host.Library.Changed += () => Post(new { type = "library.changed" });
        _host.Updater.Changed += state => Post(UpdateEvent(state));
        _speech = new SpeechService(_host.Server);
        // 录音时把响度和时长推给界面画动效。回调在音频线程上，Post 内部会切回 UI 线程
        _speech.Tick += (level, elapsed) => Post(new
        {
            type = "speech.tick",
            level,
            elapsedMs = (long)elapsed.TotalMilliseconds,
            maxMs = (long)Flyknit.Core.Speech.RecordingLimits.MaxDuration.TotalMilliseconds,
        });
        _speech.AutoStopped += () => Post(new { type = "speech.autoStop" });
        _host.StatusChanged += () => Post(new
        {
            type = "app.status",
            connected = _host.Connected,
            serverMessage = _host.ServerMessage,
            modelName = _host.ModelName,
            department = _host.Department,
            owner = _host.Owner,
            // IT 在安全中心改了配置，客户端长轮询一两秒内就拿到；界面上跟着变的几项也要一起推过去，
            // 不然要重启客户端才看得到（比如关了工作区隔离，「完全权限」还是锁着）
            sandboxed = _host.Security.On(Flyknit.Core.Security.SecuritySettings.Sandbox),
            notifications = _settings.EnableNotifications,
            notificationSound = _host.NotificationSoundChoice(),
            notificationsLocked = _host.Security.Get(Flyknit.Core.Security.SecuritySettings.Notifications)?.Locked ?? false,
            soundLocked = _host.Security.Get(Flyknit.Core.Security.SecuritySettings.NotificationSound)?.Locked ?? false,
            keepAwakeAllowed = _host.KeepAwakeAllowed,
            keepScreenAllowed = _host.KeepScreenOnAllowed,
        });
    }

    /// <summary>安全中心要显示的全部条目。锁住的也要显示——让用户看见自己被什么规则管着。</summary>
    private static object UpdateEvent(Services.UpdateState state) => new
    {
        type = "update.state",
        stage = state.Stage.ToString().ToLowerInvariant(),
        version = state.Version,
        notes = state.Notes,
        progress = state.Progress,
        message = state.Message,
    };

    private static object UpdateInfo(Services.UpdateState state) => new
    {
        stage = state.Stage.ToString().ToLowerInvariant(),
        version = state.Version,
        notes = state.Notes,
        progress = state.Progress,
        message = state.Message,
    };

    private object SecurityList() => _host.Security.Items
        .Select(kv => new
        {
            key = kv.Key,
            value = kv.Value.Value,
            locked = kv.Value.Locked,
            kind = kv.Value.Kind,
            title = kv.Value.Title,
            risk = kv.Value.Risk,
            min = kv.Value.Min,
            max = kv.Value.Max,
        })
        .ToList();

    /// <summary>改一项安全设置：锁住的拒绝，改成功了立即生效并写一条配置变更。返回 null 表示成功，否则是原因。</summary>
    private string? SetSecurityValue(string key, object value)
    {
        var before = _host.Security.Get(key) is { } old ? SecurityValueText(old) : "";
        if (!_host.Security.TrySet(key, value, out var why))
        {
            return why;
        }
        _settings.SecurityChoices = _host.Security.LocalChanges();
        _settings.Save();
        _host.RefreshSecurity();
        if (_host.Security.Get(key) is { } changed && SecurityValueText(changed) != before)
        {
            _host.RecordSettingChange(key, changed.Title, before, SecurityValueText(changed));
        }
        return null;
    }

    private static string SecurityValueText(Flyknit.Core.Security.SecurityItem item) =>
        item.Kind == "int" ? item.AsInt(0).ToString() : item.AsBool() ? "开启" : "关闭";

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
            RuleDisplay = request.Decision.Rule?.Display ?? "",
            Effect = request.Decision.Effect.ToString().ToLowerInvariant(),
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
            ruleDisplay = request.Decision.Rule?.Display ?? "",
            effect = request.Decision.Effect.ToString().ToLowerInvariant(),
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
        // 只认界面自己发来的请求。网页预览放在沙箱 iframe 里，不该有机会调宿主——
        // WebView2 本来就只把顶层页面的消息送到这里，这一行是再保险一层
        if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var source)
            || !source.Host.Equals(Flyknit.Client.Windows.MainWindow.VirtualHost, StringComparison.OrdinalIgnoreCase))
        {
            Log.Warn($"忽略来源不明的界面消息：{e.Source}");
            return;
        }
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
        List<string> Ids() => p.ValueKind == JsonValueKind.Object && p.TryGetProperty("ids", out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : new List<string>();
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
                    serverUrl = _settings.ServerUrl,
                    department = _host.Department,
                    owner = _host.Owner,
                    connected = _host.Connected,
                    serverMessage = _host.ServerMessage,
                    modelName = _host.ModelName,
                    defaultModelId = _settings.DefaultModelId,
                    defaultWorkspace = _settings.ResolveWorkspace(_settings.DefaultWorkspace),
                    defaultPermission = _settings.DefaultPermission == "readonly" ? "readonly" : "workspace",
                    workspaces = WorkspaceList(),
                    learning = _settings.EnableLearning,
                    maxSteps = _settings.ResolveAgentMaxSteps(),
                    notifications = _settings.EnableNotifications,
                    notificationSound = _host.NotificationSoundChoice(),
                    notificationsLocked = _host.Security.Get(Flyknit.Core.Security.SecuritySettings.Notifications)?.Locked ?? false,
                    soundLocked = _host.Security.Get(Flyknit.Core.Security.SecuritySettings.NotificationSound)?.Locked ?? false,
                    fontScale = _settings.FontScale,
                    autoStart = AutoStart.IsEnabled(),   // 以注册表为准，用户可能在任务管理器里关过
                    keepAwake = Flyknit.Core.Settings.KeepAwake.Normalize(_settings.KeepAwakeMode),
                    keepAwakeAllowed = _host.KeepAwakeAllowed,
                    keepScreenAllowed = _host.KeepScreenOnAllowed,
                    proxyMode = _settings.ProxyMode,
                    proxyUrl = _settings.ProxyUrl,
                    proxyUser = _settings.ProxyUser,
                    dataDir = AppPaths.Root,
                    shortcuts = ShortcutList(),
                    maximized = _window.IsMaximized,
                    micAvailable = SpeechService.Available(), // 没有麦克风就不显示语音按钮
                    // 工作区隔离开着时「完全权限」实际按「工作区内修改」算。界面据此把那一项
                    // 置灰并说明原因——选了却悄悄降级，比不给选更糟
                    sandboxed = _host.Security.On(Flyknit.Core.Security.SecuritySettings.Sandbox),
                };

            case "update.state":
                return UpdateInfo(_host.Updater.State);

            case "update.apply":
            {
                // 装不上去就别让程序退出——退了用户只会看到助手消失了
                if (!_host.Updater.ApplyNow())
                {
                    return new { ok = false, message = "更新没能启动，请联系 IT" };
                }
                _host.RequestExit();
                return new { ok = true, message = "" };
            }

            case "update.check":
            {
                // 设置里「检查更新」：等服务器回话（最多 20 秒），告诉用户结果；下载在后台继续
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20));
                var result = await _host.Updater.CheckNowAsync(cts.Token);
                return new
                {
                    outcome = result.Outcome switch
                    {
                        UpdateCheckOutcome.UpToDate => "upToDate",
                        UpdateCheckOutcome.Downloading => "downloading",
                        UpdateCheckOutcome.Ready => "ready",
                        UpdateCheckOutcome.NeedsIt => "needsIt",
                        _ => "failed",
                    },
                    version = result.Version,
                    current = typeof(WebBridge).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
                    message = result.Message,
                };
            }

            case "security.settings":
                return SecurityList();

            case "security.setItem":
            {
                // 锁住的项在这里就拒掉。界面上置灰只是提示，不能只靠界面拦。
                var key = Str("key");
                object value = p.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number
                    ? v.GetInt32()
                    : Bool("value");
                var why = SetSecurityValue(key, value);
                return new { ok = why is null, message = why ?? "", items = SecurityList() };
            }

            case "security.openBackups":
                StorageUsage.Open(AppPaths.Backups);
                return null;

            case "speech.start":
                try
                {
                    _speech.Start();
                    return new { ok = true };
                }
                catch (Services.Audio.AudioDeviceException ex)
                {
                    // 麦克风被占用、被禁用等等，消息本身就是中文，直接给界面显示
                    return new { ok = false, reason = "device", message = ex.Message };
                }

            case "speech.stop":
            {
                var outcome = await _speech.StopAndTranscribeAsync(Str("language"));
                return new { ok = outcome.Ok, text = outcome.Text, reason = outcome.Reason, message = outcome.Message };
            }

            case "speech.cancel":
                _speech.Cancel();
                return null;

            case "window.toggleMaximize":
                return _window.ToggleMaximize();

            case "window.startResize":
                _window.StartResize(Str("direction"));
                return null;

            case "settings.setMaxSteps":
            {
                var n = Int("value") ?? 0;
                if (!AppSettings.AgentMaxStepsChoices.Contains(n))
                {
                    throw new ArgumentException("不支持的步数上限");
                }
                _settings.AgentMaxSteps = n;
                _settings.Save();
                return n;
            }

            case "settings.setLearning":
                _settings.EnableLearning = Bool("enabled");
                _settings.Save();
                return null;

            // 完成通知和提示音只有一份设置：服务端下发了对应的安全项就改那一项（IT 锁了就改不了、改了进审计），
            // 没下发（连不上服务端的新机器）才退回本机设置
            case "settings.setNotifications":
            {
                var enabled = Bool("enabled");
                string? why = null;
                if (_host.Security.Get(Flyknit.Core.Security.SecuritySettings.Notifications) is not null)
                {
                    why = SetSecurityValue(Flyknit.Core.Security.SecuritySettings.Notifications, enabled);
                }
                else
                {
                    _settings.EnableNotifications = enabled;
                    _settings.Save();
                }
                return new { ok = why is null, message = why ?? "", enabled = _settings.EnableNotifications };
            }

            case "settings.setNotificationSound":
            {
                var sound = Str("sound") is "soft" or "alert" ? Str("sound") : "none";
                if (_host.Security.Get(Flyknit.Core.Security.SecuritySettings.NotificationSound) is not null)
                {
                    var why = SetSecurityValue(Flyknit.Core.Security.SecuritySettings.NotificationSound, sound != "none");
                    if (why is not null)
                    {
                        return new { ok = false, message = why, sound = _host.NotificationSoundChoice() };
                    }
                    if (sound != "none")
                    {
                        _settings.NotificationSound = sound; // 关掉时保留上次选的音色，再打开还是它
                    }
                }
                else
                {
                    _settings.NotificationSound = sound;
                }
                _settings.Save();
                return new { ok = true, message = "", sound = _host.NotificationSoundChoice() };
            }

            case "settings.previewSound":
                NotificationService.Preview(Str("sound"));
                return null;

            case "settings.setFontScale":
            {
                // 只接受预设的几档，避免存进一个让界面没法看的数
                var scale = p.TryGetProperty("scale", out var sv) && sv.ValueKind == JsonValueKind.Number ? sv.GetDouble() : 1.0;
                _settings.FontScale = Flyknit.Core.Settings.FontScales.Nearest(scale);
                _settings.Save();
                return _settings.FontScale;
            }

            case "settings.setAutoStart":
            {
                var (ok, message) = AutoStart.Set(Bool("enabled"));
                if (ok)
                {
                    _settings.AutoStart = Bool("enabled");
                    _settings.Save();
                }
                return new { ok, message, enabled = AutoStart.IsEnabled() };
            }

            case "settings.setKeepAwake":
            {
                // 界面上锁住的选项置灰了，这里再拦一次：不能只靠界面
                var mode = Flyknit.Core.Settings.KeepAwake.Normalize(Str("mode"));
                string? why = mode == Flyknit.Core.Settings.KeepAwake.Off ? null
                    : !_host.KeepAwakeAllowed ? "锁屏运行由 IT 统一配置，本机只能选“关闭”"
                    : mode == Flyknit.Core.Settings.KeepAwake.Screen && !_host.KeepScreenOnAllowed ? "“保持屏幕常亮”已被 IT 关闭"
                    : null;
                if (why is null)
                {
                    _settings.KeepAwakeMode = mode;
                    _settings.Save();
                    _host.RefreshKeepAwake();
                }
                return new { ok = why is null, message = why ?? "", mode = Flyknit.Core.Settings.KeepAwake.Normalize(_settings.KeepAwakeMode) };
            }

            case "persona.get":
                return PersonaInfo();

            case "persona.set":
            {
                // 每次只传改了的那几项
                if (OptStr("preset") is { } preset)
                {
                    preset = Flyknit.Core.Settings.Personas.Normalize(preset);
                    if (Flyknit.Core.Settings.Personas.Effective(preset, _host.PlayfulPersonasAllowed, _host.CustomPersonaAllowed) != preset)
                    {
                        return new { ok = false, message = "这个语气已被 IT 关闭", persona = PersonaInfo() };
                    }
                    _settings.Persona = preset;
                }
                if (p.TryGetProperty("custom", out var custom) && custom.ValueKind == JsonValueKind.String)
                {
                    if (!_host.CustomPersonaAllowed)
                    {
                        return new { ok = false, message = "自定义语气已被 IT 关闭", persona = PersonaInfo() };
                    }
                    var text = custom.GetString()!.Trim();
                    _host.Memory.Write(Flyknit.Core.Memory.MemoryStore.SoulFile, "# 性格与语气\n\n" + (text.Length > 2000 ? text[..2000] : text) + "\n");
                }
                if (p.TryGetProperty("callName", out var call) && call.ValueKind == JsonValueKind.String)
                {
                    _settings.UserCallName = Flyknit.Core.Settings.Personas.CleanName(call.GetString());
                }
                if (p.TryGetProperty("assistantName", out var name) && name.ValueKind == JsonValueKind.String)
                {
                    if (!_host.CustomPersonaAllowed && name.GetString()!.Trim().Length > 0)
                    {
                        return new { ok = false, message = "修改 AI 的名字已被 IT 关闭", persona = PersonaInfo() };
                    }
                    _settings.AssistantName = Flyknit.Core.Settings.Personas.CleanName(name.GetString());
                }
                if (p.TryGetProperty("about", out var about) && about.ValueKind == JsonValueKind.Object)
                {
                    string F(string key) => about.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";
                    var me = new Flyknit.Core.Settings.AboutMe(F("department"), F("position"), F("language"), F("systems"), F("folders"), F("other"));
                    _host.Memory.Write(Flyknit.Core.Memory.MemoryStore.RoleFile, me.Render());
                }
                _settings.Save();
                return new { ok = true, message = "", persona = PersonaInfo() };
            }

            case "settings.setProxy":
            {
                var mode = Str("mode");
                var url = Str("url");
                var (valid, why) = ProxyFactory.Validate(mode, url);
                if (!valid)
                {
                    return new { ok = false, message = why };
                }
                _settings.ProxyMode = mode is "direct" or "manual" ? mode : "system";
                _settings.ProxyUrl = url;
                _settings.ProxyUser = Str("user");
                if (p.TryGetProperty("password", out var pw) && pw.ValueKind == JsonValueKind.String)
                {
                    _settings.ProxyPassword = pw.GetString() ?? "";
                }
                _settings.Save();
                _host.ApplyProxy();   // 立即生效，不用重启
                return new { ok = true, message = "" };
            }

            case "settings.testProxy":
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var (ok, message) = await _host.TestConnectionAsync(cts.Token);
                return new { ok, message };
            }

            case "legal.get":
            {
                // 「意见反馈」里的隐私保护声明链接：读服务器上 IT 维护的那份（和登录界面同一份）
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var json = await _host.Server.GetLegalAsync(cts.Token);
                var kind = Str("kind");
                var doc = System.Text.Json.Nodes.JsonNode.Parse(json)?["docs"]?.AsArray()
                    .FirstOrDefault(d => (string?)d?["kind"] == kind);
                return doc is null ? null : new { title = (string?)doc["title"] ?? "", content = (string?)doc["content"] ?? "" };
            }

            case "feedback.submit":
            {
                var text = Str("content").Trim();
                if (text.Length > Flyknit.Core.Diagnostics.FeedbackPackage.MaxContent)
                {
                    return new { ok = false, message = "too_long" };
                }
                var images = new List<Flyknit.Core.Diagnostics.FeedbackImage>();
                if (p.TryGetProperty("images", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in list.EnumerateArray().Take(Flyknit.Core.Diagnostics.FeedbackPackage.MaxImages))
                    {
                        var image = item.ValueKind == JsonValueKind.String ? Flyknit.Core.Diagnostics.FeedbackPackage.DecodeImage(item.GetString()!) : null;
                        if (image is null)
                        {
                            return new { ok = false, message = "bad_image" };
                        }
                        images.Add(image);
                    }
                }
                if (text.Length == 0 && images.Count == 0)
                {
                    return new { ok = false, message = "empty" };
                }
                byte[]? logs = null;
                if (Bool("logs"))
                {
                    Log.Info("员工提交反馈，附带日志");
                    logs = await Task.Run(() => Flyknit.Core.Diagnostics.FeedbackPackage.BuildLogs(AppPaths.Logs, DateTime.Now, new Dictionary<string, string>
                    {
                        ["version"] = typeof(WebBridge).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
                        ["machine"] = Environment.MachineName,
                        ["user"] = $"{Environment.UserDomainName}\\{Environment.UserName}",
                        ["os"] = Environment.OSVersion.VersionString,
                        ["arch"] = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
                        ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                        ["webview2"] = SafeWebViewVersion(),
                        ["language"] = _settings.ResolveUiLanguage(),
                        ["culture"] = System.Globalization.CultureInfo.CurrentCulture.Name,
                        ["time"] = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"),
                        ["memoryMB"] = (GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024).ToString(),
                        ["processMB"] = (Environment.WorkingSet / 1024 / 1024).ToString(),
                    }));
                }
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    await _host.Server.SubmitFeedbackAsync(text, images, logs, cts.Token);
                    return new { ok = true, message = "" };
                }
                catch (Exception ex)
                {
                    Log.Warn("提交反馈失败", ex);
                    return new { ok = false, message = ex is OperationCanceledException ? "timeout" : ex.Message };
                }
            }

            case "account.logout":
                // 界面已经确认过。服务端作废令牌，App 清掉本机令牌后重启到登录窗口
                await _host.LogoutAsync();
                return null;

            case "network.last":
                return _lastNetwork is { } last ? NetworkInfo(last.Report, last.Path) : null;

            case "network.diagnose":
            {
                var server = _host.Server;
                var proxy = Uri.TryCreate(server.ServerUrl, UriKind.Absolute, out var target)
                    ? ProxyFactory.Describe(_settings, target)
                    : new Flyknit.Core.Diagnostics.ProxyInfo(_settings.ProxyMode ?? "", "");
                var probe = new Flyknit.Core.Diagnostics.SystemNetworkProbe(server.HealthStatusAsync, server.AuthStatusAsync);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var report = await Task.Run(() => Flyknit.Core.Diagnostics.NetworkDiagnostics.RunAsync(server.ServerUrl, proxy, probe, cts.Token));
                var dir = Path.Combine(AppPaths.Logs, "network");
                var file = Path.Combine(dir, $"network-diagnostics-{report.At:yyyyMMdd-HHmmss-fff}.txt");
                try
                {
                    Directory.CreateDirectory(dir);
                    var text = Flyknit.Core.Diagnostics.NetworkDiagnostics.FormatReport(report, new Dictionary<string, string>
                    {
                        ["version"] = typeof(WebBridge).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
                        ["machine"] = Environment.MachineName,
                        ["user"] = $"{Environment.UserDomainName}\\{Environment.UserName}",
                        ["os"] = Environment.OSVersion.VersionString,
                    });
                    await File.WriteAllTextAsync(file, text);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warn("写网络检测报告失败", ex);
                    file = "";
                }
                Log.Info($"网络检测：{report.Overall}，{string.Join(" ", report.Checks.Select(c => $"{c.Id}={c.Status}"))}");
                _lastNetwork = (report, file);
                return NetworkInfo(report, file);
            }

            case "network.openReports":
            {
                var dir = Path.Combine(AppPaths.Logs, "network");
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
                return null;
            }

            case "network.revealReport":
                if (_lastNetwork is { Path.Length: > 0 } shown && File.Exists(shown.Path))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{shown.Path}\"") { UseShellExecute = true });
                }
                return null;

            case "network.exportReport":
            {
                if (_lastNetwork is not { Path.Length: > 0 } latest || !File.Exists(latest.Path))
                {
                    return new { ok = false, path = "" };
                }
                var dest = _window.PickSaveFile(Path.GetFileName(latest.Path), "文本文件 (*.txt)|*.txt");
                if (dest is null)
                {
                    return new { ok = false, path = "" };
                }
                File.Copy(latest.Path, dest, overwrite: true);
                return new { ok = true, path = dest };
            }

            case "storage.info":
            {
                var data = await Task.Run(() => StorageUsage.Measure(AppPaths.Root));
                var workspace = _settings.ResolveWorkspace(_settings.DefaultWorkspace);
                return new
                {
                    dataDir = data.Path,
                    bytes = data.Bytes,
                    files = data.Files,
                    diskTotal = data.DiskTotal,
                    diskUsed = data.DiskUsed,
                    diskFree = data.DiskFree,
                    workspace,
                };
            }

            case "storage.openDataFolder":
                StorageUsage.Open(AppPaths.Root);
                return null;

            case "shortcuts.list":
                return ShortcutList();

            case "shortcuts.set":
            {
                var id = Str("id");
                var binding = Str("binding");
                var command = Flyknit.Core.Settings.Shortcuts.Find(id);
                if (command is null || command.Fixed)
                {
                    return new { ok = false, reason = "fixed" };
                }
                if (binding.Length > 0)
                {
                    var (valid, reason) = Flyknit.Core.Settings.Shortcuts.Validate(binding);
                    if (!valid)
                    {
                        return new { ok = false, reason };
                    }
                    var resolved = Flyknit.Core.Settings.Shortcuts.Resolve(_settings.Shortcuts);
                    if (Flyknit.Core.Settings.Shortcuts.Conflict(id, binding, resolved) is { } clash)
                    {
                        return new { ok = false, reason = "conflict", conflictsWith = clash };
                    }
                }
                _settings.Shortcuts[id] = Flyknit.Core.Settings.Shortcuts.Normalize(binding);
                _settings.Save();
                _window.RefreshHotkey();
                return new { ok = true, reason = "", shortcuts = ShortcutList() };
            }

            case "shortcuts.reset":
            {
                var id = OptStr("id");
                if (id is { Length: > 0 })
                {
                    _settings.Shortcuts.Remove(id);
                }
                else
                {
                    _settings.Shortcuts.Clear();
                }
                _settings.Save();
                _window.RefreshHotkey();
                return ShortcutList();
            }

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
                        lastSeen = i.LastSeen?.ToString("yyyy-MM-dd"),
                        proofCount = i.ProofCount,
                        uses = i.Uses,
                        feedback = i.Feedback,
                        source = i.Source,
                        history = i.History,
                        pinned = i.Pinned,
                        origin = i.Origin,
                        evidence = i.Evidence,
                        slot = i.Slot,
                        slotLabel = i.Slot is { } s ? Flyknit.Core.Memory.MemorySlots.LabelOf(s) : null,
                        validUntil = i.ValidUntil?.ToString("yyyy-MM-dd"),
                        expired = i.IsExpired(DateOnly.FromDateTime(DateTime.Now)),
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
                    skills = _host.Skills.Skills.Where(k => k.IsLearned).Select(k =>
                    {
                        var version = Flyknit.Core.Skills.LearnedSkillStatus.VersionOf(k);
                        var stats = _host.LearnedSkillLedger.Stats(k.Name, version);
                        return new
                        {
                            name = k.Name,
                            description = k.Description,
                            path = k.Directory,
                            status = k.LearnedStatus,
                            version,
                            uses = stats.Uses,
                            successes = stats.Successes,
                            failures = stats.Failures,
                            lastUsed = _host.LearnedSkillLedger.LastUsed(k.Name)?.ToString("O"),
                        };
                    }).ToList(),
                    learning = _settings.EnableLearning,
                };
            }

            case "memory.delete":
            {
                // 遗忘：记忆里的原文清掉，历史任务里同样的教训也删掉
                var forgotten = _host.Memory.Forget(Str("id"));
                if (forgotten is not null)
                {
                    _host.Episodes.Forget(forgotten);
                }
                return null;
            }

            case "memory.pin":
                return _host.Memory.Pin(Str("id"), Bool("pinned"));

            case "memory.renew":
                // 用户确认一条过了有效期的记忆仍然对：从今天起重新算有效期
                return _host.Memory.Renew(Str("id"));

            case "memory.add":
            {
                var kind = Str("kind") switch
                {
                    "preference" => Flyknit.Core.Memory.MemoryKind.Preference,
                    "success" => Flyknit.Core.Memory.MemoryKind.Success,
                    "lesson" => Flyknit.Core.Memory.MemoryKind.Lesson,
                    _ => Flyknit.Core.Memory.MemoryKind.Fact,
                };
                var saved = _host.Memory.Save(kind, Str("text"), "user", origin: Flyknit.Core.Memory.MemoryOrigin.UserSaid, pinned: Bool("pinned"));
                return new { outcome = saved.Outcome.ToString().ToLowerInvariant(), reason = saved.Reason };
            }

            case "memory.consolidate":
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(8));
                var report = await _host.ConsolidateMemoryAsync(cts.Token);
                return new { groups = report.Groups, merged = report.ItemsMerged, errors = report.Errors };
            }

            case "episodes.delete":
                _host.Episodes.Delete(Str("id"));
                return null;

            case "memory.metrics":
            {
                var m = _host.Memory.Metrics();
                var (total, reused, recent) = _host.Episodes.Stats();
                var learned = _host.Skills.Skills.Where(k => k.IsLearned).ToList();
                return new
                {
                    days = m.Days,
                    active = m.Active,
                    pinned = m.Pinned,
                    inferred = m.Inferred,
                    answers = m.Answers,
                    answersWithMemory = m.AnswersWithMemory,
                    avgItems = m.AvgItemsInjected,
                    avgTokens = m.AvgTokensInjected,
                    maxTokens = m.MaxTokensInjected,
                    usedRecently = m.UsedRecently,
                    usedShare = m.UsedShare,
                    neverUsed = m.NeverUsed,
                    liked = m.Liked,
                    disliked = m.Disliked,
                    fresh30 = m.Fresh30,
                    fresh90 = m.Fresh90,
                    stale = m.Stale,
                    medianAgeDays = m.MedianAgeDays,
                    expired = m.Expired,
                    semantic = _host.Memory.Semantic is { Working: true },
                    runs = _host.Store.RunStats(m.Days) is var r
                        ? new
                        {
                            total = r.Runs,
                            completed = r.Completed,
                            paused = r.Paused,
                            cancelled = r.Cancelled,
                            errors = r.Errors,
                            completionRate = r.CompletionRate,
                            avgSteps = r.AvgSteps,
                            disliked = r.Disliked,
                            liked = r.Liked,
                            outputProblems = r.RunsWithOutputProblems,
                            outputProblemsAtEnd = r.RunsWithProblemsAtEnd,
                            planNudges = r.PlanNudges,
                            avgTokens = r.AvgTokens,
                            maxTokens = r.MaxTokens,
                            budgetStops = r.BudgetStops,
                            stuckStops = r.StuckStops,
                            guardNudgeRuns = r.RunsWithGuardNudges,
                        }
                        : null,
                    episodes = total,
                    episodesReused = reused,
                    episodesRecent = recent,
                    skillsActive = learned.Count(k => k.LearnedStatus == Flyknit.Core.Skills.LearnedSkillStatus.Active),
                    skillsCandidate = learned.Count(k => k.LearnedStatus == Flyknit.Core.Skills.LearnedSkillStatus.Candidate),
                    skillsRetired = learned.Count(k => k.LearnedStatus == Flyknit.Core.Skills.LearnedSkillStatus.Retired),
                };
            }

            case "skills.setLearnedStatus":
            {
                // 用户手动把退役的技能重新启用（或者停掉一个）
                var skill = _host.Skills.Skills.FirstOrDefault(k => k.IsLearned && k.Name == Str("name"));
                var ok = skill is not null && _host.LearnedSkillLedger.SetStatus(skill, Str("status"));
                if (ok)
                {
                    _host.SkillManager.Refresh();
                }
                return ok;
            }

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

            case "ui.activeConversation":
                ActiveConversationId = OptStr("id");
                return null;

            case "schedules.list":
                return await Task.Run(() => _host.Store.Schedules.List().Select(ScheduleDto).ToList());

            case "schedules.save":
            {
                var id = Str("id");
                var task = id.Length > 0 ? _host.Store.Schedules.Get(id) : null;
                task ??= new Flyknit.Core.Scheduling.ScheduledTask();
                task.Name = Str("name");
                task.Instructions = Str("instructions");
                task.Schedule = ParseSchedule(p);
                task.Workspace = OptStr("workspace");
                task.Permission = Flyknit.Core.Security.PermissionModes.Parse(Str("permission"));
                task.ModelId = Int("modelId");
                task.CatchUp = !p.TryGetProperty("catchUp", out var cu) || cu.ValueKind != JsonValueKind.False;
                if (p.TryGetProperty("enabled", out var en))
                {
                    task.Enabled = en.ValueKind != JsonValueKind.False;
                }
                _host.Store.Schedules.Reschedule(task, DateTimeOffset.Now);
                return ScheduleDto(task);
            }

            case "schedules.setEnabled":
            {
                var task = _host.Store.Schedules.Get(Str("id"));
                if (task is null)
                {
                    return null;
                }
                task.Enabled = Bool("enabled");
                _host.Store.Schedules.Reschedule(task, DateTimeOffset.Now);
                return ScheduleDto(task);
            }

            case "schedules.delete":
                _host.Store.Schedules.Delete(Str("id"));
                return null;

            case "schedules.run":
            {
                var task = _host.Store.Schedules.Get(Str("id"));
                if (task is null)
                {
                    return new { ok = false, message = "任务不存在" };
                }
                var (ok, message) = _host.Scheduler.Run(task, DateTimeOffset.Now, manual: true);
                return new { ok, message, conversationId = task.LastConversationId };
            }

            case "usage.stats":
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var usage = await _host.Server.GetUsageAsync(cts.Token);
                return new
                {
                    day = usage.Day,
                    todayTokens = usage.TodayTokens,
                    dailyLimit = usage.DailyLimit,
                    remaining = usage.Remaining,
                    exceeded = usage.Exceeded,
                    byScene = usage.ByScene.Select(x => new { scene = x.Scene, prompt = x.Prompt, completion = x.Completion, total = x.Total, requests = x.Requests }).ToList(),
                    byDay = usage.ByDay.Select(x => new { day = x.Day, tokens = x.Tokens }).ToList(),
                    contactName = usage.ContactName,
                    contactEmail = usage.ContactEmail,
                    contactPhone = usage.ContactPhone,
                };
            }

            case "security.list":
            {
                var filter = Str("decision");
                var limit = Math.Clamp(Int("limit") ?? 200, 1, ConversationStore.SecurityEventLimit);
                var events = await Task.Run(() => _host.Store.ListSecurityEvents(filter.Length > 0 ? filter : null, limit));
                return events.Select(e => new
                {
                    id = e.Id,
                    conversationId = e.ConversationId,
                    title = e.ConversationTitle,
                    scene = e.Scene,
                    tool = e.Tool,
                    detail = e.Detail,
                    decision = e.Decision,
                    reason = e.Reason,
                    createdAt = e.CreatedAt.ToString("O"),
                }).ToList();
            }

            case "security.clear":
                await Task.Run(() => _host.Store.ClearSecurityEvents());
                return null;

            case "security.export":
            {
                var path = _window.PickSaveFile($"flyknit-security-{DateTime.Now:yyyyMMdd-HHmm}.csv", "CSV 文件 (*.csv)|*.csv");
                if (path is null)
                {
                    return new { ok = false, cancelled = true, message = "", path = "", count = 0 };
                }
                // 界面按条件筛过时只导出筛出来的那些
                HashSet<long>? only = null;
                if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("ids", out var idList) && idList.ValueKind == JsonValueKind.Array)
                {
                    only = idList.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt64()).ToHashSet();
                }
                try
                {
                    var events = await Task.Run(() => _host.Store.ListSecurityEvents(null, ConversationStore.SecurityEventLimit)
                        .Where(e => only is null || only.Contains(e.Id)).ToList());
                    await Task.Run(() =>
                    {
                        using var writer = new StreamWriter(path, false, new System.Text.UTF8Encoding(false));
                        ConversationStore.WriteSecurityEventsCsv(events, writer);
                    });
                    return new { ok = true, cancelled = false, message = "", path, count = events.Count };
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return new { ok = false, cancelled = false, message = ex.Message, path, count = 0 };
                }
            }

            case "security.network":
            {
                var network = _host.Policy.Network;
                return new
                {
                    enabled = _host.Security.On(Flyknit.Core.Security.SecuritySettings.NetworkAllowlist),
                    domains = network.Domains,
                    allowPrivate = network.AllowPrivateNetwork,
                };
            }

            case "approvals.list":
                return _host.Approvals.List().Select(a => new
                {
                    id = a.Id,
                    tool = a.Tool,
                    shell = a.Shell,
                    prefix = a.Prefix,
                    scope = a.Scope,
                    display = a.Display,
                    approvedAt = a.CreatedAt.ToString("O"),
                    lastUsedAt = a.LastUsedAt.ToString("O"),
                    uses = a.Uses,
                }).ToList();

            case "approvals.revoke":
                _host.Approvals.Revoke(Str("id"));
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

            // ---------- MCP 连接器（和技能分开的一组调用） ----------
            case "mcp.list":
                return await _host.Mcp.ListAsync(Bool("refresh"));

            case "mcp.connect":
            {
                // 连接可能要等浏览器登录，最长几分钟；结果（成功或原因）都放在返回值里，界面就地显示
                var values = new Dictionary<string, string>();
                if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("values", out var raw) && raw.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in raw.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.String)
                        {
                            values[prop.Name] = prop.Value.GetString() ?? "";
                        }
                    }
                }
                var vendorId = Str("id");
                try
                {
                    var vendor = await _host.Mcp.ConnectAsync(vendorId, values, interactive: true, CancellationToken.None);
                    return new { ok = true, message = "", vendor };
                }
                catch (Flyknit.Core.Mcp.McpException ex)
                {
                    return new { ok = false, message = ex.Message, vendor = _host.Mcp.Get(vendorId) };
                }
            }

            case "mcp.cancel":
                _host.Mcp.Cancel(Str("id"));
                return null;

            case "mcp.disconnect":
                return await _host.Mcp.DisconnectAsync(Str("id"), Bool("forget"));

            case "skills.list":
                _host.SkillManager.Refresh();
                return SkillList();

            case "skills.setEnabled":
            {
                var (ok, message) = _host.SkillManager.Enable(Str("name"), Bool("enabled"));
                return new { ok, message, skills = SkillList() };
            }

            case "skills.uninstall":
            {
                var (ok, message) = _host.SkillManager.Uninstall(Str("name"));
                return new { ok, message, skills = SkillList() };
            }

            case "skills.inspect":
            {
                var path = OptStr("path") ?? _window.PickFiles().FirstOrDefault();
                if (path is null)
                {
                    return null;
                }
                return new { path, inspection = Inspection(_host.SkillManager.Inspect(path)) };
            }

            case "skills.install":
            {
                // path 为空时弹文件选择框；zip 里有多个技能时全部安装
                var path = OptStr("path") ?? _window.PickFiles().FirstOrDefault();
                if (path is null)
                {
                    return null;
                }
                var results = Directory.Exists(path)
                    ? new List<Flyknit.Core.Skills.SkillInstallResult> { _host.SkillManager.InstallFrom(path) }
                    : _host.SkillManager.InstallAllFrom(path);
                return InstallOutcome(results);
            }

            case "skills.installFolder":
            {
                var folder = OptStr("path") ?? _window.PickFolder();
                if (folder is null)
                {
                    return null;
                }
                var roots = Flyknit.Core.Skills.SkillPackage.FindAllSkillRoots(folder);
                var results = (roots.Count > 0 ? roots : new List<string> { folder })
                    .Select(r => _host.SkillManager.InstallFrom(r, folder)).ToList();
                return InstallOutcome(results);
            }

            case "skills.installFromUrl":
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                var result = await _host.SkillManager.InstallFromUrlAsync(Str("url"), cts.Token);
                return InstallOutcome(new List<Flyknit.Core.Skills.SkillInstallResult> { result });
            }

            case "skills.library":
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var library = await _host.Server.GetSkillsAsync(cts.Token);
                // 服务端的技能名是包名（slug，如 excel-xlsx），而本地 SkillInfo.Name 取自 SKILL.md 的
                // name 字段（可能是 "Excel / XLSX" 这种显示名）。安装时目录名用的是 Sanitize(SKILL.md 名)，
                // 正好等于包名，所以这里同时按 原名 / 规范化名 / 目录名 建索引，避免已装却仍显示“安装”。
                var installed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var sk in _host.Skills.Skills)
                {
                    foreach (var key in new[]
                    {
                        sk.Name,
                        Flyknit.Core.Skills.SkillPackage.Sanitize(sk.Name),
                        Path.GetFileName(sk.Directory.TrimEnd('\\', '/')),
                    })
                    {
                        if (!string.IsNullOrEmpty(key))
                        {
                            installed[key] = sk.Version;
                        }
                    }
                }
                return library.Select(s => new
                {
                    name = s.Name,
                    description = s.Description,
                    version = s.Version,
                    author = s.Author,
                    origin = s.Origin,
                    size = s.Size,
                    required = s.Required,
                    installed = installed.ContainsKey(s.Name),
                    updatable = installed.TryGetValue(s.Name, out var v) && s.Version.Length > 0 && v != s.Version,
                }).ToList();
            }

            case "skills.installFromLibrary":
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                var result = await _host.SkillManager.InstallFromLibraryAsync(Str("name"), cts.Token);
                return InstallOutcome(new List<Flyknit.Core.Skills.SkillInstallResult> { result });
            }

            case "skills.openFolder":
            {
                var target = OptStr("path") is { } p2 && Directory.Exists(p2) ? p2 : AppPaths.Skills;
                Directory.CreateDirectory(target);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
                return null;
            }

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

            case "window.setBackground":
                _window.SetBackground(Str("color"));
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

            case "conversations.plan":
                return Flyknit.Core.Tools.TaskPlan.Parse(_host.Store.Get(Str("id"))?.Plan)
                    .Select(p => new { step = p.Step, status = p.Status }).ToList();

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
                    // 「本任务内不再询问」：之前没映射，落到下面的 Reject——用户点了允许，操作却被拒绝
                    "allowForSession" => ConfirmChoice.AllowForSession,
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

            // ---------- 资料库 ----------
            case "library.list":
            {
                var query = new LibraryQuery
                {
                    Tab = Str("tab") is { Length: > 0 } tab ? tab : "all",
                    FolderId = OptStr("folderId"),
                    Search = Str("search"),
                    Kind = Str("kind"),
                    Sort = Str("sort") is { Length: > 0 } sort ? sort : "updated",
                    IncludeHidden = Bool("hidden"),
                };
                var lib = _host.Library.Store;
                var (items, folders) = await Task.Run(() => (lib.List(query), lib.Folders()));
                return new { items = items.Select(LibraryDto).ToList(), folders = folders.Select(FolderDto).ToList() };
            }

            case "library.upload":
            {
                var filter = Str("accept") == "image" ? "图片|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp;*.svg|所有文件|*.*" : null;
                var paths = _window.PickFiles(filter);
                var folder = OptStr("folderId");
                var added = await Task.Run(() => _host.Library.Upload(paths, folder));
                return added.Select(LibraryDto).ToList();
            }

            case "library.addDropped":
            {
                var folder = OptStr("folderId");
                var added = await Task.Run(() => _host.Library.Upload(droppedPaths.Where(File.Exists).ToList(), folder));
                return added.Select(LibraryDto).ToList();
            }

            case "library.addNote":
                return LibraryDto(_host.Library.Store.AddNote(Str("title"), Str("text"), OptStr("folderId")));

            case "library.updateNote":
                return _host.Library.Store.UpdateNote(Str("id"), Str("text"));

            case "library.rename":
                return _host.Library.Store.Rename(Str("id"), Str("name"));

            case "library.favorite":
                return _host.Library.Store.SetFavorite(Ids(), Bool("favorite"));

            case "library.move":
                return _host.Library.Store.Move(Ids(), OptStr("folderId"));

            case "library.delete":
                return _host.Library.Store.Delete(Ids());

            case "library.restore":
                return _host.Library.Store.Restore(Ids());

            case "library.purge":
                return await Task.Run(() => _host.Library.Store.Purge(Ids()));

            case "library.emptyTrash":
                return await Task.Run(() => _host.Library.Store.EmptyTrash());

            case "library.createFolder":
                return FolderDto(_host.Library.Store.CreateFolder(Str("name")));

            case "library.renameFolder":
                return _host.Library.Store.RenameFolder(Str("id"), Str("name"));

            case "library.deleteFolder":
                return _host.Library.Store.DeleteFolder(Str("id"));

            case "library.download":
                return _host.Library.Download(Ids(), name =>
                {
                    var ext = Path.GetExtension(name);
                    return _window.PickSaveFile(name, ext.Length > 1 ? $"{ext.TrimStart('.').ToUpperInvariant()}|*{ext}|所有文件|*.*" : "所有文件|*.*");
                }, _window.PickFolder);

            case "library.share":
                return _host.Library.CopyToClipboard(Ids());

            case "library.reveal":
            {
                var item = _host.Library.Store.Get(Str("id"));
                if (item is null || !File.Exists(item.Path))
                {
                    return new { ok = false, message = "文件不存在，可能已被移动或删除" };
                }
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Path}\"") { UseShellExecute = true });
                return new { ok = true, message = "" };
            }

            case "library.preview":
            {
                var item = _host.Library.Store.Get(Str("id")) ?? throw new InvalidOperationException("文件不存在");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var doc = await _preview.LoadAsync(item.Path, cts.Token);
                return new
                {
                    path = item.Path,
                    name = item.Name,
                    kind = doc.Kind.ToString().ToLowerInvariant(),
                    text = doc.Text,
                    language = doc.Language,
                    // 图片直接用资料库地址显示原图，不再转一遍 base64
                    dataUrl = doc.Kind.ToString() == "Image" ? null : doc.DataUrl,
                    notice = doc.Notice,
                    error = doc.Error,
                    sections = doc.Sections.Select(x => new { title = x.Title, text = x.Text, rows = x.Rows }).ToList(),
                };
            }

            case "library.attachments":
                // 围绕这些文件开始对话：转成附件交给输入框
                return Ids().Select(_host.Library.Store.Get).OfType<LibraryItem>().Where(i => File.Exists(i.Path))
                    .Select(i => new AttachmentDto { FileName = i.Name, LocalPath = i.Path, Mime = i.Mime, Size = i.Size }).ToList();

            case "files.open":
            case "files.reveal":
            {
                // 在资源管理器里定位到这个文件（选中它），或者直接打开这个文件夹
                var path = Str("path");
                if (File.Exists(path))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                }
                else if (Directory.Exists(path))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
                }
                else
                {
                    return new { ok = false, message = "文件不存在，可能已被移动或删除" };
                }
                return new { ok = true, message = "" };
            }

            case "files.launch":
            {
                // 用系统默认应用打开（.pptx → PowerPoint，.xlsx → Excel…）
                var path = Str("path");
                if (!File.Exists(path))
                {
                    return new { ok = false, message = "文件不存在，可能已被移动或删除" };
                }
                try
                {
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                    return new { ok = true, message = "" };
                }
                catch (Exception ex)
                {
                    Log.Warn($"打开文件失败：{path}", ex);
                    return new { ok = false, message = $"打开失败：{ex.Message}" };
                }
            }

            case "files.preview":
            {
                var path = Str("path");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var doc = await _preview.LoadAsync(path, cts.Token);
                return new
                {
                    path,
                    name = Path.GetFileName(path),
                    kind = doc.Kind.ToString().ToLowerInvariant(),
                    text = doc.Text,
                    language = doc.Language,
                    dataUrl = doc.DataUrl,
                    notice = doc.Notice,
                    error = doc.Error,
                    sections = doc.Sections.Select(x => new { title = x.Title, text = x.Text, rows = x.Rows }).ToList(),
                };
            }

            default:
                throw new NotSupportedException($"未知方法：{method}");
        }
    }

    private static object LibraryDto(LibraryItem i) => new
    {
        id = i.Id,
        name = i.Name,
        path = i.Path,
        kind = i.Kind,
        mime = i.Mime,
        size = i.Size,
        source = i.Source,
        conversationId = i.ConversationId,
        folderId = i.FolderId,
        favorite = i.Favorite,
        managed = i.Managed,
        hidden = i.Hidden,
        exists = i.Exists,
        createdAt = i.CreatedAt.ToString("O"),
        updatedAt = i.UpdatedAt.ToString("O"),
        deletedAt = i.DeletedAt?.ToString("O"),
        // 带上修改时间：文件变了缩略图要重新取，不能用浏览器缓存的旧图
        url = $"https://{LibraryService.Host}/{i.Id}?v={i.UpdatedAt.UtcTicks}",
        thumbUrl = i.Kind == "image" ? $"https://{LibraryService.Host}/{i.Id}?thumb=1&v={i.UpdatedAt.UtcTicks}" : null,
    };

    private static object FolderDto(LibraryFolder f) => new
    {
        id = f.Id,
        name = f.Name,
        count = f.Count,
        createdAt = f.CreatedAt.ToString("O"),
        updatedAt = f.UpdatedAt.ToString("O"),
    };

    /// <summary>默认值叠加用户改动后的最终快捷键表，界面直接按它渲染和分派。</summary>
    private object ShortcutList()
    {
        var resolved = Flyknit.Core.Settings.Shortcuts.Resolve(_settings.Shortcuts);
        return Flyknit.Core.Settings.Shortcuts.All.Select(c => new
        {
            id = c.Id,
            group = c.Group,
            binding = resolved[c.Id],
            @default = Flyknit.Core.Settings.Shortcuts.Normalize(c.Default),
            global = c.Global,
            @fixed = c.Fixed,
            customized = _settings.Shortcuts.ContainsKey(c.Id),
        }).ToList();
    }

    private static object ScheduleDto(Flyknit.Core.Scheduling.ScheduledTask t) => new
    {
        id = t.Id,
        name = t.Name,
        instructions = t.Instructions,
        kind = t.Schedule.Kind.ToString().ToLowerInvariant(),
        hour = t.Schedule.Hour,
        minute = t.Schedule.Minute,
        weekday = (int)t.Schedule.Weekday,
        dayOfMonth = t.Schedule.DayOfMonth,
        at = t.Schedule.At?.ToString("O"),
        enabled = t.Enabled,
        workspace = t.Workspace,
        permission = Flyknit.Core.Security.PermissionModes.ToText(t.Permission),
        modelId = t.ModelId,
        catchUp = t.CatchUp,
        nextRunAt = t.NextRunAt?.ToString("O"),
        lastRunAt = t.LastRunAt?.ToString("O"),
        lastStatus = t.LastStatus,
        lastSummary = t.LastSummary,
        lastConversationId = t.LastConversationId,
        runCount = t.RunCount,
    };

    private static Flyknit.Core.Scheduling.ScheduleSpec ParseSchedule(JsonElement p)
    {
        string S(string n) => p.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        int I(string n, int fallback) => p.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;
        var kind = Enum.TryParse<Flyknit.Core.Scheduling.ScheduleKind>(S("kind"), ignoreCase: true, out var k)
            ? k
            : Flyknit.Core.Scheduling.ScheduleKind.Manual;
        return new Flyknit.Core.Scheduling.ScheduleSpec(
            kind,
            I("hour", 9),
            I("minute", 0),
            (DayOfWeek)Math.Clamp(I("weekday", 1), 0, 6),
            I("dayOfMonth", 1),
            DateTimeOffset.TryParse(S("at"), out var at) ? at : null);
    }

    private object SkillList() => _host.Skills.Skills.Select(s => new
    {
        name = s.Name,
        description = s.Description,
        version = s.Version,
        author = s.Author,
        license = s.License,
        homepage = s.Homepage,
        origin = s.Origin,
        source = s.Source.ToString().ToLowerInvariant(),
        organization = s.IsOrganization,
        learned = s.IsLearned,
        required = s.Required,
        enabled = s.Enabled,
        directory = s.Directory,
        files = s.ListFiles(60),
        scripts = s.ListScripts(),
        bytes = s.TotalBytes(),
    }).ToList();

    private object Inspection(Flyknit.Core.Skills.SkillInspection i) => new
    {
        ok = i.Ok,
        error = i.Error,
        name = i.Name,
        description = i.Description,
        version = i.Version,
        files = i.Files,
        scripts = i.Scripts,
        bytes = i.Bytes,
        warnings = i.Warnings,
        replaces = i.Replaces,
    };

    private object InstallOutcome(List<Flyknit.Core.Skills.SkillInstallResult> results) => new
    {
        ok = results.Any(r => r.Ok),
        installed = results.Where(r => r.Ok).Select(r => r.Inspection.Name).ToList(),
        messages = results.Select(r => r.Message).ToList(),
        warnings = results.Where(r => r.Ok).SelectMany(r => r.Inspection.Warnings).Distinct().ToList(),
        skills = SkillList(),
    };

    /// <summary>个性化页面需要的全部内容。</summary>
    private object PersonaInfo()
    {
        var soul = _host.Memory.Read(Flyknit.Core.Memory.MemoryStore.SoulFile);
        var about = Flyknit.Core.Settings.AboutMe.Parse(_host.Memory.Read(Flyknit.Core.Memory.MemoryStore.RoleFile));
        return new
        {
            preset = _host.PersonaKey,
            effective = Flyknit.Core.Settings.Personas.Effective(_host.PersonaKey, _host.PlayfulPersonasAllowed, _host.CustomPersonaAllowed),
            custom = string.Join("\n", soul.Replace("\r", "").Split('\n').Where(l => !l.TrimStart().StartsWith('#'))).Trim(),
            callName = _settings.UserCallName,
            assistantName = _settings.AssistantName,
            playfulAllowed = _host.PlayfulPersonasAllowed,
            customAllowed = _host.CustomPersonaAllowed,
            presets = Flyknit.Core.Settings.Personas.Presets.Select(x => new { key = x.Key, playful = x.Playful }).ToList(),
            about = new
            {
                department = about.Department,
                position = about.Position,
                language = about.Language,
                systems = about.Systems,
                folders = about.Folders,
                other = about.Other,
            },
        };
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

    private static string SafeWebViewVersion()
    {
        try
        {
            return CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static object NetworkInfo(Flyknit.Core.Diagnostics.NetworkReport r, string path) => new
    {
        at = r.At.ToString("O"),
        endpoint = r.Endpoint,
        host = r.Host,
        port = r.Port,
        scheme = r.Scheme,
        proxyMode = r.Proxy.Mode,
        proxyUrl = r.Proxy.Url,
        overall = r.Overall.ToString().ToLowerInvariant(),
        checks = r.Checks.Select(c => new
        {
            id = c.Id,
            status = c.Status.ToString().ToLowerInvariant(),
            code = c.Code,
            value = c.Value,
            ms = c.Ms,
            error = c.Error,
        }).ToList(),
        reportPath = path,
        reportDir = Path.Combine(AppPaths.Logs, "network"),
    };
}

/// <summary>主窗口提供给桥接层的操作。</summary>
public interface IWindowActions
{
    void HideMain();
    void MinimizeMain();

    /// <summary>界面底色（#rrggbb），跟着主题变。</summary>
    void SetBackground(string color);

    bool ToggleTopmost();
    void RequestAttention();
    void LanguageChanged(string language);
    /// <param name="filter">文件类型过滤（SaveFileDialog 格式），为空表示所有文件。</param>
    IReadOnlyList<string> PickFiles(string? filter = null);
    string? PickFolder();
    string? PickSaveFile(string defaultName, string filter);
    bool IsMaximized { get; }
    bool ToggleMaximize();
    void StartResize(string direction);

    /// <summary>全局热键改了之后重新注册。</summary>
    void RefreshHotkey();
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

    /// <summary>勾选「以后自动执行」后生效的规则，例如 "npm run *（在 D:\\工作区 内）"。</summary>
    public string RuleDisplay { get; init; } = "";

    /// <summary>read / write / destructive / unknown，界面按这个给确认卡片配色。</summary>
    public string Effect { get; init; } = "unknown";
    public string Title { get; init; } = "";
    public TaskCompletionSource<ConfirmChoice> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
