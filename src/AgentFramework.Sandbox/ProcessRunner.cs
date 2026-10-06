using System.Diagnostics;
using System.Text;
using AgentFramework.Contracts;

namespace AgentFramework.Sandbox;

/// <summary>
/// 「跑一条命令并收干净输出」的公共实现 —— 两个内置后端共用。
///
/// <para>
/// 抽出来是因为<b>护栏不同、收尾必须相同</b>：不管有没有 Job Object，
/// 输出都要过程中封顶、超时都要连子孙一起终结、stderr 与 stdout 都要分开收。
/// 各写一份的话，迟早出现「job 档位少收了一半输出」这种只在某些机器上复现的毛病。
/// </para>
/// </summary>
internal static class ProcessRunner
{
    internal sealed record RunOptions(
        string Command,
        string WorkingDirectory,
        string? TempDirectory,
        SandboxLimits Limits,
        IReadOnlyList<string> Notes,
        Action<Process>? OnStarted,
        Action? OnTerminate,
        /// <summary>会话钉死的 shell。null = 自动解析（优先 POSIX，只解析这一次的调用方应自己缓存）。</summary>
        ShellSpec? Shell = null,
        /// <summary>
        /// 可选包装器（安全审查 T10）：非空时以 <c>Wrapper[0]</c> 作为外层可执行文件，
        /// <c>Wrapper[1..]</c> 原样作为它的前置参数（通常以真正的 shell 可执行文件结尾），
        /// 之后再追加 shell 自己的参数（<c>-c &lt;command&gt;</c> 等）。
        /// 用于把命令塞进 bwrap / docker 这类隔离器；null = 直接跑 shell。
        /// </summary>
        IReadOnlyList<string>? Wrapper = null,
        /// <summary>
        /// 在白名单之外<b>补</b>进子进程的环境变量。白名单是「不含密钥」的窄名单，
        /// 但某些后端本身需要额外变量才能干活（如容器运行时靠 <c>DOCKER_HOST</c> 找 socket）。
        /// 补进来的键由后端负责保证不含密钥。
        /// </summary>
        IReadOnlyDictionary<string, string>? ExtraEnvironment = null);

