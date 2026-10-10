<#
  打包员工端（FlyknitBuddy），产物是可以直接上传到管理后台「员工端版本」的 zip。

  版本号会编进程序里：员工电脑上「关于」里看到的、自动更新时上报给服务端的，都是这个号。
  不要只在后台填版本号——程序里还是旧号的话，装完会一直以为「有新版本」，反复下载重装。

  用法（在 client 目录或任意位置）：
    powershell -ExecutionPolicy Bypass -File scripts\publish-client.ps1 -Version 0.2.0

  产物：client\dist\FlyknitBuddy-0.2.0.zip（里面是 FlyknitBuddy 文件夹，含员工端、更新器、运维代理和安装程序外壳）
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Output = "$PSScriptRoot\..\dist",
    # 公司电脑统一装了 .NET 8 Desktop Runtime 时可以加 -FrameworkDependent，包从约 150 MB 变成十几 MB
    [switch]$FrameworkDependent,
    # 界面已经编译过、只改了 C# 时可以跳过 npm
    [switch]$SkipWeb
)

$ErrorActionPreference = "Stop"
$Version = $Version.Trim().TrimStart('v', 'V')
if ($Version -notmatch '^\d+\.\d+\.\d+([-+][0-9A-Za-z.-]+)?$') {
    throw "版本号要写成 0.2.0 这样（可带 -beta.1 之类的后缀），现在是：$Version"
}

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$publish = Join-Path $root "publish\FlyknitBuddy"
New-Item -ItemType Directory -Force -Path $Output | Out-Null
$Output = Resolve-Path $Output
$zip = Join-Path $Output "FlyknitBuddy-$Version.zip"

function Run([string]$what, [scriptblock]$cmd) {
    Write-Host ""
    Write-Host "==> $what" -ForegroundColor Cyan
    & $cmd
    if ($LASTEXITCODE -ne 0) { throw "$what 失败（退出码 $LASTEXITCODE）" }
}

# 1) 聊天界面和登录页
if (-not $SkipWeb) {
    Push-Location (Join-Path $root "web")
    try {
        if (-not (Test-Path "node_modules")) { Run "npm ci" { npm ci } }
        Run "npm run build" { npm run build }
    } finally { Pop-Location }
}

# 2) 主程序和更新器发布到同一个文件夹。先清掉上次的，免得混进旧文件
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
$selfContained = if ($FrameworkDependent) { "false" } else { "true" }
Run "发布 FlyknitBuddy $Version" {
    dotnet publish (Join-Path $root "src\Flyknit.Client") -c Release -r win-x64 --self-contained $selfContained `
        "-p:Version=$Version" -o $publish
}
Run "发布 FlyknitUpdater $Version" {
    dotnet publish (Join-Path $root "src\Flyknit.Updater") -c Release -r win-x64 `
        "-p:Version=$Version" -o $publish
}

# 运维代理和安装程序也放进同一个文件夹：后台「下载员工端安装包」选 exe 时，
# 服务端拿 FlyknitSetup.exe 当外壳拼出 FlyknitBuddy-Setup-x.y.z.exe，IT 双击一次员工端和代理一起装好
Run "发布运维代理 FlyknitAgent $Version" {
    dotnet publish (Join-Path $root "src\Flyknit.Agent") -c Release -r win-x64 `
        "-p:Version=$Version" -p:EnableCompressionInSingleFile=true -o (Join-Path $publish "agent")
}
# 安装程序外壳先发布到一个临时目录，只把 exe 拷进员工端文件夹，用完就删
$setupOut = Join-Path ([System.IO.Path]::GetTempPath()) "FlyknitSetup-publish"
if (Test-Path $setupOut) { Remove-Item $setupOut -Recurse -Force }
Run "发布安装程序 FlyknitSetup $Version" {
    dotnet publish (Join-Path $root "src\Flyknit.Setup") -c Release -r win-x64 `
        "-p:Version=$Version" -o $setupOut
}
Copy-Item (Join-Path $setupOut "FlyknitSetup.exe") $publish -Force
Remove-Item $setupOut -Recurse -Force
# 老版本脚本留下的中间目录
$oldSetup = Join-Path $root "publish\setup"
if (Test-Path $oldSetup) { Remove-Item $oldSetup -Recurse -Force }
# 发布目录里的 pdb 用不上，不打进包
Get-ChildItem $publish -Recurse -Filter *.pdb | Remove-Item -Force

# 3) 核对：编进程序的版本号确实是这次的
$exe = Join-Path $publish "FlyknitBuddy.exe"
if (-not (Test-Path $exe)) { throw "没有生成 FlyknitBuddy.exe" }
if (-not (Test-Path (Join-Path $publish "FlyknitUpdater.exe"))) { throw "没有生成 FlyknitUpdater.exe（缺了它员工端装不上更新）" }
if (-not (Test-Path (Join-Path $publish "agent\FlyknitAgent.exe"))) { throw "没有生成 agent\FlyknitAgent.exe" }
if (-not (Test-Path (Join-Path $publish "FlyknitSetup.exe"))) { throw "没有生成 FlyknitSetup.exe（后台就下载不了 exe 安装程序）" }
$built = (Get-Item $exe).VersionInfo.ProductVersion -replace '\+.*$', ''
if ($built -ne $Version) { throw "FlyknitBuddy.exe 里的版本是 $built，不是 $Version" }

# 4) 打成 zip（里面是 FlyknitBuddy 文件夹）
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $publish -DestinationPath $zip -CompressionLevel Optimal

$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host ""
Write-Host "完成：$zip（$size MB，版本 $Version）" -ForegroundColor Green
Write-Host "下一步：管理后台「员工端版本」→ 上传新版本，版本号填 $Version，确认无误后点「发布」。"
Write-Host "装新电脑：同一页「下载员工端安装包」选「安装程序 .exe」，IT 在员工电脑上双击一次，员工端和运维代理一起装好。"
