using System;
using System.Threading;

namespace Flyknit.Core.Gateway;

/// <summary>
/// 这一轮是谁发起的：定时任务、钉钉/企业微信、IT 指令、手机 App。发给服务端只用于用量统计
/// （后台「场景分布」里单独成一块），选模型照旧按 scene。
///
/// 用 AsyncLocal 跟着这次运行走：在发起处 <c>using (UsageSource.Use(UsageSource.Schedule))</c>，
/// 之后在里面起的任务、await 下去的所有模型调用（含标题、压缩上下文、反思）都会带上。
/// </summary>
public static class UsageSource
{
    public const string Schedule = "schedule";
    public const string Channel = "channel";
    public const string Instruction = "instruction";
    /// <summary>手机 App 让电脑办的事。</summary>
    public const string Mobile = "mobile";

    private static readonly AsyncLocal<string?> _current = new();

    public static string? Current => _current.Value;

    public static IDisposable Use(string source)
    {
        var previous = _current.Value;
        _current.Value = source;
        return new Restore(previous);
    }

    private sealed class Restore(string? previous) : IDisposable
    {
        public void Dispose() => _current.Value = previous;
    }
}
