using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Flyknit.Core.Chat;
using Flyknit.Core.Security;

namespace Flyknit.Core.Gateway;

public sealed record DeviceRegistration(
    [property: JsonPropertyName("device_id")] int DeviceId,
    [property: JsonPropertyName("token")] string Token);

public sealed class SceneInfo
{
    [JsonPropertyName("scene")] public string Scene { get; set; } = "";
    [JsonPropertyName("available")] public bool Available { get; set; }
    [JsonPropertyName("model_name")] public string ModelName { get; set; } = "";
    [JsonPropertyName("supports_tools")] public bool SupportsTools { get; set; }
    [JsonPropertyName("supports_vision")] public bool SupportsVision { get; set; }
    [JsonPropertyName("context_length")] public int ContextLength { get; set; }
}

public sealed class ClientConfig
{
    [JsonPropertyName("server_version")] public string ServerVersion { get; set; } = "";
    [JsonPropertyName("scenes")] public List<SceneInfo> Scenes { get; set; } = new();
    [JsonPropertyName("policy")] public PolicyConfig? Policy { get; set; }

    /// <summary>安全中心每一项的值和锁状态。锁住的由 IT 统一配置，本机改不了。</summary>
    [JsonPropertyName("security")]
    public Dictionary<string, Flyknit.Core.Security.SecurityItem> Security { get; set; } = new();

    [JsonPropertyName("revision")] public int Revision { get; set; }
    [JsonPropertyName("owner")] public string Owner { get; set; } = "";
    [JsonPropertyName("department")] public string Department { get; set; } = "";
}

public sealed class ClientModel
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("provider")] public string Provider { get; set; } = "";
    [JsonPropertyName("supports_tools")] public bool SupportsTools { get; set; }
    [JsonPropertyName("supports_vision")] public bool SupportsVision { get; set; }
}

/// <summary>本机的 token 用量与配额。</summary>
public sealed class UsageStats
{
    [JsonPropertyName("day")] public string Day { get; set; } = "";
    [JsonPropertyName("today_tokens")] public int TodayTokens { get; set; }
    [JsonPropertyName("daily_limit")] public int DailyLimit { get; set; }
    [JsonPropertyName("remaining")] public int Remaining { get; set; }
    [JsonPropertyName("exceeded")] public bool Exceeded { get; set; }
    [JsonPropertyName("by_scene")] public List<SceneUsage> ByScene { get; set; } = new();
    [JsonPropertyName("by_day")] public List<DayUsage> ByDay { get; set; } = new();
    [JsonPropertyName("contact_name")] public string ContactName { get; set; } = "";
    [JsonPropertyName("contact_email")] public string ContactEmail { get; set; } = "";
    [JsonPropertyName("contact_phone")] public string ContactPhone { get; set; } = "";
}

public sealed class TranscriptResult
{
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("language")] public string Language { get; set; } = "";
    [JsonPropertyName("duration_seconds")] public int DurationSeconds { get; set; }
    [JsonPropertyName("transport")] public string Transport { get; set; } = "";
    [JsonPropertyName("model")] public string Model { get; set; } = "";
}

public sealed class SceneUsage
{
    [JsonPropertyName("scene")] public string Scene { get; set; } = "";
    [JsonPropertyName("prompt")] public int Prompt { get; set; }
    [JsonPropertyName("completion")] public int Completion { get; set; }
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("requests")] public int Requests { get; set; }
}

public sealed class DayUsage
{
    [JsonPropertyName("day")] public string Day { get; set; } = "";
    [JsonPropertyName("tokens")] public int Tokens { get; set; }
}

/// <summary>服务端有没有更新。Available=false 时其余字段不用看。</summary>
public sealed class ClientUpdate
{
    [JsonPropertyName("available")] public bool Available { get; set; }
    [JsonPropertyName("version")] public string Version { get; set; } = "";

    /// <summary>更新日志，员工点「更新日志」看到的就是这段。</summary>
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }

    /// <summary>下完自己算一遍。对不上就不装。</summary>
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
}

/// <summary>公司技能库里的一个技能。</summary>
public sealed class ServerSkill
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("origin")] public string Origin { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }

    /// <summary>管理员要求所有电脑都安装。</summary>
    [JsonPropertyName("required")] public bool Required { get; set; }

    [JsonPropertyName("updated_at")] public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class AuditEntry
{
    [JsonPropertyName("conversation_id")] public string ConversationId { get; set; } = "";
    [JsonPropertyName("tool_name")] public string ToolName { get; set; } = "";
    [JsonPropertyName("arguments")] public string Arguments { get; set; } = "";
    /// <summary>发生在哪种模式：agent / chat / translate。</summary>
    [JsonPropertyName("scene")] public string Scene { get; set; } = "";

    [JsonPropertyName("risk")] public string Risk { get; set; } = "auto";
    [JsonPropertyName("decision")] public string Decision { get; set; } = "auto";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
    [JsonPropertyName("occurred_at")] public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.Now;

    /// <summary>专家执行时所属的子任务；主助手自己的操作为空。</summary>
    [JsonPropertyName("subtask_id")] public string? SubtaskId { get; set; }

    /// <summary>main（主助手）或 expert:&lt;专家 ID&gt;。</summary>
    [JsonPropertyName("actor")] public string Actor { get; set; } = "main";
}

