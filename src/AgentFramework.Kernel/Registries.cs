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
    private readonly ILogger _logger;

    public ServiceRegistry(ILogger? logger = null) => _logger = logger ?? NullLogger.Instance;

    /// <summary>服务供给变化通知：依赖驱动加载靠它触发。</summary>
    public event Action? Changed;

    /// <summary>
    /// 派发供给变化通知。<b>与 EventBus.EmitAsync 同款隔离</b>：
    /// 逐个订阅者 try/catch，谁炸都只记日志。
    ///
    /// <para>
    /// 为什么不能裸 <c>Changed?.Invoke()</c>：事件在锁外触发是对的（订阅者可能再进注册表），
    /// 但多播委托是「一炸全停」—— 第一个订阅者抛异常，后面的收不到通知，
    /// 更糟的是异常会一路打穿 Provide / 撤销，把「注册已经成功」变成调用方眼里的失败。
    /// 通知是顺带的，注册本身必须成立。
    /// </para>
    /// </summary>
    private void NotifyChanged()
    {
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action)handler)();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "服务供给变化订阅者抛出异常（已隔离）");
            }
        }
    }

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

        NotifyChanged();

        return new ActionDisposable(() =>
        {
            lock (_gate)
            {
                if (_services.TryGetValue(key, out var current) && ReferenceEquals(current, implementation))
                {
                    _services.Remove(key);
                }
            }

            NotifyChanged();
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

    /// <summary>
    /// 工具名 → 所属<b>工具包</b>。
    ///
    /// <para>
    /// 为什么包要记在注册表这一层：可见性是「每轮现取」的，而包是可见性的最小单位 ——
    /// 把包记在工具旁边，算暴露面时就地可查，不必再去问每个工具一遍。
    /// </para>
    /// </summary>
    private readonly Dictionary<string, string> _toolsets = new(StringComparer.Ordinal);

    /// <summary>包 id → 包描述（宿主与插件都可以登记；后来者覆盖，因为提供者更清楚自己在提供什么）。</summary>
    private readonly Dictionary<string, Contracts.ToolsetDescriptor> _descriptors = new(StringComparer.Ordinal);

    public IDisposable Register(Contracts.ITool tool, string source, string? toolset = null)
    {
        // 包的判定顺序：显式指定 > 工具自报 > 注册来源。
        // 「来源兜底」这一条让老插件不改一行就天然成包（插件 id 就是包 id）。
        var resolved = string.IsNullOrWhiteSpace(toolset)
            ? (tool as Contracts.IToolWithToolset)?.Toolset ?? source
            : toolset;

        lock (_gate)
        {
            if (!_tools.TryAdd(tool.Name, tool))
            {
                throw new InvalidOperationException(
                    $"工具名重复：{tool.Name}（已被 {_sources.GetValueOrDefault(tool.Name, "未知来源")} 注册）");
            }

            _sources[tool.Name] = source;
            _toolsets[tool.Name] = resolved;

            if (!_descriptors.ContainsKey(resolved))
            {
                // 先自动建一个朴素描述，宿主/插件随后可以 DescribeToolset 补上显示名与说明
                _descriptors[resolved] = new Contracts.ToolsetDescriptor
                {
                    Id = resolved,
                    Name = resolved,
                    Source = source,
                    Protected = Contracts.BuiltinToolsets.Protected.Contains(resolved),
                };
            }
        }

        return new ActionDisposable(() =>
        {
            lock (_gate)
            {
                _tools.Remove(tool.Name);
                _sources.Remove(tool.Name);
                _toolsets.Remove(tool.Name);
            }
        });
    }

    /// <summary>
    /// 登记/更新包描述。后登记的覆盖先前的 —— 提供者（插件/宿主模块）比自动生成的
    /// 朴素描述更清楚自己在提供什么。
    /// </summary>
    public void DescribeToolset(Contracts.ToolsetDescriptor descriptor)
    {
        lock (_gate)
        {
            // ★ 写时 clone 再替换引用：已发布出去的描述符对象永不原地改。
            //   读者要么拿到旧的完整对象、要么拿到新的完整对象，
            //   不会读到「Name 已改、Description 还没改」的半更新 ——
            //   界面开关与诊断面都直接渲染这些字段，半更新会显示成一个不存在的包。
            //   存副本而不是调用方传入的实例：调用方事后再改它，不该继续污染注册表。
            if (_descriptors.TryGetValue(descriptor.Id, out var existing))
            {
                _descriptors[descriptor.Id] = MergeDescriptor(existing, descriptor);
                return;
            }

            _descriptors[descriptor.Id] = CopyDescriptor(descriptor);
        }
    }

    private static Contracts.ToolsetDescriptor CopyDescriptor(Contracts.ToolsetDescriptor source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        Description = source.Description,
        Eager = source.Eager,
        Protected = source.Protected,
        Source = source.Source,
    };

    private static Contracts.ToolsetDescriptor MergeDescriptor(
        Contracts.ToolsetDescriptor existing,
        Contracts.ToolsetDescriptor incoming) => new()
    {
        Id = existing.Id,
        Name = incoming.Name,
        Description = incoming.Description,
        Eager = incoming.Eager,
        // 保留包一旦标上就不许被后写的描述「洗白」—— 否则一次错误的 Describe 就能把 core 变成可关
        Protected = incoming.Protected || existing.Protected,
        Source = string.IsNullOrEmpty(incoming.Source) ? existing.Source : incoming.Source,
    };

    /// <summary>某工具属于哪个包（未注册时返回 null）。</summary>
    public string? ToolsetOf(string toolName)
    {
        lock (_gate)
        {
            return _toolsets.GetValueOrDefault(toolName);
        }
    }

    /// <summary>当前有工具的包 id，按名排序（顺序稳定，界面与诊断面才不会每次刷新都换样子）。</summary>
    public IReadOnlyList<string> ToolsetIds
    {
        get
        {
            lock (_gate)
            {
                return [.. _toolsets.Values.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal)];
            }
        }
    }

    /// <summary>工具名 → 包 的快照。</summary>
    public IReadOnlyDictionary<string, string> ToolsetMap
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, string>(_toolsets, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>
    /// 包 id → 描述 的快照（只含当前真有工具的包）。
    /// 返回的是当前版本的描述符实例 —— 写时 clone 保证它一旦发布就不再被原地改，
    /// 换版本时是换引用而不是改字段，旧读者手里那份永远是完整的。
    /// </summary>
    public IReadOnlyDictionary<string, Contracts.ToolsetDescriptor> Descriptors
    {
        get
        {
            lock (_gate)
            {
                var result = new Dictionary<string, Contracts.ToolsetDescriptor>(StringComparer.Ordinal);
                foreach (var id in _toolsets.Values.Distinct(StringComparer.Ordinal))
                {
                    result[id] = _descriptors.TryGetValue(id, out var descriptor)
                        ? descriptor
                        : new Contracts.ToolsetDescriptor { Id = id, Name = id };
                }

                return result;
            }
        }
    }

    /// <summary>某包里的工具名（按名排序）。</summary>
    public IReadOnlyList<string> ToolsInToolset(string toolset)
    {
        lock (_gate)
        {
            return [.. _toolsets
                .Where(kv => string.Equals(kv.Value, toolset, StringComparison.Ordinal))
                .Select(kv => kv.Key)
                .OrderBy(n => n, StringComparer.Ordinal)];
        }
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
