using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Windows.Threading;
using Flyknit.Client.Windows;
using Flyknit.Core.Agent;
using Flyknit.Core.Desktop;
using Flyknit.Core.Security;
using Flyknit.Core.Storage;

namespace Flyknit.Client.Services;

/// <summary>
/// 电脑操作（computer use，docs/computer-use.md）：IT 在安全中心打开「电脑操作」（默认关）后，办事对话里才有 desktop_* 工具。
/// 每个对话一个 UI Automation 驱动（编号各管各的）；AI 操作时屏幕顶部显示提示条，员工点「停止」立刻停掉这个任务。
/// 能操作哪些程序：命令策略里的 desktop_apps（IT 后台设置）；确认粒度：安全中心「每一步都先确认」。
/// </summary>
public sealed partial class AgentHost
{
    private readonly ConcurrentDictionary<string, UiaDesktopDriver> _desktopDrivers = new();
    private DesktopBanner? _desktopBanner;

    private bool DesktopEnabled => Security.On(SecuritySettings.ComputerUse, fallback: false);

    private DesktopOptions DesktopOptionsNow() =>
        new(_policy.Config.DesktopApps, Security.On(SecuritySettings.ComputerUseConfirmAll, fallback: true));

    /// <summary>这个对话能不能用电脑操作：办事对话、IT 打开了开关、界面已就绪（提示条要能显示）。</summary>
    private IDesktopDriver? DesktopFor(Conversation conv)
    {
        if (conv.Mode != ConversationMode.Agent || Events is null || !DesktopEnabled || System.Windows.Application.Current is null)
        {
            return null;
        }
        return _desktopDrivers.GetOrAdd(conv.Id, id => new UiaDesktopDriver(window => ShowDesktopBanner(id, window)));
    }

    private void ShowDesktopBanner(string conversationId, DesktopWindow window)
    {
        if (System.Windows.Application.Current is not { } app)
        {
            return;
        }
        // 同步等提示条摆好再操作：员工要在 AI 动手之前就看到（异步的话快的操作做完了提示条才出来）。
        // 界面线程忙时最多等 2 秒；提示条出错只记日志，不影响这一步（底座的确认照常生效）。
        try
        {
            app.Dispatcher.Invoke(() => ShowBannerOnUi(app, conversationId, window), DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("电脑操作：提示条显示失败", ex);
        }
    }

    private void ShowBannerOnUi(System.Windows.Application app, string conversationId, DesktopWindow window)
    {
        if (_desktopBanner is null)
        {
            _desktopBanner = new DesktopBanner();
            _desktopBanner.StopRequested += id =>
            {
                Log.Info($"电脑操作：员工点了提示条上的「停止」（对话 {id}）");
                Stop(id);
            };
            // 任务结束（做完、停止、出错）：提示条收起，这个对话的编号作废（下次重新读）
            RunFinished += info =>
            {
                _desktopDrivers.TryRemove(info.ConversationId, out _);
                app.Dispatcher.BeginInvoke(() => _desktopBanner?.HideFor(info.ConversationId));
            };
        }
        _desktopBanner.ShowFor(conversationId, window.Title, window.Handle);
    }
}
