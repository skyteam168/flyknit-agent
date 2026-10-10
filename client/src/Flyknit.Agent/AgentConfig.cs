using System.Text.Json;
using System.Text.Json.Serialization;

namespace Flyknit.Agent;

/// <summary>
/// 代理配置，存在 %ProgramData%\Flyknit\agent.json：
/// - server_url、enrollment_key 由安装脚本（组策略）在装机时写入；
///   用后台下载的安装程序（Setup.exe）装的，写的是 ticket（安装凭证）和 client_dir（员工端目录）；
/// - agent_id、token 是注册成功后由代理自己补上的身份，重装时会重新注册。
/// </summary>
public sealed class AgentConfig
{
    [JsonPropertyName("server_url")] public string ServerUrl { get; set; } = "";
    [JsonPropertyName("enrollment_key")] public string EnrollmentKey { get; set; } = "";
    /// <summary>安装程序里带的安装凭证（和员工端登录用的同一张），有它就不用注册密钥。</summary>
    [JsonPropertyName("ticket")] public string Ticket { get; set; } = "";
    /// <summary>安装程序装的员工端目录。代理负责它的升级（员工账号写不了 Program Files）。</summary>
    [JsonPropertyName("client_dir")] public string ClientDir { get; set; } = "";
    [JsonPropertyName("agent_id")] public int AgentId { get; set; }
    [JsonPropertyName("token")] public string Token { get; set; } = "";

    [JsonIgnore] public bool IsRegistered => AgentId > 0 && !string.IsNullOrEmpty(Token);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static AgentConfig Load()
    {
        try
        {
            if (File.Exists(AgentPaths.Config))
            {
                var json = File.ReadAllText(AgentPaths.Config);
                if (JsonSerializer.Deserialize<AgentConfig>(json) is { } cfg)
                {
                    return cfg;
                }
            }
        }
        catch (Exception ex)
        {
            AgentLog.Error("读取配置失败", ex);
        }
        return new AgentConfig();
    }

    public void Save()
    {
        try
        {
            AgentPaths.EnsureDirectories();
            var tmp = AgentPaths.Config + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
            File.Move(tmp, AgentPaths.Config, overwrite: true);
            AgentPaths.RestrictConfig();
        }
        catch (Exception ex)
        {
            AgentLog.Error("保存配置失败", ex);
        }
    }
}
