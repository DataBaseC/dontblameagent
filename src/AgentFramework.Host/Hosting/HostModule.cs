using AgentFramework.Agent;
using AgentFramework.Contracts;
using AgentFramework.Data;
using AgentFramework.Kernel;
using AgentFramework.Llm;
using AgentFramework.Tools;

namespace AgentFramework.Host.Hosting;

/// <summary>
/// 宿主模块：**以插件的形式参与宿主装配**。
///
/// 这是「一切皆插件」在宿主内部的落点（dsh 的 boot 也是这么长的）：
/// 宿主不再是一个几百行的装配方法，而是一串各管一段的模块。
/// 「加一个功能」于是变成「加一个模块」，而不是「改装配方法」。
///
/// 模块拿到的是内核作用域（<see cref="IKernelScope"/>）——
/// 它注册的工具、服务、事件订阅、后台资源，关闭时都会被自动逆序撤销，
/// 所以模块作者（很可能也是编码 AI）不需要记得清理。
/// </summary>
public interface IHostModule
{
    /// <summary>模块名（诊断用）。</summary>
    string Name { get; }

    /// <summary>
    /// 装配顺序，小的先装。
    /// 模块之间**不声明依赖**，只靠顺序 —— 序号越小越靠底层，
    /// 这样一眼就能看出「谁能用谁」，比一张依赖图好懂。
    /// </summary>
    int Order { get; }

    /// <summary>执行装配。往 <paramref name="state"/> 里填自己产出的东西。</summary>
    ValueTask ConfigureAsync(HostState state, CancellationToken ct = default);
}

/// <summary>
/// 装配期共享状态：模块之间交换东西的唯一通道。
///
/// 它只在装配期活着；装配完成后，需要长期存在的东西由 <see cref="AgentHost"/> 持有。
/// 写成可变属性的袋子的理由很实在：模块之间是「先后填 → 后来取」的关系，
/// 用构造函数互相注入会把顺序重新变成一个隐式依赖，反而更脆。
/// </summary>
public sealed class HostState
{
    public HostState(HostOptions options, PluginHost kernel)
    {
        Options = options;
        Kernel = kernel;
    }

    public HostOptions Options { get; }

    /// <summary>插件内核。模块经它拿作用域、发事件、取服务。</summary>
    public PluginHost Kernel { get; }

    // ── 模型接入（ModelModule 填）────────────────────────────

    public ModelSettingsStore? ModelStore { get; set; }

    public ModelSettings? ModelSettings { get; set; }

    /// <summary>主链路用的客户端（可能是路由器 / 直连 / 离线演示）。</summary>
    public ILlmClient? Llm { get; set; }

    /// <summary>可热替换的壳 —— 换模型只换它的内层。</summary>
    public SwitchableLlmClient? Switchable { get; set; }

    public ILlmClient? LocalClient { get; set; }

    public ILlmClient? CloudClient { get; set; }

    /// <summary>true 表示当前用的是离线演示模型（没配任何端点）。</summary>
    public bool Offline { get; set; }

    public IUserInputRephraser? Rephraser { get; set; }

    public IContextSummarizer? ContextSummarizer { get; set; }

    // ── 数据平面（StorageModule 填）──────────────────────────

    public JsonlEventLog? Log { get; set; }

    public string LogPath { get; set; } = "";

    public ISessionIndex Index { get; set; } = NullSessionIndex.Instance;

    public IMemoryStore Memory { get; set; } = NullMemoryStore.Instance;

    public string MemoryDir { get; set; } = "";

    public string NotesPath { get; set; } = "";

    /// <summary>
    /// 当前会话 id 提供者（F5）。装配期 ToolModule 的工具闭包读它；
    /// AgentHost 在多会话切换（SwapTo）时回填。为 null 时退回装配期的 Options.SessionId。
    /// </summary>
    public Func<string?>? CurrentSessionIdProvider { get; set; }

