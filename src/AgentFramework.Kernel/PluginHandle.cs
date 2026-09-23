using System.Reflection;
using System.Runtime.Loader;
using AgentFramework.Contracts;

namespace AgentFramework.Kernel;

/// <summary>
/// 已加载插件的手柄。卸载顺序很重要：
///   1. 先撤销全部副作用（这一步释放宿主对插件类型的所有引用）
///   2. 再请求 ALC 卸载（异步，GC 跑完才算真回收）
/// 顺序反了的话，副作用还钉着程序集，永远卸不掉。
/// </summary>
public sealed class PluginHandle : IAsyncDisposable
{
    private readonly PluginLoadContext _loadContext;

    /// <summary>
    /// v3.5 审查 P2：这个字段**不能是 readonly 常驻强引用** ——
    /// 它是 ALC 内 Assembly 对象的根，handle 只要活着就钉住整个 ALC，
    /// 使 <c>alc.Unload()</c> 之后的回收永远发生不了（「假卸载」）。
    /// 卸载时置空，让弱引用（诊断用）能真实反映可达性。
    /// </summary>
    private Assembly? _entryAssembly;

    /// <summary>卸载完成后回调宿主（<see cref="PluginHost.Forget"/>），把本句柄从活跃表摘掉。</summary>
    private readonly Action<PluginHandle>? _onDisposed;

    private bool _unloaded;

    internal PluginHandle(
        PluginManifest manifest,
        PluginLoadContext loadContext,
        PluginScope scope,
        Assembly? entryAssembly,
        Action<PluginHandle>? onDisposed = null)
    {
        Manifest = manifest;
        _loadContext = loadContext;
        Scope = scope;
        _entryAssembly = entryAssembly;
        _onDisposed = onDisposed;
    }

    public PluginManifest Manifest { get; }

    public string Id => Manifest.Id;

    public string Version => Manifest.Version;

    internal PluginScope Scope { get; }

    /// <summary>本次生命周期内被撤销的副作用数量（诊断用）。</summary>
    public int RevokedEffectCount => Scope.RevokedCount;

    public bool IsUnloaded { get; private set; }

    /// <summary>
    /// 诊断用：为入口程序集创建弱引用。
    /// 卸载后反复 GC，若弱引用仍存活，说明有东西钉住了插件 —— 这就叫「假卸载」。
    /// </summary>
    public WeakReference CreateAssemblyWeakReference() => new(_entryAssembly);

    /// <summary>卸载：撤销副作用 → 摘除宿主强引用 → 请求 ALC 卸载。</summary>
    public ValueTask DisposeAsync()
    {
        if (_unloaded)
        {
            return ValueTask.CompletedTask;
        }

        _unloaded = true;

        Scope.Dispose();
        _loadContext.Unload();

        // v3.5 审查 P2（两处一起才有效）：
        //   ① 摘掉自己对入口程序集的强引用；
        //   ② 回调宿主把自己从 _plugins 活跃表移除。
        // 少了任何一处，长寿命的 handle / 活跃表都会一直钉住 ALC 内的 Assembly，
        // GC 收不掉 —— 而原先的「摘除」入口 Forget() 全仓根本没有调用点。
        _entryAssembly = null;
        _onDisposed?.Invoke(this);

        IsUnloaded = true;
        return ValueTask.CompletedTask;
    }
}
