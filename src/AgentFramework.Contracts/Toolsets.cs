namespace AgentFramework.Contracts;

/// <summary>
/// 工具自报所属的<b>工具包</b>（Toolset）。
///
/// <para>
/// 为什么要有它：可见性的最小单位决定「人愿不愿意去关」。
/// 一个个工具名没人记得住，4~6 个包则一眼就懂 ——
/// 于是「有选择地开、降低 agent 负担」才落得到实处。
/// </para>
///
/// <para>
/// 不实现这个接口的工具，会落进与<b>注册来源</b>同名的包（官方工具 → <c>official-tools</c>，
/// 插件工具 → 插件 id）。于是老插件不改清单也天然成包，不必一次性全改。
/// </para>
/// </summary>
public interface IToolWithToolset : ITool
{
    /// <summary>包 id，如 <c>core</c> / <c>devkit</c> / <c>mcp:github</c>。</summary>
    string Toolset { get; }
}

/// <summary>
/// 一个工具包的元信息（内核登记，诊断面与界面开关都读它）。
/// </summary>
public sealed class ToolsetDescriptor
{
    public required string Id { get; set; }

    /// <summary>显示名（界面上给主人看的）。</summary>
    public string Name { get; set; } = "";

    /// <summary>一句话说明这个包装的是什么能力。</summary>
    public string? Description { get; set; }

    /// <summary>
    /// 是否<b>常驻</b>（进模型可见的工具定义）。
    /// <c>false</c> = 延迟包：工具仍注册着，但默认不进 schema —— 这是 Tool Search 的地基。
    /// </summary>
    public bool Eager { get; set; } = true;

    /// <summary>
    /// 是否<b>保留包</b>（不许关闭）。
    /// 关掉「读文件 + 写文件 + 列目录 + 问用户」会把 agent 关成残废；
    /// 关掉「工具包开关」这个工具，就再也开不回来了。
    /// </summary>
    public bool Protected { get; set; }

    /// <summary>谁提供的：<c>core</c>（宿主官方）/ 插件 id / <c>mcp:&lt;server&gt;</c>。</summary>
    public string Source { get; set; } = "";
}

/// <summary>
/// 插件清单里的工具包声明（<c>plugin.json</c> 的 <c>toolsets</c> 段）。
///
/// <para>
/// 不写也能用：插件工具默认落进与插件 id 同名的包。写它是为了让界面上那一排开关
/// 显示成「写作扩展工具包」而不是「writing-kit」，并能声明这个包是不是<b>常驻</b>。
/// </para>
/// </summary>
public sealed class ToolsetDeclaration
{
    public string Id { get; set; } = "";

    public string? Name { get; set; }

    public string? Description { get; set; }

    /// <summary>常驻（进模型可见的工具定义）。<c>false</c> = 延迟包候选（Tool Search 的地基）。</summary>
    public bool Eager { get; set; } = true;
}

/// <summary>
/// 内置包 id 与保留名单。
///
/// <para>
/// 切分口径（2026-09 重划 · 降工具调用压力）：<b>能收 core 的全收 core</b> ——
/// 读/写/改/检索/搬移/删/问用户/记忆/计划/笔记/历史/派活/技能/转换是一条完整工作流，
/// 拆包会让模型先 <c>use_toolset</c> 再干活（多一轮、多一次猜包），
/// 也逼主人记住「哪个文件操作在哪个包」。
/// 可关的包只留「安全收窄」与「出网」：exec（跑命令）/ web / self（含技能工坊）。
/// 领域扩展由基石插件自带（如 writing-kit）。
/// </para>
/// </summary>
public static class BuiltinToolsets
{
    /// <summary>
    /// 核心：完整文件链（读/写/列/改/行读/建/移/删/搜内容/找文件）、问用户、
    /// 记忆、计划与小本本、历史检索、派子 Agent、结构化转换。<b>不可关</b>。
    /// </summary>
    public const string Core = "core";

    /// <summary>元能力：看/开关工具包本身。**不可关**（关了就没法开回来）。</summary>
    public const string Meta = "meta";

    /// <summary>
    /// 执行命令。<b>刻意单独成包</b> —— 「这次不想让它跑命令」是个合理诉求，
    /// 而 run_command 若混进 core（不可关），这个诉求就没有出口。
    /// </summary>
    public const string Exec = "exec";

    /// <summary>联网：搜索与抓取。</summary>
    public const string Web = "web";

    /// <summary>
    /// 自我升级：写插件 / 热重装 / 卸载 / 列插件 + 技能工坊（生成/校验/提炼技能）。
    /// 两者都是「给自己长能力」，合成一包省一次开关。
    /// </summary>
    public const string Self = "self";

    /// <summary>写作扩展（基石插件 writing-kit）。领域包，随插件清单进表。</summary>
    public const string WritingKit = "writing-kit";

    /// <summary>不许关闭的包（宿主会拒绝这类开关请求）。</summary>
    public static IReadOnlySet<string> Protected { get; } =
        new HashSet<string>(StringComparer.Ordinal) { Core, Meta };
}
