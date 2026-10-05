using Flyknit.Core.Chat;

namespace Flyknit.Core.Context;

/// <summary>
/// 不依赖分词器的 token 估算：中日韩字符约 0.75 token/字，其他字符约 0.3 token/字（≈ 3.3 字符/token）。
/// 只用于决定何时压缩上下文，模型返回真实用量后以真实用量为准。
/// </summary>
public static class TokenEstimator
{
    public const int PerMessageOverhead = 4;
    public const int PerImage = 1200;

    public static int Estimate(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }
        double tokens = 0;
        foreach (var ch in text)
        {
            tokens += IsCjk(ch) ? 0.75 : 0.3;
        }
        return (int)Math.Ceiling(tokens);
    }

    public static int Estimate(ChatMessage m)
    {
        var n = PerMessageOverhead + Estimate(m.Content);
        foreach (var call in m.ToolCalls)
        {
            n += Estimate(call.Name) + Estimate(call.ArgumentsJson) + 4;
        }
        foreach (var a in m.Attachments)
        {
            n += a.IsImage ? PerImage : 20; // 非图片附件只发送路径
        }
        return n;
    }

    public static int Estimate(IEnumerable<ChatMessage> messages) => messages.Sum(Estimate);

    private static bool IsCjk(char c) =>
        c is >= '⺀' and <= '鿿' or >= '가' and <= '힯' or >= '豈' and <= '﫿' or >= '＀' and <= '￯';
}

/// <summary>轻量的文本相似度（字符二元组），中文、越南语、英文都适用，用于记忆检索与去重。</summary>
public static class TextSimilarity
{
    public static HashSet<string> Grams(string text)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var norm = Normalize(text);
        for (var i = 0; i + 1 < norm.Length; i++)
        {
            if (norm[i] == ' ' && norm[i + 1] == ' ')
            {
                continue;
            }
            set.Add(norm.Substring(i, 2));
        }
        if (norm.Length == 1)
        {
            set.Add(norm);
        }
        return set;
    }

    /// <summary>Jaccard 相似度，0..1。</summary>
    public static double Jaccard(string a, string b)
    {
        var ga = Grams(a);
        var gb = Grams(b);
        if (ga.Count == 0 || gb.Count == 0)
        {
            return 0;
        }
        var inter = ga.Count(gb.Contains);
        return (double)inter / (ga.Count + gb.Count - inter);
    }

    /// <summary>查询词在文档中的覆盖率，0..1（查询越多的片段出现在文档中，得分越高）。</summary>
    public static double Coverage(string query, string document)
    {
        var gq = Grams(query);
        if (gq.Count == 0)
        {
            return 0;
        }
        var gd = Grams(document);
        return (double)gq.Count(gd.Contains) / gq.Count;
    }

    /// <summary>余弦式相关度，0..1，长短文本之间比较时比 Jaccard 更稳定。</summary>
    public static double Relevance(string query, string document)
    {
        var gq = Grams(query);
        var gd = Grams(document);
        if (gq.Count == 0 || gd.Count == 0)
        {
            return 0;
        }
        var inter = gq.Count(gd.Contains);
        return inter / Math.Sqrt((double)gq.Count * gd.Count);
    }

    public static string Normalize(string text)
    {
        var chars = text.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray();
        return System.Text.RegularExpressions.Regex.Replace(new string(chars), @"\s+", " ").Trim();
    }
}
