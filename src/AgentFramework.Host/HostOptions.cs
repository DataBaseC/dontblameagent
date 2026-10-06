namespace AgentFramework.Host;

/// <summary>单个模型端点的配置。云端与本地（LM Studio）共用这一份 —— 因为协议相同。</summary>
public sealed class LlmEndpointOptions
{
    public string BaseUrl { get; set; } = "";

    public string ApiKey { get; set; } = "";

    public string Model { get; set; } = "";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Model);
}

/// <summary>宿主装配参数。</summary>
public sealed class HostOptions
{
    /// <summary>工作区根目录：文件工具与命令工具的边界就在这。</summary>
    public required string WorkspaceRoot { get; set; }

    /// <summary>会话日志目录（每个会话一个 .jsonl）。</summary>
    public required string SessionsDir { get; set; }

    /// <summary>插件目录（其下每个子目录是一个插件）。留空则不加载插件。</summary>
    public string? PluginsDir { get; set; }

    /// <summary>
    /// 命令沙箱档位：<c>auto</c>（默认）/ <c>off</c> / <c>process</c> / <c>job</c> /
    /// <c>bwrap</c> / <c>container</c>，或插件注册的后端名。名字不认识或平台不可用时回落到可用档。
    /// </summary>
    public string? Sandbox { get; set; }

    /// <summary>
    /// <c>container</c> 档用的镜像（如 <c>node:22</c> / <c>mcr.microsoft.com/dotnet/sdk:10.0</c>）。
    /// <b>选容器档就必须配它</b> —— 独立 rootfs 没有普适默认，所以容器档不进 auto。
    /// </summary>
    public string? SandboxImage { get; set; }

    /// <summary>容器网络策略：<c>none</c>（默认，禁网）/ <c>bridge</c>（可出网）/ <c>host</c>。</summary>
    public string? SandboxNetwork { get; set; }

    /// <summary>容器运行时：<c>docker</c> / <c>podman</c> / 可执行文件路径。空 = 自动探测（docker 优先）。</summary>
    public string? SandboxRuntime { get; set; }

    /// <summary>
    /// shell 偏好（会话级一次决定）：<c>bash</c>/<c>sh</c>/<c>cmd</c>/<c>powershell</c>/<c>pwsh</c> /
    /// 显式路径 / <c>auto</c>（默认，优先 POSIX）。空 = 自动解析并钉死到会话结束。
    /// </summary>
    public string? Shell { get; set; }

    /// <summary>
    /// 启动时就关掉的<b>工具包</b>。默认空 = 全部开启。
    ///
    /// <para>
    /// 保留包（<c>core</c> / <c>meta</c>）写了也不生效 —— 关掉「读文件 + 写文件 + 列目录 + 问用户」
    /// 会把 agent 关成残废，关掉「工具包开关」就再也开不回来了。
    /// </para>
    /// </summary>
    public IReadOnlyCollection<string>? DisabledToolsets { get; set; }

    /// <summary>
    /// <b>自写插件仓库</b>（agent 自己写的插件落这里）。留空 = <c>&lt;工作区&gt;/plugins</c>。
    ///
    /// <para>
    /// 为什么不复用 <see cref="PluginsDir"/>：那是"随包分发"的插件目录，
    /// 可能落在 Program Files 这类只读位置；而 agent 得能往里写。
    /// 两者来源不同、权限不同，混在一起必然出现"装得上但存不下"的怪状。
    /// </para>
    /// </summary>
    public string? WorkspacePluginsDir { get; set; }

    /// <summary>本次生效的自写插件仓库（没显式指定就落在工作区里，跟着工作区走）。</summary>
    public string EffectiveWorkspacePluginsDir =>
        string.IsNullOrWhiteSpace(WorkspacePluginsDir)
            ? Path.Combine(WorkspaceRoot, "plugins")
            : WorkspacePluginsDir!;

    public string SessionId { get; set; } = "default";

    /// <summary>云端端点（负责复杂任务）。</summary>
    public LlmEndpointOptions? Cloud { get; set; }

    /// <summary>本地端点（LM Studio，负责量大但简单的任务）。</summary>
    public LlmEndpointOptions? Local { get; set; }

    /// <summary>
    /// 路由规则的长上下文阈值（**估算 token**，超过且不需要工具 → 走本地）。
    ///
    /// <para>
    /// 从前这里按**字符数**判（默认 6000），中英混排 / 代码场景下偏差很大；
    /// v3.23 起改用真实 token 估算（中英分别计权），默认 2000 tokens。
    /// </para>
    /// </summary>
    public int LongContextTokens { get; set; } = 2000;

