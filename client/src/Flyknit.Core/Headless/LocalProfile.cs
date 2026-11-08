using System.Text.Json;
using Flyknit.Core.Security;

namespace Flyknit.Core.Headless;

/// <summary>
/// 命令行（flyknit.exe）用的本机登录信息：直接读员工端写的 %APPDATA%\Flyknit\settings.json。
///
/// 命令行不自己登录、不自己存令牌：用的就是这台电脑、这个 Windows 账号在员工端登录后的设备令牌（DPAPI 保护，
/// 换个账号解不开），所以权限、额度、审计都记在这台设备名下，和在聊天窗口里做一样。
/// 只读这个文件，从不写它（员工端可能同时开着）。
/// </summary>
public sealed class LocalProfile
{
    public string Root { get; init; } = "";
    public string ServerUrl { get; init; } = "";
    public string DeviceToken { get; init; } = "";
    public string UiLanguage { get; init; } = "zh-CN";
    public string DefaultPermission { get; init; } = "workspace";
    public bool DeepThinking { get; init; }
    public string ProxyMode { get; init; } = "system";
    public string ProxyUrl { get; init; } = "";
    public string ProxyUser { get; init; } = "";
    public string ProxyPassword { get; init; } = "";

    /// <summary>员工在安全中心自己改过的项（没被 IT 锁住的）。</summary>
    public Dictionary<string, object?> SecurityChoices { get; init; } = new();

    /// <summary>员工在员工端停用的技能（命令行里同样不用）。</summary>
    public HashSet<string> DisabledSkills { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsRegistered => ServerUrl.Length > 0 && DeviceToken.Length > 0;

    /// <summary>员工确认过的「以后自动执行」规则，和员工端同一个文件。</summary>
    public string ApprovalRulesFile => Path.Combine(Root, "approval-rules.json");

    /// <summary>
    /// 数据目录：默认 %APPDATA%\Flyknit（和员工端一致）。环境变量 FLYKNIT_HOME 可以换一个目录——
    /// 只用于测试和排查，换过去的目录里也得有员工端登录后写下的设置文件，并不能凭空登录。
    /// </summary>
    public static string DefaultRoot()
    {
        var overridden = Environment.GetEnvironmentVariable("FLYKNIT_HOME");
        return !string.IsNullOrWhiteSpace(overridden)
            ? overridden
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Flyknit");
    }

    /// <summary>员工端正好在保存设置时可能读到写了一半的文件：隔一下再读一次，不然会误报「没登录」。</summary>
    private static JsonDocument ParseWithRetry(string file)
    {
        try
        {
            return JsonDocument.Parse(File.ReadAllText(file));
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Thread.Sleep(300);
            return JsonDocument.Parse(File.ReadAllText(file));
        }
    }

    /// <summary>读设置文件。没有或读不了时返回未登录的空档案（调用方据此提示先在员工端登录）。</summary>
    public static LocalProfile Load(string? root = null, Action<string>? warn = null)
    {
        root ??= DefaultRoot();
        var file = Path.Combine(root, "settings.json");
        if (!File.Exists(file))
        {
            return new LocalProfile { Root = root };
        }
        try
        {
            using var doc = ParseWithRetry(file);
            var r = doc.RootElement;
            string Str(string name, string fallback = "") =>
                r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;
            var choices = new Dictionary<string, object?>();
            if (r.TryGetProperty("securityChoices", out var sc) && sc.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in sc.EnumerateObject())
                {
                    choices[p.Name] = p.Value.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.Number when p.Value.TryGetInt32(out var n) => n,
                        JsonValueKind.String => p.Value.GetString(),
                        _ => null,
                    };
                }
            }
            var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (r.TryGetProperty("disabledSkills", out var ds) && ds.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in ds.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } name)
                    {
                        disabled.Add(name);
                    }
                }
            }
            return new LocalProfile
            {
                Root = root,
                DisabledSkills = disabled,
                ServerUrl = Str("serverUrl").Trim(),
                DeviceToken = LocalSecret.Unprotect(Str("deviceToken"), warn),
                UiLanguage = Str("uiLanguage") is "zh-CN" or "vi-VN" or "en-US" ? Str("uiLanguage") : "zh-CN",
                DefaultPermission = Str("defaultPermission", "workspace"),
                DeepThinking = r.TryGetProperty("deepThinking", out var dt) && dt.ValueKind == JsonValueKind.True,
                ProxyMode = Str("proxyMode", "system"),
                ProxyUrl = Str("proxyUrl"),
                ProxyUser = Str("proxyUser"),
                ProxyPassword = LocalSecret.Unprotect(Str("proxyPassword"), warn),
                SecurityChoices = choices,
            };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            warn?.Invoke($"读取设置文件失败：{ex.Message}");
            return new LocalProfile { Root = root };
        }
    }
}
