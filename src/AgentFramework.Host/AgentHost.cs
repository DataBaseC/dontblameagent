using System.Text.Json;
using AgentFramework.Agent;
using AgentFramework.Contracts;
using AgentFramework.Data;
using AgentFramework.Index;
using AgentFramework.Kernel;
using AgentFramework.Llm;
using AgentFramework.Tools;

namespace AgentFramework.Host;

/// <summary>一次审批的留痕（诊断用）。</summary>
public sealed record ApprovalRecord(string ToolName, bool Denied, string? Reason);

/// <summary>
/// 宿主：把插件内核、数据平面、模型接入、工具集**装配成一个可运行的整体**。
/// 这是前面几层第一次作为一个程序活起来的地方。
/// </summary>
public sealed partial class AgentHost : IAsyncDisposable
{
    // ★ 下面三个是「当前会话」的**派生视图**（v3.5 审查 P2 修正）：
    //   原先它们是三个独立字段，切会话时要逐个赋值 —— 4 个引用分 4 次写，
    //   并发读者可能撞见「新 _session + 旧 _log」这种撕裂组合（投影读 A 的日志、事件写进 B 的流）。
    //   改成从 _session 派生的只读属性后，「切会话」退化为**一次引用赋值**（原子），
    //   撕裂态在结构上不再可能出现。
    private JsonlEventLog _log => _session.Log;
    private AgentRunner _runner => _session.Runner;
    private HostEventSink _sink => _session.Sink;
    private readonly PluginHost _plugins;
    // 注：不再持有"启动时的插件快照"—— 插件名单一律实时读内核，
    // 否则 agent 运行期装上的插件会被界面和关闭流程双双漏掉。
    private readonly SnapshotProjectionCache _projectionCache;
    private IUserInputRephraser? _rephraser;
    private IContextSummarizer? _contextSummarizer;

    /// <summary>
    /// **当前**会话的运行态：日志 + 事件出口 + 主循环 + 它自己的回合闸。
    /// 可变 —— 「切会话」就是把这几根指针指向另一个 runtime（P1）。
    /// </summary>
    private Hosting.SessionRuntime _session;

    /// <summary>
    /// 本宿主开过的全部会话（P1）：sessionId → 运行态。
    ///
    /// <para>
    /// 与「一个宿主 = 一个会话」的旧模型相比，这里换掉的是一整套账：
    /// 老做法是切一次会话就**重建整个宿主** —— 重载全部插件（ALC 创建/激活）、
    /// 重开 SQLite、重扫 profile、重读记忆，插件一多就是秒级卡顿，
    /// 而且 ALC 反复创建卸载还增加碎片与卸载失败面。
    /// </para>
    /// <para>
    /// 现在装配只有一份，会话只是它下面的一个运行态：切换等于换个引用，
    /// 零延迟、没有 dispose 就没有竞态，而且**与子 agent 走的是同一条机制**。
    /// </para>
    /// </summary>
    private readonly Dictionary<string, Hosting.SessionRuntime> _sessions = new(StringComparer.Ordinal);

    /// <summary>保护「查/建会话」与「换当前指针」这两件事的互斥。</summary>
    private readonly SemaphoreSlim _sessionSwapGate = new(1, 1);

    /// <summary>装配结果。留着是为了一件事：派生新会话（子 agent 走这条路）。</summary>
    private readonly Hosting.HostState _state;

    /// <summary>
    /// 串行化「首次打开会话」这一步（v3.6 审查修复）：
    /// GetOrOpenSession 原在 _sessions 锁外新建 runtime，并发切到同一会话会各建一个，
    /// 覆盖掉的那个日志句柄永不 Dispose。有了这把闸，「查—建—登记」只可能在闸内各做一次。
    /// </summary>
    private readonly object _openGate = new();

    /// <summary>
    /// 保护会话元数据（titles.json / session-meta.json）的「读—改—写」（v3.6 审查修复）：
    /// 原先无锁读改写，并发重命名 / 新建会话会丢更新。配合 <see cref="WriteJsonAtomic"/> 一起用。
    /// </summary>
    private readonly object _metaGate = new();

