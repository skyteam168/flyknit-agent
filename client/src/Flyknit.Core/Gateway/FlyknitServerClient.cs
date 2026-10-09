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

/// <summary>与 Flyknit 服务端通信：模型网关、设备注册、配置、审计。</summary>
public sealed class FlyknitServerClient : IChatGateway, IEmbeddingGateway
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

    public Task<DeviceRegistration> RegisterAsync(string enrollmentKey, string uiLanguage, string clientVersion, CancellationToken ct) =>
        PostRegistrationAsync(new
        {
            enrollment_key = enrollmentKey,
            machine_name = Environment.MachineName,
            user_name = Environment.UserName,
            os_version = Environment.OSVersion.VersionString,
            client_version = clientVersion,
            ui_language = uiLanguage,
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
        }, ct);

    /// <summary>用户协议和隐私政策（登录界面显示，还没登录所以不带令牌）。原样返回服务端的 JSON。</summary>
    public async Task<string> GetLegalAsync(CancellationToken ct)
    {
        using var resp = await _http.GetAsync("api/v1/legal", ct);
        await EnsureOk(resp, ct);
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private async Task<DeviceRegistration> PostRegistrationAsync(object body, CancellationToken ct)
    {
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
    /// 长轮询等配置变更：带上手里的版本号，服务端一旦发现 IT 改了配置就立刻返回新版本号，
    /// 否则挂起到服务端超时（约 25 秒）再返回。据此做到近乎即时地拉取新配置。
    /// </summary>
    public async Task<int> WaitForConfigChangeAsync(int revision, CancellationToken ct)
    {
        using var req = Authorized(HttpMethod.Get, $"api/v1/client/config/wait?rev={revision}");
        using var resp = await _http.SendAsync(req, ct);
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

    /// <summary>拉取后台下发、还没执行的指令。</summary>
    public async Task<List<ClientInstruction>> PollInstructionsAsync(CancellationToken ct)
    {
        using var req = Authorized(HttpMethod.Get, "api/v1/client/instructions");
        using var resp = await _http.SendAsync(req, ct);
        await EnsureOk(resp, ct);
        var result = await resp.Content.ReadFromJsonAsync<ClientInstructionPoll>(cancellationToken: ct);
        return result?.Instructions ?? new();
    }

    /// <summary>开始执行前报一声。返回 false 表示已被后台取消，不要再做。</summary>
    public async Task<bool> StartInstructionAsync(int runId, CancellationToken ct)
    {
        using var req = Authorized(HttpMethod.Post, $"api/v1/client/instructions/{runId}/start");
        using var resp = await _http.SendAsync(req, ct);
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
        using var resp = await _http.SendAsync(req, ct);
        await EnsureOk(resp, ct);
    }
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

    /// <summary>管理员上架的 MCP 连接器。和技能库是两个接口，互不影响。</summary>
    public async Task<List<Flyknit.Core.Mcp.McpVendor>> GetMcpVendorsAsync(CancellationToken ct)
    {
        using var req = Authorized(HttpMethod.Get, "api/v1/client/mcp/vendors");
        using var resp = await _http.SendAsync(req, ct);
        await EnsureOk(resp, ct);
        return await resp.Content.ReadFromJsonAsync<List<Flyknit.Core.Mcp.McpVendor>>(cancellationToken: ct) ?? new();
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

    /// <summary>有没有比 currentVersion 更新的版本。</summary>
    public async Task<ClientUpdate> CheckUpdateAsync(string currentVersion, CancellationToken ct)
    {
        using var req = Authorized(HttpMethod.Get,
            $"api/v1/client/update?version={Uri.EscapeDataString(currentVersion)}");
        using var resp = await _http.SendAsync(req, ct);
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
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureOk(resp, ct);

        var total = resp.Content.Headers.ContentLength ?? 0;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(destination);
        var buffer = new byte[128 * 1024];
        long done = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
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
            resp = await _http.SendAsync(req, ct);
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
