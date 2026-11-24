namespace Flyknit.Core.Desktop;

/// <summary>桌面上的一个顶层窗口。</summary>
/// <param name="Handle">窗口句柄（AI 用它指定要操作哪个窗口）。</param>
/// <param name="Process">进程名（不带 .exe），IT 的程序白名单按它判断。</param>
public sealed record DesktopWindow(long Handle, string Title, string Process, int ProcessId);

/// <summary>
/// 窗口里的一个控件（来自 Windows UI Automation，也就是读屏软件读的那棵「无障碍树」）。
/// 能操作的控件有编号（AI 点击、填写时用编号）；纯文字没有编号，只作为界面内容给 AI 看。
/// </summary>
public sealed record DesktopElement
{
    /// <summary>编号，从 1 开始；0 表示不能操作（纯文字）。</summary>
    public int Index { get; init; }

    /// <summary>控件类型（button / edit / checkbox / combobox / list_item / menu_item / tab / tree_item / cell / link / text …）。</summary>
    public string Role { get; init; } = "";

    public string Name { get; init; } = "";

    /// <summary>输入框、下拉框当前的值；密码框永远是空的（不读出来）。</summary>
    public string Value { get; init; } = "";

    public bool Enabled { get; init; } = true;

    /// <summary>密码框（UI Automation 的 IsPassword）。AI 不能往里填。</summary>
    public bool IsPassword { get; init; }

    /// <summary>勾选框、单选框是否选中；null = 不是这类控件。</summary>
    public bool? Checked { get; init; }

    /// <summary>缩进层级（树形菜单、分组），只用于排版。</summary>
    public int Depth { get; init; }

    private static readonly Dictionary<string, string> RoleNames = new(StringComparer.Ordinal)
    {
        ["button"] = "按钮", ["edit"] = "输入框", ["checkbox"] = "勾选框", ["radio"] = "单选框", ["combobox"] = "下拉框",
        ["list_item"] = "列表项", ["menu_item"] = "菜单项", ["tab"] = "标签页", ["tree_item"] = "树节点", ["cell"] = "表格单元格",
        ["link"] = "链接", ["text"] = "文字", ["document"] = "文档", ["split_button"] = "按钮", ["spinner"] = "数值框", ["slider"] = "滑块",
    };

    /// <summary>给模型看的一行。</summary>
    public string Describe()
    {
        var role = RoleNames.TryGetValue(Role, out var r) ? r : Role;
        var indent = new string(' ', Math.Min(Depth, 8) * 2);
        var label = Name.Length > 0 ? $"「{Clip(Name, 80)}」" : "（无名称）";
        var extra = new List<string>();
        if (IsPassword)
        {
            extra.Add("密码框，AI 不能填写");
        }
        else if (Value.Length > 0 && Value != Name)
        {
            extra.Add($"当前值「{Clip(Value, 80)}」");
        }
        if (Checked is { } c)
        {
            extra.Add(c ? "已选中" : "未选中");
        }
        if (!Enabled)
        {
            extra.Add("不可用");
        }
        var tail = extra.Count > 0 ? "（" + string.Join("，", extra) + "）" : "";
        return Index > 0 ? $"{indent}[{Index}] {role}{label}{tail}" : $"{indent}{role}{label}{tail}";
    }

    private static string Clip(string s, int max)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length > max ? s[..max] + "…" : s;
    }
}

/// <summary>读一次窗口得到的内容。</summary>
public sealed record DesktopSnapshot(DesktopWindow Window, IReadOnlyList<DesktopElement> Elements, bool Truncated = false)
{
    public DesktopElement? Element(int index) => index <= 0 ? null : Elements.FirstOrDefault(e => e.Index == index);
}

/// <summary>一次操作的结果：成功时附上操作后的窗口内容（AI 不用再读一遍）。</summary>
public sealed record DesktopActionResult(bool Ok, string Error, DesktopSnapshot? Snapshot)
{
    public static DesktopActionResult Done(DesktopSnapshot? snapshot) => new(true, "", snapshot);
    public static DesktopActionResult Fail(string error, DesktopSnapshot? snapshot = null) => new(false, error, snapshot);
}

/// <summary>
/// 桌面操作的驱动。Windows 上由宿主用 UI Automation 实现（Flyknit.Client/Desktop/UiaDesktopDriver）；
/// Core 只认这个接口，单测用假驱动。驱动只管「怎么读、怎么点」，能不能操作由 <see cref="DesktopRisk"/> 判定。
/// </summary>
public interface IDesktopDriver
{
    /// <summary>桌面上看得见的顶层窗口（不含最小化到看不见的、没有标题的）。</summary>
    Task<IReadOnlyList<DesktopWindow>> WindowsAsync(CancellationToken ct);

    /// <summary>读一个窗口的控件。<paramref name="handle"/> 为 0 时读上一次读过的那个窗口。</summary>
    Task<DesktopActionResult> ReadAsync(long handle, CancellationToken ct);

    /// <summary>上一次读到（或操作后）的窗口内容；还没读过是 null。编号都以它为准。</summary>
    DesktopSnapshot? Last { get; }

    Task<DesktopActionResult> ClickAsync(int index, CancellationToken ct);

    /// <summary>在编号为 index 的输入框里填入文字（替换原有内容）；submit = 填完按回车。</summary>
    Task<DesktopActionResult> TypeAsync(int index, string text, bool submit, CancellationToken ct);

    /// <summary>在当前窗口按键（已由 <see cref="DesktopKeys"/> 校验过的组合，如 "Tab"、"Ctrl+S"）。</summary>
    Task<DesktopActionResult> KeyAsync(DesktopKeys.Combo keys, CancellationToken ct);

    /// <summary>滚动：index 为 0 时滚动整个窗口；direction 为 up / down / left / right。</summary>
    Task<DesktopActionResult> ScrollAsync(int index, string direction, int amount, CancellationToken ct);
}
