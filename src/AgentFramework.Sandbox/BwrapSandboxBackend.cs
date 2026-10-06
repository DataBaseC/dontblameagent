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
/// <b>网络</b>：默认放开（<c>--unshare-net</c> 会让 <c>npm install</c> 之类直接失效）。
/// 需要彻底禁网时把沙箱网络档设为 <c>none</c>（<c>--sandbox-network none</c>）——
/// 宿主会以 <c>isolateNetwork: true</c> 构造本后端，与容器后端同一套语义。
/// </para>
///
/// <para>
/// <b>边界（如实说明）</b>：在部分受限容器里没有 user namespace 权限，bwrap 会启动失败 ——
/// 那种环境下不要选它。当前只在探测到 <c>bwrap</c> 可执行文件时注册（不做「跑一次试跑」的深探测：
/// 那会在安装期引入额外进程与延迟，而失败会如实以 sandbox-start-fail 回报，不会静默成功）。
/// </para>
/// </summary>
public sealed class BwrapSandboxBackend(bool isolateNetwork = false) : ISandboxBackend
{
    private static readonly string? BwrapPath = PathLookup.Find("bwrap");

    public string Name => "bwrap";

    public bool IsAvailable => OperatingSystem.IsLinux() && BwrapPath is not null;

    /// <summary>是否对命令禁网（<c>--unshare-net</c>）。</summary>
    public bool IsolateNetwork => isolateNetwork;

    public string Describe() =>
        "bwrap 隔离：根文件系统只读、工作区可写、/tmp 独立、PID/UTS/IPC/用户命名空间隔离"
        + (isolateNetwork ? "、网络隔离（--unshare-net）。" : "（不隔离网络）。");

    public Task<SandboxOutcome> RunAsync(SandboxRequest request, CancellationToken ct)
    {
        // 包装器：bwrap <隔离参数> -- <真正的 shell>，随后由 ProcessRunner 追加 shell 自己的 -c <command>。
        // 环境清洗 / 输出封顶 / 超时连子孙终结等收尾完全复用 ProcessRunner —— 与另两档同源。
        var shell = request.Shell ?? ShellResolver.Resolve();
        var workdir = request.WorkingDirectory;

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
                Wrapper: BuildWrapper(BwrapPath!, shell, workdir, isolateNetwork)),
            ct);
    }

    /// <summary>
    /// 拼隔离参数（纯函数，便于验收直接断言安全属性：禁网 / 只读根 / 只挂工作区）。
    /// </summary>
    internal static IReadOnlyList<string> BuildWrapper(
        string bwrapPath,
        ShellSpec shell,
        string workdir,
        bool isolateNetwork)
    {
        var wrapper = new List<string>
        {
            bwrapPath,
            "--die-with-parent",
            "--unshare-user", "--unshare-pid", "--unshare-uts", "--unshare-ipc",
        };

        if (isolateNetwork)
        {
            wrapper.Add("--unshare-net");
        }

        wrapper.AddRange(
        [
            "--ro-bind", "/", "/",
            "--dev", "/dev",
            "--proc", "/proc",
            "--tmpfs", "/tmp",
            "--bind", workdir, workdir,
            "--chdir", workdir,
            "--",
            shell.FileName,
        ]);

        return wrapper;
    }
}
