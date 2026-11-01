using System;
using Flyknit.Client.Windows;
using Flyknit.Core.Agent;
using Flyknit.Core.Browser;
using Flyknit.Core.Storage;

namespace Flyknit.Client.Services;

/// <summary>
/// AI 浏览器：IT 在安全中心打开「浏览器操作」（默认关）后，办事对话里才有浏览器工具。
/// 窗口第一次用到时才建，之后一直留着（关闭只是隐藏），登录过的内部系统不用每次重新登录。
/// </summary>
public sealed partial class AgentHost
{
    private BrowserWindow? _browserWindow;

    /// <summary>这一轮能不能用浏览器：办事对话、界面已就绪（窗口要能显示出来）、IT 打开了开关。</summary>
    private IBrowserDriver? BrowserFor(Conversation conv)
    {
        if (conv.Mode != ConversationMode.Agent || Events is null
            || !Security.On(Flyknit.Core.Security.SecuritySettings.BrowserTools, fallback: false)
            || System.Windows.Application.Current is not { } app)
        {
            return null;
        }
        return _browserWindow ??= app.Dispatcher.Invoke(() =>
        {
            var window = new BrowserWindow();
            window.StateChanged += (page, action, shot) => Events?.Post(new
            {
                type = "browser.state",
                active = true,
                url = page?.Url ?? "",
                title = page?.Title ?? "",
                action,
                shot,
            });
            return window;
        });
    }

    /// <summary>员工点了「显示浏览器窗口」。还没用过浏览器时什么都不做。</summary>
    public void ShowBrowser()
    {
        if (_browserWindow is { } w)
        {
            w.Dispatcher.Invoke(() =>
            {
                w.Show();
                w.Activate();
            });
        }
    }
}
