using System.Text;

namespace Flyknit.Core.Context;

/// <summary>指令的层级。</summary>
public enum InstructionLevel
{
    /// <summary>组织/公司级统一策略（IT 管理）。</summary>
    Managed,

    /// <summary>用户个人全局偏好（~/.flyknit/）。</summary>
    User,

    /// <summary>项目/团队共享规范（工作区根目录）。</summary>
    Project,

    /// <summary>当前目录的本地私有规则。</summary>
    Local,
}

/// <summary>一个指令文件的内容和来源。</summary>
public sealed record InstructionFile(
    InstructionLevel Level,
    string Path,
    string Content,
    bool Exists)
{
    public int TokenEstimate => TokenEstimator.Estimate(Content);
}

/// <summary>
/// 指令记忆加载器：参照 Claude Code 的四层体系和反转加载策略。
/// 
/// 四层体系：
/// 1. Managed - 组织/公司级统一策略（%ProgramData%\FlyknitBuddy\）
/// 2. User - 用户个人全局偏好（%APPDATA%\Flyknit\）
/// 3. Project - 项目/团队共享规范（工作区根目录）
/// 4. Local - 当前目录的本地私有规则
/// 
/// 加载策略（反转加载）：
/// 1. 从当前工作目录向上遍历，收集沿途的指令文件
/// 2. 反转顺序：从全局（Managed）到局部（Local）加载
/// 3. 越具体的作用域拥有越强的针对性
/// 
/// 支持的文件名：
/// - FLYKNIT.md（推荐）
/// - AGENTS.md
/// - CLAUDE.md（兼容）
/// </summary>
public sealed class InstructionLoader
{
    /// <summary>支持的指令文件名（按优先级）。</summary>
    public static readonly string[] FileNames = { "FLYKNIT.md", "AGENTS.md", "CLAUDE.md" };

    /// <summary>组织级目录（IT 管理，只读）。</summary>
    public static string ManagedDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "FlyknitBuddy");

    /// <summary>用户级目录。</summary>
    public static string UserDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Flyknit");

    private readonly string _workspaceRoot;
    private readonly string _currentDirectory;

    /// <param name="workspaceRoot">工作区根目录。</param>
    /// <param name="currentDirectory">当前工作目录（默认同工作区根目录）。</param>
    public InstructionLoader(string workspaceRoot, string? currentDirectory = null)
    {
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _currentDirectory = currentDirectory is not null ? Path.GetFullPath(currentDirectory) : _workspaceRoot;
    }

    /// <summary>
    /// 加载所有层级的指令文件。
    /// 返回按加载顺序排列的文件列表（Managed → User → Project → Local）。
    /// </summary>
    public List<InstructionFile> Load()
    {
        var files = new List<InstructionFile>();

        // 1. Managed 层（组织级）
        var managed = FindInDirectory(ManagedDirectory);
        if (managed is not null)
        {
            files.Add(new InstructionFile(InstructionLevel.Managed, managed.Value.Path, managed.Value.Content, true));
        }

        // 2. User 层（用户级）
        var user = FindInDirectory(UserDirectory);
        if (user is not null)
        {
            files.Add(new InstructionFile(InstructionLevel.User, user.Value.Path, user.Value.Content, true));
        }

        // 3. 从当前目录向上遍历到工作区根目录，收集沿途的指令文件
        var projectFiles = CollectFromDirectoryTree();
        
        // 4. 反转顺序（从根目录到当前目录）
        projectFiles.Reverse();

        // 5. 添加到结果中
        var isFirst = true;
        foreach (var (path, content) in projectFiles)
        {
            // 第一个（工作区根目录）是 Project 层，其余是 Local 层
            var level = isFirst ? InstructionLevel.Project : InstructionLevel.Local;
            files.Add(new InstructionFile(level, path, content, true));
            isFirst = false;
        }

        return files;
    }

    /// <summary>
    /// 构建合并后的指令内容。
    /// 按层级顺序拼接，每层用注释分隔。
    /// </summary>
    public string BuildCombined(bool includeHeaders = true)
    {
        var files = Load();
        if (files.Count == 0)
        {
            return "";
        }

        var sb = new StringBuilder();
        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file.Content))
            {
                continue;
            }

            if (includeHeaders && sb.Length > 0)
            {
                sb.AppendLine();
            }

            if (includeHeaders)
            {
                sb.AppendLine($"<!-- {LevelLabel(file.Level)}: {file.Path} -->");
            }

            sb.AppendLine(file.Content.Trim());
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// 从当前目录向上遍历到工作区根目录，收集沿途的指令文件。
    /// 返回顺序：从当前目录到根目录（稍后会反转）。
    /// </summary>
    private List<(string Path, string Content)> CollectFromDirectoryTree()
    {
        var files = new List<(string, string)>();
        var dir = _currentDirectory;
        var root = Path.GetPathRoot(_workspaceRoot);

        while (dir is not null && dir.Length >= _workspaceRoot.Length)
        {
            var found = FindInDirectory(dir);
            if (found is not null)
            {
                files.Add(found.Value);
            }

            // 到达工作区根目录则停止
            if (string.Equals(dir, _workspaceRoot, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            dir = Path.GetDirectoryName(dir);
            
            // 安全检查：不要超出根目录
            if (dir is null || (root is not null && dir.Length < root.Length))
            {
                break;
            }
        }

        return files;
    }

    /// <summary>
    /// 在指定目录中查找指令文件。
    /// </summary>
    private static (string Path, string Content)? FindInDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        foreach (var fileName in FileNames)
        {
            var path = Path.Combine(directory, fileName);
            if (File.Exists(path))
            {
                try
                {
                    var content = File.ReadAllText(path);
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        return (path, content);
                    }
                }
                catch
                {
                    // 读取失败，尝试下一个
                }
            }
        }

        return null;
    }

    private static string LevelLabel(InstructionLevel level) => level switch
    {
        InstructionLevel.Managed => "组织规范",
        InstructionLevel.User => "用户偏好",
        InstructionLevel.Project => "项目规范",
        InstructionLevel.Local => "本地规则",
        _ => level.ToString(),
    };

    /// <summary>
    /// 获取指令文件的统计信息。
    /// </summary>
    public InstructionStats GetStats()
    {
        var files = Load();
        return new InstructionStats
        {
            TotalFiles = files.Count,
            TotalTokens = files.Sum(f => f.TokenEstimate),
            ByLevel = files.GroupBy(f => f.Level)
                .ToDictionary(g => g.Key, g => g.Sum(f => f.TokenEstimate)),
        };
    }
}

/// <summary>指令文件的统计信息。</summary>
public sealed record InstructionStats
{
    public int TotalFiles { get; init; }
    public int TotalTokens { get; init; }
    public Dictionary<InstructionLevel, int> ByLevel { get; init; } = new();
}