/// <summary>后台下发给本机、要由 AI agent 执行的一条指令。</summary>
public sealed class ClientInstruction
{
    [JsonPropertyName("run_id")] public int RunId { get; set; }
    [JsonPropertyName("instruction_id")] public int InstructionId { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("prompt")] public string Prompt { get; set; } = "";
}

internal sealed class ClientInstructionPoll
{
    [JsonPropertyName("instructions")] public List<ClientInstruction> Instructions { get; set; } = new();
    [JsonPropertyName("poll_after")] public int PollAfter { get; set; } = 60;
}

internal sealed class InstructionStartResult
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "";
}

/// <summary>通道（钉钉）里员工发来、要在这台电脑上执行的一条消息。</summary>
public sealed class ChannelTask
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("channel")] public string Channel { get; set; } = "";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("sender")] public string Sender { get; set; } = "";
    /// <summary>接着上次的对话；空表示新开一个。</summary>
    [JsonPropertyName("conversation_id")] public string ConversationId { get; set; } = "";
}

internal sealed class ChannelPoll
{
    [JsonPropertyName("messages")] public List<ChannelTask> Messages { get; set; } = new();
}

public sealed class ChannelBindingInfo
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("channel")] public string Channel { get; set; } = "";
    [JsonPropertyName("external_name")] public string ExternalName { get; set; } = "";
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; set; }
    [JsonPropertyName("last_used")] public DateTimeOffset? LastUsed { get; set; }
}

public sealed class ChannelState
{
    /// <summary>后台开了钉钉通道。</summary>
    [JsonPropertyName("dingtalk")] public bool DingTalk { get; set; }
    [JsonPropertyName("bindings")] public List<ChannelBindingInfo> Bindings { get; set; } = new();
}

public sealed class ChannelBindCode
{
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    [JsonPropertyName("expires_at")] public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>与 Flyknit 服务端通信：模型网关、设备注册、配置、审计。</summary>
public sealed class FlyknitServerClient : IChatGateway, IEmbeddingGateway, Flyknit.Core.Mcp.IMcpRuntimeSource, Flyknit.Core.Experts.Delegation.ISubtaskRegistry
{
    private HttpClient _http;
    private string? _token;

    private readonly string _serverUrl;

    /// <summary>换掉底层的 HttpClient（改代理时用）。地址和 Token 保持不变，上层无感。</summary>
    public void ReplaceHttpClient(HttpClient http)
    {
        var old = _http;
        http.BaseAddress = new Uri(_serverUrl.TrimEnd('/') + "/");
        http.Timeout = Timeout.InfiniteTimeSpan;
        _http = http;
        try { old.Dispose(); } catch (Exception) { /* 旧连接可能还在用，释放失败不影响新的 */ }
    }

    public FlyknitServerClient(HttpClient http, string serverUrl, string? deviceToken)
    {
        _serverUrl = serverUrl;
        _http = http;
        _http.BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/");
        _http.Timeout = Timeout.InfiniteTimeSpan; // 流式请求由 CancellationToken 控制
        _token = deviceToken;
    }

    /// <summary>等响应头最多这么久（流式请求、下载）。服务端拿到上游的响应头就会回，正常几秒内。</summary>
    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>流式读取时这么久一点数据都没收到，就当连接已经悄悄断了。</summary>
    public TimeSpan StreamIdleTimeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>普通请求（含非流式的模型调用）整体最多等这么久，兜住连接悄悄断掉一直挂着的情况。</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(5);

    internal const string TimeoutMessage = "连接模型服务超时，请检查网络后重试";
    internal const string BrokenMessage = "与服务器的连接中断了，请重试";
    internal const string TruncatedMessage = "回答被中断了，请重试";

    public bool IsRegistered => !string.IsNullOrEmpty(_token);

    public string ServerUrl => _serverUrl.TrimEnd('/');

    /// <summary>网络检测：服务端健康检查（不带令牌）。返回 HTTP 状态码，连不上就抛异常。</summary>
    public async Task<int> HealthStatusAsync(CancellationToken ct)
    {
        using var resp = await _http.GetAsync("healthz", ct);
        return (int)resp.StatusCode;
    }

    /// <summary>网络检测：带设备令牌访问一次，看服务端还认不认这台电脑。返回 HTTP 状态码。</summary>
    public async Task<int> AuthStatusAsync(CancellationToken ct)
    {
        using var req = Authorized(HttpMethod.Get, "api/v1/client/usage?days=1");
        using var resp = await _http.SendAsync(req, ct);
        return (int)resp.StatusCode;
    }

