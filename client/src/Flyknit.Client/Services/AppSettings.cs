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

    /// <summary>资料库：上传和截图的副本（files）、缩略图（thumbs）。索引在 history.db。</summary>
    public static string Library => Path.Combine(Root, "library");
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

    /// <summary>覆盖写之前留的原文件副本。</summary>
    public static string Backups => Path.Combine(Root, "backups");
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

    /// <summary>
    /// 设备令牌。内存里是明文，落盘时用 DPAPI 包一层——它等同于这台机器的身份，
    /// 和代理密码一样不该以明文躺在 settings.json 里。
    /// </summary>
    [JsonIgnore]
    public string DeviceToken { get; set; } = "";

    /// <summary>落盘用。老版本升上来时文件里是明文，读出来照用，下次保存自动包上。</summary>
    [JsonPropertyName("deviceToken")]
    public string DeviceTokenStored
    {
        get => DataProtection.Protect(DeviceToken);
        set => DeviceToken = DataProtection.Unprotect(value);
    }

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

    /// <summary>办事模式一轮最多执行多少步（调用模型的次数）。空转另有检测，这里只是最后的保险。</summary>
    public int AgentMaxSteps { get; set; } = Flyknit.Core.Agent.AgentOptions.DefaultMaxSteps;

    /// <summary>可选的步数上限。</summary>
    public static readonly int[] AgentMaxStepsChoices = { 50, 100, 200 };

    public int ResolveAgentMaxSteps() => AgentMaxSteps is >= 10 and <= 500 ? AgentMaxSteps : Flyknit.Core.Agent.AgentOptions.DefaultMaxSteps;

    /// <summary>窗口不在前台时，用 Windows 系统通知提醒确认和任务完成。</summary>
    public bool EnableNotifications { get; set; } = true;

    /// <summary>上次关闭时窗口是否最大化。</summary>
    public bool WindowMaximized { get; set; }

    /// <summary>
    /// 界面当前的底色（跟着主题走，网页每次切换主题时告诉宿主）。窗口和 WebView2 的默认底色用它，
    /// 收起、最小化、恢复的瞬间露出来的底色就和界面一样，不会闪白；下次启动也直接用上次的颜色。
    /// </summary>
    public string WindowBackground { get; set; } = "#F4F6F9";

    /// <summary>界面字号倍率：0.85 / 0.925 / 1.0（默认）/ 1.1 / 1.25。</summary>
    public double FontScale { get; set; } = 1.0;

    /// <summary>登录 Windows 后自动启动。实际开关写在注册表的 Run 项里，这里只记用户的选择。</summary>
    public bool AutoStart { get; set; }

    /// <summary>direct（直连）/ system（跟随系统）/ manual（手动填地址）。</summary>
    public string ProxyMode { get; set; } = "system";

    /// <summary>手动代理地址，例如 http://10.0.0.8:8080。</summary>
    public string ProxyUrl { get; set; } = "";

    public string ProxyUser { get; set; } = "";

    [JsonIgnore]
    public string ProxyPassword { get; set; } = "";

    [JsonPropertyName("proxyPassword")]
    public string ProxyPasswordStored
    {
        get => DataProtection.Protect(ProxyPassword);
        set => ProxyPassword = DataProtection.Unprotect(value);
    }

    /// <summary>通知提示音：none / soft / alert。</summary>
    public string NotificationSound { get; set; } = "none";

    /// <summary>安全中心里用户自己改过的项。只存没被 IT 锁住的那几项。</summary>
    public System.Collections.Generic.Dictionary<string, object?> SecurityChoices { get; set; } = new();

    /// <summary>
    /// 划词翻译的目标语言（翻译语言代码）。"auto" 表示跟界面语言走；
    /// 用户在弹窗里选过一次就记住它。原文本来就是这种语言时会自动换一个。
    /// </summary>
    public string SelectionTranslateTo { get; set; } = "auto";

    /// <summary>快捷键改动：命令 id → 按键。只存和默认不一样的，默认值变了也能跟上。</summary>
    public System.Collections.Generic.Dictionary<string, string> Shortcuts { get; set; } = new();

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
