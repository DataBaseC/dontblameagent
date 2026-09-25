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

    // ── 工具包（可见性的最小单位）────────────────────────────────

    /// <summary>当前有工具的包 id，按名排序。</summary>
    public IReadOnlyList<string> ToolsetIds => _tools.ToolsetIds;

    /// <summary>包 id → 包描述 的快照（诊断面与界面开关都读它）。</summary>
    public IReadOnlyDictionary<string, ToolsetDescriptor> Toolsets => _tools.Descriptors;

    /// <summary>某工具属于哪个包（未注册返回 null）。</summary>
    public string? ToolsetOf(string toolName) => _tools.ToolsetOf(toolName);

    /// <summary>某包里的工具名（按名排序）。</summary>
    public IReadOnlyList<string> ToolsInToolset(string toolsetId) => _tools.ToolsInToolset(toolsetId);

    /// <summary>
    /// 登记/更新包描述（宿主模块与插件都可以调）。
    /// 自动生成的朴素描述只有 id，界面开关靠这个补上「这个包是干什么的」。
    /// </summary>
    public void DescribeToolset(ToolsetDescriptor descriptor) => _tools.DescribeToolset(descriptor);

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

    /// <summary>
    /// 从插件目录加载插件。目录内需有 <c>plugin.json</c>，以及二者之一：
    /// 入口程序集（<c>assembly</c> + <c>entry</c>），或脚本（<c>script</c>，<b>免编译</b>）。
    /// </summary>
    public async Task<PluginHandle> LoadAsync(string pluginDirectory, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);

        var manifest = ReadManifest(pluginDirectory);
        Validate(manifest, pluginDirectory);
        CheckDependencies(manifest);

        var isScript = !string.IsNullOrWhiteSpace(manifest.Script);

        PluginLoadContext? alc = null;
        Assembly? assembly = null;
        IPlugin plugin;

        if (isScript)
        {
            // 脚本插件不需要 ALC：没有程序集要隔离，也没有程序集要卸载。
            plugin = new ScriptPlugin(pluginDirectory, manifest, InvokeToolForScript);
        }
        else
        {
            var assemblyPath = ResolvePluginFile(pluginDirectory, manifest.Assembly, manifest.Id, "assembly");
            var alcName = $"{manifest.Id}@{manifest.Version}#{Guid.NewGuid():N}";
            alc = new PluginLoadContext(assemblyPath, alcName);

            try
            {
                assembly = alc.LoadFromAssemblyPath(assemblyPath);

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

                plugin = (IPlugin)Activator.CreateInstance(entryType)!;
            }
            catch
            {
                alc.Unload();
                throw;
            }
        }

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
            // 激活失败（脚本插件还包括「装载自测不过」）就把 ALC 放掉，不留半吊子状态
            scope.Dispose();
            alc?.Unload();
            throw;
        }

        var script = plugin as ScriptPlugin;

        // ── 插件包的"人话"描述 ────────────────────────────────────
        // 插件工具默认落进与插件 id 同名的包；自动生成的描述只有 id，
        // 界面上那一排开关就会显示成「devkit」。把清单里的名字与说明填进去，
        // 主人看到的是「编程扩展工具包」—— 选择开关的第一步是看得懂。
        DescribePluginToolset(manifest);

        // v3.5 审查 P2：卸载时回调 Forget 摘除活跃表条目（否则 ALC 永远真回收不了）。
        var handle = new PluginHandle(
            manifest,
            alc,
            scope,
            assembly,
            Forget,
            selfTestReport: script?.SelfTestReport,
            registeredTools: script?.RegisteredToolNames);

        lock (_gate)
        {
            _plugins.Add(handle);
        }

        _log.LogInformation(
            "插件已加载：{PluginId}@{Version}（{Kind}，工具 {ToolCount} 个，副作用 {EffectCount} 个）",
            manifest.Id, manifest.Version, isScript ? "脚本" : "程序集", _tools.Count, scope.RevokedCount);

        return handle;
    }

    internal void Forget(PluginHandle handle)
    {
        lock (_gate)
        {
            _plugins.Remove(handle);
        }
    }

    // ── 运行期装卸（热更新）─────────────────────────────────────────

    /// <summary>按 id 找一个已装插件（没有返回 null）。</summary>
    /// <summary>
    /// 把插件清单里的名字、说明与「是否常驻」登记到它贡献的包上。
    ///
    /// <para>
    /// 自动生成的包描述只有 id，界面上那一排开关会显示成「devkit」；
    /// 填上清单里的名字之后显示的是「编程扩展工具包」—— 让人愿意去关的第一步，是看得懂。
    /// </para>
    /// </summary>
    private void DescribePluginToolset(PluginManifest manifest)
    {
        foreach (var declaration in manifest.Toolsets)
        {
            if (string.IsNullOrWhiteSpace(declaration.Id))
            {
                continue;
            }

            _tools.DescribeToolset(new ToolsetDescriptor
            {
                Id = declaration.Id,
                Name = string.IsNullOrWhiteSpace(declaration.Name) ? declaration.Id : declaration.Name,
                Description = declaration.Description,
                Eager = declaration.Eager,
                Source = manifest.Id,
            });
        }

        // 默认包（= 插件 id）：只有它真有工具时才登记，避免造出空包
        if (_tools.ToolsInToolset(manifest.Id).Count > 0)
        {
            _tools.DescribeToolset(new ToolsetDescriptor
            {
                Id = manifest.Id,
                Name = string.IsNullOrWhiteSpace(manifest.Name) ? manifest.Id : manifest.Name,
                Description = manifest.Description,
                Source = manifest.Id,
            });
        }
    }

    public PluginHandle? Find(string pluginId)
    {
        lock (_gate)
        {
            return _plugins.FirstOrDefault(p => string.Equals(p.Id, pluginId, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// 卸载一个已装插件（撤销副作用 → 请求 ALC 回收 → 摘除活跃表）。
    /// 返回 false = 本来就没装它 —— 调用方据此区分「卸掉了」与「没这回事」。
    /// </summary>
    public async Task<bool> UnloadAsync(string pluginId, CancellationToken ct = default)
    {
        var handle = Find(pluginId);
        if (handle is null)
        {
            return false;
        }

        await handle.DisposeAsync().ConfigureAwait(false);
        _log.LogInformation("插件已卸载：{PluginId}", pluginId);
        return true;
    }

    /// <summary>
    /// <b>重新装载</b>：先卸掉同 id 的旧版，再装这份新目录 —— 这就是热更新。
    ///
    /// <para>
    /// 顺序不能反（<b>单版本单实例</b>纪律）：先装后卸会出现两个同名插件的工具
    /// 同时挂在注册表里，工具名重复会直接抛。
    /// </para>
    /// <para>
    /// 卸载是「请求式」（ALC 要等 GC 才真回收），但<b>副作用已经同步撤销完了</b>：
    /// 从工具面 / 服务面 / 事件面看，旧版此刻已经不存在 —— 这正是热更新要的语义。
    /// </para>
    /// </summary>
    public async Task<PluginHandle> ReloadAsync(string pluginDirectory, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);

        var manifest = ReadManifest(pluginDirectory);

        // v3.6 审查修复：热重载前给旧版留一份备份。原实现「先卸后装」，新版一旦装载失败
        // （自测不过 / 激活异常），旧版也已被撤销 —— 工作区里新旧都没有了，凭空丢能力。
        var backup = TryBackupDirectory(pluginDirectory);

        try
        {
            if (!string.IsNullOrWhiteSpace(manifest.Id))
            {
                await UnloadAsync(manifest.Id, ct).ConfigureAwait(false);
            }

            return await LoadAsync(pluginDirectory, ct).ConfigureAwait(false);
        }
        catch
        {
            // 新版装载失败 → 用备份把旧版放回去重新装载，做到「失败即回滚」。
            if (backup is not null)
            {
                try
                {
                    RestoreBackup(backup, pluginDirectory);
                    return await LoadAsync(pluginDirectory, ct).ConfigureAwait(false);
                }
                catch
                {
                    // 回滚也失败：抛出原始异常语义，让调用方知道该插件当前不可用
                }
            }

            throw;
        }
    }

    /// <summary>热重载前备份插件目录（失败返回 null，绝不影响主流程）。</summary>
    private static string? TryBackupDirectory(string pluginDirectory)
    {
        try
        {
            if (!Directory.Exists(pluginDirectory))
            {
                return null;
            }

            var backup = pluginDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + ".bak-" + Guid.NewGuid().ToString("N")[..8];
            CopyDirSafe(pluginDirectory, backup);
            return backup;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把备份目录放回原位（先清空目标再拷回）。</summary>
    private static void RestoreBackup(string backup, string pluginDirectory)
    {
        if (Directory.Exists(pluginDirectory))
        {
            Directory.Delete(pluginDirectory, recursive: true);
        }

        CopyDirSafe(backup, pluginDirectory);
    }

    /// <summary>拷贝目录，**不跟随符号链接**（防链接指到盘外 / 环状链接空转）。</summary>
    private static void CopyDirSafe(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var sub in Directory.EnumerateDirectories(sourceDir))
        {
            if (new DirectoryInfo(sub).LinkTarget is not null)
            {
                continue;
            }

            CopyDirSafe(sub, Path.Combine(targetDir, Path.GetFileName(sub)));
        }
    }

    /// <summary>
    /// 脚本插件借调其他工具的通道（<c>ctx.callTool</c>）。
    /// 能力声明由 <see cref="ScriptPlugin"/> 先卡一道，这里只负责真正调用 ——
    /// 于是审批（<see cref="ToolPreExecuteEvent"/>）照常生效，脚本借来的调用也要过审批。
    /// </summary>
    private ValueTask<ToolResult> InvokeToolForScript(
        string toolName,
        IReadOnlyDictionary<string, string?>? arguments,
        CancellationToken ct)
        => InvokeToolAsync(toolName, arguments, ct);

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

        if (!string.Equals(manifest.ApiVersion, _options.ApiVersion, StringComparison.Ordinal))
        {
            // 契约「可加不可改」：主版本不一致直接拒绝，避免运行期类型错乱
            throw new InvalidOperationException(
                $"插件 {manifest.Id} 的 apiVersion={manifest.ApiVersion} 与内核 {_options.ApiVersion} 不匹配");
        }

        // 脚本插件：只需要脚本文件存在，不需要程序集与入口类型。
        if (!string.IsNullOrWhiteSpace(manifest.Script))
        {
            var scriptPath = ResolvePluginFile(pluginDirectory, manifest.Script, manifest.Id, "script");
            if (!File.Exists(scriptPath))
            {
                throw new FileNotFoundException($"插件 {manifest.Id} 的脚本不存在：{scriptPath}");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(manifest.Entry))
        {
            throw new InvalidOperationException($"插件 {manifest.Id} 缺少 entry（程序集插件需要 assembly + entry）");
        }

        if (string.IsNullOrWhiteSpace(manifest.Assembly))
        {
            throw new InvalidOperationException($"插件 {manifest.Id} 缺少 assembly（脚本插件请改用 script 字段）");
        }

        var assemblyPath = ResolvePluginFile(pluginDirectory, manifest.Assembly, manifest.Id, "assembly");
        if (!File.Exists(assemblyPath))
        {
            throw new FileNotFoundException($"插件 {manifest.Id} 的入口程序集不存在：{assemblyPath}");
        }
    }

    /// <summary>
    /// 把清单里的相对文件名解析成插件目录内的绝对路径。
    /// <c>script: "../../x.js"</c> / <c>assembly: "C:\\evil.dll"</c> 这类写法
    /// 必须在装载前就拒掉 —— 否则 Path.Combine 会老实拼出区外路径。
    /// </summary>
    private static string ResolvePluginFile(string pluginDirectory, string relative, string pluginId, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(relative)
            || Path.IsPathRooted(relative)
            || relative.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"插件 {pluginId} 的 {fieldName} 必须是插件目录内的相对路径：{relative}");
        }

        var rootFull = Path.GetFullPath(pluginDirectory);
        var combined = Path.GetFullPath(Path.Combine(rootFull, relative));
        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;

        if (!combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"插件 {pluginId} 的 {fieldName} 越出插件目录：{relative}");
        }

        return combined;
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
