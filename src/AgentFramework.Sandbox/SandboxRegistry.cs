using AgentFramework.Contracts;

namespace AgentFramework.Sandbox;

/// <summary>
/// 沙箱后端注册表（宿主提供，插件可注册）。
///
/// <para>
/// 内置三档：<c>off</c> / <c>process</c> / <c>job</c>。
/// 插件想加一档（容器、远程执行、带审计的包装……）就 <c>Register</c> 一个后端，
/// 然后在配置里写它的名字 —— 不必改宿主里任何 <c>switch</c>。
/// </para>
///
/// <para>
/// <b>回落策略</b>：名字不认识、或后端在当前平台不可用（如非 Windows 上的 <c>job</c>），
/// 一律回落到可用档并把原因记在 <see cref="ResolveNote"/> 里。
/// 配置写错不该让命令直接跑不了，但也不能悄悄换档 —— 所以"回落"这件事必须可读。
/// </para>
/// </summary>
public sealed class SandboxRegistry : ISandboxRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ISandboxBackend> _backends = new(StringComparer.OrdinalIgnoreCase);
    private string? _note;

    public SandboxRegistry()
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

    /// <summary>默认档：Windows 上有内核限额就用它，其他平台退回进程护栏。</summary>
    private ISandboxBackend Auto()
        => _backends.TryGetValue("job", out var job) && job.IsAvailable
            ? job
            : _backends["process"];

    private sealed class Unregister(Action onDispose) : IDisposable
    {
        private Action? _onDispose = onDispose;

        public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
    }
}
