using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Flyknit.Core.Gateway;

/// <summary>模型写在正文里的一次工具调用。</summary>
public sealed record LeakedToolCall(string Name, JsonObject Arguments);

/// <summary>
/// 识别模型“写进正文里的工具调用”。
///
/// 正常情况下模型的工具调用由上游服务（vLLM、SGLang、云厂商的 OpenAI 兼容接口）解析成结构化的 tool_calls。
/// 但模型原生的调用格式和服务端的解析器对不上时（比如 DeepSeek-V3.2 的 DSML 格式、服务端没开对应的解析器；
/// 或者请求里根本没带工具——对话模式——模型却还是想调用），这些标记就原样出现在回答正文里，用户看到一堆
/// &lt;｜DSML｜invoke name="bash"&gt; 之类的东西。这不是客户端的错误，但客户端要兜住：
/// 能认出来的调用拿回来执行，认不出来的从正文里去掉，不让用户看到这些标记。
///
/// 认得的格式：
/// - DeepSeek-V3.2 DSML：&lt;｜DSML｜function_calls&gt;（也见过 &lt;｜DSML｜calls&gt;）里的 invoke / parameter；
/// - DeepSeek-V3 / R1 的特殊记号：&lt;｜tool▁calls▁begin｜&gt; … &lt;｜tool▁sep｜&gt;名称 ```json {…} ```；
/// - Qwen / Hermes：&lt;tool_call&gt;{"name": …, "arguments": …}&lt;/tool_call&gt;。
/// 竖线既可能是全角“｜”也可能是半角“|”，记号前后可能夹空格。
/// </summary>
public static class ToolMarkup
{
    private const RegexOptions Opts = RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private const string Bar = @"\s*[｜|]\s*";

    /// <summary>任何一种格式的开头，用来判断有没有、以及从哪里开始截掉。</summary>
    private static readonly Regex Start = new(
        $@"<{Bar}DSML{Bar}|<{Bar}tool[▁_ ]calls?[▁_ ]begin{Bar}>|<tool_call>", Opts);

    private static readonly Regex DsmlInvoke = new(
        $@"<{Bar}DSML{Bar}invoke\s+name\s*=\s*""(?<name>[^""]+)""\s*>(?<body>.*?)</{Bar}DSML{Bar}invoke\s*>", Opts);

    private static readonly Regex DsmlParam = new(
        $@"<{Bar}DSML{Bar}parameter\s+name\s*=\s*""(?<name>[^""]+)""(?:\s+string\s*=\s*""(?<string>true|false)"")?\s*>(?<value>.*?)</{Bar}DSML{Bar}parameter\s*>", Opts);

    /// <summary>DSML 整块（含没写完被截断的）。</summary>
    private static readonly Regex DsmlBlock = new(
        $@"<{Bar}DSML{Bar}(?:function_)?calls\s*>.*?(?:</{Bar}DSML{Bar}(?:function_)?calls\s*>|$)", Opts);

    private static readonly Regex DeepSeekCall = new(
        $@"<{Bar}tool[▁_ ]call[▁_ ]begin{Bar}>\s*(?:function)?\s*<{Bar}tool[▁_ ]sep{Bar}>\s*(?<name>[\w.\-]+)\s*(?:```(?:json)?\s*(?<args>.*?)\s*```)?\s*<{Bar}tool[▁_ ]call[▁_ ]end{Bar}>", Opts);

    private static readonly Regex DeepSeekBlock = new(
        $@"<{Bar}tool[▁_ ]calls[▁_ ]begin{Bar}>.*?(?:<{Bar}tool[▁_ ]calls[▁_ ]end{Bar}>|$)", Opts);

    private static readonly Regex HermesCall = new(@"<tool_call>\s*(?<json>.*?)\s*(?:</tool_call>|$)", Opts);

    /// <summary>正文里有没有工具调用标记。</summary>
    public static bool Contains(string? text) => !string.IsNullOrEmpty(text) && Start.IsMatch(text);

    /// <summary>
    /// 把正文里的调用标记拿出来：返回去掉标记后的正文，以及认出来的调用（认不出参数的也算一个调用，参数为空）。
    /// </summary>
    public static (string Text, List<LeakedToolCall> Calls) Extract(string text)
    {
        var calls = new List<LeakedToolCall>();
        if (!Contains(text))
        {
            return (text, calls);
        }

        foreach (Match invoke in DsmlInvoke.Matches(text))
        {
            var args = new JsonObject();
            foreach (Match p in DsmlParam.Matches(invoke.Groups["body"].Value))
            {
                var raw = p.Groups["value"].Value.Trim();
                args[p.Groups["name"].Value.Trim()] = p.Groups["string"].Value.Equals("false", StringComparison.OrdinalIgnoreCase)
                    ? ParseValue(raw)
                    : JsonValue.Create(raw);
            }
            calls.Add(new LeakedToolCall(invoke.Groups["name"].Value.Trim(), args));
        }
        foreach (Match m in DeepSeekCall.Matches(text))
        {
            calls.Add(new LeakedToolCall(m.Groups["name"].Value.Trim(), ParseObject(m.Groups["args"].Value)));
        }
        foreach (Match m in HermesCall.Matches(text))
        {
            if (TryParse(m.Groups["json"].Value) is JsonObject o && o["name"] is JsonValue nv && nv.TryGetValue<string>(out var name) && name.Length > 0)
            {
                var args = o["arguments"] ?? o["parameters"];
                calls.Add(new LeakedToolCall(name, args switch
                {
                    JsonObject obj => (JsonObject)obj.DeepClone(),
                    JsonValue v when v.TryGetValue<string>(out var s) => ParseObject(s),
                    _ => new JsonObject(),
                }));
            }
        }

        var clean = DsmlBlock.Replace(text, "");
        clean = DeepSeekBlock.Replace(clean, "");
        clean = HermesCall.Replace(clean, "");
        // 零散没配对上的记号（比如只吐了一半）：从第一个记号起整段去掉
        if (Start.Match(clean) is { Success: true } rest)
        {
            clean = clean[..rest.Index];
        }
        return (clean.Trim(), calls);
    }

    private static JsonNode? ParseValue(string raw) => TryParse(raw) ?? JsonValue.Create(raw);

    private static JsonObject ParseObject(string raw) => TryParse(raw) as JsonObject ?? new JsonObject();

    private static JsonNode? TryParse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        try
        {
            return JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
