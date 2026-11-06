using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Flyknit.Core.Commands;

/// <summary>
/// 快捷指令：输入框里打 / 选出来的提示词模板（同 Claude Code 的自定义 slash command）。
/// IT 在后台发布的全公司通用；员工自己加的只存本机（commands.json）。只是一段文字，不带任何权限。
/// </summary>
public sealed record SlashCommand
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("description")] public string Description { get; init; } = "";
    [JsonPropertyName("template")] public string Template { get; init; } = "";
}

/// <summary>IT 发布的这一批（服务端 GET /api/v1/client/commands 的返回）。</summary>
public sealed class SlashCommandList
{
    [JsonPropertyName("items")] public List<SlashCommand> Items { get; set; } = new();
}

/// <summary>员工自己的快捷指令：读写本机的 commands.json，名字规则和后台一样。</summary>
public sealed class SlashCommandStore
{
    public const int MaxCommands = 100;
    private static readonly Regex NamePattern = new(@"^[\w\-一-鿿]{1,20}$", RegexOptions.Compiled);

    /// <summary>客户端自带的指令名，不能占用。</summary>
    public static readonly IReadOnlySet<string> Reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "btw" };

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly string _path;
    private readonly object _gate = new();

    public SlashCommandStore(string path) => _path = path;

    public IReadOnlyList<SlashCommand> Load()
    {
        lock (_gate)
        {
            try
            {
                return File.Exists(_path)
                    ? JsonSerializer.Deserialize<List<SlashCommand>>(File.ReadAllText(_path)) ?? new List<SlashCommand>()
                    : new List<SlashCommand>();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return new List<SlashCommand>();
            }
        }
    }

    /// <summary>保存。名字不合规、重复、模板为空时返回原因，不保存。</summary>
    public string? Save(IReadOnlyList<SlashCommand> commands)
    {
        var clean = commands.Select(c => c with
        {
            Name = c.Name.Trim().TrimStart('/'),
            Title = c.Title.Trim(),
            Description = c.Description.Trim(),
            Template = c.Template.Trim(),
        }).ToList();
        if (Validate(clean) is { } problem)
        {
            return problem;
        }
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(clean, Json), new UTF8Encoding(false));
            File.Move(temp, _path, overwrite: true);
        }
        return null;
    }

    public static string? Validate(IReadOnlyList<SlashCommand> commands)
    {
        if (commands.Count > MaxCommands)
        {
            return $"最多 {MaxCommands} 条";
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in commands)
        {
            if (!NamePattern.IsMatch(c.Name))
            {
                return $"指令名「{c.Name}」只能用中文、字母、数字、- 和 _，不超过 20 个字，不能有空格";
            }
            if (Reserved.Contains(c.Name))
            {
                return $"/{c.Name} 是自带的指令，换个名字";
            }
            if (!seen.Add(c.Name))
            {
                return $"指令名 /{c.Name} 重复";
            }
            if (c.Template.Length == 0 || c.Template.Length > 4000)
            {
                return $"/{c.Name} 的模板不能为空，也不能超过 4000 字";
            }
        }
        return null;
    }
}
