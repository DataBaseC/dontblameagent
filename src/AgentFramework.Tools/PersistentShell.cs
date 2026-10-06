using System.Diagnostics;
using System.Text;
using AgentFramework.Contracts;

namespace AgentFramework.Tools;

/// <summary>
/// 常驻 shell 会话（对齐 dsh 的 persistent shell / bash-persistent）。
///
/// <para>
/// 每次 <c>run_command</c> 都是全新进程 —— <c>cd</c> / <c>export</c> / venv 激活
/// 跨调用全部丢失，长会话里反复重设环境是实打实的 token 与来回消耗。
/// 这一档把 shell <b>保活</b>：命令走 stdin，工作目录与环境变量跨调用保留。
/// </para>
///
/// <para>
/// 归属工具层而非沙箱层：它复用沙箱同一份环境白名单（<see cref="ProcessEnvironment"/>）
/// 与「临时目录重定向进工作区」纪律，但它是 <c>shell</c> 工具的执行细节，
/// 不随沙箱实现程序集走（工具层不拖实现依赖）。
/// </para>
///
/// <para>
/// <b>边界（如实说明）</b>：
/// <list type="bullet">
///   <item>仅支持 <b>POSIX shell</b>（bash / sh）。Windows 的 cmd / powershell 暂不支持 ——
///     调用方（工具层）会明确拒绝并提示出路，不做半吊子模拟。</item>
///   <item>走 <b>进程级护栏</b>（cwd 钉工作区、TMP 重定向进工作区、环境白名单、输出封顶），
///     不挂 Job 配额（常驻进程跨多条命令，配额语义不清）。</item>
///   <item>会话级命令超时 = <b>杀掉这个 shell 并重建</b>（半死的 shell 不可信），
///     下一条命令自动拿到新会话。</item>
///   <item>stderr 并入 stdout 收（启动时 <c>exec 2>&1</c>）—— 换来收尾简单可靠；
///     需要区分 stderr 的场合请用 <c>run_command</c>。</item>
/// </list>
/// </para>
/// </summary>
public sealed class PersistentShell : IDisposable
{
    private const int MaxOutputChars = 200_000;

    private readonly ShellSpec _shell;
    private readonly string _workDir;
    private readonly string? _tempDir;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _state = new();
    private Process? _process;
    private int _generation;
    private bool _disposed;

    private PersistentShell(ShellSpec shell, string workDir, string? tempDir)
    {
        _shell = shell;
        _workDir = workDir;
        _tempDir = tempDir;
    }

    /// <summary>当前 shell 的稳定 id（bash / sh）。</summary>
    public string ShellId => _shell.Id;

    /// <summary>shell 进程当前是否活着（false = 已死/被终结，下条命令会重建）。</summary>
    public bool IsAlive
    {
        get
        {
            lock (_state)
            {
                return _process is { HasExited: false };
            }
        }
    }

    /// <summary>会话代号：每次重建 +1（用于报告里说明"这是新会话"）。</summary>
    public int Generation
    {
        get
        {
            lock (_state)
            {
                return _generation;
            }
        }
    }

    /// <summary>
    /// 建一个持久 shell。<paramref name="shell"/> 不是 POSIX 时返回 null ——
    /// 让调用方据此给出明确拒绝，而不是静默降级成非持久行为。
    /// </summary>
    public static PersistentShell? Create(ShellSpec shell, string workDir, string? tempDir)
        => shell.IsPosix ? new PersistentShell(shell, workDir, tempDir) : null;

    /// <summary>一次持久执行的结果。</summary>
    public sealed record Outcome(
        int ExitCode,
        string Output,
        bool TimedOut,
        bool OutputTruncated,
        bool SessionRestarted,
        /// <summary>执行后 shell 的当前目录（<c>$PWD</c>）；拿不到时为 null。让 <c>cd</c> 的效果可见。</summary>
        string? WorkingDirectory = null);

    public async Task<Outcome> RunAsync(string command, int timeoutSeconds, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return new Outcome(-1, "持久 shell 已关闭", false, false, false);
            }

            Process process;
            var restarted = false;
            lock (_state)
            {
                if (_process is not { HasExited: false })
                {
                    _process = Start();
                    _generation++;
                    restarted = _generation > 1;
                }

                process = _process;
            }

            var mark = "__AF_DONE_" + Guid.NewGuid().ToString("N")[..12] + "__";

            try
            {
                // 命令 + 带退出码与当前目录的哨兵行。哨兵用唯一 token，命令自己的输出里不会有。
                await process.StandardInput.WriteAsync(command + "\n").ConfigureAwait(false);
                await process.StandardInput.WriteAsync($"printf '\\n{mark}%d|%s\\n' \"$?\" \"$PWD\"\n").ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new Outcome(-1, $"写入 shell 失败：{ex.Message}", false, false, restarted);
            }

            var output = new StringBuilder();
            var truncated = false;
            var exitCode = -1;
            string? workingDirectory = null;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));

            try
            {
                while (true)
                {
                    var line = await process.StandardOutput.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false);
                    if (line is null)
                    {
                        // shell 退出（命令自己 exit 了 / 崩了）
                        break;
                    }

                    if (line.StartsWith(mark, StringComparison.Ordinal))
                    {
                        // 哨兵尾是「退出码|工作目录」；按第一个 '|' 切，目录名里纵有 '|' 也不受影响。
                        var tail = line[mark.Length..];
                        var sep = tail.IndexOf('|');
                        int.TryParse(sep >= 0 ? tail[..sep] : tail, out exitCode);
                        var cwd = sep >= 0 ? tail[(sep + 1)..].Trim() : string.Empty;
                        if (cwd.Length > 0)
                        {
                            workingDirectory = cwd;
                        }

                        break;
                    }

                    if (output.Length < MaxOutputChars)
                    {
                        output.AppendLine(line);
                    }
                    else
                    {
                        truncated = true;
                    }
                }
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                Kill();
                return new Outcome(-1, output.ToString(), true, truncated, restarted, workingDirectory);
            }

            return new Outcome(exitCode, output.ToString(), false, truncated, restarted, workingDirectory);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Process Start()
    {
        var psi = new ProcessStartInfo
        {
            FileName = _shell.FileName,
            WorkingDirectory = _workDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // 静默启动：不读 rc/profile，避免问候语 / rc 输出混进第一条命令的结果。
        if (_shell.Id == "bash")
        {
            psi.ArgumentList.Add("--norc");
            psi.ArgumentList.Add("--noprofile");
        }

        // 环境白名单：与命令沙箱同一份（密钥不出门）。
        psi.Environment.Clear();
        foreach (var (key, value) in ProcessEnvironment.Allowlist(posix: true))
        {
            psi.Environment[key] = value;
        }

        // 临时目录重定向进工作区（与 ProcessRunner 同一条纪律）。
        if (!string.IsNullOrWhiteSpace(_tempDir))
        {
            Directory.CreateDirectory(_tempDir);
            psi.Environment["TMP"] = _tempDir;
            psi.Environment["TEMP"] = _tempDir;
            psi.Environment["TMPDIR"] = _tempDir;
        }

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动常驻 shell");

        // stderr 并入 stdout：收尾只看一条流 + 一个哨兵，简单可靠。
        process.StandardInput.Write("exec 2>&1\n");
        process.StandardInput.Flush();

        return process;
    }

    private void Kill()
    {
        lock (_state)
        {
            try
            {
                if (_process is { HasExited: false })
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // 已经死了 / 杀不动：下一次调用会重建
            }
            finally
            {
                _process?.Dispose();
                _process = null;
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Kill();
        _gate.Dispose();
    }
}