    /// <summary>退出登录：让服务端作废这台电脑的设备令牌。</summary>
    public async Task LogoutAsync(CancellationToken ct)
    {
        using var req = Authorized(HttpMethod.Post, "api/v1/devices/logout");
        using var resp = await SendAsync(req, ct);
        await EnsureOk(resp, ct);
    }

    public Task<DeviceRegistration> RegisterAsync(string enrollmentKey, string uiLanguage, string clientVersion, CancellationToken ct) =>
        PostRegistrationAsync(new
        {
            enrollment_key = enrollmentKey,
            machine_name = Environment.MachineName,
            user_name = Environment.UserName,
            os_version = Environment.OSVersion.VersionString,
            client_version = clientVersion,
            ui_language = uiLanguage,
            machine_guid = MachineInfo.SystemGuid(),
        }, ct);

    /// <summary>
    /// 用安装包里的安装凭证注册（员工点了「登录」）。员工身份由本机确认过：域账号是 Windows 已验证的，本机账号刚校验过密码。
    /// </summary>
    /// <param name="agreedLegal">登录界面上同意的用户协议和隐私政策版本（<see cref="GetLegalAsync"/> 给的 versions）。</param>
    public Task<DeviceRegistration> RegisterWithTicketAsync(string ticket, Setup.WindowsAccount account, string agreedLegal, string uiLanguage, string clientVersion, CancellationToken ct) =>
        PostRegistrationAsync(new
        {
            ticket,
            agreed_legal = agreedLegal,
            login_method = account.Kind == Setup.LoginKind.Domain ? "domain" : "local",
            domain = account.Kind == Setup.LoginKind.Domain ? account.Domain : "",
            machine_name = account.MachineName,
            user_name = account.Display,
            os_version = Environment.OSVersion.VersionString,
            client_version = clientVersion,
            ui_language = uiLanguage,
            machine_guid = MachineInfo.SystemGuid(),
        }, ct);

    /// <summary>用户协议和隐私政策（登录界面显示，还没登录所以不带令牌）。原样返回服务端的 JSON。</summary>
    public async Task<string> GetLegalAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "api/v1/legal");
        using var resp = await SendAsync(req, ct);
        await EnsureOk(resp, ct);
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private async Task<DeviceRegistration> PostRegistrationAsync(object body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/v1/devices/register") { Content = JsonContent.Create(body) };
        using var resp = await SendAsync(req, ct);
        await EnsureOk(resp, ct);
        var reg = await resp.Content.ReadFromJsonAsync<DeviceRegistration>(cancellationToken: ct)
                  ?? throw new GatewayException("注册返回为空");
        _token = reg.Token;
        return reg;
    }

    public async Task<ClientConfig> GetConfigAsync(CancellationToken ct)
    {
        using var req = Authorized(HttpMethod.Get, "api/v1/client/config");
        using var resp = await SendAsync(req, ct);
        await EnsureOk(resp, ct);
        return await resp.Content.ReadFromJsonAsync<ClientConfig>(cancellationToken: ct) ?? new ClientConfig();
    }

    /// <summary>
    /// 长轮询等配置变更：带上手里的版本号，服务端一旦发现 IT 改了配置就立刻返回新版本号，
    /// 否则挂起到服务端超时（约 25 秒）再返回。据此做到近乎即时地拉取新配置。
    /// </summary>
    public async Task<int> WaitForConfigChangeAsync(int revision, CancellationToken ct)
    {
        using var req = Authorized(HttpMethod.Get, $"api/v1/client/config/wait?rev={revision}");
        using var resp = await SendAsync(req, ct);
        await EnsureOk(resp, ct);
        var doc = await resp.Content.ReadFromJsonAsync<WaitResult>(cancellationToken: ct);
        return doc?.Revision ?? revision;
    }

    private sealed class WaitResult
    {
        [JsonPropertyName("revision")] public int Revision { get; set; }
    }

    /// <summary>
    /// 上报本机信息，供后台做资产台账。注册时采的那一份很快就过时了——IP 跟着
    /// DHCP 变，用户换人登录，客户端会升级——所以每次拉配置后都重报一次。
    /// </summary>
    public async Task ReportMachineAsync(MachineInfo info, CancellationToken ct)
    {
        using var req = Authorized(HttpMethod.Post, "api/v1/devices/heartbeat");
        req.Content = JsonContent.Create(info);
        using var resp = await SendAsync(req, ct);
        await EnsureOk(resp, ct);
    }

    /// <summary>输入框可选择的模型。</summary>
    public async Task<List<ClientModel>> GetModelsAsync(CancellationToken ct)
    {
        using var req = Authorized(HttpMethod.Get, "api/v1/client/models");
        using var resp = await SendAsync(req, ct);
        await EnsureOk(resp, ct);
        return await resp.Content.ReadFromJsonAsync<List<ClientModel>>(cancellationToken: ct) ?? new();
    }

