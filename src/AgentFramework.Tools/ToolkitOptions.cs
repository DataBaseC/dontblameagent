namespace AgentFramework.Tools;

/// <summary>
/// 官方工具集的配置。
///
/// 设计要点（学 dsh）：<b>预算与边界是运维的事，不是模型的事</b>。
/// 所以搜索结果条数、超时、字符上限这些全部在这里配置，
/// <b>不进模型可见的工具契约</b> —— 模型的工具参数永远只有 query / url / path 这种业务参数。
/// </summary>
public sealed class ToolkitOptions
{
    /// <summary>工作区根目录。所有文件与命令操作都被限制在它之内。</summary>
    public required string WorkspaceRoot { get; set; }

    /// <summary>
    /// 会话级工作区解析器（多项目支持）：由宿主接线到 AsyncLocal 回合作用域 ——
    /// 回合所属会话钉了项目目录就返回它，否则回落宿主工作区。
    /// 文件沙箱（WorkspacePath.TryResolve）与 run_command 的工作目录都走 <see cref="EffectiveRoot"/>。
    /// </summary>
    public Func<string>? WorkspaceRootResolver { get; set; }

    /// <summary>本次调用实际生效的工作区根（会话级优先，宿主级兜底）。</summary>
    public string EffectiveRoot => WorkspaceRootResolver?.Invoke() ?? WorkspaceRoot;

    /// <summary>
    /// 读操作是否允许越出工作区。<b>默认 true</b>。
    ///
    /// <para>
    /// 「读得到、写不出去」是本地 personal agent 想要的形状：模型可以翻任意位置的
    /// 资料（读文件、列目录），但落笔只能落在本会话的项目目录里 ——
    /// 边界在 <see cref="WorkspacePath.TryResolve"/> 的 forWrite 分支上。
    /// </para>
    /// <para>
    /// 想要更封闭的形态（读写都锁在工作区内），把它设为 false 即可，
    /// 行为就退回 v3.5 之前的那套边界。
    /// </para>
    /// </summary>
    public bool AllowReadOutsideWorkspace { get; set; } = true;

    // ── 长任务上下文治理（DESIGN.md 4.15 · L2 大输出引用化）────────

    /// <summary>
    /// 上下文治理配置。工具靠它决定「多大的结果该落盘引用化」——
    /// 与宿主共用同一份类型，边界参数只在一处定义。
    /// </summary>
    public Contracts.ContextOptions Context { get; set; } = new();

    /// <summary>是否启用大输出落盘（L2）。关掉则退回纯截断。</summary>
    public bool ArtifactsEnabled { get; set; } = true;

    private ArtifactStore? _artifacts;

    /// <summary>工件存储（懒创建 —— 没真落盘就不建目录）。</summary>
    public ArtifactStore? Artifacts
        => ArtifactsEnabled ? _artifacts ??= new ArtifactStore(WorkspaceRoot) : null;

    /// <summary>
    /// 把一个工具结果「引用化」：超过 <see cref="Contracts.ContextOptions.InlineResultLimit"/>
    /// 就落盘，上下文里只留摘要 + 路径 + 头尾；否则原样返回。
    /// </summary>
    public string ShrinkResult(string toolName, string content)
        => Artifacts is null
            ? content
            : Artifacts.Shrink(
                toolName,
                content,
                Context.InlineResultLimit,
                Context.InlineHeadChars,
                Context.InlineTailChars);

    // ── 文件 ─────────────────────────────────────────────
    public int MaxReadChars { get; set; } = 100_000;

    public int MaxWriteChars { get; set; } = 1_000_000;

    // ── 命令 ─────────────────────────────────────────────
    public int CommandTimeoutSeconds { get; set; } = 30;

    public int MaxCommandOutputChars { get; set; } = 50_000;

    // ── 命令沙箱 ──────────────────────────────────────────
    // 与其它预算同一原则：护栏强度是运维的事，不进模型可见的工具契约。

    /// <summary>
    /// 沙箱档位名：<c>auto</c>（默认，Windows 用 job、其他平台用 process）/
    /// <c>off</c> / <c>process</c> / <c>job</c>，或插件注册的后端名。
    /// 名字不认识或平台不可用时回落并记说明（见 <c>ISandboxRegistry.ResolveNote</c>）。
    /// </summary>
    public string? SandboxName { get; set; }

    /// <summary>单条命令所属作业的内存上限（0 = 不限）。仅 job 档生效。</summary>
    public long CommandMaxMemoryBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// 作业内活动进程数上限（0 = 不限，默认）。别设成 1 ——
    /// <c>cmd.exe /c foo</c> 本身就是两个进程，设 1 命令连启动机会都没有。
    /// </summary>
    public int CommandMaxProcesses { get; set; }

    /// <summary>单条命令的 CPU 时间上限（秒，0 = 不限）。仅 job 档生效。</summary>
    public int CommandMaxCpuSeconds { get; set; }

    // ── 搜索 ─────────────────────────────────────────────
    public int SearchMaxResults { get; set; } = 8;

    public int SearchTimeoutMs { get; set; } = 30_000;

    /// <summary>
    /// 搜索后端降级链（逗号分隔，按序尝试，第一个出结果的胜出）。
    /// 可选：bing、baidu、searxng。默认 <c>bing,baidu,searxng</c> ——
    /// 公共 SearXNG 实例（searx.be）在不少网络下不可达，所以它退到末位。
    /// </summary>
    public string SearchBackends { get; set; } = "bing,baidu,searxng";

    /// <summary>自建 SearXNG 实例地址（搜索链里含 searxng 时用）。</summary>
    public string? SearxngBaseUrl { get; set; }

    // ── 抓取 ─────────────────────────────────────────────
    public int FetchTimeoutMs { get; set; } = 30_000;

    public int FetchMaxChars { get; set; } = 50_000;

    /// <summary>
    /// 是否允许访问私有网段 / 回环地址。
    /// <b>默认 false</b> —— 这是 SSRF 防护的开关，桌面端绝不该默认放开。
    /// 仅在受控测试时才置 true。
    /// </summary>
    public bool AllowPrivateNetworks { get; set; }

    /// <summary>搜索域名黑名单（后缀匹配）。</summary>
    public List<string> BlockedDomains { get; set; } = [];

    /// <summary>历史检索返回的最大条数（同搜索一样：预算进配置，不进工具契约）。</summary>
    public int HistorySearchMaxResults { get; set; } = 8;

    /// <summary>记忆检索返回的最大条数。</summary>
    public int MemorySearchMaxResults { get; set; } = 10;

    /// <summary>产品 UA —— 显式声明身份，不发浏览器伪装 UA。</summary>
    public string UserAgent { get; set; } = "AgentFramework/0.1 (+local desktop agent)";

    public bool IsDomainBlocked(string host)
    {
        foreach (var blocked in BlockedDomains)
        {
            if (host.EndsWith(blocked, StringComparison.OrdinalIgnoreCase)
                || host.Equals(blocked, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
