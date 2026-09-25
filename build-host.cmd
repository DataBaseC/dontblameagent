@echo off
rem ============================================================
rem  Build host only (AgentFramework.Host). Encoding: ANSI/GBK.
rem  -m:1 + UseSharedCompilation=false avoids CS0009 ref races.
rem ============================================================
setlocal
cd /d "%~dp0"

tasklist /fi "imagename eq AgentFramework.Host.exe" 2>nul | find /i "AgentFramework.Host.exe" >nul
if not errorlevel 1 (
    echo.
    echo   AgentFramework.Host.exe is running and locks build output. Close it first.
    echo   Or: taskkill /f /im AgentFramework.Host.exe
    echo.
    pause
    exit /b 1
)

dotnet build-server shutdown >nul 2>&1

echo [host] Building (Debug, single-node) ...
dotnet build src\AgentFramework.Host -c Debug -m:1 --nologo /p:UseSharedCompilation=false /nodeReuse:false
if errorlevel 1 (
    echo.
    echo   Build failed. If CS0009 "file in use", wiping obj\ref and retrying once ...
    dotnet build-server shutdown >nul 2>&1
    for /d /r "src" %%d in (obj) do (
        if exist "%%d\Debug\net10.0\ref" rd /s /q "%%d\Debug\net10.0\ref" 2>nul
    )
    dotnet build src\AgentFramework.Host -c Debug -m:1 --nologo /p:UseSharedCompilation=false /nodeReuse:false
    if errorlevel 1 (
        echo.
        echo   Build still failed. Run clean.cmd, close Visual Studio / other builds, retry.
        echo.
        pause
        exit /b 1
    )
)

echo.
echo [host] Done. Output: src\AgentFramework.Host\bin\Debug\net10.0\
echo        Start UI with: run.cmd
pause
endlocal
