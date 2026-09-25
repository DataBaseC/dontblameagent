namespace AgentFramework.Host.Mcp;

/// <summary>
/// 一个 MCP（Model Context Protocol）server 的配置。
///
/// <para>
/// 接入方式是「子进程 + stdio 上的 JSON-RPC 2.0」—— 与社区生态对齐：
/// 不为 MCP 自造 RPC，直接说它的话，于是文件系统 / GitHub / 数据库等
/// 现成的 MCP server 拿来即用，而不必逐个写适配。
/// </para>
/// </summary>
public sealed class McpServerConfig
{
    /// <summary>server 标识：用于 <c>mcp:&lt;id&gt;</c> 包名与工具名前缀。</summary>
    public string Id { get; set; } = "";

    /// <summary>可执行文件（如 <c>npx</c>、<c>uvx</c>，或绝对路径）。</summary>
    public string Command { get; set; } = "";

    /// <summary>命令行参数。</summary>
    public List<string> Args { get; set; } = [];

    /// <summary>附加环境变量（常用来传 token / 根目录）。</summary>
    public Dictionary<string, string> Env { get; set; } = new(StringComparer.Ordinal);

    /// <summary>是否启用（默认启用）。</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>MCP 协议层错误（server 回了 JSON-RPC error，或进程意外退出）。</summary>
public sealed class McpException(string message) : Exception(message);
