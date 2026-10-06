using System.Diagnostics;
using System.Text;
using AgentFramework.Contracts;

namespace AgentFramework.Tools;

/// <summary>
/// 后台作业管理器（对齐 dsh 的 jobs）：把长命令扔到后台跑，回合不再干等。
///
/// <para>
/// 现状痛点：<c>run_command</c> 同步阻塞，一条 <c>npm install</c> / <c>dotnet build</c>
/// 就能把回合钉住直到超时。后台作业让「启动 → 继续干活 → 回头收结果」成为可能。
/// </para>
///
/// <para>
/// 边界（如实说明）：
/// <list type="bullet">
///   <item>只走<b>进程级护栏</b>（cwd 钉工作区、TMP 重定向、环境白名单、输出封顶），
///     不挂 Job 配额 —— 后台进程生命周期与宿主解耦，配额语义不同。</item>
///   <item>输出<b>有界</b>：超过上限时从头部裁剪（保留最新，看日志尾部更有用）。</item>
///   <item>宿主关闭时<b>连同子孙一并终结</b>（不留孤儿）。</item>
/// </list>
/// </para>
/// </summary>
public sealed class JobManager : IDisposable
{
    private const int DefaultMaxOutputChars = 120_000;

    private readonly object _gate = new();
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly int _maxOutputChars;
    private bool _disposed;

    public JobManager(int maxOutputChars = DefaultMaxOutputChars)
    {
        _maxOutputChars = Math.Max(1000, maxOutputChars);
    }

    private sealed class Job
    {
        public required string Id { get; init; }
        public required string Command { get; init; }
        public required Process Process { get; init; }
        public required DateTimeOffset StartedAt { get; init; }
        public StringBuilder Output { get; } = new();
        public bool Truncated { get; set; }
        public object Gate { get; } = new();
    }

    public IReadOnlyList<string> Ids
    {
        get
        {
            lock (_gate)
            {
                return [.. _jobs.Keys];
            }
        }
    }

    /// <summary>启动一个后台作业，返回 jobId；失败返回 null 并给出原因。</summary>
    public string? Start(string command, ShellSpec shell, string workDir, string? tempDir, out string? error)
    {
        error = null;
        if (_disposed)
        {
            error = "作业管理器已关闭";
            return null;
        }

        Process process;
        try
        {
            process = StartProcess(command, shell, workDir, tempDir);
        }
        catch (Exception ex)
        {
            error = $"启动作业失败：{ex.Message}";
            return null;
        }

        var job = new Job
        {
            Id = "job-" + Guid.NewGuid().ToString("N")[..8],
            Command = command,
            Process = process,
            StartedAt = DateTimeOffset.UtcNow,
        };

        lock (_gate)
        {
            _jobs[job.Id] = job;
        }

        _ = Task.Run(() => PumpAsync(job, process.StandardOutput));
        _ = Task.Run(() => PumpAsync(job, process.StandardError));

        return job.Id;
    }

    private Process StartProcess(string command, ShellSpec shell, string workDir, string? tempDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = shell.FileName,
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (shell.IsPosix)
        {
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
        }
        else if (shell.Id == "cmd")
        {
            // cmd 不吃 ArgumentList 的 Win32 引号规则（见 ProcessRunner 的同款说明）
            psi.Arguments = $"/c {command}";
        }
        else
        {
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(command);
        }

        // 环境白名单：与命令沙箱同一份（密钥不出门）。
        psi.Environment.Clear();
        foreach (var (key, value) in ProcessEnvironment.Allowlist(shell.IsPosix))
        {
            psi.Environment[key] = value;
        }

        if (!string.IsNullOrWhiteSpace(tempDir))
        {
            Directory.CreateDirectory(tempDir);
            psi.Environment["TMP"] = tempDir;
            psi.Environment["TEMP"] = tempDir;
            psi.Environment["TMPDIR"] = tempDir;
        }

        return Process.Start(psi) ?? throw new InvalidOperationException("无法启动进程");
    }

    private async Task PumpAsync(Job job, StreamReader reader)
    {
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                lock (job.Gate)
                {
                    if (job.Output.Length >= _maxOutputChars)
                    {
                        var keep = _maxOutputChars / 2;
                        var text = job.Output.ToString();
                        job.Output.Clear();
                        job.Output.Append("...[较早输出已裁剪]...\n")
                            .Append(text[^Math.Min(keep, text.Length)..]);
                        job.Truncated = true;
                    }

                    job.Output.AppendLine(line);
                }
            }
        }
        catch
        {
            // 进程被杀 / 管道断开：正常收尾
        }
    }

    public (bool Found, bool Running, int? ExitCode, double Seconds) Status(string id)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var job))
            {
                return (false, false, null, 0);
            }

            var exited = job.Process.HasExited;
            return (true, !exited, exited ? job.Process.ExitCode : null,
                (DateTimeOffset.UtcNow - job.StartedAt).TotalSeconds);
        }
    }

    public (bool Found, string Output, bool Truncated, bool Running) Read(string id)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var job))
            {
                return (false, "", false, false);
            }

            lock (job.Gate)
            {
                return (true, job.Output.ToString(), job.Truncated, !job.Process.HasExited);
            }
        }
    }

    public bool Kill(string id, out bool wasRunning)
    {
        wasRunning = false;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var job))
            {
                return false;
            }

            try
            {
                if (!job.Process.HasExited)
                {
                    wasRunning = true;
                    job.Process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // 已经退出
            }

            return true;
        }
    }

    public void Dispose()
    {
        _disposed = true;

        Job[] snapshot;
        lock (_gate)
        {
            snapshot = [.. _jobs.Values];
            _jobs.Clear();
        }

        foreach (var job in snapshot)
        {
            try
            {
                if (!job.Process.HasExited)
                {
                    job.Process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // 已退出
            }
            finally
            {
                job.Process.Dispose();
            }
        }
    }
}
