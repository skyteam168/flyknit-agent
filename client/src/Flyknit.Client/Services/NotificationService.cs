using System;
using System.IO;
using Flyknit.Client.Bridge;
using Flyknit.Core.Agent;
using Microsoft.Toolkit.Uwp.Notifications;

namespace Flyknit.Client.Services;

/// <summary>
/// Windows 系统通知（Toast）：
/// - 需要确认时：通知上直接有“允许 / 拒绝 / 查看”按钮，点按钮即完成确认，无需打开窗口；
/// - 任务完成或出错时：点通知回到对应的任务。
/// 使用 Windows Community Toolkit 的兼容层，未打包的桌面程序也能收到按钮点击（COM 激活）。
/// </summary>
public sealed class NotificationService
{
    private const string ConfirmGroup = "confirm";
    private const string DoneGroup = "done";

    /// <summary>用户点了确认通知上的按钮：requestId、选择。</summary>
    public event Action<string, ConfirmChoice>? ConfirmAnswered;

    /// <summary>用户点了通知正文或“查看”：conversationId。</summary>
    public event Action<string>? OpenRequested;

    public bool Available { get; private set; }

    private readonly string? _logo;

    public NotificationService()
    {
        var logo = Path.Combine(AppContext.BaseDirectory, "Assets", "flyknitbuddy-toast.png");
        _logo = File.Exists(logo) ? logo : null;
        try
        {
            ToastNotificationManagerCompat.OnActivated += OnActivated;
            Available = true;
        }
        catch (Exception ex)
        {
            Log.Warn("系统通知不可用", ex);
        }
    }

    /// <summary>
    /// IT 运维提示（右下角）：运维代理以 SYSTEM 执行任务时弹不出桌面通知，改为写文件，
    /// 由员工客户端（<see cref="NoticeWatcher"/>）读到后用这个方法弹给当前登录的员工。
    /// </summary>
    public void ShowNotice(string title, string body)
    {
        if (!Available)
        {
            return;
        }
        try
        {
            Base()
                .AddText(Clip(title, 60))
                .AddText(Clip(body, 180))
                .AddAttributionText(NativeStrings.T("toast.appName"))
                .Show();
        }
        catch (Exception ex)
        {
            Log.Warn("显示运维提示失败", ex);
        }
    }

    public void ShowConfirm(PendingConfirm p)
    {
        if (!Available)
        {
            return;
        }
        try
        {
            var allow = p.Rememberable ? "allowAlways" : "allowOnce";
            var builder = Base()
                .AddArgument("action", "open")
                .AddArgument("conversationId", p.ConversationId)
                .AddText(NativeStrings.T("toast.confirmTitle"))
                .AddText(Clip(p.Detail, 160))
                .AddAttributionText(Clip(p.Title.Length > 0 ? p.Title : NativeStrings.T("toast.appName"), 60))
                .AddButton(Button(NativeStrings.T("toast.allow"), "confirm", p, allow))
                .AddButton(Button(NativeStrings.T("toast.reject"), "confirm", p, "reject"))
                .AddButton(Button(NativeStrings.T("toast.view"), "open", p, ""))
                // 提醒类通知会一直停留在屏幕上，直到用户处理
                .SetToastScenario(ToastScenario.Reminder);
            builder.Show(t =>
            {
                t.Tag = p.RequestId;
                t.Group = ConfirmGroup;
            });
        }
        catch (Exception ex)
        {
            Log.Warn("显示确认通知失败", ex);
        }
    }

    public void ShowFinished(RunFinishedInfo info)
    {
        if (!Available)
        {
            return;
        }
        try
        {
            var title = info.StopReason switch
            {
                null => NativeStrings.T("toast.failed"),
                AgentStopReason.Completed => NativeStrings.T("toast.done"),
                _ => NativeStrings.T("toast.paused"),
            };
            Base()
                .AddArgument("action", "open")
                .AddArgument("conversationId", info.ConversationId)
                .AddText(title)
                .AddText(Clip(info.Title.Length > 0 ? info.Title : NativeStrings.T("toast.appName"), 60))
                .AddText(Clip(PlainText(info.Answer), 180))
                .AddButton(new ToastButton()
                    .SetContent(NativeStrings.T("toast.view"))
                    .AddArgument("action", "open")
                    .AddArgument("conversationId", info.ConversationId))
                .Show(t =>
                {
                    t.Tag = Tag(info.ConversationId);
                    t.Group = DoneGroup;
                });
        }
        catch (Exception ex)
        {
            Log.Warn("显示完成通知失败", ex);
        }
    }