    /// <summary>
    /// 退休标志（P0 竞态防线）：本宿主已被切走，日志随时会被 Dispose。
    /// volatile —— 写发生在切换线程、读发生在回合线程，没有它这次检查可能被优化掉。
    /// </summary>
    private volatile bool _retired;

    /// <summary>当前工作模式。可变 —— 切换模式不需要重新装配。</summary>
    private AgentMode _mode;

    private AgentHost(HostOptions options, PluginHost plugins, Hosting.HostState state)
    {
        Options = options;
        _plugins = plugins;
        _state = state;
        _session = state.MainSession!;
        _sessions[_session.SessionId] = _session;
        UsingOfflineDemo = state.Offline;
        SummarizationEnabled = state.ContextSummarizer is not null;
        _rephraser = state.Rephraser;
        _contextSummarizer = state.ContextSummarizer;
        _mode = options.Mode;
        _projectionCache = new SnapshotProjectionCache(Path.Combine(options.SessionsDir, ".cache"));

        // 装配的产出在这里归位。这样「装配」与「装配完之后的宿主」是两件事，
        // 前者可以整段替换（换模块），后者只需要拿到结果。
        Index = state.Index;
        Memory = state.Memory;
        Models = state.ModelStore;
        ModelSettings = state.ModelSettings;
        _switchable = state.Switchable;
        SkippedPlugins = state.SkippedPlugins;
        FailedPlugins = state.FailedPlugins;
    }

    // ── 对外通知（UI 用）──────────────────────────────────

    /// <summary>一条事件落库时的同步通知。UI 靠它把工具调用等过程显示出来。</summary>
    public event Action<SessionEvent>? EventEmitted;

    /// <summary>模型流式文本增量。第二个参数 = 增量所属的会话 id（B2，P1 后与“当前会话”可以不同）。</summary>
    public event Action<string, string>? TextDelta;

    /// <summary>
    /// 思考流式增量 —— 界面靠它做「边想边出」。
    /// 与 <see cref="TextDelta"/> 分开，是因为两者在界面上该放不同位置、也该有不同的默认可见性。
    /// </summary>
    public event Action<string, string>? ReasoningDelta;

    internal void RaiseEventEmitted(SessionEvent sessionEvent) => EventEmitted?.Invoke(sessionEvent);

    internal void RaiseTextDelta(string sessionId, string text) => TextDelta?.Invoke(sessionId, text);

    internal void RaiseReasoningDelta(string sessionId, string text) => ReasoningDelta?.Invoke(sessionId, text);

    /// <summary>
    /// 审批界面。由 UI 在启动后注入（Web UI / 将来的 WebView2 都会设它）。
    /// 为 null 时，<see cref="ApprovalDecision.Ask"/> 会降级为拒绝。
    /// </summary>
    public IApprovalPrompt? ApprovalPrompt { get; set; }

    /// <summary>
    /// 用户交互 seam。UI 注入它，模型就能反问用户（<c>ask_user</c>）。
    ///
    /// 没注入时会拿 <see cref="ApprovalPrompt"/> 兜底 —— 老界面只懂「批准 / 拒绝」，
    /// 于是「问用户」那类请求会如实回「问不出去」，而不是编一个答案。
    /// </summary>
    public IUserInteraction? UserInteraction { get; set; }

    /// <summary>当前生效的交互缝：显式注入的优先，否则由审批界面适配，再否则是「没人可问」。</summary>
    internal IUserInteraction EffectiveInteraction =>
        UserInteraction
        ?? (ApprovalPrompt is null
            ? NullUserInteraction.Instance
            // P6：把「本会话允许此工具」的勾选落到**当前会话**的放行集上。
            // 回合跑着的时候切不了会话（SwitchSessionAsync 会先等它空闲），
            // 所以这里的「当前会话」必然就是这次审批所属的那个会话。
            : new ApprovalPromptInteraction(ApprovalPrompt, toolName => _session.AllowTool(toolName)));

