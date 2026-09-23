@echo off
rem ══════════════════════════════════════════════════════════════
rem  编译【启动器】（AgentFramework.Launcher）
rem
rem  启动器不引用本体，所以这条命令**不会**带着本体一起编。
rem  它只在「本体已经编译过」时顺手拷一份到自己的输出目录；
rem  没编过也能跑，运行时会去源码树里找。
rem ══════════════════════════════════════════════════════════════

setlocal
chcp 65001 >nul
cd /d "%~dp0"

tasklist /fi "imagename eq AgentFramework.Launcher.exe" 2>nul | find /i "AgentFramework.Launcher.exe" >nul
if not errorlevel 1 (
    echo.
    echo   启动器还在运行，它锁着构建产物。先关掉那个窗口再编译。
    echo.
    pause
    exit /b 1
)

echo [启动器] 编译中……
dotnet build src\AgentFramework.Launcher --nologo
if errorlevel 1 (
    echo.
    echo   编译失败。若是 CS0009 / 文件被占用：
    echo     dotnet build-server shutdown
    echo   然后删掉 bin 与 obj 重试。
    exit /b 1
)

echo.
echo [启动器] 完成。产物：src\AgentFramework.Launcher\bin\Debug\net10.0\
echo        直接运行：dotnet run --project src\AgentFramework.Launcher -- --no-build
endlocal
