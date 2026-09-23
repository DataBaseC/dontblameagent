namespace AgentFramework.Contracts;

/// <summary>事件标记接口。事件类型必须定义在契约程序集内（跨 ALC 传递）。</summary>
public interface IPluginEvent
{
}

/// <summary>
/// 工具执行前事件。
/// 「分级审批」不是独立机制 —— 它就是本事件：订阅者置 <see cref="Cancelled"/> 即可拦截。
/// 内核与插件走同一条路挂把关逻辑。
/// </summary>
public sealed class ToolPreExecuteEvent : IPluginEvent
{
    public required string ToolName { get; init; }

    public required IReadOnlyDictionary<string, string?> Arguments { get; init; }

    public bool Cancelled { get; set; }

    public string? RejectReason { get; set; }
}

/// <summary>
/// 插件清单（plugin.json）。
/// 用 JSON 而非程序集特性：语言无关，且编码 AI 最容易正确生成。
/// </summary>
public sealed class PluginManifest
{
    /// <summary>插件唯一标识。</summary>
    public string Id { get; set; } = "";

    /// <summary>显示名。</summary>
    public string Name { get; set; } = "";

    /// <summary>插件版本。</summary>
    public string Version { get; set; } = "";

    /// <summary>所依赖的契约 API 版本。与内核不一致则拒绝加载。</summary>
    public string ApiVersion { get; set; } = "";

    /// <summary>入口程序集文件名（相对于插件目录）。</summary>
    public string Assembly { get; set; } = "";

    /// <summary>入口类型的完整名称。</summary>
    public string Entry { get; set; } = "";

    /// <summary>
    /// 依赖的服务（Cordis 的 inject）。
    /// 内核等这些服务就绪才加载本插件 —— 让插件加载顺序有确定性，而不是靠猜。
    /// </summary>
    public List<string> Injects { get; set; } = [];
}