    /// <summary>把工具记进当前会话放行集（审批卡「本会话允许此工具」用）。</summary>
    public void RememberTool(string toolName) => _session.AllowTool(toolName);

    // ── 状态 ───────────────────────────────────────────────

    public HostOptions Options { get; }

    /// <summary>
    /// **当前**会话的 id。
    /// 注意：切过会话之后它与 <c>Options.SessionId</c>（装配时定的那个）不再相同 ——
    /// 这是 P1 之后的重要区分，读错一个就会把事件写到别人的日志里。
    /// </summary>
    public string SessionId => _session.SessionId;

    public string SessionLogPath => _log.Path;

    public PluginHost Plugins => _plugins;

    /// <summary>true 表示当前用的是离线演示模型（没配任何端点）。</summary>
    public bool UsingOfflineDemo { get; private set; }

    /// <summary>true 表示抓取正文会先经本地小模型压缩（配了本地端点时）。</summary>
    public bool SummarizationEnabled { get; private set; }

    /// <summary>true 表示有可用的转述端点（至少配了一个模型端点）。</summary>
    public bool RephraserAvailable => _rephraser is not null;

    /// <summary>转述设置 —— 活对象：界面改它即时生效，不必重启。</summary>
    public RephraseOptions RephraseSettings => Options.Rephrase;

    /// <summary>true 表示有可用的上下文摘要器（L5，需配本地端点）。</summary>
    public bool ContextSummarizerAvailable => _contextSummarizer is not null;

    /// <summary>
    /// 本宿主是否已「退休」—— 宿主即将被拆除（进程关闭 / 宿主级重装配），不再接受新回合。
    ///
    /// <para>
    /// 存在的理由是一个**亚秒级窗口**（P0 竞态）：拆除者拿到空闲的回合闸之后、
    /// 释放它之前，一个早先被 <c>Task.Run</c> 拉起来、手里还捏着**本宿主引用**的
    /// 请求会拿到刚空出来的闸，开始往一个马上就要被关闭的日志里落事件 ——
    /// 半截回合 + 异常。有了退休标志，那批等待者醒来第一件事就是快速失败，一个事件都不写。
    /// </para>
    /// <para>
    /// P1 之后**会话切换不再拆除宿主**（换指针而已），本标志的触发方从「切换」
    /// 收窄为「宿主级拆除」—— 但原语保留：任何“即将拆掉别人还握着引用的东西”
    /// 的路径（插件卸载、将来的 runtime LRU 回收）都该先标记它。
    /// </para>
    /// </summary>
    public bool IsRetired => _retired;

    /// <summary>
    /// 标记退休：此后 <see cref="SendAsync"/> 一律拒绝。
    /// 必须在回合闸被释放**之前**调用，否则等待中的回合会先跑起来。
    /// 幂等 —— 重复标记是安全的。
    /// </summary>
    public void MarkRetired() => _retired = true;

    /// <summary>上下文治理设置 —— 活对象（**原始**配置，未经档位调整）。</summary>
    public ContextOptions ContextSettings => Options.Context;

    /// <summary>
    /// 按**当前生效档位**算出的上下文配置 —— 界面水位条与真实回合必须读同一个它。
    ///
    /// v3.5 审查 P2：回合走 <c>EffectiveContextOptions(档位)</c>；UI 若读原始
    /// <see cref="ContextSettings"/>，在「档位关掉治理」等情形下两边口径不一致，
    /// 水位条会显示一个并不存在的预算。
    /// </summary>
    public ContextOptions EffectiveContextSettings() => EffectiveContextOptions(_state.CurrentProfile);

    /// <summary>
    /// 派生索引（SQLite）。<b>永远可以丢</b> —— 它是从会话日志重建出来的。
    /// 历史检索工具与「索引落后就重建」都靠它。
    /// </summary>
    public ISessionIndex Index { get; internal set; } = NullSessionIndex.Instance;

    /// <summary>
    /// 分级记忆存储。与索引相反 —— <b>记忆不能丢</b>，它是真相源。
    /// </summary>
    public IMemoryStore Memory { get; internal set; } = NullMemoryStore.Instance;

