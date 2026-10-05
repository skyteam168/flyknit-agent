@echo off
rem 一键编译并启动 FlyknitBuddy 客户端（在任意目录双击或运行都可以）
rem 出错时窗口不会关闭，完整日志写在本目录的 build.log
setlocal
chcp 65001 >nul
cd /d "%~dp0"
set "LOG=%~dp0build.log"
echo FlyknitBuddy 构建日志 %DATE% %TIME%> "%LOG%"

echo [0/4] 检查构建工具...
where npm >nul 2>&1 || (set "WHAT=没装 Node.js（需要 npm），到 https://nodejs.org 装 LTS 版本" & goto :error)
where dotnet >nul 2>&1 || (set "WHAT=没装 .NET 8 SDK，到 https://dotnet.microsoft.com/download/dotnet/8.0 装 SDK" & goto :error)

echo [1/4] 结束正在运行的 FlyknitBuddy...
taskkill /F /IM FlyknitBuddy.exe >nul 2>&1
taskkill /F /IM Flyknit.exe >nul 2>&1

rem 清掉改名前的旧文件：解压覆盖不会删文件，留着会和 ScheduleRunner.cs 里的同一个类重名
if exist "src\Flyknit.Client\Services\TaskScheduler.cs" (
    echo     清理旧文件 Services\TaskScheduler.cs
    del /q "src\Flyknit.Client\Services\TaskScheduler.cs" >nul 2>&1
)

echo [2/4] 准备前端依赖...
pushd web
if not exist node_modules (
    echo     第一次运行，正在下载依赖，可能要几分钟...
    call npm install >> "%LOG%" 2>&1
    if errorlevel 1 (popd & set "WHAT=npm install 失败（通常是网络或代理问题）" & goto :error)
)

echo [3/4] 构建聊天界面...
call npm run build >> "%LOG%" 2>&1
if errorlevel 1 (popd & set "WHAT=聊天界面构建失败" & goto :error)
popd

echo [4/4] 编译客户端...
dotnet build src\Flyknit.Client -c Debug -v minimal >> "%LOG%" 2>&1
if errorlevel 1 (set "WHAT=客户端编译失败" & goto :errorlines)

echo.
echo 启动 FlyknitBuddy，右下角会出现悬浮球。这个窗口关掉程序也会退出。
dotnet run --project src\Flyknit.Client --no-build
if errorlevel 1 (set "WHAT=程序启动后异常退出" & goto :runtimeerror)
endlocal
exit /b 0

rem ---------- 编译失败：把报错行挑出来显示 ----------
:errorlines
echo.
echo ================ %WHAT% ================
findstr /C:": error" /C:": 错误" "%LOG%"
goto :tail

:error
echo.
echo ================ %WHAT% ================
goto :tail

:tail
echo.
echo ---------------- 日志最后 25 行 ----------------
powershell -NoProfile -Command "Get-Content -Tail 25 -LiteralPath '%LOG%'" 2>nul
echo -----------------------------------------------
echo.
echo 完整日志：%LOG%
echo 请把 build.log 这个文件发给开发人员（比截图清楚）。
pause
endlocal
exit /b 1

rem ---------- 编译成功但运行时崩了：看程序自己的日志 ----------
:runtimeerror
echo.
echo ================ %WHAT% ================
echo 程序日志目录：%APPDATA%\Flyknit\logs
powershell -NoProfile -Command "$f=Get-ChildItem '%APPDATA%\Flyknit\logs\flyknit-*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1; if ($f) { Write-Host \"---------------- $($f.Name) 最后 40 行 ----------------\"; Get-Content -Tail 40 -LiteralPath $f.FullName } else { Write-Host '还没有生成程序日志。' }" 2>nul
echo.
echo 请把 %APPDATA%\Flyknit\logs 里最新的那个 .log 发给开发人员。
pause
endlocal
exit /b 1
