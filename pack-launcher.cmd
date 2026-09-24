@echo off
rem ============================================================
rem  打包【启动器】—— 两种分发形态，一次产出
rem
rem  产物 1（推荐）：build\pack\green\
rem    AgentFramework.Launcher.exe + host\ + desktop\ 三个一起拷走即用。
rem    零解压、双击秒开；桌面壳 / 宿主 / 示例插件全是真文件，杀软最友好。
rem
rem  产物 2（备用）：build\pack\out\AgentFramework.Launcher.exe
rem    单文件，内嵌本体载荷，首跑后台解压到 %LOCALAPPDATA%\AgentFramework\host。
rem    页面秒开；解压期间点「启动宿主」会提示进度而不是报错。
rem    适合「就发一个文件」的场景；代价是首跑解压较慢（杀软逐文件扫描）。
rem
rem  开发流程（dotnet run / build-host.cmd / run.cmd）完全不受影响：
rem  没有 Payload\host-bundle.zip 时，启动器就是普通构建。
rem
rem  注意：本脚本必须保存为 ANSI/GBK 编码（中文 Windows 的 cmd 默认代码页），
rem  不要加 chcp 65001 —— UTF-8 批处理在 chcp 后会因字节重定位错位而碎行。
rem ============================================================

setlocal
cd /d "%~dp0"

rem NuGet restore needs standard shell env vars (APPDATA/LOCALAPPDATA/TEMP).
rem Some hosts (agent sandboxes, stripped-down terminals) launch cmd without them,
rem which makes dotnet restore fail with: Value cannot be null (Parameter 'path1').
rem Bootstrap them here; setlocal keeps this script's env changes contained.
if not defined APPDATA set "APPDATA=%USERPROFILE%\AppData\Roaming"
if not defined LOCALAPPDATA set "LOCALAPPDATA=%USERPROFILE%\AppData\Local"
if not defined TEMP set "TEMP=%USERPROFILE%\AppData\Local\Temp"
if not defined TMP set "TMP=%TEMP%"

rem 优先用标准安装位置的 dotnet（PATH 上的可能是别的程序自带的运行时，没有 SDK）。
set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
if not exist "%DOTNET%" set "DOTNET=dotnet"

set "PACK=build\pack"
set "HOST_OUT=%PACK%\host"
set "DESK_OUT=%PACK%\desktop"
set "GREEN=%PACK%\green"
set "ZIP=%PACK%\host-bundle.zip"
set "PAYLOAD_DIR=src\AgentFramework.Launcher\Payload"

tasklist /fi "imagename eq AgentFramework.Launcher.exe" 2>nul | find /i "AgentFramework.Launcher.exe" >nul
if not errorlevel 1 (
    echo.
    echo   启动器还在运行，它锁着构建产物。先关掉那个窗口再打包。
    echo.
    pause
    exit /b 1
)

echo [1/6] 编译本体（Release）……
"%DOTNET%" build src\AgentFramework.Host -c Release --nologo -v quiet
if errorlevel 1 (
    echo   本体编译失败。
    exit /b 1
)

echo [2/6] 发布本体（自包含 win-x64）……
if exist "%HOST_OUT%" rmdir /s /q "%HOST_OUT%"
"%DOTNET%" publish src\AgentFramework.Host\AgentFramework.Host.csproj -c Release -r win-x64 --self-contained true -o "%HOST_OUT%" --nologo -v quiet
if errorlevel 1 (
    echo   本体发布失败。
    exit /b 1
)

echo [3/6] 收入示例插件……
if exist "%HOST_OUT%\plugins" rmdir /s /q "%HOST_OUT%\plugins"
"%DOTNET%" publish src\AgentFramework.SamplePlugin\AgentFramework.SamplePlugin.csproj -c Release -o "%HOST_OUT%\plugins\hello" --nologo -v quiet
if errorlevel 1 (
    echo   示例插件发布失败。
    exit /b 1
)

