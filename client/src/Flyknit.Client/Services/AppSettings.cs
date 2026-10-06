using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Flyknit.Client.Services;

/// <summary>本地目录约定，见 docs/architecture.md。</summary>
public static class AppPaths
{
    public static string Root { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Flyknit");

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string Database => Path.Combine(Root, "data", "history.db");
    public static string Memory => Path.Combine(Root, "memory");
    public static string Skills => Path.Combine(Root, "skills");
    public static string OrgSkills => Path.Combine(Root, "skills", "org");
    public static string LearnedSkills => Path.Combine(Root, "skills", "learned");

    /// <summary>
    /// 整台电脑共用的技能目录（C:\ProgramData\FlyknitBuddy\skills）。
    /// IT 拷一次，这台机器上所有 Windows 用户都能用；普通用户没有写权限，所以按企业技能处理（只读、不能卸载）。
    /// </summary>
    public static string MachineSkills { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FlyknitBuddy", "skills");
    public static string Logs => Path.Combine(Root, "logs");
    /// <summary>旧版「记住一模一样的命令」的文件，已不再使用。</summary>
    public static string Approvals => Path.Combine(Root, "approvals.json");

    /// <summary>自动执行规则（按命令前缀 + 工作区），取代旧的 approvals.json。</summary>
    public static string ApprovalRules => Path.Combine(Root, "approval-rules.json");

    /// <summary>默认工作区：我的文档\Flyknit。</summary>
    public static string DefaultWorkspace { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Flyknit");

    public static string Temp { get; } = Path.Combine(Path.GetTempPath(), "Flyknit");

    public static string WebViewData { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Flyknit", "WebView2");
}

public sealed class AppSettings
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string ServerUrl { get; set; } = "";
    public string DeviceToken { get; set; } = "";

    /// <summary>zh-CN / vi-VN / en-US；为空时按 Windows 区域自动选择。</summary>
    public string UiLanguage { get; set; } = "";

    /// <summary>system / light / dark</summary>
    public string Theme { get; set; } = "system";

    /// <summary>新建会话默认使用的模型（服务端模型 ID），为空表示自动。</summary>
    public int? DefaultModelId { get; set; }

    public double? BallLeft { get; set; }
    public double? BallTop { get; set; }
    public bool ShowBall { get; set; } = true;

    public double WindowWidth { get; set; } = 1040;
    public double WindowHeight { get; set; } = 720;

    /// <summary>用户添加过的工作区目录（默认工作区始终在列表中）。</summary>
    public System.Collections.Generic.List<string> Workspaces { get; set; } = new();

    /// <summary>新任务默认使用的工作区，为空时使用默认工作区。</summary>
    public string? DefaultWorkspace { get; set; }

    /// <summary>新任务默认的权限：readonly / workspace。完全权限只对单个任务生效，不会成为默认值。</summary>
    public string DefaultPermission { get; set; } = "workspace";

    /// <summary>被用户停用的技能名。</summary>
    public System.Collections.Generic.List<string> DisabledSkills { get; set; } = new();

    /// <summary>企业要求安装的技能名（服务端下发，不允许停用或卸载）。</summary>
    public System.Collections.Generic.List<string> RequiredSkills { get; set; } = new();

    /// <summary>任务结束后自动复盘，学习偏好与经验。</summary>
    public bool EnableLearning { get; set; } = true;

    /// <summary>窗口不在前台时，用 Windows 系统通知提醒确认和任务完成。</summary>
    public bool EnableNotifications { get; set; } = true;

    /// <summary>上次关闭时窗口是否最大化。</summary>
    public bool WindowMaximized { get; set; }

    /// <summary>软件别名，例如 "生产程序" → "D:\MES\client.exe"。后续由服务端下发。</summary>
    public System.Collections.Generic.Dictionary<string, string> AppAliases { get; set; } = new();

    [JsonIgnore]
    public bool IsRegistered => !string.IsNullOrWhiteSpace(ServerUrl) && !string.IsNullOrWhiteSpace(DeviceToken);

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Json);
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("读取设置失败", ex);
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.Root);
        var tmp = AppPaths.SettingsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, AppPaths.SettingsFile, overwrite: true);
    }

    /// <summary>确保默认工作区存在并在列表首位，去掉重复项。</summary>
    public void EnsureWorkspaces()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DefaultWorkspace);
        }
        catch (Exception ex)
        {
            Log.Warn("创建默认工作区失败", ex);
        }
        var list = new System.Collections.Generic.List<string> { AppPaths.DefaultWorkspace };
        foreach (var w in Workspaces)
        {
            var full = NormalizeDir(w);
            if (full.Length > 0 && !list.Exists(x => x.Equals(full, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(full);
            }
        }
        Workspaces = list;
    }

    public void AddWorkspace(string path)
    {
        var full = NormalizeDir(path);
        if (full.Length > 0 && !Workspaces.Exists(x => x.Equals(full, StringComparison.OrdinalIgnoreCase)))
        {
            Workspaces.Add(full);
        }
    }

    /// <summary>任务实际使用的工作区：指定的目录不存在时退回默认工作区。</summary>
    public string ResolveWorkspace(string? workspace)
    {
        foreach (var candidate in new[] { workspace, DefaultWorkspace })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
            {
                return NormalizeDir(candidate);
            }
        }
        Directory.CreateDirectory(AppPaths.DefaultWorkspace);
        return AppPaths.DefaultWorkspace;
    }

    public static string NormalizeDir(string path)
    {
        try
        {
            var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
            var root = Path.GetPathRoot(full);
            return full.Length > (root?.Length ?? 0) ? full.TrimEnd('\\', '/') : full;
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>首次启动时根据 Windows 区域选择界面语言。</summary>
    public string ResolveUiLanguage()
    {
        if (UiLanguage is "zh-CN" or "vi-VN" or "en-US")
        {
            return UiLanguage;
        }
        var culture = System.Globalization.CultureInfo.CurrentUICulture.Name;
        return culture.StartsWith("vi", StringComparison.OrdinalIgnoreCase) ? "vi-VN"
            : culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN"
            : culture.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en-US"
            : "zh-CN";
    }
}

/// <summary>简单的文件日志，按天滚动。</summary>
public static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.Logs);
                var file = Path.Combine(AppPaths.Logs, $"flyknit-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss.fff} {level} {message}{(ex is null ? "" : Environment.NewLine + ex)}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }
}
