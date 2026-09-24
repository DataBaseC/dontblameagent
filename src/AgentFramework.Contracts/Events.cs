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

    /// <summary>入口类型（程序集插件用）的完整名称。</summary>
    public string Entry { get; set; } = "";

    /// <summary>
    /// <b>脚本入口</b>（相对于插件目录，如 <c>main.js</c>）。
    ///
    /// <para>
    /// 有它 = <b>脚本插件</b>：用内嵌 JS 引擎跑，<b>不需要编译</b> ——
    /// 这是 agent 给自己写插件的主通道（目标机不装 .NET SDK，编译不了 C#）。
    /// 没有它 = 程序集插件，走 <see cref="Assembly"/> + <see cref="Entry"/>。
    /// </para>
    /// </summary>
    public string Script { get; set; } = "";

    /// <summary>一句话说明（列表、诊断、装载报告都用它）。</summary>
    public string? Description { get; set; }

    /// <summary>
    /// 能力声明（脚本插件专用）。<b>未声明即拒</b> ——
    /// 脚本默认碰不到文件系统 / 网络 / 进程，它想干实事只有
    /// <c>ctx.callTool()</c> 借已有工具这一条路，而借谁必须在这里写明：
    /// <c>"tool:read_file"</c>、<c>"tool:write_file"</c>，或 <c>"tool:*"</c>。
    /// </summary>
    public List<string> Capabilities { get; set; } = [];

    /// <summary>
    /// 依赖的服务（Cordis 的 inject）。
    /// 内核等这些服务就绪才加载本插件 —— 让插件加载顺序有确定性，而不是靠猜。
    /// </summary>
    public List<string> Injects { get; set; } = [];

    /// <summary>
    /// 贡献给主界面的 UI 片段。为 null 表示这个插件不碰界面。
    ///
    /// <para>
    /// 这是「插件自带一小块 UI」的第二步：<c>panel.html</c> 是一个独立面板，
    /// 而这里的样式/脚本直接作用于<b>主界面本身</b> —— 换肤、加密度、改消息排版
    /// 都得走这条路（iframe 里的面板改不了父窗口）。
    /// </para>
    /// </summary>
    public PluginUiManifest? Ui { get; set; }

    /// <summary>
    /// 本插件贡献的工具包声明（可选）。
    /// 不写也能用：插件工具默认落进与插件 id 同名的包；写它则能给出显示名、说明与是否常驻，
    /// 于是界面上的开关显示成「写作扩展工具包」而不是「writing-kit」。
    /// </summary>
    public List<ToolsetDeclaration> Toolsets { get; set; } = [];
}

/// <summary>
/// 插件的界面贡献声明（相对插件目录的文件名）。
///
/// <para>
/// <b>声明式</b>是关键：宿主只提供清单里列出的文件，不做任意路径读取。
/// 想加载什么就先写进清单 —— 「装了什么」永远查得到。
/// </para>
/// </summary>
public sealed class PluginUiManifest
{
    /// <summary>注入主页面的样式表（相对插件目录，如 <c>theme.css</c>）。</summary>
    public List<string> Styles { get; set; } = [];

    /// <summary>注入主页面的脚本（相对插件目录，如 <c>theme.js</c>）。按声明顺序在页面末尾加载。</summary>
    public List<string> Scripts { get; set; } = [];
}
