using System.Collections.Generic;

namespace Flyknit.Client.Services;

/// <summary>原生界面（托盘菜单、悬浮球提示、注册窗口）的三语文案。Web 界面的文案在 client/web/src/i18n。</summary>
public static class NativeStrings
{
    private static readonly Dictionary<string, Dictionary<string, string>> Table = new()
    {
        ["zh-CN"] = new()
        {
            ["tray.open"] = "打开 Flyknit",
            ["tray.newChat"] = "新建对话",
            ["tray.ball"] = "显示悬浮球",
            ["tray.exit"] = "退出",
            ["ball.tip"] = "有什么可以帮你？",
            ["ball.waiting"] = "有操作等待你确认",
            ["setup.title"] = "连接 Flyknit 服务器",
            ["setup.intro"] = "首次使用需要连接公司的 Flyknit 服务器。服务器地址和注册密钥请向 IT 部门获取。",
            ["setup.server"] = "服务器地址",
            ["setup.key"] = "注册密钥",
            ["setup.connect"] = "连接",
            ["setup.connecting"] = "正在连接…",
            ["setup.failed"] = "连接失败：{0}",
            ["error.webview"] = "缺少 Microsoft Edge WebView2 运行时，请联系 IT 安装后重试。",
            ["confirm.notify"] = "Flyknit 需要你确认一个操作",
            ["loading.title"] = "正在启动 Flyknit…",
            ["loading.subtitle"] = "准备就绪后将自动打开工作区。",
        },
        ["vi-VN"] = new()
        {
            ["tray.open"] = "Mở Flyknit",
            ["tray.newChat"] = "Trò chuyện mới",
            ["tray.ball"] = "Hiện nút nổi",
            ["tray.exit"] = "Thoát",
            ["ball.tip"] = "Tôi có thể giúp gì cho bạn?",
            ["ball.waiting"] = "Có thao tác đang chờ bạn xác nhận",
            ["setup.title"] = "Kết nối máy chủ Flyknit",
            ["setup.intro"] = "Lần đầu sử dụng cần kết nối máy chủ Flyknit của công ty. Vui lòng hỏi bộ phận IT địa chỉ máy chủ và mã đăng ký.",
            ["setup.server"] = "Địa chỉ máy chủ",
            ["setup.key"] = "Mã đăng ký",
            ["setup.connect"] = "Kết nối",
            ["setup.connecting"] = "Đang kết nối…",
            ["setup.failed"] = "Kết nối thất bại: {0}",
            ["error.webview"] = "Thiếu Microsoft Edge WebView2 Runtime, vui lòng liên hệ IT để cài đặt.",
            ["confirm.notify"] = "Flyknit cần bạn xác nhận một thao tác",
            ["loading.title"] = "Đang khởi động Flyknit…",
            ["loading.subtitle"] = "Không gian làm việc sẽ tự mở khi sẵn sàng.",
        },
        ["en-US"] = new()
        {
            ["tray.open"] = "Open Flyknit",
            ["tray.newChat"] = "New chat",
            ["tray.ball"] = "Show floating button",
            ["tray.exit"] = "Exit",
            ["ball.tip"] = "How can I help?",
            ["ball.waiting"] = "An action is waiting for your approval",
            ["setup.title"] = "Connect to the Flyknit server",
            ["setup.intro"] = "Connect to your company's Flyknit server to get started. Ask IT for the server address and enrollment key.",
            ["setup.server"] = "Server address",
            ["setup.key"] = "Enrollment key",
            ["setup.connect"] = "Connect",
            ["setup.connecting"] = "Connecting…",
            ["setup.failed"] = "Couldn't connect: {0}",
            ["error.webview"] = "Microsoft Edge WebView2 Runtime is missing. Ask IT to install it, then try again.",
            ["confirm.notify"] = "Flyknit needs your approval for an action",
            ["loading.title"] = "Starting Flyknit…",
            ["loading.subtitle"] = "Your workspace will open automatically when it's ready.",
        },
    };

    public static string Language { get; set; } = "zh-CN";

    public static string T(string key) =>
        Table.TryGetValue(Language, out var lang) && lang.TryGetValue(key, out var value) ? value
        : Table["zh-CN"].TryGetValue(key, out var fallback) ? fallback
        : key;
}