    /// <summary>取某会话最后一条事件的 Seq（G1 spawn_subagent 分叉点用）。AgentHost 回填。</summary>
    public Func<string, long>? LastSeqOf { get; set; }

    // ── G2 技能系统 ──────────────────────────────────────────

    /// <summary>扫描到的技能清单（StorageModule 或 ToolModule 填）。</summary>
    public IReadOnlyList<SkillDefinition> Skills { get; set; } = [];

    /// <summary>已启用技能名集合（回合边界生效；AgentHost 持有，运行期可变）。</summary>
    public HashSet<string> EnabledSkills { get; } = new(StringComparer.Ordinal);

    /// <summary>子 Agent 编排入口（G1）。AgentHost 回填（需要 OpenSession/SendAsync，装配期还没有）。</summary>
    public Func<string, string, CancellationToken, Task<(string ChildId, bool Success, string Summary)>>? SubAgentRunner { get; set; }

    /// <summary>向指定会话落事件（G3 计划事件用）。AgentHost 回填。</summary>
    public Func<string, SessionEvent, CancellationToken, ValueTask>? EmitToSession { get; set; }

    // ── 工具集（ToolModule 填）───────────────────────────────

    public ToolkitOptions? Toolkit { get; set; }

    /// <summary>官方工具（不含插件工具）。它们随后被注册进内核注册表，成为唯一名单。</summary>
    public List<ITool> OfficialTools { get; } = [];

    // ── 主循环（LoopModule 填）───────────────────────────────

    public HostEventSink? Sink { get; set; }

    public AgentRunner? Runner { get; set; }

    /// <summary>主会话的运行态。子会话由 <see cref="AgentHost.OpenSession"/> 按需另开。</summary>
    public SessionRuntime? MainSession { get; set; }

    // ── 插件加载结果（PluginModule 填）───────────────────────

    public IReadOnlyList<PluginHandle> LoadedPlugins { get; set; } = [];

    public IReadOnlyList<string> SkippedPlugins { get; set; } = [];

    public IReadOnlyList<string> FailedPlugins { get; set; } = [];

    // ── 回调中继（宿主建好后回填，用于打破「runner 要先于 host」的循环）──

    /// <summary>当前模式。工具可见性、记忆层级都靠它查询。</summary>
    public Func<AgentMode>? ModeProvider { get; set; }

    /// <summary>
    /// 用户交互 seam（UI 启动后注入）。没界面时由取用处降级为
    /// <see cref="NullUserInteraction"/> —— 即「问不出去，如实说」。
    /// </summary>
    public Func<IUserInteraction>? InteractionProvider { get; set; }

    public Action<SessionEvent>? EventRelay { get; set; }

    /// <summary>
    /// 文本增量中继（B2）：签名带 <c>sessionId</c> ——
    /// P1 之后「回合在跑的会话」与「当前会话」可以不同，
    /// SSE 帧必须知道增量属于谁，前端才能分流（当前会话渲染 / 后台会话只打点）。
    /// </summary>
    public Action<string, string>? TextRelay { get; set; }

    public Action<string, string>? ReasoningRelay { get; set; }

    /// <summary>取当前模式（未回填时用启动配置里的那个）。</summary>
    public AgentMode CurrentMode => ModeProvider?.Invoke() ?? Options.Mode;

    /// <summary>
    /// 正在跑的回合所属会话的模式 id（HCI：模式随会话钉住，不再随宿主全局切换）。
    /// SendAsync 在回合开始时设置 —— AsyncLocal 随 ExecutionContext 只向下游流动，
    /// 并发会话各自的回合互不串。工具闭包（remember 的层级选择等）读它。
    /// </summary>
    private static readonly AsyncLocal<string?> TurnMode = new();
    private static readonly AsyncLocal<string?> TurnWorkspace = new();

