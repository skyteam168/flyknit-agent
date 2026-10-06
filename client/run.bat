@echo off
rem ASCII only on purpose: a .bat with Chinese text can be misparsed by cmd and the
rem window closes before anything is logged. All messages live in run.ps1 instead.
cd /d "%~dp0"

if not exist "%~dp0run.ps1" (
    echo.
    echo [X] run.ps1 is missing next to run.bat.
    echo     Please extract the FULL source snapshot into an EMPTY folder.
    echo.
    pause
    exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run.ps1"
set RC=%ERRORLEVEL%
if not "%RC%"=="0" (
    echo.
    echo [X] Build or startup failed. Full log: %~dp0build.log
    pause
)
exit /b %RC%
