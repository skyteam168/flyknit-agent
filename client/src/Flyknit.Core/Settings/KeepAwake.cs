namespace Flyknit.Core.Settings;

/// <summary>要不要阻止系统睡眠、要不要让屏幕保持亮着。</summary>
public readonly record struct AwakeRequest(bool System, bool Display)
{
    public static readonly AwakeRequest None = new(false, false);
}

/// <summary>
/// 锁屏运行：锁屏本身不会让程序停下，真正打断任务的是电脑睡眠（定时任务不触发、收不到服务器下发的指令）。
/// 这里只决定“现在该不该挡住睡眠”，真正调用 Windows 接口（SetThreadExecutionState）的是客户端。
/// - off：不管，按系统设置睡眠；
/// - tasks：只在有任务在跑、或者定时任务快到点时挡住睡眠（默认，空闲时照常睡，不费电）；
/// - awake：一直挡住睡眠，屏幕照常熄灭；
/// - screen：一直挡住睡眠，屏幕也保持亮着。
/// 挡得住的只是“空闲太久自动睡眠”；合盖、开始菜单里点睡眠、按电源键照样会睡。
/// IT 可以在安全中心关掉 keep_awake（只能选“关闭”）或 keep_screen_on（“保持屏幕常亮”按“熄屏后保持唤醒”算）。
/// </summary>
public static class KeepAwake
{
    public const string Off = "off";
    public const string Tasks = "tasks";
    public const string Awake = "awake";
    public const string Screen = "screen";

    public static readonly IReadOnlyList<string> Modes = new[] { Off, Tasks, Awake, Screen };

    /// <summary>定时任务离现在这么近时，tasks 模式就提前挡住睡眠（免得睡过去错过）。</summary>
    public static readonly TimeSpan ScheduleLead = TimeSpan.FromMinutes(15);

    public static string Normalize(string? mode) => Modes.Contains(mode) ? mode! : Tasks;

    /// <summary>按公司策略实际生效的模式。</summary>
    public static string Effective(string? mode, bool allowAwake, bool allowScreen)
    {
        mode = Normalize(mode);
        if (!allowAwake)
        {
            return Off;
        }
        return mode == Screen && !allowScreen ? Awake : mode;
    }

    /// <param name="busy">有任务在跑，或者定时任务快到点。</param>
    public static AwakeRequest Resolve(string? mode, bool allowAwake, bool allowScreen, bool busy) =>
        Effective(mode, allowAwake, allowScreen) switch
        {
            Tasks => busy ? new AwakeRequest(true, false) : AwakeRequest.None,
            Awake => new AwakeRequest(true, false),
            Screen => new AwakeRequest(true, true),
            _ => AwakeRequest.None,
        };
}
