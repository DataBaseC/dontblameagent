@echo off
rem ============================================================
rem  pack-launcher.cmd - build and package the launcher
rem
rem  MUST be ANSI/ASCII + CRLF. Do NOT save as UTF-8. Do NOT use
rem  LF-only line endings: cmd.exe then executes rem lines as
rem  commands and the window shows "'...is not recognized'".
rem
rem  Output 1 (recommended): build\pack\green\
rem    AgentFramework.Launcher.exe + host\ + desktop\
rem    Copy the whole folder. No extract on first run.
rem
rem  Output 2 (single file): build\pack\out\AgentFramework.Launcher.exe
rem    Embeds host-bundle.zip; extracts to %LOCALAPPDATA%\AgentFramework\host
rem ============================================================

setlocal
cd /d "%~dp0"

if not defined APPDATA set "APPDATA=%USERPROFILE%\AppData\Roaming"
if not defined LOCALAPPDATA set "LOCALAPPDATA=%USERPROFILE%\AppData\Local"
if not defined TEMP set "TEMP=%USERPROFILE%\AppData\Local\Temp"
if not defined TMP set "TMP=%TEMP%"

set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
if not exist "%DOTNET%" set "DOTNET=dotnet"

set "PACK=build\pack"
set "HOST_OUT=%PACK%\host"
set "DESK_OUT=%PACK%\desktop"
set "GREEN=%PACK%\green"
set "ZIP=%PACK%\host-bundle.zip"
set "PAYLOAD_DIR=src\AgentFramework.Launcher\Payload"

rem Stop running instances so they release bin\*.dll (CS0009 / MSB4018 file-in-use).
rem User asked: pack must clean processes first instead of asking the user to close windows.
echo [0/6] Stopping AgentFramework processes ...
taskkill /f /im AgentFramework.Host.exe >nul 2>&1
taskkill /f /im AgentFramework.Launcher.exe >nul 2>&1
taskkill /f /im AgentFramework.Desktop.exe >nul 2>&1
rem Give the OS a moment to release file handles.
rem ping is used instead of timeout: timeout breaks when stdin is redirected.
ping -n 3 127.0.0.1 >nul

rem Drop build-server locks on Contracts ref/dll.
call :release_locks

echo [1/6] Build host (Release, single-node) ...
"%DOTNET%" build src\AgentFramework.Host -c Release -m:1 --nologo -v quiet
if errorlevel 1 (
    echo   Host build failed once. Retrying after lock release ...
    call :release_locks
    "%DOTNET%" build src\AgentFramework.Host -c Release -m:1 --nologo -v quiet
    if errorlevel 1 (
        echo   Host build failed. Close Visual Studio / other builds, run clean.cmd, retry.
        call :maybe_pause
        exit /b 1
    )
)

echo [2/6] Publish host (self-contained win-x64) ...
if exist "%HOST_OUT%" rmdir /s /q "%HOST_OUT%"
"%DOTNET%" publish src\AgentFramework.Host\AgentFramework.Host.csproj -c Release -r win-x64 --self-contained true -o "%HOST_OUT%" --nologo -v quiet
if errorlevel 1 (
    echo   Host publish failed once. Retrying ...
    call :release_locks
    "%DOTNET%" publish src\AgentFramework.Host\AgentFramework.Host.csproj -c Release -r win-x64 --self-contained true -o "%HOST_OUT%" --nologo -v quiet
    if errorlevel 1 (
        echo   Host publish failed.
        call :maybe_pause
        exit /b 1
    )
)

echo [3/6] Publish base plugins (writing-kit / console-kit) ...
if exist "%HOST_OUT%\plugins" rmdir /s /q "%HOST_OUT%\plugins"
"%DOTNET%" publish src\AgentFramework.Plugins.WritingKit\AgentFramework.Plugins.WritingKit.csproj -c Release -m:1 -o "%HOST_OUT%\plugins\writing-kit" --nologo -v quiet
if errorlevel 1 (
    echo   writing-kit publish failed.
    call :maybe_pause
    exit /b 1
)
"%DOTNET%" publish src\AgentFramework.Plugins.ConsoleKit\AgentFramework.Plugins.ConsoleKit.csproj -c Release -m:1 -o "%HOST_OUT%\plugins\console-kit" --nologo -v quiet
if errorlevel 1 (
    echo   console-kit publish failed.
    call :maybe_pause
    exit /b 1
)