    public async Task ReportAuditAsync(IReadOnlyList<AuditEntry> items, CancellationToken ct)
    {
        if (items.Count == 0)
        {
            return;
        }
        using var req = Authorized(HttpMethod.Post, "api/v1/audit");
        req.Content = JsonContent.Create(new { items });
        using var resp = await SendAsync(req, ct);
        await EnsureOk(resp, ct);
    }

    /// <summary>拉取后台下发、还没执行的指令。</summary>
    public async Task<List<ClientInstruction>> PollInstructionsAsync(CancellationToken ct)
    {
        using var req = Authorized(HttpMethod.Get, "api/v1/client/instructions");
        using var resp = await SendAsync(req, ct);
        await EnsureOk(resp, ct);
        var result = await resp.Content.ReadFromJsonAsync<ClientInstructionPoll>(cancellationToken: ct);
        return result?.Instructions ?? new();
    }

    /// <summary>开始执行前报一声。返回 false 表示已被后台取消，不要再做。</summary>
    public async Task<bool> StartInstructionAsync(int runId, CancellationToken ct)
    {
        using var req = Authorized(HttpMethod.Post, $"api/v1/client/instructions/{runId}/start");
        using var resp = await SendAsync(req, ct);
        await EnsureOk(resp, ct);
        var result = await resp.Content.ReadFromJsonAsync<InstructionStartResult>(cancellationToken: ct);
        return result?.Ok ?? false;
    }

    public async Task FinishInstructionAsync(int runId, bool ok, string answer, string error, string conversationId, CancellationToken ct)
    {
        using var req = Authorized(HttpMethod.Post, $"api/v1/client/instructions/{runId}/finish");
        req.Content = JsonContent.Create(new
        {
            status = ok ? "succeeded" : "failed",
            answer,
            error,
            conversation_id = conversationId,
        });
        using var resp = await SendAsync(req, ct);
        await EnsureOk(resp, ct);
    }
    // ---------- 通道（钉钉） ----------

    public async Task<ChannelState> GetChannelsAsync(CancellationToken ct)
    {
        using var req = Authorized(HttpMethod.Get, "api/v1/client/channels");
        using var resp = await SendAsync(req, ct);
        await EnsureOk(resp, ct);
        return await resp.Content.ReadFromJsonAsync<ChannelState>(cancellationToken: ct) ?? new();
    }

    public async Task<ChannelBindCode> NewChannelBindCodeAsync(CancellationToken ct)
    {
        using var req = Authorized(HttpMethod.Post, "api/v1/client/channels/bind-code");
        using var resp = await SendAsync(req, ct);
        await EnsureOk(resp, ct);
        return await resp.Content.ReadFromJsonAsync<ChannelBindCode>(cancellationToken: ct) ?? new();
    }

    public async Task RemoveChannelBindingAsync(int id, CancellationToken ct)
    {
        using var req = Authorized(HttpMethod.Delete, $"api/v1/client/channels/bindings/{id}");
        using var resp = await SendAsync(req, ct);
        await EnsureOk(resp, ct);
    }

    /// <summary>领钉钉发来的消息：没有就在服务端挂最多 wait 秒，来了立刻返回。</summary>
    public async Task<List<ChannelTask>> PollChannelAsync(int wait, CancellationToken ct)
    {
        using var req = Authorized(HttpMethod.Get, $"api/v1/client/channels/messages?wait={wait}");
        using var resp = await SendAsync(req, ct);
        await EnsureOk(resp, ct);
        var result = await resp.Content.ReadFromJsonAsync<ChannelPoll>(cancellationToken: ct);
        return result?.Messages ?? new();
    }

    public async Task FinishChannelAsync(int id, bool ok, string answer, string error, string conversationId, CancellationToken ct)
    {
        using var req = Authorized(HttpMethod.Post, $"api/v1/client/channels/messages/{id}/finish");
        req.Content = JsonContent.Create(new
        {
            status = ok ? "succeeded" : "failed",
            answer = answer.Length > 20_000 ? answer[..20_000] : answer,
            error = error.Length > 2000 ? error[..2000] : error,
            conversation_id = conversationId,
        });
        using var resp = await SendAsync(req, ct);
        await EnsureOk(resp, ct);
    }

    public async Task<UsageStats> GetUsageAsync(CancellationToken ct, int days = 7)
    {
        using var req = Authorized(HttpMethod.Get, $"api/v1/client/usage?days={days}");
        using var resp = await SendAsync(req, ct);
        await EnsureOk(resp, ct);
        return await resp.Content.ReadFromJsonAsync<UsageStats>(cancellationToken: ct) ?? new UsageStats();
    }

    /// <summary>语音转文字。音频字节直接上传，上游地址和密钥都在服务端，客户端不碰。</summary>
    public async Task<TranscriptResult> TranscribeAsync(byte[] audio, string format, string language, CancellationToken ct)
    {
        using var content = new MultipartFormDataContent();
        var part = new ByteArrayContent(audio);
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        content.Add(part, "audio", $"clip.{format}");
        content.Add(new StringContent(format), "audio_format");
        content.Add(new StringContent(language ?? ""), "language");

        using var req = Authorized(HttpMethod.Post, "api/v1/speech/transcribe");
        req.Content = content;
        using var resp = await SendAsync(req, ct);
        await EnsureOk(resp, ct);
        return await resp.Content.ReadFromJsonAsync<TranscriptResult>(cancellationToken: ct) ?? new TranscriptResult();
    }

