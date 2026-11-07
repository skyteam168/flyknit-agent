官方 Claude Code 插件的真实 MCP 配置文件（原样拷贝，只取清单和 MCP 配置，测试 PluginMcpResolver 用）：

- playwright：anthropics/claude-plugins-official `external_plugins/playwright`（main）——写法 A（顶层直接是服务名）
- figma：figma/mcp-server-guide @ f2789420e7e8b4e2465dfeb1e12e1fc6a31397e5——写法 B（mcpServers，带 _meta）
- postman：postmanlabs/postman-plugin @ 1fdd3f1e2736c7cb83780a221cb9f83641215ab7——写法 C（plugin.json 的 mcpServers 指向 mcp.claude-code.json）
