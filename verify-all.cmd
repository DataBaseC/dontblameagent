@echo off
rem ============================================================
rem  Run all 17 verify projects. Encoding MUST be ANSI/GBK.
rem  Expect: every suite reports M failures = 0.
rem
rem  Build uses -m:1 + UseSharedCompilation=false: parallel or
rem  shared-compiler builds can race on ref dlls under obj\ and
rem  produce CS0009 "file in use" plus fake CS0234/CS0246 errors.
rem ============================================================
setlocal
cd /d "%~dp0"

echo [1/2] Building solution (Debug, single-node) ...

rem Release file locks held by the incremental build server / VS.
dotnet build-server shutdown >nul 2>&1

dotnet build AgentFramework.sln -c Debug -m:1 --nologo -v quiet /p:UseSharedCompilation=false /nodeReuse:false
if errorlevel 1 (
    echo.
    echo   Build failed. If CS0009 "file in use" on *.dll under obj\:
    echo   retrying once after build-server shutdown and ref wipe ...
    dotnet build-server shutdown >nul 2>&1
    for /d /r "src" %%d in (obj) do (
        if exist "%%d\Debug\net10.0\ref" rd /s /q "%%d\Debug\net10.0\ref" 2>nul
    )
    for /d /r "tests" %%d in (obj) do (
        if exist "%%d\Debug\net10.0\ref" rd /s /q "%%d\Debug\net10.0\ref" 2>nul
    )
    dotnet build AgentFramework.sln -c Debug -m:1 --nologo -v quiet /p:UseSharedCompilation=false /nodeReuse:false
    if errorlevel 1 (
        echo.
        echo   Build still failed. Close Visual Studio / other builds, run clean.cmd, retry.
        pause
        exit /b 1
    )
)

echo [2/2] Running verify suites ...
rem Serial + 1s gap: suites share plugin DLL outputs; a still-open
rem process causes false "file in use" failures.
for %%n in (Verify VerifyData VerifyAgent VerifyTools VerifySandbox VerifyPlugins VerifyLlm VerifyHost VerifyLauncher VerifyWeb VerifySummary VerifyRephrase VerifyContext VerifyMemory VerifyToolsets VerifyBaseKit VerifyCheckpoint) do (
    echo.
    echo ============== %%n ==============
    dotnet run --project "tests\AgentFramework.%%n\AgentFramework.%%n.csproj" -c Debug --no-build --nologo
    timeout /t 1 /nobreak >nul
)

echo.
echo Done. Every suite must show "M failures = 0".
echo If a suite says file-in-use / missing plugin DLL, wait a second and rerun it.
pause
