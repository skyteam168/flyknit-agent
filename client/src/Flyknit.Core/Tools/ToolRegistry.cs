using System.Text.Json.Nodes;

namespace Flyknit.Core.Tools;

/// <summary>
/// Agent 能用的工具。内置工具启动时注册一次；MCP 工具随连接器连上、断开随时增减，
/// 所以读写都加锁，正在跑的任务拿到的是开始那一刻的快照。
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public ToolRegistry Add(ITool tool)
    {
        lock (_gate)
        {
            _tools[tool.Name] = tool;
        }
        return this;
    }

    public ITool? Get(string name)
    {
        lock (_gate)
        {
            return _tools.TryGetValue(name, out var t) ? t : null;
        }
    }

    public IReadOnlyCollection<ITool> All
    {
        get
        {
            lock (_gate)
            {
                return _tools.Values.ToList();
            }
        }
    }

    /// <summary>去掉满足条件的工具（断开一个 MCP 连接器时用），返回去掉了几个。</summary>
    public int RemoveWhere(Func<ITool, bool> predicate)
    {
        lock (_gate)
        {
            var doomed = _tools.Values.Where(predicate).Select(t => t.Name).ToList();
            foreach (var name in doomed)
            {
                _tools.Remove(name);
            }
            return doomed.Count;
        }
    }

    /// <summary>
    /// 把一组工具整体换掉：先去掉满足 predicate 的，再加上新的，中间不会被别人看到「一个都没有」的状态。
    /// 已经存在的同名工具不会被 MCP 工具顶掉——内置工具的名字里没有 mcp__ 前缀，本来也撞不上。
    /// </summary>
    public void Replace(Func<ITool, bool> predicate, IEnumerable<ITool> tools)
    {
        lock (_gate)
        {
            foreach (var name in _tools.Values.Where(predicate).Select(t => t.Name).ToList())
            {
                _tools.Remove(name);
            }
            foreach (var tool in tools)
            {
                _tools.TryAdd(tool.Name, tool);
            }
        }
    }

    /// <summary>OpenAI tools 字段。</summary>
    public JsonArray ToOpenAiTools()
    {
        var array = new JsonArray();
        foreach (var t in All)
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
        .Add(new MemorySearchTool())
        .Add(new LoadSkillTool())
        .Add(new SearchSkillsTool());
}
