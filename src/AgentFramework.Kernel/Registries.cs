using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFramework.Kernel;

/// <summary>把 Action 包成 IDisposable 的小工具。</summary>
internal sealed class ActionDisposable(Action onDispose) : IDisposable
{
    private Action? _onDispose = onDispose;

    public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
}

/// <summary>
/// 事件总线。按注册顺序同步派发；事件对象是引用传递，所以「可取消」
/// （如 ToolPreExecuteEvent.Cancelled）天然成立，无需额外机制。
/// </summary>
internal sealed class EventBus
{
    private readonly object _gate = new();
    private readonly List<Subscription> _subscriptions = [];
    private readonly ILogger _logger;

    public EventBus(ILogger? logger = null) => _logger = logger ?? NullLogger.Instance;

    public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler)
        where TEvent : notnull
    {
        var subscription = new Subscription(typeof(TEvent), handler);
        lock (_gate)
        {
            _subscriptions.Add(subscription);
        }

        return new ActionDisposable(() =>
        {
            lock (_gate)
            {
                _subscriptions.Remove(subscription);
            }
        });
    }

    public async ValueTask EmitAsync<TEvent>(TEvent evt, CancellationToken ct = default)
        where TEvent : notnull
    {
        Subscription[] snapshot;
        lock (_gate)
        {
            snapshot = [.. _subscriptions];
        }

        foreach (var subscription in snapshot)
        {
            if (subscription.EventType != typeof(TEvent))
            {
                continue;
            }

            try
            {
                var typed = (Func<TEvent, CancellationToken, ValueTask>)subscription.Handler;
                await typed(evt, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // ★ 单个订阅者（多半是插件）不许炸掉整条总线与当前回合（P1-F2）。
                //   审批语义仍然 fail-closed：它没来得及表态 ≠ 放行，
                //   最终决策由宿主策略兜底 —— 所以隔离在这里是安全的。
                _logger.LogError(ex, "事件订阅者处理 {EventType} 时抛出异常（已隔离）", typeof(TEvent).Name);
            }
        }
    }

    public int SubscriptionCount
    {
        get
        {
            lock (_gate)
            {
                return _subscriptions.Count;
            }
        }
    }

    private sealed record Subscription(Type EventType, Delegate Handler);
}

/// <summary>
/// 服务注册表（seam 的 Definition / Provider / Consumer 三角色落脚点）。
/// 注意：本表会持有服务实例 → 卸载时必须撤销，否则插件类型被钉住、ALC 永远卸不掉。
/// 这正是「注册即副作用、卸载即撤销」存在的理由。
/// </summary>
internal sealed class ServiceRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<Type, object> _services = [];

    /// <summary>服务供给变化通知：依赖驱动加载靠它触发。</summary>
    public event Action? Changed;

    public IDisposable Provide<TService>(TService implementation, bool overwrite = false)
        where TService : class
    {
        var key = typeof(TService);
        lock (_gate)
        {
            // 与工具注册表同一哲学（加固 B）：明确失败优于静默顶替 ——
            // 「谁提供了什么」若是隐式的，排查起来就是猜谜。
            if (!overwrite && _services.ContainsKey(key))
            {
                throw new InvalidOperationException(
                    $"服务 {typeof(TService).Name} 已经有人提供；确要替换请显式传 overwrite: true");
            }

            _services[key] = implementation;
        }

        Changed?.Invoke();

        return new ActionDisposable(() =>
        {
            lock (_gate)
            {
                if (_services.TryGetValue(key, out var current) && ReferenceEquals(current, implementation))
                {
                    _services.Remove(key);
                }
            }

            Changed?.Invoke();
        });
    }

    public TService Get<TService>()
        where TService : class
    {
        lock (_gate)
        {
            if (_services.TryGetValue(typeof(TService), out var value))
            {
                return (TService)value;
            }
        }

        throw new Contracts.ServiceNotAvailableException(typeof(TService));
    }

    public bool TryGet(Type serviceType, out object? implementation)
    {
        lock (_gate)
        {
            return _services.TryGetValue(serviceType, out implementation);
        }
    }

    public IReadOnlyCollection<Type> ProvidedTypes
    {
        get
        {
            lock (_gate)
            {
                return [.. _services.Keys];
            }
        }
    }
}

/// <summary>工具注册表。</summary>
internal sealed class ToolRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Contracts.ITool> _tools = new(StringComparer.Ordinal);

    /// <summary>工具名 → 谁注册的。重名冲突时说清「是被谁占的」，比只说「重复」有用得多。</summary>
    private readonly Dictionary<string, string> _sources = new(StringComparer.Ordinal);

    public IDisposable Register(Contracts.ITool tool, string source)
    {
        lock (_gate)
        {
            if (!_tools.TryAdd(tool.Name, tool))
            {
                throw new InvalidOperationException(
                    $"工具名重复：{tool.Name}（已被 {_sources.GetValueOrDefault(tool.Name, "未知来源")} 注册）");
            }

            _sources[tool.Name] = source;
        }

        return new ActionDisposable(() =>
        {
            lock (_gate)
            {
                _tools.Remove(tool.Name);
                _sources.Remove(tool.Name);
            }
        });
    }

    /// <summary>某工具是谁注册的（未注册时返回 null）。</summary>
    public string? SourceOf(string name)
    {
        lock (_gate)
        {
            return _sources.GetValueOrDefault(name);
        }
    }

    public bool TryGet(string name, out Contracts.ITool? tool)
    {
        lock (_gate)
        {
            return _tools.TryGetValue(name, out tool);
        }
    }

    /// <summary>
    /// 已注册的工具名，<b>按名排序</b>。
    /// 排序不是洁癖：这份名单每轮都会变成请求前缀的一部分，
    /// 顺序一抖，端点那边的前缀缓存就整段作废。
    /// </summary>
    public IReadOnlyCollection<string> Names
    {
        get
        {
            lock (_gate)
            {
                return [.. _tools.Keys.OrderBy(n => n, StringComparer.Ordinal)];
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _tools.Count;
            }
        }
    }

    /// <summary>
    /// 当前已注册工具的只读快照（宿主装配与主循环每轮取用）。
    /// 同样按名排序 —— 理由见 <see cref="Names"/>。
    /// </summary>
    public IReadOnlyList<Contracts.ITool> Snapshot
    {
        get
        {
            lock (_gate)
            {
                return [.. _tools.Values.OrderBy(t => t.Name, StringComparer.Ordinal)];
            }
        }
    }

    /// <summary>工具名 → 来源 的快照（诊断用）。</summary>
    public IReadOnlyDictionary<string, string> Sources
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, string>(_sources, StringComparer.Ordinal);
            }
        }
    }
}
