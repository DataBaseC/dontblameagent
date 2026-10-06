using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using AgentFramework.Contracts;

namespace AgentFramework.Sandbox;

/// <summary>
/// 容器隔离后端：把命令关进 Docker / Podman 容器里跑。
///
/// <para>
/// 定位（与另几档的分工）：
/// <list type="bullet">
///   <item><c>process</c> —— 进程护栏，<b>不是隔离</b>；</item>
///   <item><c>bwrap</c> —— Linux 命名空间隔离，借宿主根文件系统、默认不禁网；</item>
///   <item><c>container</c>（本档）—— <b>最彻底的一档</b>：独立 rootfs（镜像自带工具链）、
///     网络可完全隔绝、根只读、内存配额、工作区可写。代价是要镜像与容器运行时。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>为什么学 dsh 做成后端而不是写死</b>：dsh 把 sandbox 做成可替换的 provider
/// （bwrap / Landlock / E2B / Docker 皆是后端），换一种沙箱不改一行消费方。
/// 我们的 <see cref="ISandboxBackend"/> 缝就是照这个来的 —— 于是容器档落地，
/// <c>run_command</c> / 诊断面 / 配置一处未动。
/// </para>
///
/// <para>
/// <b>不进 auto（有意）</b>：容器要一个镜像，而「用哪个镜像」没有普适默认
/// （跑 .NET / Node / Python 各不相同）。没有合理默认的东西，不该被自动选中；
/// 它是<b>显式选用</b>的档：<c>--sandbox container --sandbox-image &lt;镜像&gt;</c>。
/// </para>
///
/// <para>
/// <b>边界（如实说明）</b>：
/// <list type="bullet">
///   <item>无容器运行时（docker / podman 都不在 PATH）时本档不注册 —— 配置写了它会得到
///     一句「没有这个后端」，而不是一个静默失败。</item>
///   <item>有运行时但没配镜像时后端仍在，只是不可用；回落文案会说清「缺镜像是原因」。</item>
///   <item>只负责把命令<b>送进容器</b>；超时 / 取消 / 输出封顶 / 环境白名单仍由
///     <see cref="ProcessRunner"/> 统一收尾（与另几档同源）。超时或取消时按容器名强制清理，
///     不留孤儿容器。</item>
///   <item>不隔离<b>宿主的 Docker 守护进程本身</b> —— 给了 socket 的容器是能反过来控宿主的，
///     所以本档只挂载工作区、不挂 docker.sock。</item>
/// </list>
/// </para>
/// </summary>
public sealed class ContainerSandboxBackend : ISandboxBackend
{
    private readonly ContainerSandboxOptions _options;
    private readonly string? _runtime;
    private readonly string? _runtimeProblem;

    public ContainerSandboxBackend(ContainerSandboxOptions options)
    {
        _options = options ?? new ContainerSandboxOptions();
        (_runtime, _runtimeProblem) = ResolveRuntime(_options.Runtime);
    }

    public string Name => "container";

    /// <summary>本机是否找得到容器运行时（registry 据此决定要不要注册这一档）。</summary>
    public bool RuntimeFound => _runtime is not null;

    /// <summary>可用 = 有运行时 <b>且</b> 配了镜像。缺一不可 —— 少了镜像，这条命令进不了容器。</summary>
    public bool IsAvailable => _runtime is not null && _options.HasImage;

    public string? UnavailableReason =>
        _runtime is null
            ? _runtimeProblem ?? "未找到容器运行时（docker / podman 都不在 PATH 上）"
            : !_options.HasImage
                ? "已找到容器运行时，但未配置镜像（sandboxImage / --sandbox-image）"
                : null;

    public string Describe()
    {
        var runtime = _runtime is null ? "docker" : Path.GetFileName(_runtime);
        var root = _options.ReadOnlyRoot ? "根文件系统只读（/tmp 为可写临时区）" : "根文件系统可写";
        var net = _options.Network switch
        {
            ContainerNetwork.Bridge => "网络 bridge（可出网）",
            ContainerNetwork.Host => "网络 host（与宿主共享网络栈，隔离最弱）",
            _ => "网络 none（禁网；装依赖需改 bridge）",
        };

        return $"容器隔离（{runtime} · {_options.Image}）：独立 rootfs、工作区挂载可写、{net}、{root}。";
    }

