using System.Text.Json;
using Flyknit.Core.Mcp;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>插件带来的 MCP 连接器：来源字段兼容老服务端、本机进程的环境变量走白名单、全局连接器行为不变。</summary>
public class PluginMcpTests
{
    [Fact]
    public void VendorWithoutSourceFieldsIsGlobal()
    {
        // 老服务端：没有来源字段
        var v = JsonSerializer.Deserialize<McpVendor>("""{"id":"internal-db","name":"内部库","transport":"http","url":"https://db/mcp"}""")!;
        Assert.Equal("global", v.SourceType);
        Assert.False(v.IsPlugin);
        Assert.Equal("", v.Plugin);
        Assert.False(McpTemplate.Resolve(v, null).IsolatedEnvironment);
    }

    [Fact]
    public void PluginVendorCarriesItsSourceAndRunsIsolated()
    {
        var v = JsonSerializer.Deserialize<McpVendor>("""
            {"id":"playwright","name":"playwright","transport":"stdio","command":"npx","args":["@playwright/mcp@latest"],
             "source_type":"plugin","plugin":"playwright","source_plugin_version":"unknown",
             "source_manifest":".claude-plugin/plugin.json","source_mcp_config":".mcp.json","source_server_name":"playwright"}
            """)!;
        Assert.True(v.IsPlugin);
        Assert.Equal(("playwright", ".mcp.json", ".claude-plugin/plugin.json"), (v.Plugin, v.SourceMcpConfig, v.SourceManifest));
        Assert.True(McpTemplate.Resolve(v, null).IsolatedEnvironment);
        // 来源变了（同 id 从全局变成插件）要重连
        var global = JsonSerializer.Deserialize<McpVendor>(JsonSerializer.Serialize(v))!;
        global.SourceType = "global";
        Assert.NotEqual(v.ConnectionFingerprint(), global.ConnectionFingerprint());
    }

    [Fact]
    public void PluginProcessesOnlyInheritSafeSystemVariables()
    {
        var env = new Dictionary<string, string?>
        {
            ["Path"] = @"C:\Windows\system32",
            ["SystemRoot"] = @"C:\Windows",
            ["USERPROFILE"] = @"C:\Users\a",
            ["APPDATA"] = @"C:\Users\a\AppData\Roaming",
            ["HTTPS_PROXY"] = "http://proxy.corp:8080",
            ["OPENAI_API_KEY"] = "sk-secret",
            ["GITHUB_TOKEN"] = "ghp_secret",
            ["DB_PASSWORD"] = "pw",
        };
        var runtime = new McpRuntimeEnvironment
        {
            PathDirs = new[] { @"C:\rt\node" },
            Env = new Dictionary<string, string> { ["NPM_CONFIG_REGISTRY"] = "http://mirror/" },
        };
        var vendorEnv = new Dictionary<string, string>
        {
            ["PLAYWRIGHT_BROWSERS_PATH"] = "0",
            ["PATH"] = @"C:\evil",
            ["NODE_OPTIONS"] = "--require evil.js",
            ["npm_config_registry"] = "http://evil/",
            ["HTTPS_PROXY"] = "http://evil:1",
        };
        StdioTransport.ApplyEnvironment(env, runtime, vendorEnv, isolated: true);

        Assert.False(env.ContainsKey("OPENAI_API_KEY"));
        Assert.False(env.ContainsKey("GITHUB_TOKEN"));
        Assert.False(env.ContainsKey("DB_PASSWORD"));
        Assert.Equal(@"C:\Users\a", env["USERPROFILE"]);
        Assert.Equal("http://proxy.corp:8080", env["HTTPS_PROXY"]); // 系统代理沿用，插件改不了
        Assert.StartsWith(@"C:\rt\node", env["Path"]);
        Assert.DoesNotContain(@"C:\evil", env["Path"]);
        Assert.False(env.ContainsKey("NODE_OPTIONS"));
        Assert.Equal("http://mirror/", env["NPM_CONFIG_REGISTRY"]); // 公司镜像（我们自己设的）不被插件覆盖
        Assert.False(env.ContainsKey("npm_config_registry"));
        Assert.Equal("0", env["PLAYWRIGHT_BROWSERS_PATH"]); // 普通变量照常生效
    }

    [Fact]
    public void GlobalConnectorsKeepTheirOldEnvironmentBehavior()
    {
        var env = new Dictionary<string, string?> { ["OPENAI_API_KEY"] = "sk", ["Path"] = "/usr/bin" };
        StdioTransport.ApplyEnvironment(env, null, new Dictionary<string, string> { ["NODE_OPTIONS"] = "--max-old-space-size=4096" });
        Assert.Equal("sk", env["OPENAI_API_KEY"]);
        Assert.Equal("--max-old-space-size=4096", env["NODE_OPTIONS"]);
    }
}
