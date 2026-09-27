using AgentFramework.Contracts;

namespace AgentFramework.Sandbox;

/// <summary>
/// Linux 隔离后端：用 <c>bubblewrap</c>（bwrap）把命令关进命名空间（安全审查 T10）。
///
/// <para>
/// <c>process</c> 档只做进程级护栏，<b>不是隔离</b>；这一档补上真正的一层：
/// 根文件系统<b>只读挂载</b>、工作区可写、<c>/tmp</c> 独立、PID / UTS / IPC / 用户命名空间隔离。
/// 于是命令再也读写不到 <c>~/.ssh</c>、也看不到宿主的其它进程。
/// </para>
///
/// <para>
/// <b>为什么选 bwrap 而不是自研 namespace</b>：bwrap 是 Flatpak 生态里成熟、零配置、
/// 无需 root 的隔离器，拿到 mount / pid / net namespace 的成本远低于自己写。
/// </para>
///
/// <para>
/// <b>边界（如实说明）</b>：
/// <list type="bullet">
///   <item>不隔离<b>网络</b>（故意）：<c>--unshare-net</c> 会让 <c>npm install</c> 之类直接失效，
///     需要隔绝网络时可在配置里换成自建后端。</item>
///   <item>在部分受限容器里没有 user namespace 权限，bwrap 会启动失败 —— 所以在那种环境下
///     不要选它（本档仅在探测到 <c>bwrap</c> 可执行文件时才注册）。</item>
/// </list>
/// </para>
/// </summary>
public sealed class BwrapSandboxBackend : ISandboxBackend
{
    private static readonly string? BwrapPath = FindOnPath("bwrap");

    public string Name => "bwrap";

    public bool IsAvailable => OperatingSystem.IsLinux() && BwrapPath is not null;

    public string Describe() =>
        "bwrap 隔离：根文件系统只读、工作区可写、/tmp 独立、PID/UTS/IPC/用户命名空间隔离（不隔离网络）。";

    public Task<SandboxOutcome> RunAsync(SandboxRequest request, CancellationToken ct)
    {
        // 包装器：bwrap <隔离参数> -- <真正的 shell>，随后由 ProcessRunner 追加 shell 自己的 -c <command>。
        // 环境清洗 / 输出封顶 / 超时连子孙终结等收尾完全复用 ProcessRunner —— 与另两档同源。
        var shell = request.Shell ?? ShellResolver.Resolve();
        var workdir = request.WorkingDirectory;

        var wrapper = new List<string>
        {
            BwrapPath!,
            "--die-with-parent",
            "--unshare-user", "--unshare-pid", "--unshare-uts", "--unshare-ipc",
            "--ro-bind", "/", "/",
            "--dev", "/dev",
            "--proc", "/proc",
            "--tmpfs", "/tmp",
            "--bind", workdir, workdir,
            "--chdir", workdir,
            "--",
            shell.FileName,
        };

        return ProcessRunner.RunAsync(
            new ProcessRunner.RunOptions(
                request.Command,
                workdir,
                request.TempDirectory,
                request.Limits,
                [Describe()],
                OnStarted: null,
                OnTerminate: null,
                Shell: shell,
                Wrapper: wrapper),
            ct);
    }

    /// <summary>在 PATH 里找一个可执行文件；找不到返回 null（= 本档不可用）。</summary>
    private static string? FindOnPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(dir, exe);
                if (File.Exists(full))
                {
                    return full;
                }
            }
            catch
            {
                // 非法 PATH 项跳过
            }
        }

        return null;
    }
}
