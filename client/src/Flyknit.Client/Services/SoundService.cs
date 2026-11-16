using System;
using System.Collections.Concurrent;
using System.IO;
using Flyknit.Core.Notices;

namespace Flyknit.Client.Services;

/// <summary>
/// 提示音：任务做完、要你处理（确认 / 回答 / 到上限）、出错时各响一种。
/// 原来声音挂在 Windows 通知上：员工正看着这个任务时不弹通知，声音也就没了；
/// 系统开了「专注助手 / 勿扰」、关了通知，也听不到。现在由客户端自己放，和通知分开。
/// 音色、每种时刻响不响在设置里选；IT 在安全中心关掉「通知提示音」时一律不响（工厂里一屋子电脑同时响会很吵）。
/// </summary>
public sealed class SoundService
{
    private readonly Func<string> _theme;
    private readonly Func<SoundCue, bool> _enabled;
    private readonly ConcurrentDictionary<(string, SoundCue), byte[]> _cache = new();
    private DateTimeOffset _last = DateTimeOffset.MinValue;
    private readonly object _gate = new();

    public SoundService(Func<string> theme, Func<SoundCue, bool> enabled)
    {
        _theme = theme;
        _enabled = enabled;
    }

    /// <summary>按设置放一次。1.5 秒内连着来的（一批确认、几个子任务同时结束）只响一次。</summary>
    public void Play(SoundCue cue)
    {
        var theme = _theme();
        if (theme == SoundThemes.None || !_enabled(cue))
        {
            return;
        }
        lock (_gate)
        {
            var now = DateTimeOffset.Now;
            if (now - _last < TimeSpan.FromSeconds(1.5))
            {
                return;
            }
            _last = now;
        }
        PlayTheme(theme, cue);
    }

    /// <summary>设置里点「试听」：不看开关和间隔。</summary>
    public void Preview(string theme, SoundCue cue) => PlayTheme(SoundThemes.Normalize(theme), cue);

    private void PlayTheme(string theme, SoundCue cue)
    {
        try
        {
            if (SoundThemes.Wav(theme, cue) is not null)
            {
                var bytes = _cache.GetOrAdd((theme, cue), k => SoundThemes.Wav(k.Item1, k.Item2)!);
                // SoundPlayer.Play 是异步的，不卡界面；同一时间只放一个，新的会顶掉旧的
                new System.Media.SoundPlayer(new MemoryStream(bytes)).Play();
                return;
            }
            if (SystemSoundFile(theme, cue) is { } file)
            {
                new System.Media.SoundPlayer(file).Play();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"播放提示音失败：{theme}/{cue}", ex);
        }
    }

    /// <summary>
    /// 两种系统音色：用 Windows 当前声音方案里的文件（员工改过系统声音就跟着变），找不到用 Windows\Media 里的默认文件。
    /// 出错用系统的「严重错误」声，等你处理用日历提醒声。
    /// </summary>
    private static string? SystemSoundFile(string theme, SoundCue cue)
    {
        if (theme is not ("soft" or "alert"))
        {
            return null;
        }
        var (eventName, fallback) = cue switch
        {
            SoundCue.Failed => ("SystemHand", "Windows Critical Stop.wav"),
            SoundCue.Attention => ("Notification.Reminder", "Windows Notify Calendar.wav"),
            _ => theme == "alert"
                ? ("Notification.Reminder", "Windows Notify Calendar.wav")
                : ("Notification.Default", "Windows Notify System Generic.wav"),
        };
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey($@"AppEvents\Schemes\Apps\.Default\{eventName}\.Current");
            var file = Environment.ExpandEnvironmentVariables(key?.GetValue("") as string ?? "");
            if (File.Exists(file))
            {
                return file;
            }
        }
        catch (Exception)
        {
            // 读不到注册表就用默认文件
        }
        var media = Path.Combine(Environment.ExpandEnvironmentVariables(@"%WINDIR%\Media"), fallback);
        return File.Exists(media) ? media : null;
    }
}
