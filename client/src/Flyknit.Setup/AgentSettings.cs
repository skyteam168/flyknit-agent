using System.Text.Json;
using System.Text.Json.Nodes;

namespace Flyknit.Setup;

/// <summary>运维代理的配置文件（%ProgramData%\Flyknit\agent.json）怎么写。单独拎出来好测。</summary>
public static class AgentSettings
{
    /// <summary>
    /// 在原来的配置上写入这次安装的服务器地址、安装凭证和员工端目录。
    /// 同一台服务器：保留代理身份（agent_id、token），后台里还是同一台、任务记录不断；
    /// 换了服务器：清掉身份，让代理用新凭证重新注册。原来手工装时填的注册密钥留着，凭证失效时还能用它。
    /// </summary>
    public static string Merge(string? existingJson, string serverUrl, string ticket, string clientDir)
    {
        JsonObject config;
        try
        {
            config = existingJson is not null && JsonNode.Parse(existingJson) is JsonObject obj ? obj : new JsonObject();
        }
        catch (JsonException)
        {
            config = new JsonObject();
        }
        var oldServer = ((string?)config["server_url"] ?? "").TrimEnd('/');
        var newServer = serverUrl.TrimEnd('/');
        if (!string.Equals(oldServer, newServer, StringComparison.OrdinalIgnoreCase))
        {
            config["agent_id"] = 0;
            config["token"] = "";
        }
        config["server_url"] = newServer;
        config["ticket"] = ticket;
        config["client_dir"] = clientDir;
        config["enrollment_key"] ??= "";
        config["agent_id"] ??= 0;
        config["token"] ??= "";
        return config.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
