using System.Text;
using Flyknit.Core.Chat;

namespace Flyknit.Core.Storage;

/// <summary>
/// 把一个对话导出成 Markdown（输入框里的 /export）。
///
/// 只导出员工看得到的东西：员工说的话、AI 的回答、AI 用了哪些工具（只写工具名，不写参数和输出——
/// 那里面可能有大段文件内容），附件和产出文件只列名字。
/// 不导出：系统提示词、每轮附带的参考资料（TurnContext：技能、当前时间、专家委派说明）、思考过程。
/// </summary>
public static class ConversationExport
{
    public static string ToMarkdown(string title, IEnumerable<ChatMessage> messages, DateTimeOffset exportedAt)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(string.IsNullOrWhiteSpace(title) ? "对话" : title.Trim()).Append("\n\n");
        sb.Append("> 从 FlyknitBuddy 导出于 ").Append(exportedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")).Append("\n");

        var pendingTools = new List<string>();
        void FlushTools()
        {
            if (pendingTools.Count == 0)
            {
                return;
            }
            // 连续的工具调用合成一行：「用了：read_file ×3、run_shell」
            var grouped = pendingTools.GroupBy(n => n).Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key);
            sb.Append("\n*用了：").Append(string.Join("、", grouped)).Append("*\n");
            pendingTools.Clear();
        }

        foreach (var m in messages)
        {
            switch (m.Role)
            {
                case ChatRole.User:
                    // 续跑标记这类不是员工说的话，跳过
                    if (m.Content == Context.ContextManager.ContinueMarker)
                    {
                        continue;
                    }
                    FlushTools();
                    sb.Append("\n## 我\n\n").Append(m.Content.Trim()).Append('\n');
                    if (m.Attachments.Count > 0)
                    {
                        sb.Append("\n附件：").Append(string.Join("、", m.Attachments.Select(a => a.FileName))).Append('\n');
                    }
                    break;
                case ChatRole.Assistant:
                    pendingTools.AddRange(m.ToolCalls.Select(c => c.Name));
                    if (m.Content.Trim().Length == 0)
                    {
                        continue; // 只有工具调用的中间步骤
                    }
                    FlushTools();
                    sb.Append("\n## FlyknitBuddy\n\n").Append(m.Content.Trim()).Append('\n');
                    if (m.Outputs.Count > 0)
                    {
                        sb.Append("\n产出文件：").Append(string.Join("、", m.Outputs.Select(NameOf))).Append('\n');
                    }
                    break;
            }
        }
        FlushTools();
        return sb.ToString();
    }

    /// <summary>只要文件名：路径是 Windows 写法（D:\\a\\b.xlsx），在哪个系统上跑都按两种分隔符切。</summary>
    private static string NameOf(string path) => path.Split('\\', '/').Last();

    /// <summary>导出文件名：「对话导出-标题-20261112-1530.md」，去掉 Windows 文件名里不能用的字符，标题太长截断。</summary>
    public static string FileName(string title, DateTimeOffset at)
    {
        var name = string.IsNullOrWhiteSpace(title) ? "对话" : title.Trim();
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }).ToHashSet();
        name = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim(' ', '.');
        if (name.Length > 40)
        {
            name = name[..40];
        }
        if (name.Length == 0)
        {
            name = "对话";
        }
        return $"对话导出-{name}-{at.ToLocalTime():yyyyMMdd-HHmm}.md";
    }

    /// <summary>文件已经有了就加 (2)、(3)……，不覆盖员工的文件。</summary>
    public static string UniquePath(string dir, string fileName)
    {
        var path = Path.Combine(dir, fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 2; File.Exists(path); i++)
        {
            path = Path.Combine(dir, $"{stem} ({i}){ext}");
        }
        return path;
    }
}
