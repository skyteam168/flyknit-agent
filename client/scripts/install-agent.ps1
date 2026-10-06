<#
  安装 FlyknitBuddy 运维代理为 Windows 服务（以 SYSTEM 身份运行）。
  可从组策略「计算机配置 - 启动脚本」调用，装机即生效、全员一致。

  用法（在管理员权限下）：
    powershell -ExecutionPolicy Bypass -File install-agent.ps1 `
        -ServerUrl "https://flyknit.yourcompany.com" `
        -EnrollmentKey "你的注册密钥"

  同一密钥对应服务端的 FLYKNIT_ENROLLMENT_KEY。
#>
param(
    [Parameter(Mandatory = $true)][string]$ServerUrl,
    [Parameter(Mandatory = $true)][string]$EnrollmentKey,
    [string]$InstallDir = "$env:ProgramFiles\FlyknitAgent"
)

$ErrorActionPreference = "Stop"
$ServiceName = "FlyknitAgent"
$exeName = "FlyknitAgent.exe"

# 必须管理员运行
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "请以管理员身份运行本脚本。"
}

# 1) 把 exe 拷到安装目录
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
$src = Join-Path $PSScriptRoot $exeName
if (-not (Test-Path $src)) { throw "找不到 $exeName，请先运行 publish-agent.ps1。" }
$target = Join-Path $InstallDir $exeName

# 升级场景：服务可能正占用旧 exe，先停服务再覆盖
if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}
Copy-Item $src $target -Force

# 2) 写入配置（服务端地址 + 注册密钥），只让 SYSTEM / Administrators 可读
$dataDir = Join-Path $env:ProgramData "Flyknit"
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
$configPath = Join-Path $dataDir "agent.json"

# 保留已注册身份（agent_id / token），只更新地址和密钥，避免重装后变成新设备
$config = @{ server_url = $ServerUrl; enrollment_key = $EnrollmentKey; agent_id = 0; token = "" }
if (Test-Path $configPath) {
    try {
        $existing = Get-Content $configPath -Raw | ConvertFrom-Json
        if ($existing.agent_id) { $config.agent_id = $existing.agent_id }
        if ($existing.token)    { $config.token = $existing.token }
    } catch { }
}
($config | ConvertTo-Json) | Set-Content -Path $configPath -Encoding UTF8

# 锁定配置文件 ACL：只有 SYSTEM 和 Administrators
icacls $configPath /inheritance:r /grant:r "SYSTEM:(F)" "Administrators:(F)" | Out-Null

# 3) 创建 / 更新服务：LocalSystem、开机自启
if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
    sc.exe config $ServiceName binPath= "`"$target`"" start= auto obj= LocalSystem | Out-Null
} else {
    sc.exe create $ServiceName binPath= "`"$target`"" start= auto obj= LocalSystem DisplayName= "FlyknitBuddy 运维代理" | Out-Null
}
sc.exe description $ServiceName "FlyknitBuddy IT 运维代理：采集电脑信息、清理提速、安装软件、系统修复。" | Out-Null

# 失败自动恢复：5 秒后重启，三次之内每次都重启
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null

# 4) 启动
Start-Service $ServiceName
Write-Host "运维代理已安装并启动。日志：$dataDir\agent.log"
