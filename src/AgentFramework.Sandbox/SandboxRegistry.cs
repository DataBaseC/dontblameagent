using AgentFramework.Contracts;

namespace AgentFramework.Sandbox;

/// <summary>
/// 沙箱后端注册表（宿主提供，插件可注册）。
///
/// <para>
/// 内置档：<c>off</c> / <c>process</c> / <c>job</c>（Windows），Linux 上装了 bubblewrap 则多一档 <c>bwrap</c>，
/// 装了 docker / podman 则多一档 <c>container</c>。
/// 插件想加一档（远程执行、带审计的包装……）就 <c>Register</c> 一个后端，
/// 然后在配置里写它的名字 —— 不必改宿主里任何 <c>switch</c>。
/// </para>
///
/// <para>
/// <b>回落策略</b>：名字不认识、或后端在当前平台不可用（如非 Windows 上的 <c>job</c>、
/// 没配镜像的 <c>container</c>），一律回落到可用档并把原因记在 <see cref="ResolveNote"/> 里。
/// 配置写错不该让命令直接跑不了，但也不能悄悄换档 —— 所以"回落"这件事必须可读。
/// </para>
/// </summary>
public sealed class SandboxRegistry : ISandboxRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ISandboxBackend> _backends = new(StringComparer.OrdinalIgnoreCase);
    private string? _note;

    /// <param name="container">容器后端配置（Docker / Podman）。</param>
    /// <param name="isolateNetwork">
    /// 是否让 <c>bwrap</c> 也<b>禁网</b>（<c>--unshare-net</c>）。默认 false —— 与「不打断包管理器」的取舍一致；
    /// 宿主在配置为 <c>--sandbox-network none</c> 时置 true（与容器后端同一套网络档语义）。
    /// </param>
    public SandboxRegistry(ContainerSandboxOptions? container = null, bool isolateNetwork = false)
    {
        Register(new OffSandboxBackend());
        Register(new PortableSandboxBackend());

        // job 后端是 Windows 专有（Job Object）。非 Windows 上干脆不注册 ——
        // 名字不在名单里，配置里写了它就会得到一句「没有这个后端」的明确回落提示，
        // 比注册一个永远不可用的后端更好懂。
        if (OperatingSystem.IsWindows())
        {
            Register(new WindowsJobSandboxBackend());
        }

        // Linux 的 bwrap 隔离后端（安全审查 T10/P0）：装了 bubblewrap 才注册 ——
        // 没装就不在名单里，配置写了它也会得到一句明确的「没有这个后端」。
        // 它现在是 auto 的**首选档**（见 Auto()）：默认就该用能拿到的最强护栏。
        // 仍可显式 --sandbox process / job 覆盖；网络隔离保持关闭（理由见 BwrapSandboxBackend）。
        if (OperatingSystem.IsLinux())
        {
            var bwrap = new BwrapSandboxBackend(isolateNetwork);
            if (bwrap.IsAvailable)
            {
                Register(bwrap);
            }
        }

        // 容器后端（Docker / Podman）：装了运行时才进名单。
        // 有运行时但没配镜像时后端仍在，只是 IsAvailable=false —— 回落文案会说清「缺镜像是原因」，
        // 而不是含糊一句「不可用」。刻意不进 auto：容器要一个镜像，没有普适默认，不该被自动选中。
        var containerBackend = new ContainerSandboxBackend(container ?? new ContainerSandboxOptions());
        if (containerBackend.RuntimeFound)
        {
            Register(containerBackend);
        }
    }

    public IReadOnlyCollection<string> Names
    {
        get
        {
            lock (_gate)
            {
                return [.. _backends.Keys.OrderBy(n => n, StringComparer.Ordinal)];
            }
        }
    }

    public string? ResolveNote
    {
        get
        {
            lock (_gate)
            {
                return _note;
            }
        }
    }

    public ISandboxBackend Resolve(string? name)
    {
        lock (_gate)
        {
            var wanted = string.IsNullOrWhiteSpace(name) ? "auto" : name.Trim();

            if (wanted.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                _note = null;
                return Auto();
            }

            if (_backends.TryGetValue(wanted, out var backend) && backend.IsAvailable)
            {
                _note = null;
                return backend;
            }

            var reason = _backends.TryGetValue(wanted, out var unavailable)
                ? $"沙箱后端「{wanted}」在当前平台不可用"
                  + (string.IsNullOrWhiteSpace(unavailable.UnavailableReason)
                        ? string.Empty
                        : $"（{unavailable.UnavailableReason}）")
                : $"没有名为「{wanted}」的沙箱后端";
            var fallback = Auto();

            _note = $"{reason}，已回落 {fallback.Name}（可选：{string.Join(" / ", Names)}）";
            return fallback;
        }
    }

    public IDisposable Register(ISandboxBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);

        if (string.IsNullOrWhiteSpace(backend.Name))
        {
            throw new ArgumentException("沙箱后端必须有名字", nameof(backend));
        }

        lock (_gate)
        {
            // 与工具/服务注册同一纪律：明确失败优于静默顶替
            if (!_backends.TryAdd(backend.Name, backend))
            {
                throw new InvalidOperationException($"沙箱后端名重复：{backend.Name}");
            }
        }

        return new Unregister(() =>
        {
            lock (_gate)
            {
                _backends.Remove(backend.Name);
            }
        });
    }

    /// <summary>
    /// 默认档：**优先真隔离** —— Linux 上装了 bwrap 就用它（根只读 / 工作区可写 / 命名空间隔离），
    /// 其次 Windows 的内核限额（job），最后退回进程护栏（process）。
    ///
    /// <para>
    /// 安全审查 P0：从前 auto 是 job→process，把最强的 bwrap 降级成「显式选用」，
    /// 于是默认部署下 agent 等同「以用户身份裸跑」。默认档就该是能拿到的最强护栏。
    /// </para>
    /// </summary>
    private ISandboxBackend Auto()
    {
        if (_backends.TryGetValue("bwrap", out var bwrap) && bwrap.IsAvailable)
        {
            return bwrap;
        }

        if (_backends.TryGetValue("job", out var job) && job.IsAvailable)
        {
            return job;
        }

        return _backends["process"];
    }

    private sealed class Unregister(Action onDispose) : IDisposable
    {
        private Action? _onDispose = onDispose;

        public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
    }
}
