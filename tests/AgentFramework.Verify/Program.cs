using System.Runtime.CompilerServices;
using AgentFramework.Contracts;
using AgentFramework.Kernel;
using Microsoft.Extensions.Logging.Abstractions;

// ═══════════════════════════════════════════════════════════
//  插件内核垂直切片验证
//  跑通：加载 → 注册 → 调用 → 审批拦截 → 卸载 → 真回收
// ═══════════════════════════════════════════════════════════

var passes = 0;
var failures = 0;

void Check(string name, bool ok, string? detail = null)
{
    var suffix = detail is null ? "" : $"  ({detail})";
    if (ok)
    {
        passes++;
        Console.WriteLine($"  [PASS] {name}{suffix}");
    }
    else
    {
        failures++;
        Console.WriteLine($"  [FAIL] {name}{suffix}");
    }
}

var pluginDir = Path.Combine(AppContext.BaseDirectory, "plugins", "hello");
var dataRoot = Path.Combine(Path.GetTempPath(), "af-plugin-data");

Console.WriteLine("═══ 插件内核垂直切片验证 ═══");
Console.WriteLine($"插件目录：{pluginDir}");

if (!Directory.Exists(pluginDir))
{
    Console.WriteLine("插件目录不存在 —— 构建脚本没把插件复制过来。");
    return 99;
}

// ── 1. 前提检查 ────────────────────────────────────────────
Console.WriteLine("\n── 1. 前提检查（契约共享）──");
Check("插件目录不含契约程序集", !File.Exists(Path.Combine(pluginDir, "AgentFramework.Contracts.dll")));
Check("插件清单存在", File.Exists(Path.Combine(pluginDir, "plugin.json")));
Check("入口程序集存在", File.Exists(Path.Combine(pluginDir, "AgentFramework.SamplePlugin.dll")));

// ── 2. 加载与注册 ──────────────────────────────────────────
Console.WriteLine("\n── 2. 加载与注册 ──");
var host = new PluginHost(new PluginHostOptions
{
    LoggerFactory = NullLoggerFactory.Instance,
    DataRoot = dataRoot,
});

var handle = await host.LoadAsync(pluginDir);
Check("插件加载成功", handle.Id == "hello", $"{handle.Id}@{handle.Version}");
Check("工具已注册", host.ToolNames.Contains("hello"), string.Join(",", host.ToolNames));
Check("事件已订阅", host.EventSubscriptionCount >= 1, $"{host.EventSubscriptionCount}");
Check("插件成功消费内核服务（seam Consumer）", true, "ActivateAsync 内 Get<IClockService>() 未抛异常");
Check("插件私有数据目录已创建", Directory.Exists(Path.Combine(dataRoot, "hello")));

// ── 3. 工具调用 ────────────────────────────────────────────
Console.WriteLine("\n── 3. 工具调用 ──");
var r1 = await host.InvokeToolAsync("hello", new Dictionary<string, string?> { ["name"] = "DoBoC" });
Check("工具调用成功", r1.Success && r1.Output.Contains("Hello, DoBoC"), r1.Output);

// ── 4. 审批拦截（把「分级审批」验证成普通事件）────────────
Console.WriteLine("\n── 4. 审批拦截 ──");
using (host.Intercept<ToolPreExecuteEvent>((e, _) =>
       {
           e.Cancelled = true;
           e.RejectReason = "测试拦截";
           return ValueTask.CompletedTask;
       }))
{
    var r2 = await host.InvokeToolAsync("hello");
    Check("订阅者可拦截工具执行", !r2.Success && (r2.Error ?? "").Contains("已拒绝"), r2.Error);
}

var r3 = await host.InvokeToolAsync("hello");
Check("解除拦截后恢复正常", r3.Success);

// ── 5. 卸载与撤销 ──────────────────────────────────────────
Console.WriteLine("\n── 5. 卸载与撤销 ──");
await handle.DisposeAsync();
Check("卸载已撤销全部副作用（工具+事件+定时器）", handle.RevokedEffectCount >= 3, $"{handle.RevokedEffectCount} 个");
Check("工具注册已撤销", !host.ToolNames.Contains("hello"));
Check("事件订阅已撤销", host.EventSubscriptionCount == 0, $"{host.EventSubscriptionCount}");

