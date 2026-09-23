using System.Reflection;
using System.Text.Json;
using AgentFramework.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFramework.Kernel;

public sealed class PluginHostOptions
{
    /// <summary>插件私有数据根目录（每个插件一个子目录）。</summary>
    public string DataRoot { get; set; } = Path.Combine(AppContext.BaseDirectory, "plugin-data");

    /// <summary>本内核支持的契约 API 版本。</summary>
    public string ApiVersion { get; set; } = "1";

    public ILoggerFactory LoggerFactory { get; set; } = NullLoggerFactory.Instance;
}

/// <summary>
/// 插件宿主。负责：发现插件 → 校验清单 → 检查依赖 → 加载 ALC → 激活插件。
/// 卸载由 <see cref="PluginHandle"/> 负责。
/// </summary>
public sealed class PluginHost
{
    private readonly object _gate = new();
    private readonly EventBus _events;
    private readonly ServiceRegistry _services = new();
    private readonly ToolRegistry _tools = new();
    private readonly List<PluginHandle> _plugins = [];
    /// <summary>内核自己的作用域（宿主模块登记注册用）。与插件作用域同一套撤销机制。</summary>
    private readonly List<PluginScope> _kernelScopes = [];
    private readonly ILogger _log;
    private readonly PluginHostOptions _options;

    public PluginHost(PluginHostOptions? options = null)
    {
        _options = options ?? new PluginHostOptions();
        _log = _options.LoggerFactory.CreateLogger("Kernel.PluginHost");
        _events = new EventBus(_log);

        // 内核自带的基础服务（不属于任何插件，所以不走插件的 effect 撤销）
        _services.Provide<IClockService>(new SystemClockService());
    }

    // ── 宿主可见的诊断面 ───────────────────────────────────────────

    public IReadOnlyCollection<string> ToolNames => _tools.Names;

    /// <summary>
    /// 当前可用工具的实例快照。**每次调用都重新取** ——
    /// 于是运行期挂上来的工具（技能、子 agent、模型自写插件）下一轮就能被模型看见，
    /// 不需要重建主循环。
    /// </summary>
    public IReadOnlyCollection<ITool> GetTools() => _tools.Snapshot;

    /// <summary>某个工具是谁注册的（诊断用；未注册返回 null）。</summary>
    public string? ToolSource(string toolName) => _tools.SourceOf(toolName);

    /// <summary>工具名 → 来源 的快照（诊断用）。</summary>
    public IReadOnlyDictionary<string, string> ToolSources => _tools.Sources;

    public int EventSubscriptionCount => _events.SubscriptionCount;

    public IReadOnlyCollection<Type> ProvidedServiceTypes => _services.ProvidedTypes;

    public IReadOnlyList<PluginHandle> Plugins
    {
        get
        {
            lock (_gate)
            {
                return [.. _plugins];
            }
        }
    }

    public TService GetService<TService>() where TService : class => _services.Get<TService>();

    // ── 内核作用域：宿主自己的模块走这条路（见 IKernelScope）──────────

    /// <summary>当前内核作用域数量（诊断）。</summary>
    public int KernelScopeCount
    {
        get
        {
            lock (_gate)
            {
                return _kernelScopes.Count;
            }
        }
    }

    /// <summary>
    /// 建一个内核作用域。
    ///
    /// 宿主模块在它上面 <c>Provide</c> / <c>RegisterTool</c> / <c>On</c> / <c>Effect</c>，
    /// 与插件用的是同一套机制、同一条纪律（注册即副作用）—— 区别只在「谁来 dispose」。
    /// 关闭宿主时由 <see cref="DisposeKernelScopes"/> 逆序回收。
    /// </summary>
    public IKernelScope CreateKernelScope(string name, string? dataDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var dir = dataDirectory ?? Path.Combine(_options.DataRoot, "_kernel", name);
        Directory.CreateDirectory(dir);

        var scope = new PluginScope(
            $"kernel:{name}",
            _events,
            _services,
            _tools,
            _options.LoggerFactory.CreateLogger($"Kernel.{name}"),
            dir);

        lock (_gate)
        {
            _kernelScopes.Add(scope);
        }

        return scope;
    }