    /// <summary>
    /// 子 Agent 的**派生深度上限**（默认 2：顶层派一级、子 Agent 再派一级）。
    /// 没有闸的话，子 Agent 可以无限往下派 —— 那是一条悄悄烧钱的路径。
    /// </summary>
    public int SubAgentMaxDepth { get; set; } = 2;

    public string SystemPrompt { get; set; } =
        "你是[咪咪]，dba桌面助手。可以调用工具读写文件、执行命令、搜索网络。"
        + "回答简洁；引用网络来源时使用 markdown 链接。";

    public double Temperature { get; set; } = 0.7;

    /// <summary>
    /// 一个回合的最大步数。默认 60 —— 编程任务一次回合动辄几十步工具调用，
    /// 12 步的旧默认会在任务中途把 agent 掐死（「跑一会儿就自己停了」）。
    /// </summary>
    public int MaxSteps { get; set; } = 60;

    /// <summary>
    /// 是否要求端点回报用量（成本与缓存命中率的数据来源）。
    ///
    /// 默认开。少数老版本本地端点不认 <c>stream_options</c>，会直接 400 —— 那时把它关掉即可。
    /// 关掉之后界面会显示「用量未知」，而不是假装是 0。
    /// </summary>
    public bool IncludeUsage { get; set; } = true;

    /// <summary>搜索后端降级链（逗号分隔：bing / baidu / searxng；null = 默认全开）。</summary>
    public string? SearchBackends { get; set; }

    /// <summary>自建 SearXNG 实例地址。</summary>
    public string? SearxngBaseUrl { get; set; }

    /// <summary>
    /// 分级审批策略：决定每次工具调用是放行、拒绝还是询问用户。
    /// 默认见 <see cref="DefaultApprovalPolicy"/>。
    /// </summary>
    public Func<Contracts.ToolPreExecuteEvent, ApprovalDecision> ApprovalPolicy { get; set; }
        = DefaultApprovalPolicy.Decide;

    /// <summary>
    /// 输入级权限规则（MiMo 的 permission rules）：对「工具名 + 关键参数」签名做模式匹配，
    /// 三值处置（allow / ask / deny），后写覆盖先写（last-match-wins）。
    /// 例：<c>run_command git * = allow</c>、<c>run_command rm * = deny</c>。
    /// 空 = 不配规则，纯按档位判定。<strong>活配置</strong> —— 列表改完下一次调用即生效。
    /// </summary>
    public List<ApprovalRule> ApprovalRules { get; set; } = [];

    /// <summary>
    /// <c>external_directory</c> 特殊防护的解除授权。默认 <c>false</c>：
    /// 越界写（写到工作区之外）一律不静默放行，最多问用户（无界面即拒绝），
    /// 规则与档位都推不翻。<c>true</c> = 用户显式授予越界访问。
    /// </summary>
    public bool AllowExternalDirectory { get; set; }

    /// <summary>
    /// 输入转述（澄清模式）配置。
    /// 这里持有的是**活的实例**：界面改它、宿主立即生效，不需要重启。
    /// </summary>
    public Contracts.RephraseOptions Rephrase { get; set; } = new();

    /// <summary>
    /// 长任务上下文治理配置（DESIGN.md 4.15）。
    /// 同样是活实例 —— 水位、窗口、遮蔽策略都能改完即生效。
    /// </summary>
    public Contracts.ContextOptions Context { get; set; } = new();

    /// <summary>
    /// checkpoint（写盘）配置（任务 1）。
    ///
    /// <para>
    /// 与 <see cref="Context"/> 一样是<b>活实例</b>：界面改完即生效、不重启。
    /// 注意 <see cref="Contracts.CheckpointOptions.TriggerRatio"/>（写盘，0.35）与
    /// <see cref="Contracts.ContextOptions.CompressionTriggerRatio"/>（裁剪，0.8）是
    /// <b>两个触发点</b>，勿绑成一个。
    /// </para>
    /// </summary>
    public Contracts.CheckpointOptions Checkpoint { get; set; } = new();

    /// <summary>
    /// 冻结段字节哈希诊断（任务 8 可选增强，<b>默认关</b>）。
    ///
    /// <para>
    /// 打开后：每次装配上下文都算一次冻结段（缓存前缀那一段）的哈希，
    /// 与同会话上一次不一致时记一笔计数并打一行日志 ——
    /// 「前缀被打掉」从一句猜测变成一个可证的数字。默认关是刻意的：
    /// 诊断只为查缓存命中率服务，不该给所有会话都白加一次 SHA256。
    /// </para>
    /// </summary>
    public bool CacheDiagnostics { get; set; }