    /// <summary>「意见反馈」：问题描述、截图，员工勾了「上传日志」就带上日志包。</summary>
    public async Task SubmitFeedbackAsync(string text, IReadOnlyList<Diagnostics.FeedbackImage> images, byte[]? logs, CancellationToken ct)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(text), "content");
        for (var i = 0; i < images.Count; i++)
        {
            var part = new ByteArrayContent(images[i].Data);
            part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(images[i].MediaType);
            content.Add(part, "images", $"image-{i + 1}.{images[i].Extension}");
        }
        if (logs is { Length: > 0 })
        {
            var part = new ByteArrayContent(logs);
            part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
            content.Add(part, "logs", "logs.zip");
        }
        using var req = Authorized(HttpMethod.Post, "api/v1/client/feedback");
        req.Content = content;
        using var resp = await SendAsync(req, ct);
        await EnsureOk(resp, ct);
    }

    /// <summary>管理员上架的 MCP 连接器。和技能库是两个接口，互不影响。</summary>
    public async Task<List<Flyknit.Core.Mcp.McpVendor>> GetMcpVendorsAsync(CancellationToken ct)
    {
        using var req = Authorized(HttpMethod.Get, "api/v1/client/mcp/vendors");
        using var resp = await SendAsync(req, ct);
        await EnsureOk(resp, ct);
        return await resp.Content.ReadFromJsonAsync<List<Flyknit.Core.Mcp.McpVendor>>(cancellationToken: ct) ?? new();
    }

    /// <summary>管理员发布的专家（发布时的快照）。</summary>
    public async Task<List<Flyknit.Core.Experts.Expert>> GetExpertsAsync(CancellationToken ct)
    {
        using var req = Authorized(HttpMethod.Get, "api/v1/client/experts");
        using var resp = await SendAsync(req, ct);
        await EnsureOk(resp, ct);
        return await resp.Content.ReadFromJsonAsync<List<Flyknit.Core.Experts.Expert>>(cancellationToken: ct) ?? new();
    }

    /// <summary>员工向专家发了一句话：服务端用来算使用量。</summary>
    public async Task ReportExpertUseAsync(string expertId, CancellationToken ct)
    {
        using var req = Authorized(HttpMethod.Post, $"api/v1/client/experts/{Uri.EscapeDataString(expertId)}/use");
        using var resp = await SendAsync(req, ct);
        await EnsureOk(resp, ct);
    }

    // ---------- 派遣专家：子任务登记 ----------

    /// <summary>向服务端登记一次派遣。服务端拒绝（开关关着、专家不可用、次数用完）时抛 DelegationRejectedException。</summary>
    public async Task<Flyknit.Core.Experts.Delegation.SubtaskTicket> RegisterAsync(
        Flyknit.Core.Experts.Delegation.SubtaskRegistration registration, CancellationToken ct)
    {
        using var req = Authorized(HttpMethod.Post, "api/v1/client/subtasks");
        req.Content = JsonContent.Create(new
        {
            run_id = registration.RunId,
            conversation_id = registration.ConversationId,
            expert_id = registration.ExpertId,
            expert_version = registration.ExpertVersion,
            task = registration.Task,
            token_cap = registration.TokenCap,
            timeout_seconds = registration.TimeoutSeconds,
        });
        using var resp = await SendAsync(req, ct);
        if ((int)resp.StatusCode is 403 or 409)
        {
            var (code, message) = await ReadDetailAsync(resp, ct);
            if (code.Length > 0)
            {
                throw new Flyknit.Core.Experts.Delegation.DelegationRejectedException(code, message);
            }
        }
        await EnsureOk(resp, ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var expert = root.TryGetProperty("expert", out var e) ? e : default;
        return new Flyknit.Core.Experts.Delegation.SubtaskTicket(
            root.GetProperty("id").GetString() ?? "",
            root.TryGetProperty("seq", out var seq) ? seq.GetInt32() : 0,
            root.TryGetProperty("token_cap", out var cap) ? cap.GetInt64() : registration.TokenCap,
            root.TryGetProperty("deadline_at", out var dl) && dl.TryGetDateTimeOffset(out var deadline)
                ? deadline : DateTimeOffset.Now.AddSeconds(registration.TimeoutSeconds),
            expert.ValueKind == JsonValueKind.Object && expert.TryGetProperty("published_version", out var pv) && pv.ValueKind == JsonValueKind.Number
                ? pv.GetInt32() : registration.ExpertVersion);
    }

    public async Task<Flyknit.Core.Experts.Delegation.SubtaskServerState> FinishAsync(
        string id, Flyknit.Core.Experts.Delegation.SubtaskFinish finish, CancellationToken ct)
    {
        using var req = Authorized(HttpMethod.Post, $"api/v1/client/subtasks/{Uri.EscapeDataString(id)}/finish");
        req.Content = JsonContent.Create(new JsonObject
        {
            ["status"] = finish.Status,
            ["summary"] = finish.Summary,
            ["result"] = finish.Result?.DeepClone(),
            ["error_code"] = finish.ErrorCode,
            ["error"] = finish.Error,
            ["tool_calls"] = finish.ToolCalls,
            ["local_tokens"] = finish.LocalTokens,
        });
        return await SubtaskStateAsync(req, ct);
    }

    public async Task<Flyknit.Core.Experts.Delegation.SubtaskServerState> CancelAsync(string id, string reason, CancellationToken ct)
    {
        using var req = Authorized(HttpMethod.Post, $"api/v1/client/subtasks/{Uri.EscapeDataString(id)}/cancel");
        req.Content = JsonContent.Create(new { reason });
        return await SubtaskStateAsync(req, ct);
    }

    private async Task<Flyknit.Core.Experts.Delegation.SubtaskServerState> SubtaskStateAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using var resp = await SendAsync(req, ct);
        await EnsureOk(resp, ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var tokens = root.TryGetProperty("tokens", out var t) && t.ValueKind == JsonValueKind.Object ? t : default;
        int Num(JsonElement el, string name) =>
            el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
        string Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        bool Flag(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        return new Flyknit.Core.Experts.Delegation.SubtaskServerState(
            Text("id"), Text("status"), Flag("final"), Flag("already_final"),
            Num(tokens, "prompt"), Num(tokens, "completion"), Num(tokens, "estimated_calls"), Text("error_code"));
    }

    /// <summary>FastAPI 的 {"detail": {"code": "...", "message": "..."}}（detail 也可能只是一句话）。</summary>
    private static async Task<(string Code, string Message)> ReadDetailAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("detail", out var detail))
            {
                if (detail.ValueKind == JsonValueKind.Object)
                {
                    var code = detail.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
                    var message = detail.TryGetProperty("message", out var m) ? m.GetString() ?? code : code;
                    return (code, message);
                }
                if (detail.ValueKind == JsonValueKind.String)
                {
                    return ("rejected", detail.GetString() ?? "");
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or HttpRequestException)
        {
        }
        return ("", "");
    }

    /// <summary>公司技能库列表。</summary>
    public async Task<List<ServerSkill>> GetSkillsAsync(CancellationToken ct)
    {
        using var req = Authorized(HttpMethod.Get, "api/v1/client/skills");
        using var resp = await SendAsync(req, ct);
        await EnsureOk(resp, ct);
        return await resp.Content.ReadFromJsonAsync<List<ServerSkill>>(cancellationToken: ct) ?? new();
    }

    /// <summary>下载技能包到本地文件。</summary>
    public async Task DownloadSkillAsync(string name, string destination, CancellationToken ct)
    {
        using var req = Authorized(HttpMethod.Get, $"api/v1/client/skills/{Uri.EscapeDataString(name)}/download");
        using var resp = await SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ResponseTimeout, ct);
        await EnsureOk(resp, ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(destination);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var buffer = new byte[81920];
        int read;
        while ((read = await ReadIdleAsync(stream, buffer, idle, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    /// <summary>MCP 运行环境清单（便携 Node.js / uv / Python 和 npm、PyPI 镜像地址）。旧服务端没有这个接口，返回 null。</summary>
    public async Task<Flyknit.Core.Mcp.McpRuntimeManifest?> GetMcpRuntimesAsync(CancellationToken ct)
    {
        using var req = Authorized(HttpMethod.Get, "api/v1/client/runtimes");
        using var resp = await SendAsync(req, ct);
        if ((int)resp.StatusCode == 404)
        {
            return null;
        }
        await EnsureOk(resp, ct);
        return await resp.Content.ReadFromJsonAsync<Flyknit.Core.Mcp.McpRuntimeManifest>(cancellationToken: ct);
    }

    /// <summary>
    /// 下载一个运行环境的压缩包。服务端第一次要先去上游拉，响应头可能要等好几分钟。
    /// 返回响应头 X-Runtime-Sha256（服务端知道的话），调用方拿来校验。
    /// </summary>
    public async Task<string?> DownloadMcpRuntimeAsync(string name, string destination, CancellationToken ct)
    {
        using var req = Authorized(HttpMethod.Get, $"api/v1/client/runtimes/{Uri.EscapeDataString(name)}/download");
        using var resp = await SendAsync(req, HttpCompletionOption.ResponseHeadersRead, TimeSpan.FromMinutes(10), ct);
        await EnsureOk(resp, ct);
        var sha = resp.Headers.TryGetValues("X-Runtime-Sha256", out var values) ? values.FirstOrDefault()?.Trim() : null;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(destination);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var buffer = new byte[128 * 1024];
        int read;
        while ((read = await ReadIdleAsync(stream, buffer, idle, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return string.IsNullOrEmpty(sha) ? null : sha;
    }

    /// <summary>有没有比 currentVersion 更新的版本。</summary>
    public async Task<ClientUpdate> CheckUpdateAsync(string currentVersion, CancellationToken ct)
    {
        using var req = Authorized(HttpMethod.Get,
            $"api/v1/client/update?version={Uri.EscapeDataString(currentVersion)}");
        using var resp = await SendAsync(req, ct);
        await EnsureOk(resp, ct);
        return await resp.Content.ReadFromJsonAsync<ClientUpdate>(cancellationToken: ct) ?? new();
    }

    /// <summary>
    /// 下载安装包。一两百 MB，所以边下边写，并通过 progress 回报进度（0~1）。
    /// </summary>
    public async Task DownloadUpdateAsync(string version, string destination,
                                          Action<double>? progress, CancellationToken ct)
    {
        using var req = Authorized(HttpMethod.Get,
            $"api/v1/client/update/download?version={Uri.EscapeDataString(version)}");
        using var resp = await SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ResponseTimeout, ct);
        await EnsureOk(resp, ct);

        var total = resp.Content.Headers.ContentLength ?? 0;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(destination);
        var buffer = new byte[128 * 1024];
        long done = 0;
        int read;
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        while ((read = await ReadIdleAsync(stream, buffer, idle, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            if (total > 0)
            {
                progress?.Invoke((double)done / total);
            }
        }
    }

    public async Task<ChatTurn> CompleteAsync(ChatRequest request, IStreamSink? sink, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = request.Scene,
            ["messages"] = OpenAiSerializer.ToMessages(request.Messages),
            ["stream"] = request.Stream,
        };
        if (!string.IsNullOrEmpty(request.ConversationId))
        {
            body["conversation_id"] = request.ConversationId;
        }
        if (request.Stream)
        {
            // 让模型在流的最后返回 token 用量（OpenAI 兼容接口通用参数，百炼、vLLM 均支持）
            body["stream_options"] = new JsonObject { ["include_usage"] = true };
        }
        if (request.Tools is { Count: > 0 })
        {
            body["tools"] = request.Tools.DeepClone();
            body["tool_choice"] = "auto";
        }
        if (request.Temperature is { } t)
        {
            body["temperature"] = t;
        }
        if (request.MaxTokens is { } max)
        {
            body["max_tokens"] = max;
        }
        if (request.ModelId is { } modelId)
        {
            body["flyknit_model_id"] = modelId;
        }
        if (!string.IsNullOrEmpty(request.SubtaskId))
        {
            body["flyknit_subtask_id"] = request.SubtaskId;
        }
        if (request.ExtraBody is not null)
        {
            foreach (var (key, value) in request.ExtraBody)
            {
                body[key] = value?.DeepClone();
            }
        }

        using var req = Authorized(HttpMethod.Post, "api/v1/chat/completions");
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(request.Stream ? "text/event-stream" : "application/json"));

        HttpResponseMessage resp;
        try
        {
            // 流式：服务端拿到上游响应头就回，等不到就是连接有问题；非流式要等模型整段答完才回
            resp = await SendAsync(req, HttpCompletionOption.ResponseHeadersRead, request.Stream ? ResponseTimeout : RequestTimeout, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new GatewayException($"无法连接 Flyknit 服务器：{ex.Message}", null, ex) { IsNetworkError = true };
        }

        using (resp)
        {
            await EnsureOk(resp, ct);
            var model = resp.Headers.TryGetValues("X-Flyknit-Model", out var values) && values.FirstOrDefault() is { } raw
                ? Uri.UnescapeDataString(raw) // 服务端对中文模型名做了 URL 编码
                : null;
            var context = resp.Headers.TryGetValues("X-Flyknit-Context", out var ctxValues) && int.TryParse(ctxValues.FirstOrDefault(), out var len)
                ? len
                : 0;
            var acc = new SseAccumulator(sink);
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);

            if (!request.Stream)
            {
                idle.CancelAfter(StreamIdleTimeout);
                string text;
                try
                {
                    text = await resp.Content.ReadAsStringAsync(idle.Token);
                }
                catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
                {
                    throw new GatewayException(TimeoutMessage, null, ex) { IsNetworkError = true };
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException)
                {
                    throw new GatewayException(BrokenMessage, null, ex) { IsNetworkError = true };
                }
                try
                {
                    acc.FeedChunk(text);
                }
                catch (JsonException ex)
                {
                    throw new GatewayException(BrokenMessage, null, ex) { IsNetworkError = true };
                }
                return acc.Build(model, context);
            }

            // 每读一行都重新计时：连接悄悄断掉（没有 FIN/RST）时不会一直挂着
            try
            {
                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                while (!acc.Done)
                {
                    idle.CancelAfter(StreamIdleTimeout);
                    var line = await reader.ReadLineAsync(idle.Token);
                    if (line is null)
                    {
                        break;
                    }
                    acc.FeedLine(line);
                }
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new GatewayException(TimeoutMessage, null, ex) { IsNetworkError = true };
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException)
            {
                // HttpIOException 也是 IOException。包成 GatewayException，AgentLoop 才会保存已做完的步骤、按失败结束
                throw new GatewayException(BrokenMessage, null, ex) { IsNetworkError = true };
            }
            // 既没有 [DONE] 也没有 finish_reason：流被截断了，不能把半截当成完整回答
            if (!acc.Done && acc.FinishReason is null)
            {
                throw new GatewayException(TruncatedMessage);
            }
            return acc.Build(model, context);
        }
    }

    /// <summary>文字转向量：POST api/v1/embeddings（OpenAI 兼容），模型由服务端的 embedding 场景决定。</summary>
    public async Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        var input = new JsonArray();
        foreach (var t in texts)
        {
            input.Add(t);
        }
        using var req = Authorized(HttpMethod.Post, "api/v1/embeddings");
        req.Content = new StringContent(new JsonObject { ["model"] = "embedding", ["input"] = input }.ToJsonString(), Encoding.UTF8, "application/json");
        HttpResponseMessage resp;
        try
        {
            resp = await SendAsync(req, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new GatewayException($"无法连接 Flyknit 服务器：{ex.Message}", null, ex);
        }
        using (resp)
        {
            await EnsureOk(resp, ct);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            var vectors = new float[texts.Count][];
            foreach (var item in root.GetProperty("data").EnumerateArray())
            {
                var index = item.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var n) ? n : Array.IndexOf(vectors, null);
                if (index < 0 || index >= vectors.Length)
                {
                    continue;
                }
                vectors[index] = item.GetProperty("embedding").EnumerateArray().Select(v => v.GetSingle()).ToArray();
            }
            if (vectors.Any(v => v is null))
            {
                throw new GatewayException("向量服务返回的条数不对");
            }
            // 优先用上游的模型名：管理员换了模型，旧向量就不能再用
            var model = root.TryGetProperty("model", out var m) && m.GetString() is { Length: > 0 } name
                ? name
                : resp.Headers.TryGetValues("X-Flyknit-Model", out var values) && values.FirstOrDefault() is { } raw ? Uri.UnescapeDataString(raw) : "embedding";
            return new EmbeddingResult(model, vectors);
        }
    }

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
        SendAsync(req, HttpCompletionOption.ResponseContentRead, RequestTimeout, ct);

    /// <summary>发请求，最多等 timeout（ResponseContentRead 时含读完响应体）。超时抛 GatewayException，调用方自己取消的照常抛 OperationCanceledException。</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, HttpCompletionOption option, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await _http.SendAsync(req, option, cts.Token);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new GatewayException(TimeoutMessage, null, ex) { IsNetworkError = true };
        }
    }

    /// <summary>下载时读一块：这么久一点数据都没收到就算超时。</summary>
    private async Task<int> ReadIdleAsync(Stream stream, byte[] buffer, CancellationTokenSource idle, CancellationToken ct)
    {
        idle.CancelAfter(StreamIdleTimeout);
        try
        {
            return await stream.ReadAsync(buffer, idle.Token);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new GatewayException(TimeoutMessage, null, ex) { IsNetworkError = true };
        }
    }

    private HttpRequestMessage Authorized(HttpMethod method, string path)
    {
        if (string.IsNullOrEmpty(_token))
        {
            throw new GatewayException("本机尚未注册到 Flyknit 服务器");
        }
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return req;
    }

    private static async Task EnsureOk(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode)
        {
            return;
        }
        var code = (int)resp.StatusCode;
        string text;
        try
        {
            text = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            text = ""; // 错误体读不出来就只看状态码
        }
        string? message = null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            message = text;
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                // 数组、字符串之类的照原样给
            }
            else if (doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var m))
            {
                message = m.GetString() ?? text;
            }
            else if (doc.RootElement.TryGetProperty("detail", out var detail))
            {
                message = detail.ValueKind == JsonValueKind.String ? detail.GetString() ?? text : detail.ToString();
            }
        }
        catch (JsonException)
        {
            // 非 JSON 错误体（比如 nginx 的 502 网页）：不往界面上塞 HTML，按状态码给一句短话
        }
        throw new GatewayException(string.IsNullOrWhiteSpace(message) ? StatusText(code) : message, code);
    }

    /// <summary>服务端没给可读的错误说明时，按状态码说一句。</summary>
    internal static string StatusText(int code) => code switch
    {
        401 or 403 => $"服务器拒绝了请求（HTTP {code}）",
        404 => $"服务器上没有这个功能（HTTP {code}），可能需要升级服务端",
        413 => $"内容太大，服务器不接收（HTTP {code}）",
        429 => $"请求太频繁或已达用量上限（HTTP {code}），请稍后再试",
        502 or 503 or 504 => $"服务器暂时不可用（HTTP {code}），请稍后再试",
        >= 500 => $"服务器出错（HTTP {code}）",
        _ => $"请求失败（HTTP {code}）",
    };
}