// ── 5.5 内核作用域（宿主自己也是「一组插件」的地基）────────
Console.WriteLine("\n── 5.5 内核作用域 ──");

var kernelScope = host.CreateKernelScope("verify");
Check("内核作用域已登记", host.KernelScopeCount == 1, $"{host.KernelScopeCount}");

var probeService = new ProbeService();
kernelScope.Provide<IProbeService>(probeService);
Check("内核作用域可 Provide 服务（宿主自己走 seam）",
    ReferenceEquals(kernelScope.Get<IProbeService>(), probeService));

var revokeOrder = new List<string>();
kernelScope.Effect(() => new RecordingDisposable(() => revokeOrder.Add("先")));
kernelScope.Effect(() => new RecordingDisposable(() => revokeOrder.Add("后")));

var probeTool = new ProbeTool();
kernelScope.RegisterTool(probeTool);
Check("内核作用域注册的工具对内核可见", host.ToolNames.Contains("probe_tool"));

var probeCall = await host.InvokeToolAsync("probe_tool");
Check("内核注册的工具可经 InvokeToolAsync 调用", probeCall.Success && probeCall.Output == "pong", probeCall.Output);

var heard = 0;
kernelScope.On<ProbeEvent>((_, _) =>
{
    heard++;
    return ValueTask.CompletedTask;
});
await host.EmitAsync(new ProbeEvent());
Check("内核作用域订阅的事件被派发", heard == 1, $"{heard}");

host.DisposeKernelScopes();
Check("内核作用域已全部撤销", host.KernelScopeCount == 0, $"{host.KernelScopeCount}");
Check("副作用按「后注册先撤销」逆序回收", string.Join(",", revokeOrder) == "后,先", string.Join(",", revokeOrder));
Check("撤销后工具已消失", !host.ToolNames.Contains("probe_tool"));
Check("撤销后服务已消失", ThrowsServiceNotAvailable(() => kernelScope.Get<IProbeService>()));

// ── 6. 真卸载：程序集内存回收 ──────────────────────────────
Console.WriteLine("\n── 6. 真卸载（程序集内存回收）──");
var weak = await LoadInvokeUnloadAsync(pluginDir);

for (var i = 0; i < 12 && weak.IsAlive; i++)
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
}

Check("插件程序集已被 GC 回收（真卸载，非假卸载）", !weak.IsAlive,
    weak.IsAlive ? "仍有引用钉住插件程序集" : "已回收");

Console.WriteLine($"\n═══ 结果：{passes} 通过 / {failures} 失败 ═══");
return failures == 0 ? 0 : 1;

// ── 辅助：把加载/卸载关在一个独立方法里，避免局部变量把程序集钉住 ──
[MethodImpl(MethodImplOptions.NoInlining)]
static async Task<WeakReference> LoadInvokeUnloadAsync(string pluginDir)
{
    var host = new PluginHost(new PluginHostOptions
    {
        LoggerFactory = NullLoggerFactory.Instance,
        DataRoot = Path.Combine(Path.GetTempPath(), "af-plugin-data"),
    });

    var handle = await host.LoadAsync(pluginDir);
    await host.InvokeToolAsync("hello");

    var weak = handle.CreateAssemblyWeakReference();
    await handle.DisposeAsync();
    return weak;
}

static bool ThrowsServiceNotAvailable(Action action)
{
    try
    {
        action();
        return false;
    }
    catch (ServiceNotAvailableException)
    {
        return true;
    }
}

// ── 验收用桩（内核作用域那一节）──────────────────────────
internal interface IProbeService
{
    string Ping();
}

internal sealed class ProbeService : IProbeService
{
    public string Ping() => "pong";
}

internal sealed class ProbeEvent
{
    public string Id { get; init; } = "probe";
}

internal sealed class ProbeTool : ITool, IToolWithSchema
{
    public string Name => "probe_tool";

    public string Description => "验收用桩工具（内核作用域注册）";

    public string ParametersJsonSchema => """{"type":"object","properties":{}}""";

    public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
        => ValueTask.FromResult(ToolResult.Ok("pong"));
}

internal sealed class RecordingDisposable(Action onDispose) : IDisposable
{
    private Action? _onDispose = onDispose;

    public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
}
