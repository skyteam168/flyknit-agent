# 一键编译并启动 FlyknitBuddy 客户端。
# 由 run.bat 调起（run.bat 保持纯英文，避免 cmd 解析中文时出问题）。
# 不管哪一步失败，窗口都不会关，完整日志在同目录的 build.log。

$ErrorActionPreference = 'Continue'
$root = $PSScriptRoot
$log = Join-Path $root 'build.log'

try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

Set-Content -LiteralPath $log -Encoding UTF8 -Value @(
    "FlyknitBuddy 构建日志  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')",
    "目录: $root"
)

function Note($text) { Write-Host $text -ForegroundColor Cyan }

function Write-Log($text) { Add-Content -LiteralPath $log -Encoding UTF8 -Value $text }

function Stop-Here($what, $hint) {
    Write-Host ''
    Write-Host "================ $what ================" -ForegroundColor Red
    if ($hint) { Write-Host $hint -ForegroundColor Yellow }

    if (Test-Path -LiteralPath $log) {
        $errors = Select-String -LiteralPath $log -Pattern ': error|: 错误|ERR!' -ErrorAction SilentlyContinue
        if ($errors) {
            Write-Host ''
            Write-Host '---------------- 报错行 ----------------'
            $errors | Select-Object -Last 15 | ForEach-Object { Write-Host $_.Line }
        }
        Write-Host ''
        Write-Host '---------------- 日志最后 20 行 ----------------'
        Get-Content -LiteralPath $log -Tail 20
        Write-Host '------------------------------------------------'
    }
    Write-Host ''
    Write-Host "完整日志：$log"
    Write-Host '请把 build.log 这个文件发给开发人员（比截图清楚）。'
    Write-Host ''
    Read-Host '按回车键关闭'
    exit 1
}

# 在当前目录跑一条命令，输出写进日志，返回退出码
function Invoke-Step($commandLine) {
    Write-Log ''
    Write-Log ">>> [$(Get-Location)] $commandLine"
    $output = cmd /c $commandLine 2>&1
    $code = $LASTEXITCODE
    if ($output) { Write-Log ($output | Out-String) }
    return $code
}

# ---------- 0. 源码是否完整 ----------
Note '[0/5] 检查源码是否完整...'
Set-Location -LiteralPath $root

$settings = 'src\Flyknit.Client\Services\AppSettings.cs'
$runner   = 'src\Flyknit.Client\Services\ScheduleRunner.cs'
$missing = @()
if (-not (Test-Path -LiteralPath $settings)) {
    $missing += 'src\Flyknit.Client\Services\AppSettings.cs（文件不存在）'
} elseif (-not (Select-String -LiteralPath $settings -Pattern 'MachineSkills' -Quiet)) {
    $missing += 'src\Flyknit.Client\Services\AppSettings.cs（版本过旧，缺 MachineSkills）'
}
if (-not (Test-Path -LiteralPath $runner))            { $missing += 'src\Flyknit.Client\Services\ScheduleRunner.cs（文件不存在）' }
if (-not (Test-Path -LiteralPath 'web\package.json')) { $missing += 'web\package.json（文件不存在）' }

if ($missing.Count -gt 0) {
    Write-Log "源码不完整: $($missing -join '; ')"
    Stop-Here '源码不完整' ("  " + ($missing -join "`n  ") + "`n`n这台电脑上的源码不是最新的。`n请把完整源码快照解压到一个【空文件夹】再跑，不要覆盖旧目录。")
}

# 改名前的旧文件：解压覆盖不会删文件，留着会和 ScheduleRunner.cs 里的同一个类重名
if (Test-Path -LiteralPath 'src\Flyknit.Client\Services\TaskScheduler.cs') {
    Write-Host '    清理旧文件 Services\TaskScheduler.cs'
    Remove-Item -LiteralPath 'src\Flyknit.Client\Services\TaskScheduler.cs' -Force -ErrorAction SilentlyContinue
}

