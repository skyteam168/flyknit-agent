using System.Collections.Concurrent;
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
        return _desktopDrivers.GetOrAdd(conv.Id, id => new UiaDesktopDriver(window => ShowDesktopBanner(id, window.Title)));
    }

    private void ShowDesktopBanner(string conversationId, string windowTitle)
    {
        if (System.Windows.Application.Current is not { } app)
        {
            return;
        }
        app.Dispatcher.BeginInvoke(() =>
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
            _desktopBanner.ShowFor(conversationId, windowTitle);
        });
    }
}
