@echo off
rem ============================================================
rem  Build launcher only. Encoding: ANSI/GBK. -m:1 avoids CS0009.
rem ============================================================
setlocal
cd /d "%~dp0"

tasklist /fi "imagename eq AgentFramework.Launcher.exe" 2>nul | find /i "AgentFramework.Launcher.exe" >nul
if not errorlevel 1 (
    echo.
    echo   Launcher is running and locks build output. Close it first.
    echo.
    pause
    exit /b 1
)

dotnet build-server shutdown >nul 2>&1

echo [launcher] Building (Debug, single-node) ...
dotnet build src\AgentFramework.Launcher -c Debug -m:1 --nologo
if errorlevel 1 (
    echo.
    echo   Build failed. If CS0009 "file in use", run clean.cmd and retry.
    echo.
    pause
    exit /b 1
)

echo.
echo [launcher] Done. Output: src\AgentFramework.Launcher\bin\Debug\net10.0\
echo        Run: dotnet run --project src\AgentFramework.Launcher -- --no-build
pause
endlocal