    /// <summary>
    /// 审批档位（任务 5）：ask / plan / build / yolo。**活配置** —— 切档下一轮立即生效。
    ///
    /// <para>
    /// 与 <see cref="ApprovalPolicy"/> 的关系：档位是「总有一档在生效」的产品面，
    /// 切档会据此重建 <see cref="ApprovalPolicy"/>；<see cref="ApprovalPolicy"/> 保持可覆盖
    /// （验收测试与插件可以塞自己的策略）。
    /// </para>
    /// </summary>
    public ApprovalTier ApprovalTier { get; set; } = ApprovalTier.Ask;

    /// <summary>
    /// 注入自定义上下文摘要器（L5；验收测试用）。
    /// 若不注入，宿主会在配了本地端点时自动用本地小模型做摘要。
    /// </summary>
    public Contracts.IContextSummarizer? ContextSummarizerOverride { get; set; }

    /// <summary>
    /// 注入自定义 checkpoint writer（验收测试 / 自定义提取者用）。
    /// 若不注入，宿主会在配了模型端点时自动用旁路小模型做状态提取（<c>LlmCheckpointWriter</c>）。
    /// 只有在 <see cref="Checkpoint"/> 的 <c>Enabled</c> 打开时才会被调用。
    /// </summary>
    public Contracts.ICheckpointWriter? CheckpointWriterOverride { get; set; }

    /// <summary>
    /// Goal 停止条件（自然语言，如「所有测试通过且代码已提交」）。空 = 不启用目标核验。
    /// 模型每次想收尾时由旁路验证者裁决「真的达成了吗」—— 防止提前宣称完成（MiMo Goal 机制）。
    /// </summary>
    public string? Goal { get; set; }

    /// <summary>Goal 验证器覆盖（验收测试 / 自定义裁决用）。</summary>
    public Contracts.IGoalVerifier? GoalVerifierOverride { get; set; }

    /// <summary>
    /// 进化配置（PLAN-memory-evolution 支柱四）：Dream（记忆周期整理）/ Distill（技能固化）。
    /// 按<b>累计回合数</b>触发（不是挂钟）；Dream 默认开、Distill 默认关（会写工作区）。
    /// 同样是活实例 —— 改完即生效。
    /// </summary>
    public EvolutionOptions Evolution { get; set; } = new();

    /// <summary>
    /// 是否启用会话历史派生索引（SQLite）。
    /// 关掉只是少了「检索历史」与增量写入，主流程照常 —— 索引永远是派生数据。
    /// </summary>
    public bool IndexEnabled { get; set; } = true;

    /// <summary>注入自定义索引实现（验收测试用；也可换成内存实现）。</summary>
    public Contracts.ISessionIndex? IndexOverride { get; set; }

    // ── 分级记忆与工作模式（DESIGN.md 4.16）──────────────────

    /// <summary>
    /// 记忆目录（放<b>全局</b>记忆）。留空则默认 <c>&lt;SessionsDir&gt;/memory</c>。
    /// 项目记忆不在这次 —— 它跟着工作区走。
    /// </summary>
    public string? MemoryDir { get; set; }

    /// <summary>启动时的工作模式。运行期可切换（不需要重启）。</summary>
    public Contracts.AgentMode Mode { get; set; } = Contracts.AgentMode.Work;

    /// <summary>
    /// 配置单路径 —— 模型管理会读写它。
    /// 为空表示不启用运行时模型配置，此时模型完全由 <see cref="Cloud"/> / <see cref="Local"/> 决定。
    /// </summary>
    public string? ConfigPath { get; set; }

    /// <summary>注入自定义记忆存储（验收测试用）。</summary>
    public Contracts.IMemoryStore? MemoryStoreOverride { get; set; }

    /// <summary>两个端点都没配置时，回退到离线演示模型，让整条链路仍然能跑起来。</summary>
    public bool AllowOfflineDemo { get; set; } = true;

    /// <summary>注入自定义 LLM 客户端（验收测试用；也便于宿主自行接管模型装配）。</summary>
    public Contracts.ILlmClient? LlmOverride { get; set; }

    /// <summary>
    /// 注入自定义转述器（验收测试用）。
    /// 有了它，「转述链路」不必依赖真的端点也能端到端验证 —— 可验证性优先。
    /// </summary>
    public Contracts.IUserInputRephraser? RephraserOverride { get; set; }

