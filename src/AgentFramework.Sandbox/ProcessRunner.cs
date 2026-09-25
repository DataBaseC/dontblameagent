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
        Action? OnTerminate);

    public static async Task<SandboxOutcome> RunAsync(RunOptions run, CancellationToken ct)
    {
        var notes = new List<string>(run.Notes);

        var isWindows = OperatingSystem.IsWindows();
        var startInfo = new ProcessStartInfo
        {
            FileName = isWindows ? "cmd.exe" : "/bin/sh",
            WorkingDirectory = run.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (isWindows)
        {
            // ★ ArgumentList 的 Win32 引号规则 cmd.exe 不认（.NET 官方文档明确警告不要
            //   对 cmd/bat 用 ArgumentList）：含引号的命令会被解析成畸形转义。
            //   安全性不靠引号规则 —— 把关在审批层与沙箱层。
            startInfo.Arguments = $"/c {run.Command}";
        }
        else
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(run.Command);
        }

        // ── 环境白名单 ────────────────────────────────────────────
        // 宿主环境里可能有模型 / 搜索 API key（AGENT_*_KEY 等）。子进程一旦能
        // `echo %VAR%`，密钥就外带了 —— 「密钥不出门」必须覆盖命令通道。
        // 整表清空后只放行命令行工具真正需要的那几个变量。
        startInfo.Environment.Clear();
        foreach (var (key, value) in SafeEnvironment(isWindows))
        {
            startInfo.Environment[key] = value;
        }

        // ── 临时目录重定向 ────────────────────────────────────────
        // 没有这一步，命令想「随手写点临时文件」就落到系统临时目录去了 —— 那是最容易
        // 绕过「写只能在工作区内」的一条暗道。把 TMP/TEMP/TMPDIR 指进工作区后，
        // 命令的落地全在同一个地方：可审计、可清理，也在写边界之内。
        if (!string.IsNullOrWhiteSpace(run.TempDirectory))
        {
            Directory.CreateDirectory(run.TempDirectory);
            startInfo.Environment["TMP"] = run.TempDirectory;
            startInfo.Environment["TEMP"] = run.TempDirectory;
            startInfo.Environment["TMPDIR"] = run.TempDirectory;
        }

        using var process = new Process { StartInfo = startInfo };

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
    /// </summary>
    private static IEnumerable<KeyValuePair<string, string>> SafeEnvironment(bool isWindows)
    {
        string[] allow = isWindows
            ? ["PATH", "SystemRoot", "SYSTEMROOT", "ComSpec", "COMSPEC", "PATHEXT", "windir", "WINDIR", "SystemDrive", "SYSTEMDRIVE", "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE"]
            : ["PATH", "HOME", "USER", "LOGNAME", "SHELL", "LANG", "LC_ALL", "LC_CTYPE", "TERM", "TZ"];

        foreach (var name in allow)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
            {
                // Windows 环境变量名大小写不敏感，Get 已能命中；这里去重避免重复写入。
                yield return new KeyValuePair<string, string>(name, value);
            }
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