rem ---- Base plugins: devkit / writing-kit / console-kit ----
rem These ship with the host and are built from src\AgentFramework.Plugins.*
echo [3b/6] Base plugins (devkit / writing-kit / console-kit)
"%DOTNET%" publish src\AgentFramework.Plugins.DevKit\AgentFramework.Plugins.DevKit.csproj -c Release -o "%HOST_OUT%\plugins\devkit" --nologo -v quiet
if errorlevel 1 (
    echo   devkit publish failed.
    exit /b 1
)
"%DOTNET%" publish src\AgentFramework.Plugins.WritingKit\AgentFramework.Plugins.WritingKit.csproj -c Release -o "%HOST_OUT%\plugins\writing-kit" --nologo -v quiet
if errorlevel 1 (
    echo   writing-kit publish failed.
    exit /b 1
)
"%DOTNET%" publish src\AgentFramework.Plugins.ConsoleKit\AgentFramework.Plugins.ConsoleKit.csproj -c Release -o "%HOST_OUT%\plugins\console-kit" --nologo -v quiet
if errorlevel 1 (
    echo   console-kit publish failed.
    exit /b 1
)

echo [4/6] 发布桌面壳（原生窗口；单独目录，避免覆盖宿主 runtimeconfig）……
if exist "%DESK_OUT%" rmdir /s /q "%DESK_OUT%"
"%DOTNET%" publish src\AgentFramework.Desktop\AgentFramework.Desktop.csproj -c Release -r win-x64 --self-contained true -o "%DESK_OUT%" --nologo -v quiet
if errorlevel 1 (
    echo   桌面壳发布失败。
    exit /b 1
)

echo [5/6] 组装绿色文件夹 + 压制内嵌包……
rem 绿色形态：Launcher.exe 稍后直接落进来；host（含 desktop 子目录）全是真文件。
if exist "%GREEN%" rmdir /s /q "%GREEN%"
mkdir "%GREEN%"
xcopy "%HOST_OUT%" "%GREEN%\host\" /e /i /q >nul
mkdir "%GREEN%\host\desktop"
xcopy "%DESK_OUT%" "%GREEN%\host\desktop\" /e /i /q >nul

rem 单文件形态的内嵌包 = host + desktop 一起压（desktop 先拷进 HOST_OUT，条目名才带 desktop\）。
xcopy "%DESK_OUT%" "%HOST_OUT%\desktop\" /e /i /q >nul

if exist "%ZIP%" del /q "%ZIP%"
powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path '%HOST_OUT%\*' -DestinationPath '%ZIP%' -Force"
if errorlevel 1 (
    echo   压包失败。
    exit /b 1
)

if not exist "%PAYLOAD_DIR%" mkdir "%PAYLOAD_DIR%"
copy /y "%ZIP%" "%PAYLOAD_DIR%\host-bundle.zip" >nul

echo [6/6] 发布启动器两个形态……
rem 6a) 绿色形态启动器（自包含单文件；也嵌一份载荷作冗余备份，运行时用的是 exe 旁的 host 文件夹）。
if exist "%PACK%\out" rmdir /s /q "%PACK%\out"
"%DOTNET%" publish src\AgentFramework.Launcher\AgentFramework.Launcher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "%GREEN%" --nologo -v quiet
if errorlevel 1 (
    echo   绿色版启动器发布失败。
    exit /b 1
)

rem 6b) 单文件形态（内嵌载荷）。
"%DOTNET%" publish src\AgentFramework.Launcher\AgentFramework.Launcher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "%PACK%\out" --nologo -v quiet

echo.
echo ============ 完成 ============
echo   产物 1（推荐）：%GREEN%
echo     把里面的 AgentFramework.Launcher.exe 连同 host 文件夹整个文件夹拷走即可。
echo     零解压、双击秒开，桌面壳 / 插件全是真文件。
echo   产物 2（备用）：%PACK%\out\AgentFramework.Launcher.exe
echo     单文件；首跑后台解压到 %%LOCALAPPDATA%%\AgentFramework\host（页面立即打开，进度可见）。
echo   两种形态目标机都不需要装 .NET、不需要源码。
endlocal