    /// <summary>
    /// 只加载这些插件 id（null = 全部加载）。
    /// 由启动器的 profile 决定 —— 这就是"像游戏启动器勾选 mod"在装配层的落点。
    /// </summary>
    public IReadOnlyCollection<string>? EnabledPlugins { get; set; }

    /// <summary>
    /// 单个插件加载失败时是否中断整个装配。
    /// 默认 <c>false</c> —— 坏插件不该拖垮整个应用：记录原因后继续，用户至少能启动起来去修它。
    /// </summary>
    public bool FailFastOnPluginError { get; set; }

    /// <summary>
    /// 外部 MCP server 列表（子进程 + stdio JSON-RPC）。
    ///
    /// <para>
    /// 每个 server 成 <c>mcp:&lt;id&gt;</c> 工具包，<b>默认延迟</b>（工具注册着但不进 schema）——
    /// 多个 server 加起来往往几十上百个工具，全量塞进上下文纯属浪费；
    /// 要用时由 <c>use_toolset</c> 把对应包拉进来。
    /// </para>
    /// </summary>
    public List<Mcp.McpServerConfig> McpServers { get; set; } = [];

    /// <summary>
    /// 复制一份、只换会话 id。
    /// 会话切换的实现方式：<b>重新装配一个宿主</b> —— 因为日志文件在装配时就被打开，
    /// 而宿主本就该是"一次装配对应一个会话"的不可变体。
    /// </summary>
    public HostOptions CloneWith(string sessionId) => new()
    {
        WorkspaceRoot = WorkspaceRoot,
        SessionsDir = SessionsDir,
        PluginsDir = PluginsDir,
        // 沙箱档位是「这台机器的安全边界」，切会话不该把它悄悄换回默认。
        Sandbox = Sandbox,
        // 容器档的镜像 / 网络 / 运行时同属「这台机器怎么跑命令」，一并跟着走。
        SandboxImage = SandboxImage,
        SandboxNetwork = SandboxNetwork,
        SandboxRuntime = SandboxRuntime,
        // shell 同理会话级钉死：换会话不该每条命令重新猜。
        Shell = Shell,
        // 关掉的工具包同理：它是「这次干活要背着多少东西」，不该随会话漂移。
        DisabledToolsets = DisabledToolsets,
        SessionId = sessionId,
        Cloud = Cloud,
        Local = Local,
        LongContextTokens = LongContextTokens,
        SubAgentMaxDepth = SubAgentMaxDepth,
        SystemPrompt = SystemPrompt,
        Temperature = Temperature,
        MaxSteps = MaxSteps,
        IncludeUsage = IncludeUsage,
        SearchBackends = SearchBackends,
        SearxngBaseUrl = SearxngBaseUrl,
        ApprovalPolicy = ApprovalPolicy,
        ApprovalTier = ApprovalTier,
        CacheDiagnostics = CacheDiagnostics,
        // 权限规则与越界防护同理：「这台机器的安全边界」，不随会话漂移。
        ApprovalRules = ApprovalRules,
        AllowExternalDirectory = AllowExternalDirectory,
        AllowOfflineDemo = AllowOfflineDemo,
        LlmOverride = LlmOverride,
        // 刻意共享同一个实例：转述设置是「用户偏好」，不该随会话走。
        Rephrase = Rephrase,
        RephraserOverride = RephraserOverride,
        // 上下文治理同理：它是「这台机器的运行边界」，不是某个会话的私事。
        Context = Context,
        // checkpoint（写盘）配置同样共享同一实例 —— 切会话不该分裂出第二份水位。
        Checkpoint = Checkpoint,
        ContextSummarizerOverride = ContextSummarizerOverride,
        // Goal 是「这次任务要做到什么」——全局一份，不随会话走（SetGoal 运行期改）。
        Goal = Goal,
        GoalVerifierOverride = GoalVerifierOverride,
        // 进化节奏同理是机器级配置，不随会话漂移。
        Evolution = Evolution,
        IndexEnabled = IndexEnabled,
        IndexOverride = IndexOverride,
        // 记忆与模式：记忆是「这台机器/这个项目的事实」，模式是运行期状态 —— 都不随会话走
        MemoryDir = MemoryDir,
        Mode = Mode,
        ConfigPath = ConfigPath,
        MemoryStoreOverride = MemoryStoreOverride,
        EnabledPlugins = EnabledPlugins,
        FailFastOnPluginError = FailFastOnPluginError,
        // MCP server 列表：与「装了哪些插件」同级，属这台机器的接入配置，不随会话漂移。
        McpServers = McpServers,
    };
}