    /// <summary>
    /// 正在跑的回合所属会话的 id（与 TurnMode/TurnWorkspace 同一回合作用域）。
    ///
    /// search_history / spawn_subagent / update_plan 的闭包读它 ——
    /// 否则多会话下这些工具会永远作用在「装配期那个会话」上
    /// （新建会话后搜到的是别人的历史、子 agent 留痕写进别的日志）。
    /// 非回合上下文（UI 直接调用）读到 null，调用方回落到宿主当前会话，行为不变。
    /// </summary>
    private static readonly AsyncLocal<string?> TurnSessionId = new();

    public string? TurnModeId => TurnMode.Value;

    /// <summary>本回合所属会话的项目目录（null = 宿主工作区）。文件沙箱与项目记忆作用域都读它。</summary>
    public string? TurnWorkspaceDir => TurnWorkspace.Value;

    /// <summary>本回合所属会话的 id（null = 不在回合中，调用方回落到宿主当前会话）。</summary>
    public string? TurnSessionIdValue => TurnSessionId.Value;

    public void BeginTurn(string? modeId, string? projectDir = null, string? sessionId = null)
    {
        TurnMode.Value = modeId;
        TurnWorkspace.Value = projectDir;
        TurnSessionId.Value = sessionId;
    }

    /// <summary>回合所属会话的项目记忆作用域 id（多项目隔离的落点）。</summary>
    public string TurnProjectScope()
        => MemoryScope.ProjectFor(TurnWorkspaceDir ?? Options.WorkspaceRoot);

    /// <summary>
    /// 把模式档位里的抽象层级名映射为回合实际作用域：
    /// "project" → 回合所属会话的项目作用域（多项目隔离）；"global" 原样。
    /// 工具闭包（remember/recall/forget）与上下文装配（索引卡/召回）都必须走这里，
    /// 否则写进默认文件、读自项目文件 —— 读写错位。
    /// </summary>
    public IReadOnlyList<string> TurnMemoryScopes(IReadOnlyList<string> abstractScopes)
        => [.. abstractScopes.Select(s => s == MemoryScope.Project ? TurnProjectScope() : s)];

    /// <summary>取当前生效档位：回合中优先用回合所属会话的模式，否则宿主默认。</summary>
    public ModeProfile CurrentProfile
        => AgentModes.Resolve(TurnMode.Value ?? AgentModes.IdOf(CurrentMode));

    /// <summary>
    /// 这一轮该给模型看哪些工具：从内核注册表**现取**，再按模式收窄。
    ///
    /// 注册 ≠ 可见 —— 工具一直注册着，切模式或加载技能只改「暴露面」，
    /// 于是既不必重装工具，也不会把易变内容混进会被缓存的前缀。
    /// </summary>
    public IReadOnlyCollection<ITool> VisibleTools()
        => VisibleToolsCore(CurrentProfile);

    /// <summary>钉住模式的会话用：按指定档位收窄（不读宿主当前模式）。</summary>
    public IReadOnlyCollection<ITool> VisibleToolsFor(string modeId)
        => VisibleToolsCore(AgentModes.Resolve(modeId));

    private IReadOnlyCollection<ITool> VisibleToolsCore(ModeProfile profile)
    {
        // 每轮现取：注册表是活的，运行期挂上的工具下一轮就可见。
        // 名单已按名排序 —— 它是请求前缀的一部分，顺序抖动会打掉端点的前缀缓存。
        var all = Kernel.GetTools();

        // G2：启用中的技能把白名单收窄为「模式许可 ∩ 已启用技能的工具并集」。
        // 没有启用任何带白名单的技能时不收窄 —— 技能是可选收窄器，不是必经闸门。
        var enabledSkillTools = EnabledSkills.Count == 0
            ? null
            : Skills.Where(s => EnabledSkills.Contains(s.Name))
                .Where(s => s.Tools.Count > 0)
                .SelectMany(s => s.Tools)
                .ToHashSet(StringComparer.Ordinal);

        IEnumerable<ITool> filtered = all;

        // AllowedTools 的三态语义与 ExposedToolNames 对齐：
        //   null = 全部工具；空集 = 一个都不发（闲聊模式最实在的一笔省）；非空 = 白名单。
        // v3.4 只拦了非空白名单 —— 空集时 schema 照发，闲聊模式的省钱承诺在请求层漏掉了。
        // （发出去的 schema 每轮都是真金白银的 input token，端点也会因此把工具定义算进缓存前缀。）
        if (profile.AllowedTools is not null)
        {
            filtered = profile.AllowedTools.Count == 0
                ? []
                : filtered.Where(t => profile.AllowedTools.Contains(t.Name, StringComparer.Ordinal));
        }

        if (enabledSkillTools is not null)
        {
            filtered = filtered.Where(t => enabledSkillTools.Contains(t.Name));
        }

        return [.. filtered];
    }

