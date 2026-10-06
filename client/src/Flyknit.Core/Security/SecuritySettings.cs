using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Flyknit.Core.Security;

/// <summary>安全中心里的一项。</summary>
public sealed class SecurityItem
{
    [JsonPropertyName("value")] public object? Value { get; set; }

    /// <summary>true 表示由 IT 统一配置，这台机器上改不了。</summary>
    [JsonPropertyName("locked")] public bool Locked { get; set; } = true;

    /// <summary>bool 或 int。</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "bool";

    [JsonPropertyName("title")] public string Title { get; set; } = "";

    /// <summary>关掉它的后果。用户要关的时候原样显示给他看。</summary>
    [JsonPropertyName("risk")] public string Risk { get; set; } = "";

    [JsonPropertyName("default")] public object? Default { get; set; }
    [JsonPropertyName("min")] public int? Min { get; set; }
    [JsonPropertyName("max")] public int? Max { get; set; }

    public bool AsBool(bool fallback = true) => Value switch
    {
        bool b => b,
        System.Text.Json.JsonElement e when e.ValueKind == System.Text.Json.JsonValueKind.True => true,
        System.Text.Json.JsonElement e when e.ValueKind == System.Text.Json.JsonValueKind.False => false,
        _ => fallback,
    };

    public int AsInt(int fallback) => Value switch
    {
        int i => i,
        long l => (int)l,
        System.Text.Json.JsonElement e when e.ValueKind == System.Text.Json.JsonValueKind.Number && e.TryGetInt32(out var n) => n,
        _ => fallback,
    };
}

/// <summary>
/// 这台机器最终生效的安全设置。
///
/// 服务端下发「值 + 锁」，锁住的项用户改不了。本地只保存没锁住的那几项的改动，
/// 下一次下发回来时，锁住的以服务端为准——IT 随时能把一台机器收回统一管理。
/// </summary>
public sealed class SecuritySettings
{
    public const string Sandbox = "sandbox";
    public const string CommandPolicy = "command_policy";
    public const string DeleteProtection = "delete_protection";
    public const string NetworkAllowlist = "network_allowlist";
    public const string SystemTools = "system_tools";
    public const string AutoBackup = "auto_backup";
    public const string BackupQuotaMb = "backup_quota_mb";
    public const string BatchDeleteThreshold = "batch_delete_threshold";
    public const string Notifications = "notifications";
    public const string NotificationSound = "notification_sound";

    private readonly Dictionary<string, SecurityItem> _items;

    public SecuritySettings(Dictionary<string, SecurityItem>? items = null)
    {
        _items = items ?? new Dictionary<string, SecurityItem>(StringComparer.Ordinal);
    }

    public IReadOnlyDictionary<string, SecurityItem> Items => _items;

    public SecurityItem? Get(string key) => _items.TryGetValue(key, out var item) ? item : null;

    /// <summary>某一项是否开着。服务端没下发这一项时按 <paramref name="fallback"/> 算。</summary>
    public bool On(string key, bool fallback = true) => Get(key)?.AsBool(fallback) ?? fallback;

    public int Number(string key, int fallback) => Get(key)?.AsInt(fallback) ?? fallback;

    /// <summary>这台机器的用户能不能自己改这一项。</summary>
    public bool CanChange(string key) => Get(key) is { Locked: false };

    /// <summary>
    /// 用户改一项。锁住的项一律拒绝——这是整个管控模型的那条线，
    /// 界面上虽然也置灰了，但不能只靠界面拦。
    /// </summary>
    public bool TrySet(string key, object value, out string reason)
    {
        var item = Get(key);
        if (item is null)
        {
            reason = "没有这一项设置";
            return false;
        }
        if (item.Locked)
        {
            reason = "这一项由 IT 统一配置，本机不能修改";
            return false;
        }
        if (item.Kind == "int")
        {
            var number = value switch
            {
                int i => i,
                long l => (int)l,
                double d => (int)d,
                string s when int.TryParse(s, out var n) => n,
                _ => int.MinValue,
            };
            if (number == int.MinValue)
            {
                reason = "需要一个数字";
                return false;
            }
            if (item.Min is { } min) { number = Math.Max(min, number); }
            if (item.Max is { } max) { number = Math.Min(max, number); }
            item.Value = number;
        }
        else
        {
            item.Value = value switch
            {
                bool b => b,
                string s => s.Equals("true", StringComparison.OrdinalIgnoreCase),
                int i => i != 0,
                _ => item.AsBool(),
            };
        }
        reason = "";
        return true;
    }

    /// <summary>
    /// 把服务端新下发的一份合并进来。
    ///
    /// 锁住的项以服务端为准，本地改动作废；没锁的项保留用户已经改过的值，
    /// 否则每次刷新配置都会把用户的选择冲掉。
    /// </summary>
    public SecuritySettings MergeFromServer(Dictionary<string, SecurityItem> fromServer)
    {
        foreach (var (key, incoming) in fromServer)
        {
            if (_items.TryGetValue(key, out var local) && !incoming.Locked && !local.Locked)
            {
                // 这一项本来就归用户管，保留他改过的值
                incoming.Value = local.Value;
            }
            _items[key] = incoming;
        }
        // 服务端不再下发的项删掉，免得界面上留着一个管不着的开关
        foreach (var stale in _items.Keys.Where(k => !fromServer.ContainsKey(k)).ToList())
        {
            _items.Remove(stale);
        }
        return this;
    }

    /// <summary>只有没锁的项才值得存本地。</summary>
    public Dictionary<string, object?> LocalChanges() =>
        _items.Where(kv => !kv.Value.Locked).ToDictionary(kv => kv.Key, kv => kv.Value.Value);

    public void ApplyLocal(Dictionary<string, object?>? saved)
    {
        foreach (var (key, value) in saved ?? new Dictionary<string, object?>())
        {
            if (value is not null && CanChange(key))
            {
                TrySet(key, value, out _);
            }
        }
    }
}
