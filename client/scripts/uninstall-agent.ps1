<#
  卸载 FlyknitBuddy 运维代理。保留配置和日志，方便排查；加 -Purge 一并清除。

  用法（管理员）：
    powershell -ExecutionPolicy Bypass -File uninstall-agent.ps1 [-Purge]
#>
param(
    [switch]$Purge,
    [string]$InstallDir = "$env:ProgramFiles\FlyknitAgent"
)

$ErrorActionPreference = "SilentlyContinue"
$ServiceName = "FlyknitAgent"

if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service $ServiceName -Force
    Start-Sleep -Seconds 2
    sc.exe delete $ServiceName | Out-Null
    Write-Host "已停止并删除服务 $ServiceName。"
} else {
    Write-Host "服务 $ServiceName 未安装。"
}

if (Test-Path $InstallDir) {
    Remove-Item $InstallDir -Recurse -Force
}

if ($Purge) {
    $dataDir = Join-Path $env:ProgramData "Flyknit"
    Remove-Item (Join-Path $dataDir "agent.json") -Force
    Remove-Item (Join-Path $dataDir "agent.log") -Force
    Remove-Item (Join-Path $dataDir "agent.log.1") -Force
    Remove-Item (Join-Path $dataDir "work") -Recurse -Force
    Write-Host "已清除代理配置、日志与工作目录。"
}

Write-Host "卸载完成。"
