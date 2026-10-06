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
}

/// <summary>与 Flyknit 服务端通信：模型网关、设备注册、配置、审计。</summary>
public sealed class FlyknitServerClient : IChatGateway
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

    public bool IsRegistered => !string.IsNullOrEmpty(_token);

    public async Task<DeviceRegistration> RegisterAsync(string enrollmentKey, string uiLanguage, string clientVersion, CancellationToken ct)
    {
        var body = new
        {
            enrollment_key = enrollmentKey,
            machine_name = Environment.MachineName,
            user_name = Environment.UserName,
            os_version = Environment.OSVersion.VersionString,
            client_version = clientVersion,
            ui_language = uiLanguage,
        };
        using var resp = await _http.PostAsJsonAsync("api/v1/devices/register", body, ct);
        await EnsureOk(resp, ct);
        var reg = await resp.Content.ReadFromJsonAsync<DeviceRegistration>(cancellationToken: ct)
                  ?? throw new GatewayException("注册返回为空");
        _token = reg.Token;
        return reg;
    }

    public async Task<ClientConfig> GetConfigAsync(CancellationToken ct)
    {
        using var req = Authorized(HttpMethod.Get, "api/v1/client/config");
        using var resp = await _http.SendAsync(req, ct);
        await EnsureOk(resp, ct);
        return await resp.Content.ReadFromJsonAsync<ClientConfig>(cancellationToken: ct) ?? new ClientConfig();
    }

    /// <summary>
    /// 上报本机信息，供后台做资产台账。注册时采的那一份很快就过时了——IP 跟着
    /// DHCP 变，用户换人登录，客户端会升级——所以每次拉配置后都重报一次。
    /// </summary>
    public async Task ReportMachineAsync(MachineInfo info, CancellationToken ct)
    {
        using var req = Authorized(HttpMethod.Post, "api/v1/devices/heartbeat");
        req.Content = JsonContent.Create(info);
        using var resp = await _http.SendAsync(req, ct);
        await EnsureOk(resp, ct);
    }

    /// <summary>输入框可选择的模型。</summary>
    public async Task<List<ClientModel>> GetModelsAsync(CancellationToken ct)
    {
        using var req = Authorized(HttpMethod.Get, "api/v1/client/models");
        using var resp = await _http.SendAsync(req, ct);
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
        using var resp = await _http.SendAsync(req, ct);
        await EnsureOk(resp, ct);
    }

    /// <summary>本机今天和最近几天的 token 用量。</summary>
    public async Task<UsageStats> GetUsageAsync(CancellationToken ct, int days = 7)
    {
        using var req = Authorized(HttpMethod.Get, $"api/v1/client/usage?days={days}");
        using var resp = await _http.SendAsync(req, ct);
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
        using var resp = await _http.SendAsync(req, ct);
        await EnsureOk(resp, ct);
        return await resp.Content.ReadFromJsonAsync<TranscriptResult>(cancellationToken: ct) ?? new TranscriptResult();
    }

    /// <summary>公司技能库列表。</summary>
    public async Task<List<ServerSkill>> GetSkillsAsync(CancellationToken ct)
    {
        using var req = Authorized(HttpMethod.Get, "api/v1/client/skills");
        using var resp = await _http.SendAsync(req, ct);
        await EnsureOk(resp, ct);
        return await resp.Content.ReadFromJsonAsync<List<ServerSkill>>(cancellationToken: ct) ?? new();
    }

    /// <summary>下载技能包到本地文件。</summary>
    public async Task DownloadSkillAsync(string name, string destination, CancellationToken ct)
    {
        using var req = Authorized(HttpMethod.Get, $"api/v1/client/skills/{Uri.EscapeDataString(name)}/download");
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureOk(resp, ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(destination);
        await stream.CopyToAsync(file, ct);
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
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new GatewayException($"无法连接 Flyknit 服务器：{ex.Message}", null, ex);
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

            if (!request.Stream)
            {
                acc.FeedChunk(await resp.Content.ReadAsStringAsync(ct));
                return acc.Build(model, context);
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (!acc.Done)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null)
                {
                    break;
                }
                acc.FeedLine(line);
            }
            return acc.Build(model, context);
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
        var text = await resp.Content.ReadAsStringAsync(ct);
        var message = text;
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("error", out var err) && err.TryGetProperty("message", out var m))
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
            // 非 JSON 错误体，原样返回
        }
        throw new GatewayException(string.IsNullOrWhiteSpace(message) ? $"HTTP {(int)resp.StatusCode}" : message, (int)resp.StatusCode);
    }
}