# ---------- 1. 构建工具 ----------
Note '[1/5] 检查构建工具...'
if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
    Stop-Here '没装 Node.js' '到 https://nodejs.org 装 LTS 版本，装完重开这个窗口。'
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Stop-Here '没装 .NET 8 SDK' '到 https://dotnet.microsoft.com/download/dotnet/8.0 装 SDK（不是 Runtime），装完重开这个窗口。'
}
Invoke-Step 'dotnet --version' | Out-Null
Invoke-Step 'node --version'   | Out-Null

# ---------- 2. 结束正在运行的程序 ----------
Note '[2/5] 结束正在运行的 FlyknitBuddy...'
foreach ($name in 'FlyknitBuddy', 'Flyknit') {
    Get-Process -Name $name -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

# ---------- 3. 前端依赖 ----------
Note '[3/5] 准备前端依赖...'
Set-Location -LiteralPath (Join-Path $root 'web')
# 不能只看 node_modules 在不在：package.json 加了新依赖时目录也还在，
# 直接跳过安装就会在编译时报 "Cannot find module"。
# npm 装完会写 node_modules\.package-lock.json，拿它和 package.json / package-lock.json
# 比时间戳，就能知道依赖是不是跟得上。
$stamp = Join-Path 'node_modules' '.package-lock.json'
$needInstall = $true
$reason = '第一次运行，正在下载依赖，可能要几分钟...'
# .package-lock.json 以点开头，算隐藏文件，Get-Item 要加 -Force 才读得到
if ((Test-Path -LiteralPath 'node_modules') -and (Test-Path -LiteralPath $stamp)) {
    $installedAt = (Get-Item -LiteralPath $stamp -Force).LastWriteTimeUtc
    $newest = @('package.json', 'package-lock.json') |
        Where-Object { Test-Path -LiteralPath $_ } |
        ForEach-Object { (Get-Item -LiteralPath $_ -Force).LastWriteTimeUtc } |
        Sort-Object -Descending | Select-Object -First 1
    if ($newest -and $newest -le $installedAt) {
        $needInstall = $false
    } else {
        $reason = '依赖有更新，正在安装...'
    }
}

if ($needInstall) {
    Write-Host "    $reason"
    if ((Invoke-Step 'npm install') -ne 0) {
        Set-Location -LiteralPath $root
        Stop-Here 'npm install 失败' "多半是网络或代理。公司网要先设代理：`n  npm config set proxy http://代理地址:端口`n  npm config set https-proxy http://代理地址:端口"
    }
}

# ---------- 4. 构建界面 ----------
Note '[4/5] 构建聊天界面...'
if ((Invoke-Step 'npm run build') -ne 0) {
    Set-Location -LiteralPath $root
    Stop-Here '聊天界面构建失败' $null
}
Set-Location -LiteralPath $root

# ---------- 5. 编译客户端 ----------
Note '[5/5] 编译客户端...'
if ((Invoke-Step 'dotnet build src\Flyknit.Client -c Debug -v minimal') -ne 0) {
    Stop-Here '客户端编译失败' $null
}

Write-Host ''
Write-Host '编译完成，正在启动 FlyknitBuddy，右下角会出现悬浮球。' -ForegroundColor Green
Write-Host '这个窗口关掉程序也会退出。'
Write-Host ''

dotnet run --project src\Flyknit.Client --no-build
$code = $LASTEXITCODE

if ($code -ne 0) {
    Write-Host ''
    Write-Host '================ 程序启动后异常退出 ================' -ForegroundColor Red
    $logDir = Join-Path $env:APPDATA 'Flyknit\logs'
    Write-Host "程序日志目录：$logDir"
    $latest = Get-ChildItem -Path (Join-Path $logDir 'flyknit-*.log') -ErrorAction SilentlyContinue |
              Sort-Object LastWriteTime | Select-Object -Last 1
    if ($latest) {
        Write-Host ''
        Write-Host "---------------- $($latest.Name) 最后 40 行 ----------------"
        Get-Content -LiteralPath $latest.FullName -Tail 40
        Write-Host '---------------------------------------------------------'
        Write-Host ''
        Write-Host "请把这个文件发给开发人员：$($latest.FullName)"
    } else {
        Write-Host '还没有生成程序日志，说明在写日志之前就崩了，请把上面的报错截图发给开发人员。'
    }
    Write-Host ''
    Read-Host '按回车键关闭'
    exit 1
}

exit 0