    public static async Task<SandboxOutcome> RunAsync(RunOptions run, CancellationToken ct)
    {
        var notes = new List<string>(run.Notes);

        var shell = run.Shell ?? ShellResolver.Resolve();

        var startInfo = new ProcessStartInfo
        {
            // 包装器（如 bwrap）非空时，由它当外层可执行文件；真正的 shell 在它的参数里（见下）。
            FileName = run.Wrapper is { Count: > 0 } ? run.Wrapper[0] : shell.FileName,
            WorkingDirectory = run.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (run.Wrapper is { Count: > 1 })
        {
            // 包装器参数原样前置（通常以真正的 shell 可执行文件结尾）。
            for (var i = 1; i < run.Wrapper.Count; i++)
            {
                startInfo.ArgumentList.Add(run.Wrapper[i]);
            }
        }

        if (shell.IsPosix)
        {
            // ArgumentList 的 Win32 引号规则只服务 CreateProcess；POSIX shell 走 -c 一个字符串
            // 时把整条命令交给 shell 自己解析，语义稳定。
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(run.Command);
        }
        else if (shell.Id == "cmd")
        {
            // ★ ArgumentList 的 Win32 引号规则 cmd.exe 不认（.NET 官方文档明确警告不要
            //   对 cmd/bat 用 ArgumentList）：含引号的命令会被解析成畸形转义。
            //   安全性不靠引号规则 —— 把关在审批层与沙箱层。
            startInfo.Arguments = $"/c {run.Command}";
        }
        else
        {
            // powershell / pwsh：-Command 收整条命令
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(run.Command);
        }

        // ── 环境白名单 ────────────────────────────────────────────
        // 宿主环境里可能有模型 / 搜索 API key（AGENT_*_KEY 等）。子进程一旦能
        // `echo %VAR%`，密钥就外带了 —— 「密钥不出门」必须覆盖命令通道。
        // 整表清空后只放行命令行工具真正需要的那几个变量。
        startInfo.Environment.Clear();
        foreach (var (key, value) in SafeEnvironment(shell))
        {
            startInfo.Environment[key] = value;
        }

        // POSIX-on-Windows：补上 shell 自己的家当目录（coreutils 才找得到）
        AugmentPosixPath(startInfo, shell);

        // ── 临时目录重定向 ────────────────────────────────────────
        // 没有这一步，命令想「随手写点临时文件」就落到系统临时目录去了 —— 那是最容易
        // 绕过「写只能在工作区内」的一条暗道。把 TMP/TEMP/TMPDIR 指进工作区后，
        // 命令的落地全在同一个地方：可审计、可清理，也在写边界之内。
        //
        // POSIX-on-Windows（Git Bash / MSYS）有个坑：给它 Windows 形式的路径，
        // msys 运行时会把 TMPDIR 改写成 /tmp —— 重定向**静默失效**（实测）。
        // 所以 POSIX 工具用的 TMPDIR 必须给 POSIX 形式（/c/Users/…），它才照单全收；
        // TMP/TEMP 仍留 Windows 形式 —— 从 bash 里唤起的原生 exe（dotnet、ping…）只认它。
        // 两类工具各得其所，「临时文件不出工作区」的承诺才在每种 shell 下都成立。
        if (!string.IsNullOrWhiteSpace(run.TempDirectory))
        {
            Directory.CreateDirectory(run.TempDirectory);
            startInfo.Environment["TMP"] = run.TempDirectory;
            startInfo.Environment["TEMP"] = run.TempDirectory;
            startInfo.Environment["TMPDIR"] = shell.IsPosix && OperatingSystem.IsWindows()
                ? ToMsysPath(run.TempDirectory)
                : run.TempDirectory;
        }

        using var process = new Process { StartInfo = startInfo };

        // Windows cmd 的 stdout/stderr 是 OEM 码页（中文机=GBK/936）字节流。
        // Process 默认按 UTF-8 解 → 「'pwd' 不是内部或外部命令」变乱码。
        // POSIX shell（Git Bash 等）与 PowerShell 默认 UTF-8，别用 OEM 去解。
        if (!shell.IsPosix && shell.Id == "cmd")
        {
            try
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                var oem = Encoding.GetEncoding(
                    System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
                startInfo.StandardOutputEncoding = oem;
                startInfo.StandardErrorEncoding = oem;
            }
            catch
            {
                // 缺码页包 / 非典型区域设置：退回 UTF-8，至少别让命令跑挂
            }
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var ioGate = new object();
        var truncated = false;

        void AppendCapped(StringBuilder target, string line)
        {
            lock (ioGate)
            {
                var remaining = run.Limits.MaxOutputChars - target.Length;
                if (remaining <= 0)
                {
                    truncated = true;
                    return;
                }

                // 单行也要封顶：无换行的超长单行（压缩成一行的超大 JSON/日志）不能整段进缓冲。
                if (line.Length > remaining)
                {
                    line = line[..remaining];
                    truncated = true;
                }

                target.AppendLine(line);
            }
        }

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                AppendCapped(stdout, e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                AppendCapped(stderr, e.Data);
            }
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            notes.Add($"命令未能启动：{ex.Message}");
            return new SandboxOutcome(-1, string.Empty, string.Empty, false, false, false, notes);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // 进程一起来就交给调用方：Job 后端在这一刻把它塞进作业。
        // 早一步都不行 —— 晚了的话命令可能已经起好了自己的子进程，那些就漏在作业之外了。
        try
        {
            run.OnStarted?.Invoke(process);
        }
        catch (Exception ex)
        {
            notes.Add($"护栏挂载失败（命令继续跑，但少一层保护）：{ex.Message}");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, run.Limits.TimeoutSeconds)));

        var timedOut = false;
        var cancelled = false;

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            // 再同步等一次，确保异步输出流已经排空（否则可能丢尾部输出）
            process.WaitForExit();
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            cancelled = ct.IsCancellationRequested;
            Terminate(process, run.OnTerminate);

            // v3.6 审查修复：终止后也要把异步输出排空 —— 否则 OutputDataReceived 回调
            // 可能仍在别的线程 AppendLine，而下面要读 StringBuilder，那是数据竞争。
            // 参数无关的 WaitForExit() 会等异步输出处理完成（.NET 文档明示）。
            try
            {
                process.WaitForExit();
            }
            catch
            {
                // 进程已终止，等不到也无妨 —— 下面读取另有 ioGate 兜底。
            }
        }

