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

    /// <summary>
    /// 注册工具，并指定它属于哪个<b>工具包</b>（<c>null</c> = 按来源自动判定）。
    ///
    /// <para>
    /// 默认实现忽略 <paramref name="toolset"/> 并退回 <see cref="RegisterTool(ITool)"/> ——
    /// 于是老的内核实现不写这个方法也能编译（契约可加不可改）。
    /// </para>
    /// </summary>
    IDisposable RegisterTool(ITool tool, string? toolset) => RegisterTool(tool);

    /// <summary>
    /// 注册一个自定义工作模式（任务 4）。撤销时自动从 <see cref="AgentModes"/> 注销。
    ///
    /// <para>
    /// 默认实现直接落 <see cref="AgentModes.Register"/> —— 模式注册是纯契约层的事，不需要内核配合；
    /// 内核作用域会覆写它，把撤销句柄纳入「后注册先撤销」的收集，于是插件卸载 / 热重载时自动清理
    /// （否则静态档位表会串档与泄漏）。
    /// </para>
    /// </summary>
    IDisposable RegisterMode(ModeProfile profile)
    {
        AgentModes.Register(profile);
        return new ModeRegistration(profile.CustomId ?? string.Empty);
    }

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

/// <summary>
/// 会声明<b>输出</b>结构的“结构化输出工具”（任务 2 的「扩展工具类型」样例）。
///
/// <para>
/// 与 <see cref="IToolWithSchema"/> 对称：那个描述<b>输入</b>，这个描述<b>输出</b> ——
/// 上层（界面 / 编排）据此知道该怎么解析或展示它的结果，而不必靠猜。
/// 同样遵循「契约可加不可改」：不实现它的工具不受任何影响。
/// </para>
/// </summary>
public interface IToolWithStructuredOutput : ITool
{
    /// <summary>输出结构（JSON Schema）。</summary>
    string OutputJsonSchema { get; }
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
    /// <summary>
    /// 随结果注入上下文的图（如 <c>read_image</c>）。
    /// 主循环在 tool 消息之后以 **user 多模态消息**注入 —— OpenAI 系 tool 角色不收图。
    /// </summary>
    public IReadOnlyList<LlmImage>? Attachments { get; init; }

    public static ToolResult Ok(string output) => new(true, output);

    public static ToolResult Fail(string error) => new(false, string.Empty, error);

    public static ToolResult OkWithImages(string output, IReadOnlyList<LlmImage> images)
        => new(true, output) { Attachments = images };
}

/// <summary>内核提供、插件消费的基础服务（seam 三角色的最小演示）。</summary>
public interface IClockService
{
    DateTimeOffset Now { get; }
}

/// <summary>
/// 工作区访问 —— <b>插件做文件活儿唯一的正门</b>。
///
/// <para>
/// 存在的理由：插件跑在独立 ALC 里，<b>只有契约程序集是共享的</b>。
/// 宿主内部那套工具类型（<c>ToolkitOptions</c> / 路径检查器）跨不过 ALC 边界，
/// 插件若各自重造一份，边界纪律立刻出现 N 个版本 —— 迟早有一个版本忘了防符号链接。
/// 于是把「工作区在哪、能写到哪、能读多大」收成这一个接口：宿主提供，插件消费。
/// </para>
///
/// <para>
/// 边界纪律（与官方文件工具<b>同一份实现</b>）：<b>读默认可越出工作区，写永远只能落在工作区内</b>。
/// 插件不许绕过它直接碰 <c>File.*</c> —— 那不是靠自觉，是靠「拿不到工作区路径之外的解析结果」。
/// </para>
/// </summary>
public interface IWorkspaceService
{
    /// <summary>当前生效的工作区根（会话级优先，宿主级兜底）。</summary>
    string Root { get; }

    /// <summary>读操作是否允许越出工作区（默认 true）。</summary>
    bool AllowReadOutsideWorkspace { get; }

    /// <summary>单次读取的字符上限。</summary>
    int MaxReadChars { get; }

    /// <summary>单次写入的字符上限。</summary>
    int MaxWriteChars { get; }

    /// <summary>
    /// 解析路径并做边界检查。相对路径相对 <see cref="Root"/>，绝对路径直接用。
    /// <paramref name="forWrite"/> 为 true 时越界即拒（含穿透符号链接的比较）。
    /// </summary>
    bool TryResolve(string path, bool forWrite, out string fullPath, out string? error);

    /// <summary>
    /// 大输出引用化：超过阈值就落盘，只返回摘要 + 路径 + 头尾。
    /// 与官方工具同一策略 —— 插件工具的输出同样会进上下文，同样该被治理。
    /// </summary>
    string Shrink(string toolName, string content);
}

/// <summary>取服务失败。</summary>
public sealed class ServiceNotAvailableException(Type serviceType)
    : Exception($"服务未提供：{serviceType.FullName}（可能 injects 未声明，或依赖插件尚未加载）")
{
    public Type ServiceType { get; } = serviceType;
}
