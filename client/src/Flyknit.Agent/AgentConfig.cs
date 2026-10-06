using System.Text.Json;
using System.Text.Json.Serialization;

namespace Flyknit.Agent;

/// <summary>
/// 代理配置，存在 %ProgramData%\Flyknit\agent.json：
/// - server_url、enrollment_key 由安装脚本（组策略）在装机时写入；
/// - agent_id、token 是注册成功后由代理自己补上的身份，重装时会重新注册。
/// </summary>
public sealed class AgentConfig
{
    [JsonPropertyName("server_url")] public string ServerUrl { get; set; } = "";
    [JsonPropertyName("enrollment_key")] public string EnrollmentKey { get; set; } = "";
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