    /// <summary>
    /// 开一个独立会话：共享模型客户端 / 工具注册表 / 索引 / 记忆，独立事件日志与上下文。
    ///
    /// 这是「子 agent」的地基 —— 装配一次，会话可以开很多个。
    /// </summary>
    public SessionRuntime OpenSession(
        string sessionId,
        string? systemPrompt = null,
        int? maxSteps = null,
        Action<SessionEvent>? onEvent = null,
        bool relayToUi = false,
        string? modeId = null,
        string? projectDir = null)
    {
        if (Log is null || Switchable is null)
        {
            throw new InvalidOperationException("装配尚未完成，不能开会话");
        }

        var sessionLog = JsonlEventLog.Open(Path.Combine(Options.SessionsDir, sessionId + ".jsonl"));

        // 先声明、后赋值：sink 的回调要抓 runtime，而 runtime 的构造要拿到 sink。
        // 用闭包捕获这个变量打破循环（与 LoopModule 里同一个手法）。
        SessionRuntime? runtime = null;

        // HCI：模式钉在会话上 —— 工具可见性按会话自己的档位收窄；
        // 未钉（null）的旧路径沿用宿主默认（向后兼容）。
        var toolsProvider = modeId is null
            ? (Func<IReadOnlyCollection<ITool>>)VisibleTools
            : () => VisibleToolsFor(modeId);

        var sink = new HostEventSink(
            sessionLog,
            Kernel,
            Options.ApprovalPolicy,
            () => InteractionProvider?.Invoke() ?? NullUserInteraction.Instance,
            e =>
            {
                // ★ 先收进本会话的内存事件表（P2），再转给调用方的事件回调。
                runtime?.Track(e);
                onEvent?.Invoke(e);
            },
            Index,
            sessionId,
            // P6：会话级审批放行集（运行期才解引用，那时 runtime 已赋好）
            toolName => runtime?.IsToolAllowed(toolName) ?? false);

        var runner = new AgentRunner(
            Switchable,
            toolsProvider,
            sink,
            new AgentOptions
            {
                SessionId = sessionId,
                Model = "auto",
                SystemPrompt = systemPrompt ?? Options.SystemPrompt,
                Temperature = Options.Temperature,
                MaxSteps = maxSteps ?? Options.MaxSteps,
                IncludeUsage = Options.IncludeUsage,

                // 流式回调默认**不接**：子 agent 的思考过程不该串进主人的对话窗口，
                // 要观察就显式传 onEvent —— 事件才是它的正经出口。
                //
                // 但 UI 的会话不一样（relayToUi = true）：它就是要在界面上打字出来的那个。
                // 同一套机制、一个开关，不另开一条路。
                //
                // B2：回调里把**这个会话自己的 id** 带出去（闭包捕获 sessionId），
                // 不读「当前会话」—— 排队期间切走的话，当前会话已经是别人了。
                OnTextDelta = relayToUi ? text => TextRelay?.Invoke(sessionId, text) : null,
                OnReasoningDelta = relayToUi ? text => ReasoningRelay?.Invoke(sessionId, text) : null,
            });

        runtime = new SessionRuntime(sessionId, sessionLog, sink, runner, modeId, projectDir);
        return runtime;
    }
}
