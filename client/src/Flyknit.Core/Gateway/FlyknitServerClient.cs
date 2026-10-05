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
}

public sealed class AuditEntry
{
    [JsonPropertyName("conversation_id")] public string ConversationId { get; set; } = "";
    [JsonPropertyName("tool_name")] public string ToolName { get; set; } = "";
    [JsonPropertyName("arguments")] public string Arguments { get; set; } = "";
    [JsonPropertyName("risk")] public string Risk { get; set; } = "auto";
    [JsonPropertyName("decision")] public string Decision { get; set; } = "auto";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
    [JsonPropertyName("occurred_at")] public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.Now;
}

/// <summary>与 Flyknit 服务端通信：模型网关、设备注册、配置、审计。</summary>
public sealed class FlyknitServerClient : IChatGateway
{
    private readonly HttpClient _http;
    private string? _token;

    public FlyknitServerClient(HttpClient http, string serverUrl, string? deviceToken)
    {
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

    public async Task<ChatTurn> CompleteAsync(ChatRequest request, IStreamSink? sink, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = request.Scene,
            ["messages"] = OpenAiSerializer.ToMessages(request.Messages),
            ["stream"] = request.Stream,
        };
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
            var model = resp.Headers.TryGetValues("X-Flyknit-Model", out var values) ? values.FirstOrDefault() : null;
            var acc = new SseAccumulator(sink);

            if (!request.Stream)
            {
                acc.FeedChunk(await resp.Content.ReadAsStringAsync(ct));
                return acc.Build(model);
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
            return acc.Build(model);
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
