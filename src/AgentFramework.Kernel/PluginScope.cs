using AgentFramework.Contracts;
using Microsoft.Extensions.Logging;

namespace AgentFramework.Kernel;

/// <summary>
/// 作用域：<see cref="IPluginContext"/> 的实现，也是「effect 收集器」。
///
/// 全部机制就一句话：<b>每个注册都被记进一个列表，卸载时逆序撤销</b>。
/// 于是插件作者（很可能是编码 AI）不需要记得清理任何东西 ——
/// 「忘了 dispose」这类 bug 从设计上就不存在。
///
/// 它同时充当两种角色，而这两者本是同一件事：
///   · <b>插件作用域</b> —— 卸载插件时整体撤销；
///   · <b>内核作用域</b>（<see cref="IKernelScope"/>）—— 宿主自己的模块用它登记注册，
///     关闭宿主时整体撤销。
/// 差别只在「谁来 dispose」，机制一模一样，所以不必写第二份。
/// </summary>
internal sealed class PluginScope : IKernelScope
{
    private readonly object _gate = new();
    private readonly List<IDisposable> _effects = [];
    private readonly EventBus _events;
    private readonly ServiceRegistry _services;
    private readonly ToolRegistry _tools;
    private readonly ILogger _log;
    private bool _disposed;

    public PluginScope(
        string pluginId,
        EventBus events,
        ServiceRegistry services,
        ToolRegistry tools,
        ILogger log,
        string dataDirectory)
    {
        PluginId = pluginId;
        _events = events;
        _services = services;
        _tools = tools;
        _log = log;
        DataDirectory = dataDirectory;
    }

    public string PluginId { get; }

    /// <summary>作用域名。插件作用域就是插件 id。</summary>
    public string Name => PluginId;

    public ILogger Log => _log;

    public string DataDirectory { get; }

    /// <summary>已被撤销的副作用数量（诊断用）。</summary>
    public int RevokedCount { get; private set; }

    public IDisposable On<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler)
        where TEvent : notnull
        => Track(_events.Subscribe(handler));

    public IDisposable Provide<TService>(TService implementation)
        where TService : class
        => Track(_services.Provide(implementation));

    public TService Get<TService>()
        where TService : class
        => _services.Get<TService>();

    /// <summary>
    /// 注册工具。来源标为当前作用域 —— 于是「这个工具是谁挂上来的」永远答得出来，
    /// 将来技能、子 agent、模型自写插件挂的工具也一视同仁。
    /// </summary>
    public IDisposable RegisterTool(ITool tool) => Track(_tools.Register(tool, PluginId));

    /// <summary>
    /// 注册工具并指定所属工具包（脚本插件用 <c>toolset</c> 字段指定；
    /// 不指定就按来源（插件 id）自动成包）。
    /// </summary>
    public IDisposable RegisterTool(ITool tool, string? toolset) => Track(_tools.Register(tool, PluginId, toolset));

    public IDisposable Effect(Func<IDisposable> setup) => Track(setup());

    private IDisposable Track(IDisposable effect)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                effect.Dispose();
                throw new ObjectDisposedException(nameof(PluginScope), $"插件 {PluginId} 已卸载，不能再注册副作用");
            }

            _effects.Add(effect);
        }

        return effect;
    }

    /// <summary>逆序撤销全部副作用。</summary>
    public void Dispose()
    {
        IDisposable[] snapshot;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            snapshot = [.. _effects];
            _effects.Clear();
        }

        for (var i = snapshot.Length - 1; i >= 0; i--)
        {
            try
            {
                snapshot[i].Dispose();
                RevokedCount++;
            }
            catch (Exception ex)
            {
                // 单个撤销失败不能阻断其余撤销 —— 否则一处漏清理会拖垮整个卸载
                _log.LogError(ex, "撤销副作用失败：plugin={PluginId}", PluginId);
            }
        }

        _log.LogDebug("插件 {PluginId} 已撤销 {Count} 个副作用", PluginId, RevokedCount);
    }
}