    /// <summary>
    /// 撤销全部内核作用域（后建的先撤，与插件卸载同一顺序纪律）。
    /// 宿主关闭时调用一次即可 —— 调完这个宿主就不能再干活了。
    /// </summary>
    public void DisposeKernelScopes()
    {
        PluginScope[] snapshot;
        lock (_gate)
        {
            snapshot = [.. _kernelScopes];
            _kernelScopes.Clear();
        }

        for (var i = snapshot.Length - 1; i >= 0; i--)
        {
            snapshot[i].Dispose();
        }
    }

    // ── 工具调用（含审批事件）─────────────────────────────────────

    /// <summary>
    /// 调用工具。会先派发 <see cref="ToolPreExecuteEvent"/> —— 任一订阅者置 Cancelled 即拦截。
    /// 「分级审批」就是靠这条路实现的，内核和插件用的是同一套机制。
    /// </summary>
    public async ValueTask<ToolResult> InvokeToolAsync(
        string toolName,
        IReadOnlyDictionary<string, string?>? arguments = null,
        CancellationToken ct = default)
    {
        var args = arguments ?? new Dictionary<string, string?>();

        var preEvent = new ToolPreExecuteEvent { ToolName = toolName, Arguments = args };
        await _events.EmitAsync(preEvent, ct).ConfigureAwait(false);

        if (preEvent.Cancelled)
        {
            return ToolResult.Fail($"已拒绝执行：{preEvent.RejectReason ?? "无理由"}");
        }

        if (!_tools.TryGet(toolName, out var tool) || tool is null)
        {
            return ToolResult.Fail($"工具不存在：{toolName}");
        }

        return await tool.InvokeAsync(new ToolInvocation(toolName, args), ct).ConfigureAwait(false);
    }

    public ValueTask EmitAsync<TEvent>(TEvent evt, CancellationToken ct = default)
        where TEvent : notnull
        => _events.EmitAsync(evt, ct);

