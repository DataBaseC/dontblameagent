@echo off
rem ══════════════════════════════════════════════════════════════
rem  跑全部验证工程（17 个）—— 双击即可，或在包根目录执行 verify-all.cmd
rem
rem  预期：全部 0 失败（总数以每次输出为准，不要写死）
rem        · VerifyTools 里有 4 条要创建符号链接：Windows 非开发者模式下会打印
rem          [SKIP] 跳过，于是总数少 4 条 —— 那是环境限制，不是失败
rem        · VerifySandbox 里有 2 条 Job Object 专有项：非 Windows 上跳过（Windows 上转 PASS）
rem
rem  注意：不要给这个脚本加 --artifacts-path 或换输出根 ——
rem        Verify/VerifyHost/VerifyLauncher 的 CopySamplePlugin 硬编码了
rem        src\AgentFramework.SamplePlugin\bin\<配置>\<TFM>，换了输出根就复制不到插件。
rem ══════════════════════════════════════════════════════════════
setlocal
chcp 65001 >nul
cd /d "%~dp0"

echo [1/2] 编译（首次会 restore，需联网，约 1 分钟）……
dotnet build AgentFramework.sln -c Debug --nologo -v quiet
if errorlevel 1 (
    echo.
    echo   编译失败 —— 先把编译错误解决掉再跑验证。
    pause
    exit /b 1
)

echo [2/2] 逐个跑验证工程……
for %%n in (Verify VerifyData VerifyAgent VerifyTools VerifyHost VerifyLauncher VerifyWeb VerifySummary VerifyRephrase VerifyContext VerifyMemory VerifyLlm VerifyPlugins VerifyBaseKit VerifySandbox VerifyToolsets VerifyCheckpoint) do (
    echo.
    echo ============== %%n ==============
    dotnet run --project "tests\AgentFramework.%%n\AgentFramework.%%n.csproj" -c Debug --no-build --nologo
)

echo.
echo 全部跑完。逐个对一下每个工程的「结果：N 通过 / M 失败」——M 必须为 0。
pause
