@echo off
rem ============================================================
rem  Clean build artifacts: bin / obj / build / out / Payload.
rem  Encoding MUST be ANSI/GBK. Source and plugins\ are kept.
rem ============================================================
setlocal
cd /d "%~dp0"

echo Cleaning bin / obj / build / out / Launcher Payload ...

for /d /r "%~dp0" %%d in (bin obj) do (
    if exist "%%d" rd /s /q "%%d" 2>nul
)

if exist "build" rd /s /q "build" 2>nul
if exist "out" rd /s /q "out" 2>nul
if exist "src\AgentFramework.Launcher\Payload" rd /s /q "src\AgentFramework.Launcher\Payload" 2>nul

echo.
echo Done. Next run.cmd / verify-all.cmd / build-*.cmd will rebuild.
pause
endlocal