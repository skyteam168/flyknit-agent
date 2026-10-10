<#
  发布运维代理（Flyknit.Agent）为自包含单文件 exe。
  产物在 dist\agent\FlyknitAgent.exe，连同 install.ps1、uninstall.ps1 一起下发到各电脑。

  用法：
    powershell -ExecutionPolicy Bypass -File publish-agent.ps1
#>
param(
    [string]$Configuration = "Release",
    # 默认 client\dist\agent（参数默认值里的 $PSScriptRoot 有的 PowerShell 版本拿到的是空的，下面再算）
    [string]$Output = ""
)

$ErrorActionPreference = "Stop"
$proj = Join-Path $PSScriptRoot "..\src\Flyknit.Agent\Flyknit.Agent.csproj"
if (-not $Output) { $Output = Join-Path (Resolve-Path (Join-Path $PSScriptRoot "..")) "dist\agent" }

Write-Host "发布运维代理 -> $Output"
dotnet publish $proj -c $Configuration -r win-x64 --self-contained true `
    /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true `
    -o $Output

Copy-Item (Join-Path $PSScriptRoot "install-agent.ps1")   $Output -Force
Copy-Item (Join-Path $PSScriptRoot "uninstall-agent.ps1") $Output -Force

Write-Host "完成。请把 $Output 下的文件通过组策略分发到员工电脑后运行 install-agent.ps1。"