    /// <summary>确认已在其他地方处理，撤下对应的通知。</summary>
    public void RemoveConfirm(string requestId)
    {
        if (!Available)
        {
            return;
        }
        try
        {
            ToastNotificationManagerCompat.History.Remove(requestId, ConfirmGroup);
        }
        catch (Exception ex)
        {
            Log.Warn("撤下通知失败", ex);
        }
    }

    /// <summary>用户回到某个任务后，撤下它的完成通知。</summary>
    public void RemoveFinished(string conversationId)
    {
        if (!Available)
        {
            return;
        }
        try
        {
            ToastNotificationManagerCompat.History.Remove(Tag(conversationId), DoneGroup);
        }
        catch (Exception)
        {
            // 通知中心里没有这条通知时会抛异常，忽略
        }
    }

    /// <summary>退出时清除本程序的所有通知（等待确认的请求已失效）。</summary>
    public void ClearAll()
    {
        if (!Available)
        {
            return;
        }
        try
        {
            ToastNotificationManagerCompat.History.Clear();
        }
        catch (Exception)
        {
            // 忽略
        }
    }

    /// <summary>设置里选的提示音，默认静音——工厂里一屋子电脑同时响会很吵。</summary>
    public Func<string>? SoundSetting { get; set; }

    private ToastContentBuilder Base()
    {
        var b = new ToastContentBuilder();
        if (_logo is not null)
        {
            b.AddAppLogoOverride(new Uri(_logo), ToastGenericAppLogoCrop.Default);
        }
        if (SoundEvent(SoundSetting?.Invoke()) is { } sound)
        {
            b.AddAudio(new Uri($"ms-winsoundevent:{sound}"));
        }
        else
        {
            b.AddAudio(new ToastAudio { Silent = true });
        }
        return b;
    }

    /// <summary>
    /// 音色对应的 Windows 系统声音。Looping.* 那类铃声只在闹钟 / 来电类通知里循环播放才生效，
    /// 普通通知里会被换成默认声音，所以「提示」用的是日历提醒音，和「标准」能听出区别。
    /// </summary>
    private static string? SoundEvent(string? sound) => sound switch
    {
        "soft" => "Notification.Default",
        "alert" => "Notification.Reminder",
        _ => null,
    };

    /// <summary>设置里点「试听」：直接播放通知会用的那个系统声音文件，不用等任务跑完。</summary>
    public static void Preview(string sound)
    {
        if (SoundEvent(sound) is not { } name)
        {
            return;
        }
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey($@"AppEvents\Schemes\Apps\.Default\{name}\.Current");
            var file = Environment.ExpandEnvironmentVariables(key?.GetValue("") as string ?? "");
            if (!File.Exists(file))
            {
                var media = Environment.ExpandEnvironmentVariables(@"%WINDIR%\Media");
                file = Path.Combine(media, name == "Notification.Reminder" ? "Windows Notify Calendar.wav" : "Windows Notify System Generic.wav");
            }
            if (File.Exists(file))
            {
                new System.Media.SoundPlayer(file).Play();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("试听提示音失败", ex);
        }
    }

    private static ToastButton Button(string text, string action, PendingConfirm p, string choice)
    {
        var button = new ToastButton()
            .SetContent(text)
            .AddArgument("action", action)
            .AddArgument("requestId", p.RequestId)
            .AddArgument("conversationId", p.ConversationId);
        if (choice.Length > 0)
        {
            button.AddArgument("choice", choice);
            // 允许、拒绝在后台处理，不打开窗口
            button.SetBackgroundActivation();
        }
        return button;
    }

    private void OnActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        try
        {
            var args = ToastArguments.Parse(e.Argument);
            args.TryGetValue("action", out string? action);
            args.TryGetValue("conversationId", out string? conversationId);
            if (action == "confirm" && args.TryGetValue("requestId", out string? requestId) && args.TryGetValue("choice", out string? choice))
            {
                var c = choice switch
                {
                    "allowAlways" => ConfirmChoice.AllowAlways,
                    "allowOnce" => ConfirmChoice.AllowOnce,
                    _ => ConfirmChoice.Reject,
                };
                ConfirmAnswered?.Invoke(requestId!, c);
                return;
            }
            if (!string.IsNullOrEmpty(conversationId))
            {
                OpenRequested?.Invoke(conversationId!);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("处理通知点击失败", ex);
        }
    }

    /// <summary>通知的 tag 最长 64 个字符。</summary>
    private static string Tag(string id) => id.Length > 60 ? id[..60] : id;

    private static string Clip(string s, int max)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }

    /// <summary>去掉常见的 Markdown 标记，通知里只显示纯文本。</summary>
    private static string PlainText(string markdown) =>
        System.Text.RegularExpressions.Regex.Replace(markdown, @"[#*`>|_~\[\]]+|\(http[^)]*\)", "").Trim();
}
