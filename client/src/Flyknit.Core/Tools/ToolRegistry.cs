using System.Text.Json.Nodes;

namespace Flyknit.Core.Tools;

public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.Ordinal);

    public ToolRegistry Add(ITool tool)
    {
        _tools[tool.Name] = tool;
        return this;
    }

    public ITool? Get(string name) => _tools.TryGetValue(name, out var t) ? t : null;

    public IReadOnlyCollection<ITool> All => _tools.Values;

    /// <summary>OpenAI tools 字段。</summary>
    public JsonArray ToOpenAiTools()
    {
        var array = new JsonArray();
        foreach (var t in _tools.Values)
        {
            array.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = t.Parameters.DeepClone(),
                },
            });
        }
        return array;
    }

    /// <summary>与平台无关的内置工具。Windows 专用工具（打开软件等）由客户端另行注册。</summary>
    public static ToolRegistry CreateDefault() => new ToolRegistry()
        .Add(new ReadFileTool())
        .Add(new ListDirTool())
        .Add(new SearchFilesTool())
        .Add(new WriteFileTool())
        .Add(new DeletePathTool())
        .Add(new RunShellTool())
        .Add(new UpdatePlanTool())
        .Add(new MemoryWriteTool())
        .Add(new LoadSkillTool());
}
