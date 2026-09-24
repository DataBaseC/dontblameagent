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
/// 切分口径：<b>按能力的用途</b>，而不是按代码来源 ——
/// 「写小说时才用的那些」和「天天要用的那些」必须能分开开关，
/// 这正是主人要的「真实工作时有的选择地开」。
/// </para>
/// </summary>
public static class BuiltinToolsets
{
    /// <summary>核心：读、写、列目录、问用户。**不可关**。（跑命令属 <c>exec</c>，可关）</summary>
    public const string Core = "core";

    /// <summary>元能力：看/开关工具包本身。**不可关**（关了就没法开回来）。</summary>
    public const string Meta = "meta";

    /// <summary>记忆：记住 / 遗忘 / 检索。</summary>
    public const string Memory = "memory";

    /// <summary>
    /// 执行命令。<b>刻意单独成包</b> —— 「这次不想让它跑命令」是个合理诉求，
    /// 而 run_command 若混进 core（不可关），这个诉求就没有出口。
    /// </summary>
    public const string Exec = "exec";

    /// <summary>历史检索：把被上下文折叠掉的东西捞回来。</summary>
    public const string Search = "search";

    /// <summary>计划与派活：计划、小本本、子 agent。</summary>
    public const string Plan = "plan";

    /// <summary>联网：搜索与抓取。</summary>
    public const string Web = "web";

    /// <summary>自我升级：写插件 / 热重装 / 卸载 / 列插件。</summary>
    public const string Self = "self";

    /// <summary>编程扩展（基石插件 devkit）。</summary>
    public const string DevKit = "devkit";

    /// <summary>写作扩展（基石插件 writing-kit）。</summary>
    public const string WritingKit = "writing-kit";

    /// <summary>不许关闭的包（宿主会拒绝这类开关请求）。</summary>
    public static IReadOnlySet<string> Protected { get; } =
        new HashSet<string>(StringComparer.Ordinal) { Core, Meta };
}
