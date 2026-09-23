@echo off
rem ══════════════════════════════════════════════════════════════
rem  编译【本体】（AgentFramework.Host）
rem
rem  本体与启动器分开编译：改本体不用重编启动器，反之亦然。
rem  启动器运行时自己会去源码树里找本体，所以先编哪个都行。
rem ══════════════════════════════════════════════════════════════

setlocal
chcp 65001 >nul
cd /d "%~dp0"

rem 界面还开着的话，它锁着 bin 里的 dll，构建必然报 CS0009「文件被占用」
tasklist /fi "imagename eq AgentFramework.Host.exe" 2>nul | find /i "AgentFramework.Host.exe" >nul
if not errorlevel 1 (
    echo.
    echo   检测到本体还在运行（AgentFramework.Host.exe），它锁着构建产物。
    echo   先关掉那个窗口，或者执行：taskkill /f /im AgentFramework.Host.exe
    echo.
    pause
    exit /b 1
)

echo [本体] 编译中……
dotnet build src\AgentFramework.Host --nologo
if errorlevel 1 (
    echo.
    echo   编译失败。若报的是 CS0009 / 文件被占用，按顺序做：
    echo     1^) 停掉所有正在运行的实例
    echo     2^) dotnet build-server shutdown
    echo     3^) 删掉 bin 与 obj 后重试
    exit /b 1
)

echo.
echo [本体] 完成。产物：src\AgentFramework.Host\bin\Debug\net10.0\
echo        直接运行：run.cmd
endlocal
