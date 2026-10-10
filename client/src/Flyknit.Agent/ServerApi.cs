using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Flyknit.Agent;

/// <summary>后台下发给这台电脑的一个任务。</summary>
public sealed class AgentRun
{
    [JsonPropertyName("run_id")] public int RunId { get; set; }
    [JsonPropertyName("job_id")] public int JobId { get; set; }
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("params")] public JsonElement Params { get; set; }
}

internal sealed class RegisterResult
{
    [JsonPropertyName("agent_id")] public int AgentId { get; set; }
    [JsonPropertyName("token")] public string Token { get; set; } = "";
}

public sealed class ClientUpdateInfo
{
    [JsonPropertyName("available")] public bool Available { get; set; }
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
}

internal sealed class PollResult
{
    [JsonPropertyName("runs")] public List<AgentRun> Runs { get; set; } = new();
    [JsonPropertyName("poll_after")] public int PollAfter { get; set; } = 30;
}

internal sealed class StartResult
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "";
}

/// <summary>任务执行结果。result 作为采集任务的台账数据回传给后台。</summary>
public sealed class RunResult
{
    public bool Succeeded { get; set; }
    public int ExitCode { get; set; }
    public string Output { get; set; } = "";
    public object? Inventory { get; set; }

    public static RunResult Ok(string output, int exitCode = 0, object? inventory = null) =>
        new() { Succeeded = true, ExitCode = exitCode, Output = output, Inventory = inventory };

    public static RunResult Fail(string output, int exitCode = -1) =>
        new() { Succeeded = false, ExitCode = exitCode, Output = output };
}

/// <summary>和服务端的 /api/v1/agent/* 接口通信。</summary>
public sealed class ServerApi
{
    private readonly HttpClient _http;
    private readonly AgentConfig _config;

    /// <summary>编译时写进程序的版本号（publish -p:Version=...）。</summary>
    public static readonly string Version =
        (typeof(ServerApi).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .FirstOrDefault() as System.Reflection.AssemblyInformationalVersionAttribute)?.InformationalVersion.Split('+')[0] ?? "0.1.0";

    public ServerApi(AgentConfig config)
    {
        _config = config;
        _http = new HttpClient { BaseAddress = new Uri(config.ServerUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(30) };
        if (_config.IsRegistered)
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _config.Token);
        }
    }

    /// <summary>注册（或重装后重新注册）。成功后把身份写回配置。</summary>
    public async Task<bool> RegisterAsync(CancellationToken ct)
    {
        var body = new
        {
            enrollment_key = _config.EnrollmentKey,
            ticket = _config.Ticket,
            machine_guid = MachineIdentity.MachineGuid(),
            machine_name = MachineIdentity.MachineName,
            os_version = MachineIdentity.OsVersion,
            agent_version = Version,
        };
        using var resp = await _http.PostAsJsonAsync("api/v1/agent/register", body, ct);
        if (!resp.IsSuccessStatusCode)
        {
            AgentLog.Error($"注册失败：HTTP {(int)resp.StatusCode} {await Safe(resp, ct)}");
            return false;
        }
        var result = await resp.Content.ReadFromJsonAsync<RegisterResult>(cancellationToken: ct);
        if (result is null || string.IsNullOrEmpty(result.Token))
        {
            return false;
        }
        _config.AgentId = result.AgentId;
        _config.Token = result.Token;
        _config.Save();
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _config.Token);
        AgentLog.Info($"注册成功 agent_id={result.AgentId}");
        return true;
    }

    /// <summary>领任务。返回 null 表示令牌失效（需要重新注册）。</summary>
    public async Task<PollOutcome?> PollAsync(CancellationToken ct)
    {
        var body = new
        {
            machine_name = MachineIdentity.MachineName,
            os_version = MachineIdentity.OsVersion,
            agent_version = Version,
        };
        using var resp = await _http.PostAsJsonAsync("api/v1/agent/poll", body, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            return null; // 令牌没了（服务端重建过库，或代理被删），交给上层重新注册
        }
        if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            // 被后台停用了，当作没任务，等启用
            return new PollOutcome(new List<AgentRun>(), 60);
        }
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<PollResult>(cancellationToken: ct) ?? new PollResult();
        return new PollOutcome(result.Runs, Math.Clamp(result.PollAfter, 5, 600));
    }

    /// <summary>开始执行前报一声。返回 false 表示任务已被取消，不要再做。</summary>
    public async Task<bool> StartAsync(int runId, CancellationToken ct)
    {
        using var resp = await _http.PostAsync($"api/v1/agent/runs/{runId}/start", null, ct);
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<StartResult>(cancellationToken: ct);
        return result?.Ok == true;
    }

    public async Task FinishAsync(int runId, RunResult result, CancellationToken ct)
    {
        var body = new
        {
            status = result.Succeeded ? "succeeded" : "failed",
            exit_code = result.ExitCode,
            output = result.Output,
            result = result.Inventory ?? new { },
        };
        using var resp = await _http.PostAsJsonAsync($"api/v1/agent/runs/{runId}/finish", body, ct);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>下载安装包到本地文件，带 SHA-256 校验由调用方做。</summary>
    public async Task DownloadPackageAsync(int packageId, string destination, CancellationToken ct)
    {
        using var resp = await _http.GetAsync($"api/v1/agent/packages/{packageId}/download",
            HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(destination);
        await stream.CopyToAsync(file, ct);
    }

    /// <summary>员工端有没有新版本（替 Program Files 里的员工端问）。</summary>
    public async Task<ClientUpdateInfo> CheckClientUpdateAsync(string currentVersion, CancellationToken ct)
    {
        using var resp = await _http.GetAsync($"api/v1/agent/client-update?version={Uri.EscapeDataString(currentVersion)}", ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<ClientUpdateInfo>(cancellationToken: ct) ?? new ClientUpdateInfo();
    }

    public async Task DownloadClientUpdateAsync(string version, string destination, CancellationToken ct)
    {
        using var resp = await _http.GetAsync($"api/v1/agent/client-update/download?version={Uri.EscapeDataString(version)}",
            HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(destination);
        await stream.CopyToAsync(file, ct);
    }

    private static async Task<string> Safe(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return await resp.Content.ReadAsStringAsync(ct); }
        catch (Exception) { return ""; }
    }
}

public sealed record PollOutcome(List<AgentRun> Runs, int PollAfterSeconds);
