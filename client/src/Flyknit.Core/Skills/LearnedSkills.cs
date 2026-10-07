using System.Text;
using System.Text.Json;
using Flyknit.Core.Chat;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Flyknit.Core.Skills;

/// <summary>复盘沉淀出的技能所处的阶段。</summary>
public static class LearnedSkillStatus
{
    /// <summary>刚总结出来，还没被实际用成功过。会列给模型，但标着“试用中”。</summary>
    public const string Candidate = "candidate";

    /// <summary>被实际使用并成功过，正式启用。</summary>
    public const string Active = "active";

    /// <summary>用了屡次失败，自动退役：不再列给模型，文件保留，可以手动改回或删除。</summary>
    public const string Retired = "retired";

    /// <summary>frontmatter 里没写的（以前版本沉淀的）当作已启用。</summary>
    public static string Of(SkillInfo skill) =>
        skill.Meta.TryGetValue("status", out var s) && s.Trim().ToLowerInvariant() is Candidate or Active or Retired ? s.Trim().ToLowerInvariant() : Active;

    public static int VersionOf(SkillInfo skill) =>
        skill.Meta.TryGetValue("version", out var v) && int.TryParse(v.Trim().TrimStart('v', 'V'), out var n) && n > 0 ? n : 1;
}

/// <summary>某个学习技能当前版本的使用情况。</summary>
public sealed record SkillStats(int Uses, int Successes, int Failures);

/// <summary>
/// 学习技能的生命周期：
/// - 同类任务成功多次后先写成<b>候选</b>（status: candidate）；
/// - 候选被实际使用、任务完成且用户没点踩，转为<b>启用</b>；
/// - 用了屡次失败（中途被停、被暂停、被点踩）自动<b>退役</b>，不再列给模型；
/// - 复盘再次给出同一技能时：候选的直接修订；已启用且没出过问题的不动；出过问题或已退役的写成新版本、重新试用。
///   旧版本留在 versions/ 下（最多 5 个），可以对照、恢复。
/// 每次使用记在 skill_usage 表（和会话在同一个库），按“这次用的是哪个版本”统计。
/// </summary>
public sealed class LearnedSkills
{
    public const int KeepVersions = 5;

    /// <summary>候选：失败这么多次、一次没成功过，就退役。</summary>
    public const int CandidateRetireFailures = 2;

    /// <summary>已启用：失败这么多次、而且失败比成功多，就退役。</summary>
    public const int ActiveRetireFailures = 3;

    private readonly string? _connectionString;

    public string Directory { get; }

    public Func<DateTime> Clock { get; init; } = () => DateTime.Now;

