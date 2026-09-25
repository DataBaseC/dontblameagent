@echo off
rem ============================================================
rem  One-shot: build host + base plugins -> open chat UI
rem  Encoding: MUST be ANSI/GBK (codepage 936). Do NOT save as
rem  UTF-8 and do NOT add chcp 65001 -- cmd parses the script
rem  in the OEM codepage first; UTF-8 Chinese breaks parsing
rem  and the window flashes out immediately.
rem  Build uses -m:1 to avoid CS0009 races on Contracts ref dll.
rem ============================================================

setlocal
cd /d "%~dp0"

rem If a host is already running it locks bin\*.dll and build fails with CS0009.
tasklist /fi "imagename eq AgentFramework.Host.exe" 2>nul | find /i "AgentFramework.Host.exe" >nul
if not errorlevel 1 (
    echo.
    echo   AgentFramework.Host.exe is still running and locks build output.
    echo   Close that window first, or run: taskkill /f /im AgentFramework.Host.exe
    echo.
    pause
    exit /b 1
)

dotnet build-server shutdown >nul 2>&1

echo [1/3] Building host (Debug, single-node) ...
dotnet build src\AgentFramework.Host -c Debug -m:1 --nologo -v quiet
if errorlevel 1 (
    echo.
    echo   Build failed. If CS0009 "file in use", retrying once ...
    dotnet build-server shutdown >nul 2>&1
    if exist "src\AgentFramework.Contracts\obj" rd /s /q "src\AgentFramework.Contracts\obj" 2>nul
    if exist "src\AgentFramework.Contracts\bin" rd /s /q "src\AgentFramework.Contracts\bin" 2>nul
    dotnet build src\AgentFramework.Host -c Debug -m:1 --nologo -v quiet
    if errorlevel 1 (
        echo.
        echo   Build still failed. Try: close other builds, run clean.cmd, retry.
        pause
        exit /b 1
    )
)

rem Base plugins are separate projects; they must land in .\plugins\ to be loaded.
rem Keep this list in sync with pack-launcher.cmd (devkit / writing-kit / console-kit).
echo [2/3] Sync base plugins to plugins\ ...
if not exist "plugins" mkdir "plugins"
dotnet build src\AgentFramework.Plugins.DevKit -c Debug -m:1 --nologo -v quiet
dotnet build src\AgentFramework.Plugins.WritingKit -c Debug -m:1 --nologo -v quiet
dotnet build src\AgentFramework.Plugins.ConsoleKit -c Debug -m:1 --nologo -v quiet

if not exist "plugins\devkit" mkdir "plugins\devkit"
if not exist "plugins\writing-kit" mkdir "plugins\writing-kit"
if not exist "plugins\console-kit" mkdir "plugins\console-kit"
xcopy /y /q "src\AgentFramework.Plugins.DevKit\bin\Debug\net10.0\*" "plugins\devkit" >nul
xcopy /y /q "src\AgentFramework.Plugins.WritingKit\bin\Debug\net10.0\*" "plugins\writing-kit" >nul
xcopy /y /q "src\AgentFramework.Plugins.ConsoleKit\bin\Debug\net10.0\*" "plugins\console-kit" >nul

echo [3/3] Starting chat UI (Ctrl+C to stop) ...
dotnet run --project src\AgentFramework.Host --no-build -- --web %*

echo.
echo Host exited. Press any key to close this window.
pause >nul
endlocal