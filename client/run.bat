@echo off
rem 一键编译并启动 FlyknitBuddy 客户端（在任意目录双击或运行都可以）
chcp 65001 >nul
cd /d "%~dp0"

echo [1/3] 结束正在运行的 FlyknitBuddy...
taskkill /F /IM FlyknitBuddy.exe >nul 2>&1
taskkill /F /IM Flyknit.exe >nul 2>&1

echo [2/3] 构建聊天界面...
pushd web
if not exist node_modules call npm install || goto :error
call npm run build || goto :error
popd

echo [3/3] 编译并启动客户端...
dotnet run --project src\Flyknit.Client
goto :eof

:error
popd
echo.
echo 构建失败，请把上面的错误截图发给开发人员。
pause