    /// <summary>
    /// 模型配置的读写口（界面增删改端点、切换当前模型都走它）。
    /// 为 null 表示这次装配没有启用运行时模型配置。
    /// </summary>
    public ModelSettingsStore? Models { get; internal set; }

    /// <summary>当前模型配置（端点列表 + 当前选中的那一个）。</summary>
    public ModelSettings? ModelSettings { get; internal set; }

    /// <summary>当前实际在用的模型名；没配置时为 null。</summary>
    public string? ActiveModelName => ModelSettings?.Current?.Model.Id;

    /// <summary>可热替换的模型客户端 —— 换模型只换它的内层，主循环与会话都不动。</summary>
    private SwitchableLlmClient? _switchable;

    /// <summary>
    /// 上一轮实际装配出的上下文（冻结段 / 动态段分离）。
    ///
    /// 诊断用，也是**验收的抓手**：能直接回答「这次请求里，哪些内容进的是会被缓存的前缀」。
    /// 哪天若有人再把易变内容塞回前缀，看这一项就能立刻发现。
    /// </summary>
    public AssembledContext? LastAssembly { get; private set; }

    /// <summary>
    /// 会话累计用量（由事件流投影而来，重启后自动就有 —— 不需要额外状态）。
    /// 界面状态栏读它。
    /// </summary>
    public SessionUsage Usage => _session.Usage;

    /// <summary>
    /// 当前注册着的全部工具（官方 + 插件 + 运行期挂上来的）。
    /// <b>每次读都重新取</b> —— 工具是可以运行期增删的，缓存的快照会骗人。
    /// </summary>
    public IReadOnlyList<string> ToolNames => [.. _plugins.ToolNames];

    /// <summary>工具名 → 是谁挂上来的（诊断）。看「这个工具哪来的」不必再翻代码。</summary>
    public IReadOnlyDictionary<string, string> ToolSources => _plugins.ToolSources;

