using System.Text.Json.Serialization;

namespace Flyknit.Core.Browser;

/// <summary>网页上一个能操作的元素（链接、按钮、输入框、下拉框……）。Index 是这次读页面时编的号，点击、填写都按它来。</summary>
public sealed class BrowserElement
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("tag")] public string Tag { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("role")] public string Role { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("href")] public string? Href { get; set; }
    [JsonPropertyName("value")] public string? Value { get; set; }
    [JsonPropertyName("options")] public List<string>? Options { get; set; }
    [JsonPropertyName("inForm")] public bool InForm { get; set; }

    /// <summary>给模型看的一行：[3] 按钮「提交」 / [5] 输入框(text) name=qty 「数量」 = 12</summary>
    public string Describe()
    {
        var kind = Tag switch
        {
            "a" => "链接",
            "button" => "按钮",
            "select" => "下拉框",
            "textarea" => "多行输入框",
            "input" when Type is "checkbox" => "勾选框",
            "input" when Type is "radio" => "单选",
            "input" when Type is "submit" or "button" => "按钮",
            "input" => $"输入框({(Type.Length > 0 ? Type : "text")})",
            _ => Role.Length > 0 ? Role : Tag,
        };
        var s = $"[{Index}] {kind}";
        if (Name.Length > 0) s += $" name={Name}";
        if (Label.Length > 0) s += $"「{Label}」";
        if (Value is { Length: > 0 }) s += $" = {Value}";
        if (Options is { Count: > 0 }) s += $" 选项：{string.Join(" / ", Options.Take(15))}";
        if (Href is { Length: > 0 } && Tag == "a") s += $" → {Href}";
        return s;
    }
}

/// <summary>读一次页面的结果：地址、标题、正文（截短）、能操作的元素。</summary>
public sealed class BrowserPage
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("elements")] public List<BrowserElement> Elements { get; set; } = new();

    public BrowserElement? Element(int index) => Elements.FirstOrDefault(e => e.Index == index);
}

/// <summary>
/// 浏览器驱动：客户端用一个看得见的 WebView2 窗口实现（员工能看着 AI 操作，也能自己点）。
/// 每个操作之后都会重新读页面，LastPage 是最近一次读到的（工具判定风险时用它看要点的是什么）。
/// </summary>
public interface IBrowserDriver
{
    BrowserPage? LastPage { get; }

    Task<BrowserPage> OpenAsync(string url, CancellationToken ct);
    Task<BrowserPage> ReadAsync(CancellationToken ct);

    /// <summary>执行一段页面脚本（BrowserScripts 里的），返回脚本的字符串结果，然后重新读页面。</summary>
    Task<(string Result, BrowserPage Page)> RunAsync(string script, string action, CancellationToken ct);
}
