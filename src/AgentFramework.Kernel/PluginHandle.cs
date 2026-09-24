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
    private readonly PluginLoadContext? _loadContext;

    /// <summary>
    /// v3.5 审查 P2：这个字段**不能是 readonly 常驻强引用** ——
    /// 它是 ALC 内 Assembly 对象的根，handle 只要活着就钉住整个 ALC，
    /// 使 <c>alc.Unload()</c> 之后的回收永远发生不了（「假卸载」）。
    /// 卸载时置空，让弱引用（诊断用）能真实反映可达性。
    /// </summary>
    private Assembly? _entryAssembly;

    /// <summary>卸载完成后回调宿主（<see cref="PluginHost.Forget"/>），把本句柄从活跃表摘掉。</summary>
    private readonly Action<PluginHandle>? _onDisposed;

    /// <summary>
    /// 0 = 在世，1 = 已进过撤销-卸载序列。
    /// 用 int + Interlocked 而不是 bool 检查-赋值：并发 Dispose 时两个线程都可能
    /// 读到 false，然后各跑一遍撤销 —— 双重撤销会让副作用计数翻倍、回调跑两遍。
    /// </summary>
    private int _unloaded;

    internal PluginHandle(
        PluginManifest manifest,
        PluginLoadContext? loadContext,
        PluginScope scope,
        Assembly? entryAssembly,
        Action<PluginHandle>? onDisposed = null,
        string? selfTestReport = null,
        IReadOnlyList<string>? registeredTools = null)
    {
        Manifest = manifest;
        _loadContext = loadContext;
        Scope = scope;
        _entryAssembly = entryAssembly;
        _onDisposed = onDisposed;
        SelfTestReport = selfTestReport;
        RegisteredTools = registeredTools ?? [];
    }

    public PluginManifest Manifest { get; }

    /// <summary>
    /// 装载自测的结果（脚本插件的 <c>selftest()</c> 返回值）。
    /// null = 这个插件没定义自测（程序集插件都是 null）。
    /// </summary>
    public string? SelfTestReport { get; }

    /// <summary>本次装载注册的工具名（装载报告 / 诊断用）。</summary>
    public IReadOnlyList<string> RegisteredTools { get; }

    /// <summary>是不是脚本插件（免编译那条道）。</summary>
    public bool IsScript => !string.IsNullOrWhiteSpace(Manifest.Script);

    public string Id => Manifest.Id;

    public string Version => Manifest.Version;

    internal PluginScope Scope { get; }

    /// <summary>本次生命周期内被撤销的副作用数量（诊断用）。</summary>
    public int RevokedEffectCount => Scope.RevokedCount;

    public bool IsUnloaded { get; private set; }

    /// <summary>
    /// 诊断用：真正进入撤销-卸载序列的次数。
    /// 并发 Dispose 下必须是 1 —— 一旦大于 1，说明「检查-赋值」的竞态漏进了双重撤销。
    /// </summary>
    public int DisposeSequenceEntries { get; private set; }

    /// <summary>
    /// 诊断用：为入口程序集创建弱引用。
    /// 卸载后反复 GC，若弱引用仍存活，说明有东西钉住了插件 —— 这就叫「假卸载」。
    /// </summary>
    public WeakReference CreateAssemblyWeakReference() => new(_entryAssembly);

    /// <summary>
    /// 诊断用：以**插件自己的 ALC** 解析类型。
    /// 验收「跨 ALC 类型同一性」就靠它：返回值必须与宿主 <c>typeof(T)</c> 是同一个 Type ——
    /// 不是同一个，就说明契约传递依赖被插件 ALC 双载了，插件一碰 ILogger 就会炸。
    /// 脚本插件没有 ALC，走 default，天然与宿主相同。
    /// </summary>
    public Type? ResolveTypeFromPlugin(string assemblyName, string typeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);

        if (_loadContext is null)
        {
            return Type.GetType($"{typeName}, {assemblyName}", throwOnError: false);
        }

        try
        {
            // LoadFromAssemblyName 会走本 ALC 的 Load 裁决：
            // 共享名单命中 → Load 返回 null → 回落 default ALC → 与宿主同 Type。
            var assembly = _loadContext.LoadFromAssemblyName(new AssemblyName(assemblyName));
            return assembly?.GetType(typeName, throwOnError: false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>卸载：撤销副作用 → 摘除宿主强引用 → 请求 ALC 卸载。</summary>
    public ValueTask DisposeAsync()
    {
        // 并发 Dispose 只允许一个线程进撤销-卸载序列。
        // Interlocked.Exchange 是原子的检查+上锁：谁换出 0 谁负责跑，输家直接返回。
        if (Interlocked.Exchange(ref _unloaded, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        DisposeSequenceEntries++;

        Scope.Dispose();
        _loadContext?.Unload();   // 脚本插件没有 ALC，跳过

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