    /// <param name="directory">learned 技能目录。</param>
    /// <param name="databasePath">记使用情况的数据库；为 null 时不记（只管写文件）。</param>
    public LearnedSkills(string directory, string? databasePath)
    {
        Directory = directory;
        if (databasePath is not null)
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
            _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, DefaultTimeout = 5, Pooling = false }.ToString();
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS skill_usage (
                    message_id TEXT NOT NULL,
                    conversation_id TEXT NOT NULL,
                    skill TEXT NOT NULL,
                    version INTEGER NOT NULL,
                    ok INTEGER NOT NULL,
                    feedback INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL,
                    PRIMARY KEY (message_id, skill)
                );
                CREATE INDEX IF NOT EXISTS ix_skill_usage_skill ON skill_usage(skill, version);
                """;
            cmd.ExecuteNonQuery();
        }
    }

    public static string? NormalizeName(string name)
    {
        var n = Regex.Replace(name.Trim().ToLowerInvariant(), @"[^a-z0-9\-]+", "-").Trim('-');
        if (n.Length < 3)
        {
            return null;
        }
        return n.Length > 48 ? n[..48].Trim('-') : n;
    }

    /// <summary>
    /// 复盘给出的技能：按上面的规则新建、修订或写成新版本。没有改动（已启用且一直好用）或不能写（同名的是用户自己的技能）时返回 null。
    /// </summary>
    public string? Propose(string rawName, string description, string body)
    {
        var name = NormalizeName(rawName);
        description = description.Replace('\n', ' ').Replace("\"", "'").Trim();
        if (name is null || description.Length == 0 || body.Trim().Length < 20)
        {
            return null;
        }
        var dir = Path.Combine(Directory, name);
        var file = Path.Combine(dir, "SKILL.md");
        var version = 1;
        if (File.Exists(file))
        {
            var existing = SkillCatalog.TryParse(file, SkillSource.Learned);
            if (existing is null || !File.ReadAllText(file).Contains("source: learned"))
            {
                return null; // 用户自己写的同名技能不覆盖
            }
            var status = LearnedSkillStatus.Of(existing);
            var current = LearnedSkillStatus.VersionOf(existing);
            if (status == LearnedSkillStatus.Active && Stats(name, current).Failures == 0)
            {
                return null; // 好用的不去动它
            }
            Archive(dir, file, current);
            version = current + 1;
        }
        System.IO.Directory.CreateDirectory(dir);
        Write(file, name, description, body.Trim(), LearnedSkillStatus.Candidate, version);
        return name;
    }

    /// <summary>
    /// 一轮任务结束：记下这轮加载过的学习技能用得怎么样，再看要不要转正或退役。
    /// 返回状态有变化的技能（调用方据此刷新技能清单）。
    /// </summary>
    public List<(string Name, string Status)> RecordRun(string conversationId, string messageId, IEnumerable<SkillInfo> used, bool ok)
    {
        var skills = used.Where(s => s.IsLearned).DistinctBy(s => s.Name).ToList();
        if (_connectionString is null || skills.Count == 0)
        {
            return new();
        }
        using (var c = Open())
        {
            foreach (var s in skills)
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = """
                    INSERT OR REPLACE INTO skill_usage(message_id, conversation_id, skill, version, ok, feedback, created_at)
                    VALUES ($m, $c, $s, $v, $ok, 0, $t)
                    """;
                cmd.Parameters.AddWithValue("$m", messageId);
                cmd.Parameters.AddWithValue("$c", conversationId);
                cmd.Parameters.AddWithValue("$s", s.Name);
                cmd.Parameters.AddWithValue("$v", LearnedSkillStatus.VersionOf(s));
                cmd.Parameters.AddWithValue("$ok", ok ? 1 : 0);
                cmd.Parameters.AddWithValue("$t", Clock().ToString("O"));
                cmd.ExecuteNonQuery();
            }
        }
        return skills.Select(Review).OfType<(string, string)>().ToList();
    }

    /// <summary>这一轮用 load_skill 加载过的技能名。</summary>
    public static List<string> LoadedIn(IEnumerable<ChatMessage> messages)
    {
        var names = new List<string>();
        foreach (var call in messages.SelectMany(m => m.ToolCalls).Where(c => c.Name == "load_skill"))
        {
            try
            {
                using var doc = JsonDocument.Parse(call.ArgumentsJson);
                if (doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString()!.Trim() is { Length: > 0 } name)
                {
                    names.Add(name);
                }
            }
            catch (JsonException)
            {
            }
        }
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>用户评价了一条回答：算到这条回答用过的学习技能上。返回用过的技能名。</summary>
    public List<string> ApplyFeedback(string messageId, int? value)
    {
        if (_connectionString is null)
        {
            return new();
        }
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE skill_usage SET feedback = $f WHERE message_id = $m RETURNING skill";
        cmd.Parameters.AddWithValue("$f", Math.Sign(value ?? 0));
        cmd.Parameters.AddWithValue("$m", messageId);
        using var r = cmd.ExecuteReader();
        var names = new List<string>();
        while (r.Read())
        {
            names.Add(r.GetString(0));
        }
        return names;
    }

    /// <summary>按当前版本的成败决定转正或退役，改写 SKILL.md 的 status。返回 (名称, 新状态)，没变化返回 null。</summary>
    public (string Name, string Status)? Review(SkillInfo skill)
    {
        if (!skill.IsLearned || !File.Exists(skill.SkillFile))
        {
            return null;
        }
        var status = LearnedSkillStatus.Of(skill);
        var stats = Stats(skill.Name, LearnedSkillStatus.VersionOf(skill));
        var next = status switch
        {
            LearnedSkillStatus.Candidate when stats.Successes == 0 && stats.Failures >= CandidateRetireFailures => LearnedSkillStatus.Retired,
            LearnedSkillStatus.Candidate when stats.Successes > stats.Failures => LearnedSkillStatus.Active,
            LearnedSkillStatus.Active when stats.Failures >= ActiveRetireFailures && stats.Failures > stats.Successes => LearnedSkillStatus.Retired,
            _ => status,
        };
        if (next == status)
        {
            return null;
        }
        SetStatus(skill.SkillFile, next);
        return (skill.Name, next);
    }

    /// <summary>某个版本的使用情况：成功 = 任务完成且没被点踩；失败 = 没完成，或被点踩。</summary>
    public SkillStats Stats(string name, int version)
    {
        if (_connectionString is null)
        {
            return new(0, 0, 0);
        }
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*),
                   COALESCE(SUM(CASE WHEN ok = 1 AND feedback >= 0 THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN ok = 0 OR feedback < 0 THEN 1 ELSE 0 END), 0)
            FROM skill_usage WHERE skill = $s AND version = $v
            """;
        cmd.Parameters.AddWithValue("$s", name);
        cmd.Parameters.AddWithValue("$v", version);
        using var r = cmd.ExecuteReader();
        r.Read();
        return new(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2));
    }

    /// <summary>
    /// 手动改状态（界面上“重新启用”）。重新启用时清掉这个版本以前的失败记录，给它一次重新证明的机会，
    /// 不然下一次失败就又被退役。
    /// </summary>
    public bool SetStatus(SkillInfo skill, string status)
    {
        if (!skill.IsLearned || status is not (LearnedSkillStatus.Candidate or LearnedSkillStatus.Active or LearnedSkillStatus.Retired) || !File.Exists(skill.SkillFile))
        {
            return false;
        }
        if (status != LearnedSkillStatus.Retired && LearnedSkillStatus.Of(skill) == LearnedSkillStatus.Retired && _connectionString is not null)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM skill_usage WHERE skill = $s AND version = $v";
            cmd.Parameters.AddWithValue("$s", skill.Name);
            cmd.Parameters.AddWithValue("$v", LearnedSkillStatus.VersionOf(skill));
            cmd.ExecuteNonQuery();
        }
        SetStatus(skill.SkillFile, status);
        return true;
    }

    private void Write(string file, string name, string description, string body, string status, int version)
    {
        var content = $"""
            ---
            name: {name}
            description: "{description}"
            source: learned
            status: {status}
            version: {version}
            updated: {Clock():yyyy-MM-dd}
            ---

            {body}

            > 这个技能由 FlyknitBuddy 根据多次成功完成的任务自动总结，先试用，实际用成功后正式启用；可以直接修改或删除。
            """;
        File.WriteAllText(file, content.Replace("\r\n", "\n"), new UTF8Encoding(false));
    }

    private static void SetStatus(string file, string status)
    {
        var text = File.ReadAllText(file).Replace("\r\n", "\n");
        var end = text.StartsWith("---\n", StringComparison.Ordinal) ? text.IndexOf("\n---", 4, StringComparison.Ordinal) : -1;
        if (end < 0)
        {
            return;
        }
        var header = text[4..end];
        header = Regex.IsMatch(header, @"(?m)^status:.*$")
            ? Regex.Replace(header, @"(?m)^status:.*$", $"status: {status}")
            : header.TrimEnd('\n') + $"\nstatus: {status}";
        File.WriteAllText(file, "---\n" + header + text[end..], new UTF8Encoding(false));
    }

    /// <summary>旧版本存到 versions/v{n}.md，只留最近几个。</summary>
    private static void Archive(string dir, string file, int version)
    {
        var versions = Path.Combine(dir, "versions");
        System.IO.Directory.CreateDirectory(versions);
        File.Copy(file, Path.Combine(versions, $"v{version}.md"), overwrite: true);
        var old = new DirectoryInfo(versions).GetFiles("v*.md")
            .Select(f => (File: f, N: int.TryParse(Path.GetFileNameWithoutExtension(f.Name)[1..], out var n) ? n : 0))
            .OrderByDescending(x => x.N)
            .Skip(KeepVersions);
        foreach (var (f, _) in old)
        {
            f.Delete();
        }
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var pragma = c.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode = WAL;";
        pragma.ExecuteNonQuery();
        return c;
    }
}