    /// <summary>
    /// 内核级事件订阅（不随插件卸载撤销）。
    /// 审批策略、审计、遥测这类「不属于任何插件」的逻辑挂这里 ——
    /// 和插件用的是同一套事件机制，只是生命周期归内核。
    /// </summary>
    public IDisposable Intercept<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler)
        where TEvent : notnull
        => _events.Subscribe(handler);

    // ── 加载 / 卸载 ────────────────────────────────────────────────

    /// <summary>从插件目录加载插件。目录内需有 plugin.json 与入口程序集。</summary>
    public async Task<PluginHandle> LoadAsync(string pluginDirectory, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);

        var manifest = ReadManifest(pluginDirectory);
        Validate(manifest, pluginDirectory);
        CheckDependencies(manifest);

        var assemblyPath = Path.Combine(pluginDirectory, manifest.Assembly);
        var alcName = $"{manifest.Id}@{manifest.Version}#{Guid.NewGuid():N}";
        var alc = new PluginLoadContext(assemblyPath, alcName);

        try
        {
            var assembly = alc.LoadFromAssemblyPath(assemblyPath);

            var entryType = assembly.GetType(manifest.Entry, throwOnError: false)
                ?? throw new InvalidOperationException(
                    $"插件 {manifest.Id}：找不到入口类型 {manifest.Entry}");

            if (!typeof(IPlugin).IsAssignableFrom(entryType))
            {
                // 最典型的原因：契约程序集没有共享加载，导致类型同一性不成立
                throw new InvalidOperationException(
                    $"插件 {manifest.Id}：{manifest.Entry} 未实现 IPlugin。" +
                    $"请检查契约程序集是否被共享加载（插件目录里不应包含 {typeof(IPlugin).Assembly.GetName().Name}.dll）");
            }

            var plugin = (IPlugin)Activator.CreateInstance(entryType)!;

            var dataDirectory = Path.Combine(_options.DataRoot, manifest.Id);
            Directory.CreateDirectory(dataDirectory);

            var scope = new PluginScope(
                manifest.Id,
                _events,
                _services,
                _tools,
                _options.LoggerFactory.CreateLogger($"Plugin.{manifest.Id}"),
                dataDirectory);

            try
            {
                await plugin.ActivateAsync(scope, ct).ConfigureAwait(false);
            }
            catch
            {
                scope.Dispose();
                throw;
            }

            // v3.5 审查 P2：卸载时回调 Forget 摘除活跃表条目（否则 ALC 永远真回收不了）。
            var handle = new PluginHandle(manifest, alc, scope, assembly, Forget);
            lock (_gate)
            {
                _plugins.Add(handle);
            }

            _log.LogInformation(
                "插件已加载：{PluginId}@{Version}（工具 {ToolCount} 个，副作用 {EffectCount} 个）",
                manifest.Id, manifest.Version, _tools.Count, scope.RevokedCount);

            return handle;
        }
        catch
        {
            // 激活失败就把 ALC 放掉，不留半吊子状态
            alc.Unload();
            throw;
        }
    }

    internal void Forget(PluginHandle handle)
    {
        lock (_gate)
        {
            _plugins.Remove(handle);
        }
    }

    // ── 私有 ───────────────────────────────────────────────────────

    private static PluginManifest ReadManifest(string pluginDirectory)
    {
        var manifestPath = Path.Combine(pluginDirectory, "plugin.json");
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException($"缺少插件清单：{manifestPath}");
        }

        var json = File.ReadAllText(manifestPath);
        var manifest = JsonSerializer.Deserialize<PluginManifest>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException($"插件清单解析失败：{manifestPath}");

        return manifest;
    }

    private void Validate(PluginManifest manifest, string pluginDirectory)
    {
        if (string.IsNullOrWhiteSpace(manifest.Id))
        {
            throw new InvalidOperationException("插件清单缺少 id");
        }

        if (string.IsNullOrWhiteSpace(manifest.Entry))
        {
            throw new InvalidOperationException($"插件 {manifest.Id} 缺少 entry");
        }

        if (string.IsNullOrWhiteSpace(manifest.Assembly))
        {
            throw new InvalidOperationException($"插件 {manifest.Id} 缺少 assembly");
        }

        if (!string.Equals(manifest.ApiVersion, _options.ApiVersion, StringComparison.Ordinal))
        {
            // 契约「可加不可改」：主版本不一致直接拒绝，避免运行期类型错乱
            throw new InvalidOperationException(
                $"插件 {manifest.Id} 的 apiVersion={manifest.ApiVersion} 与内核 {_options.ApiVersion} 不匹配");
        }

        var assemblyPath = Path.Combine(pluginDirectory, manifest.Assembly);
        if (!File.Exists(assemblyPath))
        {
            throw new FileNotFoundException($"插件 {manifest.Id} 的入口程序集不存在：{assemblyPath}");
        }
    }

    /// <summary>
    /// 依赖驱动加载（Cordis 的 inject 语义，第一版为简化版）：
    /// 依赖服务未就绪就拒绝加载，并说明缺什么 —— 让加载顺序有确定性，而不是靠猜。
    /// </summary>
    private void CheckDependencies(PluginManifest manifest)
    {
        foreach (var inject in manifest.Injects)
        {
            var serviceType = ResolveServiceType(inject)
                ?? throw new InvalidOperationException(
                    $"插件 {manifest.Id} 声明的依赖 '{inject}' 在契约程序集中不存在");

            if (!_services.TryGet(serviceType, out _))
            {
                throw new InvalidOperationException(
                    $"插件 {manifest.Id} 的依赖服务 '{inject}' 尚未就绪（缺少提供方插件？）");
            }
        }
    }

    private static Type? ResolveServiceType(string name)
    {
        var contracts = typeof(IPlugin).Assembly;
        return contracts.GetType(name, throwOnError: false)
            ?? contracts.GetTypes().FirstOrDefault(t => t.Name == name);
    }

    private sealed class SystemClockService : IClockService
    {
        public DateTimeOffset Now => DateTimeOffset.Now;
    }
}