    /// <summary>
    /// 当前模式下**真正暴露给模型**的工具名。
    /// 与 <see cref="ToolNames"/> 的区别就是「装了什么」与「这一轮给模型看什么」的区别 ——
    /// 工具一直注册着，但闲聊模式下一个 schema 都不发。
    ///
    /// <para>
    /// <b>只此一份</b>：直接问 <c>VisibleTools</c>（模式收窄 ∩ 工具包开关 ∩ 技能白名单都在那里）。
    /// 从前这里自己算了一套只看 <c>AllowedTools</c> 的逻辑 —— 于是加了工具包闸门之后，
    /// 主循环收窄了、这个诊断面还在说「全都暴露」，两处说法对不上。
    /// </para>
    /// </summary>
    public IReadOnlyList<string> ExposedToolNames
        => [.. _state.VisibleTools().Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal)];

    /// <summary>
    /// 当前装着的插件。<b>实时读内核，不是启动时的快照</b> ——
    /// 于是运行期装上 / 卸掉的插件，界面与诊断面立刻看得见（热更新的可见性靠它）。
    /// </summary>
    public IReadOnlyList<PluginHandle> LoadedPlugins => _plugins.Plugins;

    /// <summary>被 profile 挡下、未加载的插件 id。</summary>
    public IReadOnlyList<string> SkippedPlugins { get; private set; } = [];

    /// <summary>加载失败的插件（id + 原因）。坏插件不该让整个应用起不来。</summary>
    public IReadOnlyList<string> FailedPlugins { get; private set; } = [];

    public IReadOnlyList<ApprovalRecord> Approvals => _sink.Approvals;

    // ── 装配 ───────────────────────────────────────────────

    public static Task<AgentHost> CreateAsync(HostOptions options, CancellationToken ct = default)
        => Hosting.HostBuilder.BuildAsync(options, null, ct);

    /// <summary>
    /// 由装配器调用：把装配结果接成一个可用的宿主，并回填回调中继。
    /// （runner / sink 必须先于宿主要创建，所以它们的回调只能事后回填。）
    /// </summary>
    internal static AgentHost CreateCore(Hosting.HostState state)
    {
        var host = new AgentHost(state.Options, state.Kernel, state);

        state.EventRelay = host.RaiseEventEmitted;
        state.TextRelay = host.RaiseTextDelta;
        state.ReasoningRelay = host.RaiseReasoningDelta;
        state.SkillsReloader = host.ReloadSkills;
        state.LastSeqOf = sessionId =>
        {
            lock (host._sessions)
            {
                return host._sessions.TryGetValue(sessionId, out var rt) && rt.Events.Count > 0
                    ? rt.Events[^1].Seq
                    : 0;
            }
        };
        state.EmitToSession = (sessionId, evt, ct) => host.EmitToSessionAsync(sessionId, evt, ct);
        // G1：委托以宿主为入口（编排逻辑在本文件下方 / SubAgentRunner.cs）。
        // ContinueWith 里检查 t.Status：子 Agent 抛异常时返回失败摘要，而不是让异常穿透成 Unfaulted task。
        state.SubAgentRunner = (parentSessionId, task, ct) =>
            SubAgentRunner.RunSubAgentAsync(host, parentSessionId, task, null, ct)
                .ContinueWith(t => t.Status == TaskStatus.RanToCompletion
                    ? (t.Result.ChildSessionId, t.Result.Success, t.Result.Summary)
                    : (string.Empty, false, $"子 Agent 执行失败：{t.Exception?.GetBaseException().Message ?? t.Status.ToString()}"), ct);
        state.InteractionProvider = () => host.EffectiveInteraction;
        state.ModeProvider = () => host.Mode;
        state.ModeIdProvider = () => host.ModeId;
        // 工具包：agent 手上的 toolsets / use_toolset 两个工具走这两条委托。
        // 读的是同一份视图 —— 界面、诊断面、agent 三处永远一致。
        state.ToolsetViewProvider = () => host.Toolsets;
        state.ToolsetToggle = host.SetToolsetEnabled;

        // F5 补全（v3.5 审查 P1-1）：多会话下 search_history / spawn_subagent / update_plan
        // 的闭包必须读「本回合所属会话」——
        //   · 回合中：AsyncLocal 带上 sessionId，并发会话各自互不串味；
        //   · 非回合上下文（UI 直调 / 诊断）：回落到宿主当前会话，行为与旧版一致。
        // 少了这一行，这些工具会永远作用在装配期的 options.SessionId 上。
        state.CurrentSessionIdProvider = () => state.TurnSessionIdValue ?? host.Session.SessionId;

        return host;
    }

    /// <summary>是否有回合正在跑（诊断 / UI 用，指**当前会话**）。</summary>
    public bool IsBusy => _session.TurnGate.CurrentCount == 0;

    /// <summary>当前审批档位（任务 5）。</summary>
    public ApprovalTier ApprovalTier => Options.ApprovalTier;

    // ── 多会话（P1）──────────────────────────────────────────────

    /// <summary>
    /// 当前会话的运行态。
    /// UI 的 <c>/api/send</c> 靠它在 POST 那一刻把**回合归属**钉下来。
    /// </summary>
    public Hosting.SessionRuntime Session => _session;

    /// <summary>本宿主已经开着的会话 id（诊断 / UI 用）。</summary>
    public IReadOnlyList<string> OpenSessionIds
    {
        get
        {
            lock (_sessions)
            {
                return [.. _sessions.Keys];
            }
        }
    }

    /// <summary>
    // ── G2 技能系统 ───────────────────────────────────────────

    /// <summary>扫描到的技能清单。</summary>
    public IReadOnlyList<SkillDefinition> Skills => _state.Skills;

    /// <summary>工作区根目录（创意工坊：导入目标 = {workspaceRoot}/workshop）。</summary>
    public string WorkspaceRoot => Options.WorkspaceRoot;

    /// <summary>已启用技能名。</summary>
    public IReadOnlyCollection<string> EnabledSkills => _state.EnabledSkills;

    // ── 工具包（可见性的最小单位）──────────────────────────────
    // 「装了什么」与「这一轮给模型看什么」解耦的落点：
    // 插件照常装着，用不上时把它的包收起来 —— 省的是每轮的 schema token，
    // 也是模型在几十个工具里挑错的机会。

    /// <summary>当前关掉的工具包。</summary>
    public IReadOnlyCollection<string> DisabledToolsets => _state.DisabledToolsets;

    /// <summary>
    /// 工具包全景（按 id 排序）：界面上那一排开关、诊断面、agent 的工具读的都是它。
    /// </summary>
    public IReadOnlyList<ToolsetView> Toolsets
    {
        get
        {
            var descriptors = _plugins.Toolsets;

            return [.. descriptors.Values
                .OrderBy(d => d.Id, StringComparer.Ordinal)
                .Select(d => new ToolsetView(
                    d.Id,
                    string.IsNullOrWhiteSpace(d.Name) ? d.Id : d.Name,
                    d.Description,
                    _plugins.ToolsInToolset(d.Id),
                    IsToolsetExposed(d.Id),
                    d.Protected || Contracts.BuiltinToolsets.Protected.Contains(d.Id),
                    d.Eager,
                    d.Source))];
        }
    }

    /// 查/开一个会话：字典里已有就直接给，否则让装配器开一个新的（P1）。
    ///
    /// 走的是 <c>HostState.OpenSession</c> —— 也就是**子 agent 走的那条路**。
    /// 于是「UI 会话」与「子 agent 会话」从此是同一个东西：
    /// 共享模型客户端、工具注册表、索引、记忆；独立事件流、上下文、回合闸。
    /// </summary>
    private Hosting.SessionRuntime GetOrOpenSession(string sessionId)
    {
        lock (_sessions)
        {
            if (_sessions.TryGetValue(sessionId, out var existing))
            {
                return existing;
            }
        }

        // v3.6 审查修复：原先在 _sessions 锁外新建 runtime —— 两个标签页同时切到**同一未打开会话**时，
        // 两个线程都过上面那道检查，各建一个（两份内存事件表 + 两个日志句柄），登记时后写的覆盖先写的，
        // 被覆盖的那个句柄永不 Dispose。用 _openGate 串行化「建」，闸内二次确认，保证只建一次。
        lock (_openGate)
        {
            lock (_sessions)
            {
                if (_sessions.TryGetValue(sessionId, out var raced))
                {
                    return raced;
                }
            }

            // relayToUi: true —— 这是要显示在对话窗口里的会话；
            // onEvent 接到宿主的事件中继，SSE 才收得到它的增量与工具卡片。
            // 模式与项目目录从 session-meta.json 换原（会话的属性跟着会话走）。
            SessionMetas().TryGetValue(sessionId, out var meta);
            var created = _state.OpenSession(sessionId, onEvent: RaiseEventEmitted, relayToUi: true,
                modeId: meta?.Mode, projectDir: meta?.ProjectDir);

            lock (_sessions)
            {
                _sessions[sessionId] = created;
            }

            return created;
        }
    }

    public async ValueTask DisposeAsync()
    {
        // 实时取内核里的插件名单：运行期（agent 自己）装上的插件也要一起收掉，
        // 不能只收启动时那一批 —— 否则热装进来的插件在关闭时会被漏掉。
        foreach (var plugin in _plugins.Plugins)
        {
            await plugin.DisposeAsync().ConfigureAwait(false);
        }

        // 宿主自己的模块：注册即副作用，关闭时按「后注册先撤销」统一回收。
        // 从前这里是手写的一串 Dispose —— 每加一个模块都要记得补一行，迟早会漏。
        _plugins.DisposeKernelScopes();

        // 每个会话的运行态（日志 + 回合闸）各自由自己收尾（P1：可能不止一个）
        Hosting.SessionRuntime[] all;
        lock (_sessions)
        {
            all = [.. _sessions.Values];
            _sessions.Clear();
        }

        foreach (var session in all)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        _sessionSwapGate.Dispose();
        await Index.DisposeAsync().ConfigureAwait(false);
    }
}
