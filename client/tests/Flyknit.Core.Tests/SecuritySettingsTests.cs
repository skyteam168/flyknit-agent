using System.Collections.Generic;
using System.Text.Json;
using Flyknit.Core.Security;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>
/// 安全中心的客户端这一半：锁住的改不了，没锁的记得住，服务端随时能收回。
/// </summary>
public class SecuritySettingsTests
{
    private static Dictionary<string, SecurityItem> FromServer(params (string Key, object Value, bool Locked)[] items)
    {
        var map = new Dictionary<string, SecurityItem>();
        foreach (var (key, value, locked) in items)
        {
            map[key] = new SecurityItem
            {
                Value = value,
                Locked = locked,
                Kind = value is int ? "int" : "bool",
                Title = key,
                Min = value is int ? 64 : null,
                Max = value is int ? 20480 : null,
            };
        }
        return map;
    }

    [Fact]
    public void ALockedItemCannotBeChangedLocally()
    {
        var s = new SecuritySettings().MergeFromServer(FromServer((SecuritySettings.Sandbox, true, true)));

        Assert.False(s.CanChange(SecuritySettings.Sandbox));
        Assert.False(s.TrySet(SecuritySettings.Sandbox, false, out var why));
        Assert.Contains("IT 统一配置", why);
        Assert.True(s.On(SecuritySettings.Sandbox)); // 值没被改掉
    }

    [Fact]
    public void AnUnlockedItemCanBeChanged()
    {
        var s = new SecuritySettings().MergeFromServer(FromServer((SecuritySettings.AutoBackup, true, false)));

        Assert.True(s.TrySet(SecuritySettings.AutoBackup, false, out _));
        Assert.False(s.On(SecuritySettings.AutoBackup));
    }

    [Fact]
    public void NumbersAreClampedToTheAllowedRange()
    {
        var s = new SecuritySettings().MergeFromServer(FromServer((SecuritySettings.BackupQuotaMb, 512, false)));

        s.TrySet(SecuritySettings.BackupQuotaMb, 1, out _);
        Assert.Equal(64, s.Number(SecuritySettings.BackupQuotaMb, 0));

        s.TrySet(SecuritySettings.BackupQuotaMb, 999999, out _);
        Assert.Equal(20480, s.Number(SecuritySettings.BackupQuotaMb, 0));
    }

    [Fact]
    public void RefreshingConfigKeepsWhatTheUserChose()
    {
        // 否则每十分钟拉一次配置就把用户的选择冲掉一次
        var s = new SecuritySettings().MergeFromServer(FromServer((SecuritySettings.AutoBackup, true, false)));
        s.TrySet(SecuritySettings.AutoBackup, false, out _);

        s.MergeFromServer(FromServer((SecuritySettings.AutoBackup, true, false)));

        Assert.False(s.On(SecuritySettings.AutoBackup));
    }

    [Fact]
    public void ITCanTakeAnItemBackUnderCentralControl()
    {
        var s = new SecuritySettings().MergeFromServer(FromServer((SecuritySettings.SystemTools, true, false)));
        s.TrySet(SecuritySettings.SystemTools, true, out _);

        // 管理员把这一项重新锁上并设成关闭：本地的选择作废
        s.MergeFromServer(FromServer((SecuritySettings.SystemTools, false, true)));

        Assert.True(s.Get(SecuritySettings.SystemTools)!.Locked);
        Assert.False(s.On(SecuritySettings.SystemTools));
    }

    [Fact]
    public void ItemsTheServerStopsSendingDisappear()
    {
        var s = new SecuritySettings().MergeFromServer(
            FromServer((SecuritySettings.Sandbox, true, true), (SecuritySettings.AutoBackup, true, false)));

        s.MergeFromServer(FromServer((SecuritySettings.Sandbox, true, true)));

        Assert.Null(s.Get(SecuritySettings.AutoBackup));
    }

    [Fact]
    public void OnlyUnlockedItemsAreWorthSavingLocally()
    {
        var s = new SecuritySettings().MergeFromServer(
            FromServer((SecuritySettings.Sandbox, true, true), (SecuritySettings.AutoBackup, false, false)));

        var saved = s.LocalChanges();

        Assert.Single(saved);
        Assert.True(saved.ContainsKey(SecuritySettings.AutoBackup));
    }

    [Fact]
    public void RestoringLocalChoicesSkipsAnythingNowLocked()
    {
        var s = new SecuritySettings().MergeFromServer(FromServer((SecuritySettings.Sandbox, true, true)));

        // 本地存着上次放开时改的值，但现在这项已经被锁了
        s.ApplyLocal(new Dictionary<string, object?> { [SecuritySettings.Sandbox] = false });

        Assert.True(s.On(SecuritySettings.Sandbox));
    }

    [Fact]
    public void AnUnknownKeyIsRefusedNotInvented()
    {
        var s = new SecuritySettings();

        Assert.False(s.TrySet("made_up", true, out var why));
        Assert.Contains("没有这一项", why);
    }

    [Fact]
    public void MissingItemsFallBackToTheSafeAnswer()
    {
        var s = new SecuritySettings();

        // 服务端没下发时按「开着」算，而不是默认关掉保护
        Assert.True(s.On(SecuritySettings.Sandbox));
        Assert.Equal(20, s.Number(SecuritySettings.BatchDeleteThreshold, 20));
    }

    [Fact]
    public void ValuesArrivingAsJsonAreUnderstood()
    {
        // 真实下发是 JSON，反序列化出来是 JsonElement 而不是 bool / int
        var json = """
            {"sandbox":{"value":false,"locked":true,"kind":"bool"},
             "backup_quota_mb":{"value":1024,"locked":false,"kind":"int","min":64,"max":20480}}
            """;
        var map = JsonSerializer.Deserialize<Dictionary<string, SecurityItem>>(json)!;
        var s = new SecuritySettings().MergeFromServer(map);

        Assert.False(s.On(SecuritySettings.Sandbox));
        Assert.Equal(1024, s.Number(SecuritySettings.BackupQuotaMb, 0));
    }
}