    public async Task<SandboxOutcome> RunAsync(SandboxRequest request, CancellationToken ct)
    {
        if (!IsAvailable)
        {
            // 不抛异常（接口约定）：跑不起来如实回报，让调用方那一回合照常收尾。
            return new SandboxOutcome(
                -1, string.Empty, string.Empty, false, false, false,
                [Describe(), $"容器沙箱不可用：{UnavailableReason}。"]);
        }

        var containerName = "af-sbx-" + Guid.NewGuid().ToString("N")[..12];
        var wrapper = BuildWrapper(_options, _runtime!, containerName, request.WorkingDirectory, request.Limits);

        // 容器内的 shell 与宿主 shell 无关 —— rootfs 由镜像决定，用最低公共保证 /bin/sh。
        // 宿主解析出的 bash 路径在镜像里未必存在（如 alpine 只有 sh）。
        var shell = new ShellSpec("sh", _options.ShellPath, IsPosix: true, $"容器内 shell（{_options.ShellPath}）");

        return await ProcessRunner.RunAsync(
            new ProcessRunner.RunOptions(
                request.Command,
                request.WorkingDirectory,
                TempDirectory: null,   // 容器内 /tmp 自管（tmpfs），不重定向宿主临时目录
                request.Limits,
                [Describe()],
                OnStarted: null,
                // 超时 / 取消时：docker CLI 会被 kill，但容器默认仍在跑 —— 按名强制清掉，不留孤儿。
                OnTerminate: () => RemoveContainer(_runtime!, containerName),
                Shell: shell,
                Wrapper: wrapper,
                ExtraEnvironment: RuntimeEnvironment()),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 拼出「docker run … &lt;镜像&gt; &lt;shell&gt;」的参数表。抽成纯函数是为了能脱离 docker 单测 ——
    /// 断言的正是那些安全属性（禁网 / 只读根 / 工作区挂载 / 按名可清理）。
    /// </summary>
    internal static IReadOnlyList<string> BuildWrapper(
        ContainerSandboxOptions options,
        string runtime,
        string containerName,
        string workDir,
        SandboxLimits limits)
    {
        var args = new List<string>
        {
            runtime,
            "run",
            "--rm",                       // 正常退出即删容器（异常退出由 OnTerminate 兜底）
            "-i",                         // 接 stdin：与其它档保持同一种执行形状
            "--name", containerName,      // 记名 → 超时/取消时能按名强制清理
            "--network", options.NetworkValue,
            "-v", $"{workDir}:{workDir}", // 只有工作区可写（配合下面只读根）
            "-w", workDir,
        };

        if (options.ReadOnlyRoot)
        {
            args.Add("--read-only");
            args.Add("--tmpfs");
            args.Add("/tmp");             // 只读根下给一块可写临时区，否则多数工具没法喘气
        }

        if (limits.MaxMemoryBytes > 0)
        {
            args.Add("--memory");
            args.Add(limits.MaxMemoryBytes.ToString(CultureInfo.InvariantCulture));
        }

        // 用户映射（仅 Linux）：不映射的话容器里以 root 跑，写到挂载工作区的文件属主会变成 root，
        // 宿主用户反而改不动自己的项目 —— 这是容器沙箱最常见的「后遗症」。
        if (CurrentUserSpec() is { } user)
        {
            args.Add("--user");
            args.Add(user);
        }

        args.Add(options.Image!);
        args.Add(options.ShellPath);
        return args;
    }

    /// <summary>
    /// 运行时探测：显式指定优先（认不出来就说清是「指定的找不到」），
    /// 否则 docker → podman 依次找。找不到 = 本档不注册。
    /// </summary>
    private static (string? Path, string? Problem) ResolveRuntime(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var wanted = configured.Trim();
            var found = PathLookup.Find(wanted);
            return found is null
                ? (null, $"指定的容器运行时「{wanted}」找不到（不在 PATH 上，也不是有效路径）")
                : (found, null);
        }

        if (PathLookup.Find("docker") is { } docker)
        {
            return (docker, null);
        }

        if (PathLookup.Find("podman") is { } podman)
        {
            return (podman, null);
        }

        return (null, null);
    }

    /// <summary>把容器运行时进程也需要的几个变量补进白名单（白名单里没有，但 docker/podman 找 socket 靠它们）。</summary>
    private static IReadOnlyDictionary<string, string>? RuntimeEnvironment()
    {
        var extra = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in new[] { "DOCKER_HOST", "DOCKER_CONFIG", "DOCKER_CONTEXT", "XDG_RUNTIME_DIR" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
            {
                extra[name] = value;
            }
        }

        return extra.Count == 0 ? null : extra;
    }

    /// <summary>按容器名强制清理（超时/取消路径）。失败只记不抛 —— 那是收尾，不该盖过主结果。</summary>
    private static void RemoveContainer(string runtime, string containerName)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = runtime,
                ArgumentList = { "rm", "-f", containerName },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            process?.WaitForExit(5000);
        }
        catch
        {
            // 容器可能早已随 --rm 消失，或运行时不可达 —— 忽略即可
        }
    }

    /// <summary>当前用户的 <c>uid:gid</c>（仅 Linux 有意义）；拿不到返回 null（= 不做用户映射）。</summary>
    private static string? CurrentUserSpec()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            return $"{getuid()}:{getgid()}";
        }
        catch
        {
            // 某些受限环境拿不到 —— 不做映射也比整个后端失效强
            return null;
        }
    }

    [DllImport("libc", EntryPoint = "getuid")]
    private static extern uint getuid();

    [DllImport("libc", EntryPoint = "getgid")]
    private static extern uint getgid();
}
