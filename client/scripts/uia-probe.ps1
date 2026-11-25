<#
.SYNOPSIS
  电脑操作（computer use）可行性探测：看 AI 在某个程序窗口里能「看见」哪些控件。

.DESCRIPTION
  FlyknitBuddy 的电脑操作读的是 Windows 自带的 UI Automation（读屏软件用的「无障碍树」），不截图。
  这个脚本用同样的方式读一个窗口，列出能操作的控件（按钮、输入框、表格、菜单……），最后给出结论：
  这个程序适不适合交给 AI 操作。只读，不点、不填、不改任何东西；不需要安装任何组件（Windows 自带）。

  用法：
    1. 先把要测试的程序（ERP、WMS 客户端等）打开，停在常用的界面上；
    2. 在 PowerShell 里运行：
         powershell -ExecutionPolicy Bypass -File uia-probe.ps1
       会列出打开的窗口，输入编号选一个；或者直接指定标题里的关键字：
         powershell -ExecutionPolicy Bypass -File uia-probe.ps1 -Title "入库单"
    3. 结果会显示在屏幕上，同时保存到桌面的 uia-probe-<程序名>.txt，可以发给 IT。

  注意：程序是用「以管理员身份运行」打开的，脚本也要用管理员身份运行才能读到。
#>
param(
    [string]$Title = "",
    [int]$MaxElements = 400
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$root = [System.Windows.Automation.AutomationElement]::RootElement
$windows = @()
foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
    try {
        $c = $w.Current
        if ($c.NativeWindowHandle -eq 0 -or $c.IsOffscreen -or [string]::IsNullOrWhiteSpace($c.Name)) { continue }
        $proc = ""
        try { $proc = (Get-Process -Id $c.ProcessId -ErrorAction Stop).ProcessName } catch {}
        $windows += [pscustomobject]@{ Element = $w; Title = $c.Name; Process = $proc }
    } catch {}
}

if ($windows.Count -eq 0) { Write-Host "没有找到打开的窗口"; exit 1 }

if ($Title) {
    $target = $windows | Where-Object { $_.Title -like "*$Title*" } | Select-Object -First 1
    if (-not $target) { Write-Host "没有标题包含「$Title」的窗口"; exit 1 }
} else {
    Write-Host "打开的窗口："
    for ($i = 0; $i -lt $windows.Count; $i++) {
        Write-Host ("  [{0}] {1}  （{2}.exe）" -f ($i + 1), $windows[$i].Title, $windows[$i].Process)
    }
    $n = Read-Host "输入要探测的窗口编号"
    $target = $windows[[int]$n - 1]
}

$roles = @{
    "ControlType.Button" = "按钮"; "ControlType.Edit" = "输入框"; "ControlType.CheckBox" = "勾选框"; "ControlType.RadioButton" = "单选框";
    "ControlType.ComboBox" = "下拉框"; "ControlType.ListItem" = "列表项"; "ControlType.MenuItem" = "菜单项"; "ControlType.TabItem" = "标签页";
    "ControlType.TreeItem" = "树节点"; "ControlType.DataItem" = "表格单元格"; "ControlType.Hyperlink" = "链接"; "ControlType.SplitButton" = "按钮";
    "ControlType.Spinner" = "数值框"; "ControlType.Slider" = "滑块"
}
$walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
$lines = New-Object System.Collections.Generic.List[string]
$stats = @{ Interactive = 0; Named = 0; Password = 0; Texts = 0; Custom = 0 }
$watch = [Diagnostics.Stopwatch]::StartNew()

function Walk($el, $depth) {
    if ($depth -gt 25 -or $stats.Interactive -ge $MaxElements) { return }
    $child = $null
    try { $child = $walker.GetFirstChild($el) } catch { return }
    while ($child -ne $null) {
        try {
            $c = $child.Current
            if (-not $c.IsOffscreen) {
                $type = $c.ControlType.ProgrammaticName
                $name = ($c.Name -replace "\s+", " ").Trim()
                $indent = "  " * [Math]::Min($depth, 8)
                if ($roles.ContainsKey($type)) {
                    $stats.Interactive++
                    if ($name) { $stats.Named++ }
                    $flag = ""
                    if ($c.IsPassword) { $stats.Password++; $flag = "（密码框）" }
                    if (-not $c.IsEnabled) { $flag += "（不可用）" }
                    $label = if ($name) { "「$name」" } else { "（无名称）" }
                    $lines.Add(("{0}[{1}] {2}{3}{4}" -f $indent, $stats.Interactive, $roles[$type], $label, $flag))
                } elseif ($type -eq "ControlType.Text" -and $name) {
                    $stats.Texts++
                    if ($stats.Texts -le 150) { $lines.Add("$indent文字「$name」") }
                } elseif ($type -in @("ControlType.Custom", "ControlType.Pane") -and -not $name) {
                    $stats.Custom++
                }
                Walk $child ($depth + 1)
            }
        } catch {}
        try { $child = $walker.GetNextSibling($child) } catch { $child = $null }
    }
}

Walk $target.Element 0
$watch.Stop()

$named = if ($stats.Interactive -gt 0) { [Math]::Round(100.0 * $stats.Named / $stats.Interactive) } else { 0 }
if ($stats.Interactive -eq 0) {
    $verdict = "不适合：读不到任何控件（多半是自绘界面、远程桌面里的程序或游戏）。这类程序要等后续的「截图识别」版本，或者优先找接口 / 网页版。"
} elseif ($stats.Interactive -lt 5 -or $named -lt 40) {
    $verdict = "勉强：能读到一部分控件，但大多没有名称，AI 很难判断该点哪个。建议先在常用界面多试几个窗口，或优先找接口 / 网页版。"
} else {
    $verdict = "适合：能读到 $($stats.Interactive) 个可操作控件，其中 $named% 有名称。可以把这个程序加进后台「电脑操作：允许的程序」试用。"
}

$report = @(
    "窗口：$($target.Title)",
    "程序：$($target.Process).exe",
    "可操作控件：$($stats.Interactive) 个（有名称的 $named%），密码框 $($stats.Password) 个，界面文字 $($stats.Texts) 处，无名称的自绘区域 $($stats.Custom) 处",
    "读取耗时：$([Math]::Round($watch.Elapsed.TotalSeconds, 1)) 秒",
    "结论：$verdict",
    "",
    "AI 看到的内容（方括号里是编号）："
) + $lines

$report | ForEach-Object { Write-Host $_ }
$file = Join-Path ([Environment]::GetFolderPath("Desktop")) ("uia-probe-{0}.txt" -f ($target.Process -replace "[^\w\-]", "_"))
$report | Out-File -FilePath $file -Encoding UTF8
Write-Host ""
Write-Host "结果已保存到：$file"