echo [4/6] Publish desktop shell (separate dir) ...
if exist "%DESK_OUT%" rmdir /s /q "%DESK_OUT%"
"%DOTNET%" publish src\AgentFramework.Desktop\AgentFramework.Desktop.csproj -c Release -r win-x64 --self-contained true -o "%DESK_OUT%" --nologo -v quiet
if errorlevel 1 (
    echo   Desktop publish failed.
    call :maybe_pause
    exit /b 1
)

echo [5/6] Assemble green folder + embed zip ...
if exist "%GREEN%" rmdir /s /q "%GREEN%"
mkdir "%GREEN%"
rem NOTE: do not end a quoted path with backslash-quote. cmd eats the quote.
xcopy "%HOST_OUT%" "%GREEN%\host" /e /i /q >nul
mkdir "%GREEN%\host\desktop"
xcopy "%DESK_OUT%" "%GREEN%\host\desktop" /e /i /q >nul
xcopy "%DESK_OUT%" "%HOST_OUT%\desktop" /e /i /q >nul

if exist "%ZIP%" del /q "%ZIP%"
powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path '%HOST_OUT%\*' -DestinationPath '%ZIP%' -Force"
if not errorlevel 1 goto zip_ok
echo   Zip failed, file in use. Retrying ...
call :release_locks
if exist "%ZIP%" del /q "%ZIP%"
powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path '%HOST_OUT%\*' -DestinationPath '%ZIP%' -Force"
if not errorlevel 1 goto zip_ok
echo   Zip failed again. Close Host / Launcher / antivirus scan and retry.
call :maybe_pause
exit /b 1

:zip_ok
if not exist "%PAYLOAD_DIR%" mkdir "%PAYLOAD_DIR%"
copy /y "%ZIP%" "%PAYLOAD_DIR%\host-bundle.zip" >nul

echo [6/6] Publish launcher (green + single-file) ...
if exist "%PACK%\out" rmdir /s /q "%PACK%\out"
"%DOTNET%" publish src\AgentFramework.Launcher\AgentFramework.Launcher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "%GREEN%" --nologo -v quiet
if errorlevel 1 (
    echo   Green launcher publish failed.
    call :maybe_pause
    exit /b 1
)

"%DOTNET%" publish src\AgentFramework.Launcher\AgentFramework.Launcher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "%PACK%\out" --nologo -v quiet
if errorlevel 1 (
    echo   Single-file launcher publish failed.
    call :maybe_pause
    exit /b 1
)

echo.
echo ============ DONE ============
echo   Output 1 (recommended): %GREEN%
echo     Copy AgentFramework.Launcher.exe together with the host\ folder.
echo   Output 2 (single file): %PACK%\out\AgentFramework.Launcher.exe
echo     First run extracts to %LOCALAPPDATA%\AgentFramework\host
echo   Target PC needs no .NET install and no source code.
echo.
call :maybe_pause
endlocal
exit /b 0

:release_locks
dotnet build-server shutdown >nul 2>&1
if exist "src\AgentFramework.Contracts\obj\Release" rd /s /q "src\AgentFramework.Contracts\obj\Release" 2>nul
if exist "src\AgentFramework.Contracts\bin\Release" rd /s /q "src\AgentFramework.Contracts\bin\Release" 2>nul
exit /b 0

:maybe_pause
rem Pause when double-clicked. Skip in scripts:
rem   pack-launcher.cmd --no-pause
rem   set AUTO_PAUSE=1
if defined AUTO_PAUSE exit /b 0
echo %* | findstr /i /c:"--no-pause" >nul
if not errorlevel 1 exit /b 0
pause
exit /b 0