        string stdoutText;
        string stderrText;
        lock (ioGate)
        {
            stdoutText = stdout.ToString();
            stderrText = stderr.ToString();
        }

        return new SandboxOutcome(
            SafeExitCode(process),
            stdoutText,
            stderrText,
            timedOut,
            cancelled,
            truncated,
            notes);
    }

    /// <summary>
    /// 终结：先让后端自己动手（Job 的 <c>TerminateJobObject</c> 比逐个杀干净得多），
    /// 再用 .NET 的进程树终结兜一层 —— 单靠前者在「作业外还挂了进程」时会漏。
    /// </summary>
    private static void Terminate(Process process, Action? onTerminate)
    {
        try
        {
            onTerminate?.Invoke();
        }
        catch (Exception)
        {
            // 后端终结失败不能挡住下一步兜底
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // 进程可能已经退出 —— 忽略
        }
    }

    /// <summary>
    /// 命令进程可见的环境白名单：只放行 shell / 常见工具链真正需要的变量。
    /// 模型密钥、代理凭证、云厂商 token 等一律不进子进程。
    ///
    /// 白名单本身下沉到 <see cref="ProcessEnvironment"/>（契约层）——
    /// 命令沙箱与 MCP 子进程共用同一份，「密钥不出门」的纪律只有一处。
    /// </summary>
    private static IEnumerable<KeyValuePair<string, string>> SafeEnvironment(ShellSpec shell)
        => ProcessEnvironment.Allowlist(shell.IsPosix || shell.Id is "pwsh" or "powershell");

    /// <summary>
    /// Windows 路径 → MSYS/POSIX 形式（<c>C:\x\y</c> → <c>/c/x/y</c>）。
    /// Git Bash 只认这种形式的 TMPDIR；Windows 形式会被 msys 运行时改写成 /tmp。
    /// </summary>
    private static string ToMsysPath(string path)
    {
        var full = Path.GetFullPath(path).Replace('\\', '/');
        return full.Length >= 2 && full[1] == ':'
            ? "/" + char.ToLowerInvariant(full[0]) + full[2..]
            : full;
    }

    /// <summary>
    /// POSIX shell 在 Windows 上的 PATH 补全：Git Bash 的 coreutils（sleep / seq / touch…）
    /// 住在 <c>&lt;git&gt;\usr\bin</c>，而 Windows PATH 上通常只有 <c>Git\cmd</c> ——
    /// 于是 <c>bash -c "sleep 5"</c> 都会「command not found」（实测）。
    /// 把 shell 自己的家当目录补进子进程 PATH：Windows 上的 bash 才真的当 bash 用。
    /// </summary>
    private static void AugmentPosixPath(ProcessStartInfo startInfo, ShellSpec shell)
    {
        if (!shell.IsPosix || !OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var extras = new List<string>();
            var shellDir = Path.GetDirectoryName(shell.FileName);
            if (!string.IsNullOrWhiteSpace(shellDir))
            {
                extras.Add(shellDir!);
                extras.Add(Path.Combine(shellDir!, "..", "usr", "bin"));
                extras.Add(Path.Combine(shellDir!, "..", "bin"));
            }

            var current = startInfo.Environment.TryGetValue("PATH", out var p) ? p : string.Empty;
            var parts = current.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();

            foreach (var extra in extras)
            {
                string full;
                try
                {
                    full = Path.GetFullPath(extra);
                }
                catch
                {
                    continue;
                }

                if (!parts.Any(x => string.Equals(x.TrimEnd(Path.DirectorySeparatorChar),
                        full.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)))
                {
                    parts.Insert(0, full);
                }
            }

            startInfo.Environment["PATH"] = string.Join(Path.PathSeparator, parts);
        }
        catch
        {
            // PATH 是放行项而非成败项：补不全就维持原样，命令照跑
        }
    }

    private static int SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (Exception)
        {
            return -1;
        }
    }
}
