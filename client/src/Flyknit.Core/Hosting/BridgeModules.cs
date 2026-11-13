using System.Text.Json;

namespace Flyknit.Core.Hosting;

/// <summary>
/// 界面桥的一组方法（一个功能模块一份），见 docs/extension-architecture.md。
/// 宿主的界面桥先按方法名查注册表，查到就交给模块；新功能加界面接口不用再改界面桥本身。
/// 返回值原样序列化给界面；抛出的异常由界面桥按原来的方式回报给界面。
/// </summary>
public interface IBridgeModule
{
    /// <summary>这个模块处理的方法名（全局唯一，建议「功能.动作」）。</summary>
    IReadOnlyCollection<string> Methods { get; }

    Task<object?> HandleAsync(string method, BridgeArgs args, CancellationToken ct);
}

/// <summary>界面传来的参数（一个 JSON 对象）。缺的、类型不对的一律按空值处理，不抛异常。</summary>
public readonly struct BridgeArgs
{
    public BridgeArgs(JsonElement raw) => Raw = raw;

    public JsonElement Raw { get; }

    private bool TryGet(string name, out JsonElement value)
    {
        value = default;
        return Raw.ValueKind == JsonValueKind.Object && Raw.TryGetProperty(name, out value);
    }

    public string Str(string name) => TryGet(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public string? OptStr(string name) => Str(name) is { Length: > 0 } v ? v : null;

    public bool Bool(string name) => TryGet(name, out var v) && v.ValueKind == JsonValueKind.True;

    public int? Int(string name) => TryGet(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    public List<string> StrList(string name) => TryGet(name, out var v) && v.ValueKind == JsonValueKind.Array
        ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
        : new List<string>();

    /// <summary>按类型反序列化某个字段；没有或解析不了时返回 null。</summary>
    public T? Get<T>(string name) where T : class
    {
        if (!TryGet(name, out var v) || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }
        try
        {
            return v.Deserialize<T>();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>方法名 → 模块。注册时方法名重复直接报错（启动时就发现，不留到运行时）。</summary>
public sealed class BridgeModuleRegistry
{
    private readonly Dictionary<string, IBridgeModule> _byMethod = new(StringComparer.Ordinal);

    public BridgeModuleRegistry Register(IBridgeModule module)
    {
        foreach (var method in module.Methods)
        {
            if (!_byMethod.TryAdd(method, module))
            {
                throw new ArgumentException($"界面桥方法重复注册：{method}");
            }
        }
        return this;
    }

    public bool TryGet(string method, out IBridgeModule module) => _byMethod.TryGetValue(method, out module!);

    public IReadOnlyCollection<string> Methods => _byMethod.Keys;
}
