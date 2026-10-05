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
    public static string Logs => Path.Combine(Root, "logs");

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
