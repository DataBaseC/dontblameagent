using Microsoft.Extensions.Logging;

namespace AgentFramework.Contracts;

/// <summary>
/// 插件唯一入口。
/// 卸载时插件作者「无需手动清理」：所有通过 <see cref="IPluginContext"/> 的注册
/// 都由内核自动收集并按逆序撤销（Cordis 的 effect 语义）。
/// </summary>
public interface IPlugin
{
    Task ActivateAsync(IPluginContext ctx, CancellationToken ct = default);
}

/// <summary>
/// 插件上下文：插件唯一的能力面。
/// 每个注册方法都返回 <see cref="IDisposable"/>，此对象由内核持有，
/// 插件卸载时按「后注册先撤销」逆序释放。
/// </summary>
public interface IPluginContext
{
    /// <summary>订阅事件。撤销时自动解除订阅。</summary>
    IDisposable On<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler) where TEvent : notnull;

    /// <summary>提供服务（seam 的 Provider 角色）。</summary>
    IDisposable Provide<TService>(TService implementation) where TService : class;

    /// <summary>取服务（seam 的 Consumer 角色）。未提供时抛 <see cref="ServiceNotAvailableException"/>。</summary>
    TService Get<TService>() where TService : class;

    /// <summary>注册工具。</summary>
    IDisposable RegisterTool(ITool tool);

    /// <summary>托管外部资源（定时器、网络连接等）。setup 内创建资源并返回其撤销函数。</summary>
    IDisposable Effect(Func<IDisposable> setup);

    /// <summary>插件日志。</summary>
    ILogger Log { get; }

    /// <summary>插件私有数据目录（卸载后仍保留）。需要跨重载保留的状态放这里。</summary>
    string DataDirectory { get; }
}

/// <summary>
/// 内核作用域：<b>与插件作用域同一套机制，只是生命周期归内核自己</b>。
///
/// 存在的理由：宿主自身也是「一堆要注册东西的模块」——
/// 它要提供服务、注册工具、订阅事件、托管后台资源（子进程 / 定时器 / 作业）。
/// 若这些全靠构造函数手工传参，宿主就会长成一个几百行的装配方法，而且每加一个功能都要改它。
///
/// 有了它，「加一个功能」= <b>加一个模块、拿一个作用域、把东西注册进去</b>；
/// 关闭时按「后注册先撤销」逆序回收 —— 漏清理这类问题从设计上就不存在。
///
/// 这正是 dsh「一切皆插件」的落点：连宿主自己也走插件那套 seam。
/// </summary>
public interface IKernelScope : IPluginContext, IDisposable
{
    /// <summary>作用域名（诊断与日志用）。</summary>
    string Name { get; }
}

/// <summary>模型可调用的工具。</summary>
public interface ITool
{
    string Name { get; }

    string Description { get; }

    ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default);
}

/// <summary>
/// 会声明参数的“可描述工具”。单独成一个接口而不是往 ITool 上塞成员 ——
/// 契约「可加不可改」：老工具不实现它也照样工作。
/// </summary>
public interface IToolWithSchema : ITool
{
    /// <summary>参数的 JSON Schema，直接交给模型做 function calling。</summary>
    string ParametersJsonSchema { get; }
}

/// <summary>工具未声明 schema 时使用的空参数表。</summary>
public static class ToolSchemas
{
    public const string Empty = """{"type":"object","properties":{}}""";

    public static string For(ITool tool)
        => tool is IToolWithSchema aware ? aware.ParametersJsonSchema : Empty;
}

/// <summary>工具调用入参（跨 ALC 边界只能用 BCL 类型 + 契约类型）。
/// 值统一为字符串 —— 参数本就来自 JSON，字符串化后既能干净地写进事件日志，
/// 也能原样往返，由工具自己决定怎么解析。</summary>
public sealed record ToolInvocation(string ToolName, IReadOnlyDictionary<string, string?> Arguments);

/// <summary>工具调用结果。</summary>
public sealed record ToolResult(bool Success, string Output, string? Error = null)
{
    public static ToolResult Ok(string output) => new(true, output);

    public static ToolResult Fail(string error) => new(false, string.Empty, error);
}

/// <summary>内核提供、插件消费的基础服务（seam 三角色的最小演示）。</summary>
public interface IClockService
{
    DateTimeOffset Now { get; }
}

/// <summary>取服务失败。</summary>
public sealed class ServiceNotAvailableException(Type serviceType)
    : Exception($"服务未提供：{serviceType.FullName}（可能 injects 未声明，或依赖插件尚未加载）")
{
    public Type ServiceType { get; } = serviceType;
}
