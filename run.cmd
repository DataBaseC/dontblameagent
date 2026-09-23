@echo off
rem ══════════════════════════════════════════════════════════════
rem  一键运行：构建 → 起对话界面
rem
rem  为什么要有这个脚本：
rem    dotnet run 每次都会先构建。如果界面还开着（或另开了一个终端在构建），
rem    两次构建就会抢同一个 obj/bin 下的文件，报一大堆 CS0009「文件被占用」。
rem    这个脚本把「构建」和「运行」串成一条线，并且运行阶段用 --no-build，
rem    就不会再触发第二次构建。
rem ══════════════════════════════════════════════════════════════

setlocal
chcp 65001 >nul
cd /d "%~dp0"

rem 先看是不是已经有界面在跑 —— 它会锁住 bin 里的 dll，构建必然失败。
rem （这就是「海量 CS0009 / MSB4018 文件被占用」的真正来源。）
tasklist /fi "imagename eq AgentFramework.Host.exe" 2>nul | find /i "AgentFramework.Host.exe" >nul
if not errorlevel 1 (
    echo.
    echo   检测到还有界面在运行（AgentFramework.Host.exe）。
    echo   它锁着构建产物，请先关掉那个窗口再运行本脚本；
    echo   实在关不掉就在任务管理器里结束它，或者执行：
    echo       taskkill /f /im AgentFramework.Host.exe
    echo.
    pause
    exit /b 1
)

echo [1/2] 构建中（只构建宿主及其依赖，比全量快）……
dotnet build src\AgentFramework.Host --nologo -v quiet
if errorlevel 1 (
    echo.
    echo   构建失败。
    echo   若报的是 CS0009 / 文件被占用，请先按顺序做：
    echo     1^) 停掉所有正在运行的实例（跑着界面的窗口按 Ctrl+C^)
    echo     2^) dotnet build-server shutdown
    echo     3^) 删掉所有 obj 与 bin 目录后重试
    exit /b 1
)

echo [2/2] 启动对话界面（Ctrl+C 退出）……
dotnet run --project src\AgentFramework.Host --no-build -- --web %*

endlocal
