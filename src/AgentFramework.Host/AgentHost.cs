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
public sealed class AgentHost : IAsyncDisposable
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
    private readonly IUserInputRephraser? _rephraser;
    private readonly IContextSummarizer? _contextSummarizer;

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

    /// <summary>悬空工具调用是否已修过一遍（幂等，一次会话只修一次就够）。</summary>
    private bool _danglingRepaired;

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
    public bool UsingOfflineDemo { get; }

    /// <summary>true 表示抓取正文会先经本地小模型压缩（配了本地端点时）。</summary>
    public bool SummarizationEnabled { get; }

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

    // ── 工作模式 ───────────────────────────────────────────

    /// <summary>当前模式。</summary>
    public AgentMode Mode => _mode;

    /// <summary>当前会话钉住的模式 id（创建时选定；null = 跟随宿主默认）。</summary>
    public string? ModeId => _session.ModeId;

    /// <summary>按 id 取会话运行态（子 Agent 继承父会话的模式/项目目录用）；不存在返回 null。</summary>
    public Hosting.SessionRuntime? GetSession(string sessionId)
    {
        lock (_sessions)
        {
            return _sessions.TryGetValue(sessionId, out var rt) ? rt : null;
        }
    }

    /// <summary>当前会话实际生效的档位（解析自会话模式；未钉则宿主默认）。</summary>
    public ModeProfile ModeProfile => AgentModes.Resolve(ModeId ?? AgentModes.IdOf(_mode));

    /// <summary>
    /// 设置**新会话的默认模式**（HCI：模式在创建会话时选定后即钉住，
    /// 此方法只影响未钉模式的新会话 —— 老装配路径与测试的兼容入口）。
    /// </summary>
    public void SetMode(AgentMode mode) => _mode = mode;

    // ── 模型管理 ───────────────────────────────────────────

    /// <summary>
    /// 应用一份新的模型配置：落盘 + **立即生效**，不需要重启。
    ///
    /// 正在跑的那一轮不受影响（它抓的是旧的 client 引用），下一轮才是新的 ——
    /// 「先换引用、再让后续请求看见」这个顺序的价值就在于：不会把一轮对话劈成两半。
    /// </summary>
    public void ApplyModelSettings(ModelSettings settings, bool persist = true)
    {
        if (Models is null)
        {
            ModelSettings = settings;
            return;
        }

        if (persist)
        {
            Models.Save(settings);
        }

        ModelSettings = settings;

        if (_switchable is not null && Hosting.ModelModule.TryBuildActive(settings, Models.Protector, out var client))
        {
            _switchable.Switch(client);
        }
    }

    /// <summary>在某个端点内换个模型（其余配置不动）。</summary>
    public void SwitchModel(string providerId, string modelId)
    {
        if (ModelSettings is null)
        {
            return;
        }

        ModelSettings.Active = new ActiveModelRef { ProviderId = providerId, ModelId = modelId };
        ApplyModelSettings(ModelSettings);
    }

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

    /// <summary>
    /// 命令沙箱的当前档位（名 / 一句话释义 / 回落说明）。
    ///
    /// <para>
    /// 为什么要在诊断面上露出来：<b>沙箱的强度必须可见</b>。
    /// 配置里写了 <c>job</c> 但机器上回落到了 <c>process</c>，用户却以为自己受内核配额保护 ——
    /// 那比干脆没有沙箱更危险。
    /// </para>
    /// </summary>
    public (string Name, string Description, string? Note) SandboxInfo
    {
        get
        {
            var registry = _state.Sandbox;
            if (registry is null)
            {
                return ("unknown", "沙箱注册表尚未装配", null);
            }

            var backend = registry.Resolve(_state.Options.Sandbox);
            return (backend.Name, backend.Describe(), registry.ResolveNote);
        }
    }

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


    // ── 会话 ───────────────────────────────────────────────

    /// <summary>
    /// 在同一套装配下开一个<b>独立会话</b>（子 agent 走这条路）。
    ///
    /// 共享：模型客户端、工具注册表、索引、记忆、交互缝。
    /// 独立：事件日志、模型上下文、回合闸。
    ///
    /// 它默认**不挂 UI 回调** —— 子会话的流式输出不该串进主人的对话窗口；
    /// 要观察它，就显式传 <paramref name="onEvent"/>（事件才是它的正经出口）。
    /// </summary>
    /// <summary>
    /// 开一个独立会话（P1）。
    ///
    /// <paramref name="relayToUi"/> 决定它的输出要不要推到界面：
    /// 子 agent 用 false（别串台），UI 自己的会话用 true（本来就是要打字出来的那个）。
    /// 开出来的会话同时登记进宿主的会话表，之后就能被「切过去」。
    /// </summary>
    public Hosting.SessionRuntime OpenSession(
        string sessionId,
        string? systemPrompt = null,
        int? maxSteps = null,
        Action<SessionEvent>? onEvent = null,
        bool relayToUi = false,
        string? modeId = null,
        string? projectDir = null)
    {
        var created = _state.OpenSession(sessionId, systemPrompt, maxSteps, onEvent, relayToUi, modeId, projectDir);

        lock (_sessions)
        {
            _sessions[sessionId] = created;
        }

        // 模式/项目目录都是会话的属性：重启后换原（session-meta.json）
        if (modeId is not null || projectDir is not null)
        {
            SaveSessionMeta(sessionId, modeId, projectDir);
        }

        return created;
    }

    /// <summary>
    /// 回合中断自愈（P0-2）：为「有意图、无结果」的悬空工具调用补写一条合成结果事件。
    ///
    /// 遵守「补状态靠再追加一条事件」的纪律 —— 不改历史，只补一条失败结果。
    /// 补完之后模型重新看到的语义是完整的：它确实调过这个工具，结果是「回合中断」。
    /// </summary>
    private async ValueTask RepairDanglingToolCallsAsync(Hosting.SessionRuntime session, CancellationToken ct)
    {
        var requested = new Dictionary<string, ToolCallRequestedEvent>(StringComparer.Ordinal);
        var completed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var e in session.Events)
        {
            switch (e)
            {
                case ToolCallRequestedEvent r:
                    requested[r.CallId] = r;
                    break;

                case ToolCallCompletedEvent c:
                    completed.Add(c.CallId);
                    break;
            }
        }

        foreach (var (callId, req) in requested)
        {
            if (completed.Contains(callId))
            {
                continue;
            }

            await session.Sink.EmitAsync(new ToolCallCompletedEvent
            {
                // ★ 归给**传进来的这个会话**（P1）：不是「当前会话」——
                //   自愈修的就是这份事件流，写错就会把事件落进别人的日志。
                SessionId = session.SessionId,
                CallId = callId,
                Success = false,
                Output = string.Empty,
                Error = $"回合中断（进程重启或崩溃），工具 {req.ToolName} 的调用从未执行完成",
            }, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 关闭并移除一个会话（B3 修复）：
    /// 等〈最多 5 秒〉回合结束 → 从会话表移除 → 释放它的运行态（日志 + 闸）→ 清派生物。
    /// 不这样做的话，「删掉再新建同名会话」会拿到**旧 runtime**——
    /// 内存事件表还是删掉前的内容，界面直接“复活”已删除的历史，而磁盘日志是新的空文件。
    /// 返回 false = 会话不存在，或有回合正在跑（用户应先等它结束）。
    /// </summary>
    public async ValueTask<bool> CloseSessionAsync(string sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        Hosting.SessionRuntime? victim;
        lock (_sessions)
        {
            if (!_sessions.TryGetValue(sessionId, out victim))
            {
                return false;
            }
        }

        // 闸的等待与释放在这里**配对**完成 —— 与 SwitchSessionAsync 同一纪律。
        // ★ 但不能在 release 之后再 Dispose 闸 —— v3.4 原版把 DisposeAsync 放在 try 内、
        //   Release 放在 finally，结果正常路径先 Dispose 再 Release →
        //   "Cannot access a disposed object"（删除端点 500 的直接原因）。
        // 正确顺序：先还闸，再释放 runtime；acquired 标志保证 finally 只在真的持有闸时才 Release。
        var acquired = false;
        try
        {
            try
            {
                // 必须用 WaitAsync 的返回值判断是否拿到闸；
                // 用 CurrentCount==0 反推是错的 —— 超时未拿到时 CurrentCount 同样是 0（回合还持着）。
                acquired = await victim.TurnGate.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                acquired = false;
            }

            if (!acquired)
            {
                return false;   // 回合在跑（或取消），不删
            }

            // v3.5 审查 P2：先**从会话表摘除**、再还闸。
            // 原顺序（先还闸、后摘除）留了一个窗口：别的线程可能在「闸已还、条目还在」之间
            // 拿到这个 runtime 并开始新回合，随后它被 DisposeAsync 掉 —— 用一个已经释放的闸。
            // 摘除在前，则新调用者不可能再拿到它。
            lock (_sessions)
            {
                // 二次确认：等待闸期间可能已被别的路径关闭
                if (!_sessions.Remove(sessionId))
                {
                    return true;    // finally 负责把闸还回去
                }
            }

            victim.TurnGate.Release();
            acquired = false;   // 已还，finally 不再重复还

            await victim.DisposeAsync().ConfigureAwait(false);
            await InvalidateDerivedDataAsync(sessionId, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            if (acquired)
            {
                victim.TurnGate.Release();
            }
        }
    }

    /// <summary>
    /// 会话被删掉时清它留下的派生物（投影缓存 + 索引）。
    ///
    /// 不清的话，「删掉再新建同名会话」会吃到旧快照 —— 界面显示的是并不存在的历史。
    /// 派生物清理失败绝不影响主流程（它们本来就是可丢的）。
    /// </summary>
    public async ValueTask InvalidateDerivedDataAsync(string sessionId, CancellationToken ct = default)
    {
        _projectionCache.Invalidate(Path.Combine(Options.SessionsDir, sessionId + ".jsonl"));

        if (!Index.IsAvailable)
        {
            return;
        }

        try
        {
            await Index.RemoveSessionAsync(sessionId, ct).ConfigureAwait(false);
        }
        catch
        {
            // 索引是派生物，清不掉也不是错
        }
    }

    /// <summary>是否有回合正在跑（诊断 / UI 用，指**当前会话**）。</summary>
    public bool IsBusy => _session.TurnGate.CurrentCount == 0;

    // ── 以下 WaitIdleAsync / ReleaseTurn（B5）───────────────────────
    // P1 之后「会话切换不再拆除宿主」，它们不再被切换路径调用；
    // 保留给仍然会“拆掉别人还握着引用的东西”的场景：插件卸载、宿主关闭前的等待、
    // 将来的 runtime LRU 回收。注意它们操作的是**当前会话**的闸 ——
    // 针对特定会话的等待请直接拿那个 runtime 的 TurnGate（参照 CloseSessionAsync）。

    /// <summary>
    /// 等当前会话的回合结束（最多 <paramref name="maxWait"/>）。
    /// 返回 false = 回合还在跑，调用方**不得**拆除本宿主 ——
    /// 否则日志已 Dispose、插件已卸载，正在跑的回合会在写事件时炸掉。
    /// </summary>
    public async ValueTask<bool> WaitIdleAsync(TimeSpan maxWait, CancellationToken ct = default)
    {
        try
        {
            return await _session.TurnGate.WaitAsync(maxWait, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>与 <see cref="WaitIdleAsync"/> 配对：拿到的闸必须还，否则会话永远锁死。</summary>
    public void ReleaseTurn() => _session.TurnGate.Release();

    /// <summary>
    /// 发一条消息（在**当前会话**上）。
    /// 「会话续接」在这里落地：每次都从事件流重建上下文再交给主干 ——
    /// 因为日志才是唯一真相源，连进程重启都不需要额外状态。
    ///
    /// 用信号量串行化：同一会话不允许两个回合同时跑，
    /// 否则事件流会交错、上下文会撕裂。
    /// </summary>
    public Task<AgentRunResult> SendAsync(string input, CancellationToken ct = default)
        => SendAsync(_session, input, ct);

    /// <summary>
    /// 在**指定会话**上跑一轮（P1）。
    ///
    /// <para>
    /// 为什么要有这个重载：**回合的归属应该在调用那一刻就定下来**。
    /// UI 的 <c>/api/send</c> 是「先回 202、回合在后台跑」，中间主人完全可能切走；
    /// 若回合跑在「当时的当前会话」上，它就会落进别人的日志里。
    /// </para>
    /// <para>
    /// 每个会话自己有回合闸，所以不同会话的回合可以并行 ——
    /// 「不许两个回合同时跑」这条纪律本来就只针对同一会话（同一个事件流不能被交错写入）。
    /// </para>
    /// </summary>
    public async Task<AgentRunResult> SendAsync(Hosting.SessionRuntime session, string input, CancellationToken ct = default)
    {
        await session.TurnGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 退休检查（P0 竞态防线）—— **必须在拿到闸之后**做：
            // 放在闸之前会漏掉「排队等待期间才被标记退休」的那一批，
            // 偏偏那正是竞态窗口的形态。抛在这里，下面的 finally 会把闸还回去。
            if (_retired)
            {
                throw new InvalidOperationException("该宿主已关闭，消息未送达（请重新发送）");
            }

            // 中断自愈：补完就不会再悬空，所以每个会话只做一次（幂等）。
            if (!session.DanglingRepaired)
            {
                session.DanglingRepaired = true;
                await RepairDanglingToolCallsAsync(session, ct).ConfigureAwait(false);
            }

            // HCI：模式与项目目录钉在会话上 —— 本回合的档位、工具面、记忆层级、
            // 文件沙箱都取会话自己的；未钉的旧路径沿用宿主默认。
            // BeginTurn 让 AsyncLocal 随执行流流向工具闭包（含文件沙箱的路径解析）。
            var modeId = session.ModeId ?? AgentModes.IdOf(_mode);
            var profile = AgentModes.Resolve(modeId);
            var projectDir = session.ProjectDir ?? Options.WorkspaceRoot;
            _state.BeginTurn(modeId, projectDir, session.SessionId);
            var contextOptions = EffectiveContextOptions(profile);

            // 带治理的投影 —— **不是全量回灌**，那正是 dsh 长任务变差的根因。
            // events 现在直接来自会话的**内存事件表**（P2），不再每轮重读 JSONL。
            var events = session.Events;
            var projection = SessionContextBuilder.Project(events, contextOptions);

            // 水位超了才压：压缩必然打断前缀缓存，所以宁晚不频。
            // 闲聊模式直接不做这件事 —— 闲聊没有长任务，省掉每轮的投影比较与压缩决策。
            if (profile.ContextGovernance && projection.NeedsCompression)
            {
                projection = await CompactAsync(session, events, projection, contextOptions, ct).ConfigureAwait(false);
            }

            // 上下文装配（改造后）：冻结段在前、动态段在后。
            //   · 冻结段 —— 模式说明 + 常驻记忆索引卡：内容稳定，进缓存前缀
            //   · 动态段 —— 投影出的对话 + 按**当轮输入**检索到的相关记忆：每轮可变，只能放尾部
            //
            // 原先这里是 messages.Insert(0, 记忆块) —— 把每轮可能变的内容放进了前缀，
            // 正是 Anthropic 官方 prompt-caching 文档点名的 "common mistake"。
            // 现在由 ContextAssembler 在**结构上**保证「动态内容不影响静态内容的位置」。
            var dynamicMessages = projection.Messages.ToList();

            // 工作小本本摘要（DESIGN.md 4.17）—— 与任务卡并列的那份「复述」：
            // 计划必须放在末尾，放在中部会被淹没。本子不存在就什么都不加（默认零成本）。
            if (profile.InjectNotes)
            {
                var notesPath = Path.Combine(Options.WorkspaceRoot, WorkNotes.DefaultFileName);
                var notesSummary = WorkNotes.BuildSummary(
                    WorkNotes.TryRead(notesPath),
                    profile.NotesSummaryMaxChars);

                if (notesSummary is not null)
                {
                    dynamicMessages.Add(new LlmMessage { Role = LlmRole.System, Content = notesSummary });
                }
            }

            var recall = await BuildRecallBlockAsync(profile, MapScopes(profile.MemoryScopes, projectDir), input, ct).ConfigureAwait(false);
            if (recall is not null)
            {
                dynamicMessages.Add(new LlmMessage { Role = LlmRole.System, Content = recall });
            }

            var frozen = await BuildFrozenBlockAsync(profile, MapScopes(profile.MemoryScopes, projectDir), ct).ConfigureAwait(false);
            var assembled = ContextAssembler.Assemble(frozen, dynamicMessages);
            LastAssembly = assembled;

            // 「发送前自动澄清」（可选，默认关）：
            // 先让便宜的模型把话说清楚，再交给主模型。
            // 转述失败/跳过时拿到的就是**原文** —— 所以这一步在结构上不可能挡住发消息。
            string? rephrasedText = null;
            string? rephraseModel = null;

            if (Options.Rephrase.AutoBeforeSend && _rephraser is not null)
            {
                var result = await _rephraser
                    .RephraseAsync(input, Options.Rephrase, ct)
                    .ConfigureAwait(false);

                if (result.Rephrased)
                {
                    rephrasedText = result.Text;
                    rephraseModel = result.Model;
                    await EmitRephraseRecordAsync(session, input, result, "auto", applied: true, ct).ConfigureAwait(false);
                }
            }

            return await session.Runner.RunAsync(input, assembled.Messages, ct, rephrasedText, rephraseModel)
                .ConfigureAwait(false);
        }
        finally
        {
            _state.BeginTurn(null);
            session.TurnGate.Release();
        }
    }

    /// <summary>
    /// 执行一次压缩：算方案 →（可选）生成摘要 → 落一条压缩事件 → 返回收紧后的投影。
    ///
    /// 注意这里**什么历史都没删** —— 只是换了个更紧的投影函数，
    /// 并把「这次遮了哪些序号」记进日志。于是可回放、可审计、可分叉。
    /// 被钉死的序号之后永远保持遮蔽，历史视图不会来回跳。
    /// </summary>
    private async Task<ContextProjection> CompactAsync(
        Hosting.SessionRuntime session,
        IReadOnlyList<SessionEvent> events,
        ContextProjection current,
        ContextOptions contextOptions,
        CancellationToken ct)
    {
        var plan = ContextCompactor.Plan(events, contextOptions);
        if (plan is null)
        {
            // 收紧也省不下东西 → 保持现状，不写空事件污染日志
            return current;
        }

        string? summary = null;

        // L5（默认关）：只在显式开启且确实有摘要器时才做；失败一律放弃摘要，不影响本轮
        if (contextOptions.SummarizeOlderHistory
            && _contextSummarizer is not null
            && plan.OlderMessages.Count > 0)
        {
            try
            {
                var produced = await _contextSummarizer
                    .SummarizeHistoryAsync(plan.OlderMessages, plan.After.TaskCard, ct)
                    .ConfigureAwait(false);

                summary = string.IsNullOrWhiteSpace(produced) ? null : produced;
            }
            catch
            {
                summary = null;
            }
        }

        await session.Sink.EmitAsync(new ContextCompactedEvent
        {
            SessionId = session.SessionId,
            Trigger = CompactionTrigger.Auto,
            PreTokens = plan.Before.EstimatedTokens,
            PostTokens = plan.After.EstimatedTokens,
            MaskedSeqs = [.. plan.MaskedSeqs],
            MaskedCount = plan.After.MaskedResults,
            CollapsedTurns = plan.CollapsedTurns,
            // ★ 把「这次收到多紧」一起落盘（P3a）：下一轮以它为起点，
            //   不必从默认窗口重新减半 —— 收到底之后每轮重算的那笔白账就此消掉。
            TightenedTurns = plan.Tightened.RecentTurnsKeptVerbatim,
            Summary = summary,
            TaskCard = plan.After.TaskCard,
        }, ct).ConfigureAwait(false);

        // 摘要刚落进日志 → 重新投影一次，这一轮就用得上它
        return SessionContextBuilder.Project(session.Events, plan.Tightened);
    }

    /// <summary>
    /// 手动转述 —— 界面上「✨ 优化」按钮走这里。
    ///
    /// 结果**只回填输入框**：此刻尚未发送，仍属草稿，不进模型上下文。
    /// 成功时留一条 <see cref="UserInputRephrasedEvent"/>（Applied=false）作交互留痕，
    /// 这样事后能回答「那句话当时是怎么被改写的」。
    /// </summary>
    public async Task<RephraseResult> RephraseAsync(string text, CancellationToken ct = default)
    {
        var trimmed = (text ?? string.Empty).Trim();

        if (_rephraser is null)
        {
            return RephraseResult.Skip(trimmed, RephraseSkip.NoModel);
        }

        var result = await _rephraser
            .RephraseAsync(trimmed, Options.Rephrase, ct)
            .ConfigureAwait(false);

        if (result.Rephrased)
        {
            await EmitRephraseRecordAsync(_session, trimmed, result, "manual", applied: false, ct).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>保存转述设置（写「用户偏好」文件，不进 JSONL）。</summary>
    public void SaveRephraseSettings() => RephraseSettingsStore.Save(Options.SessionsDir, Options.Rephrase);

    /// <summary>
    /// 按模式算出这一轮实际生效的上下文配置。
    ///
    /// 闲聊模式关掉治理：闲聊没有长任务，任务卡与水位压缩都是白花的钱。
    /// （注意是把配置**克隆**一份再改 —— 绝不改写用户的活配置对象。）
    /// </summary>
    /// <summary>把模式档位里的抽象层级名映射为实际作用域：project → 本会话项目目录的作用域。</summary>
    private IReadOnlyList<string> MapScopes(IReadOnlyList<string> scopes, string projectDir)
        => [.. scopes.Select(s => s == MemoryScope.Project ? MemoryScope.ProjectFor(projectDir) : s)];

    private ContextOptions EffectiveContextOptions(ModeProfile profile)
    {
        if (profile.ContextGovernance && profile.InjectTaskCard)
        {
            return Options.Context;
        }

        var effective = Options.Context.Clone();
        effective.InjectTaskCard = profile.InjectTaskCard;

        if (!profile.ContextGovernance)
        {
            effective.MaskOldToolResults = false;
            effective.TokenBudget = int.MaxValue;   // 永不触发压缩
        }

        return effective;
    }

    /// <summary>
    /// 构建**冻结段**：模式说明 + 常驻「记忆索引卡」。
    ///
    /// 它进的是请求最前面的缓存前缀，所以纪律是「**内容必须稳定**」。
    /// 这里每轮都重建，但不缓存也不打紧 —— 只要记忆没变，重建出的字符串就**逐字节相同**
    /// （<c>LoadAsync</c> 取最近 N 条，顺序确定），前缀缓存照样命中。
    /// 真正会破坏前缀的是「每轮内容都不同」，而不是「每轮都算一遍」。
    ///
    /// 「不用的那一级根本不打开文件」依然成立：按模式只读该读的层级。
    /// 而「这一轮用得上的细节」不走这里，走 <see cref="BuildRecallBlockAsync"/>（动态段）。
    /// </summary>
    private async Task<string?> BuildFrozenBlockAsync(ModeProfile profile, IReadOnlyList<string> scopes, CancellationToken ct)
    {
        var blocks = new List<string>();

        if (!string.IsNullOrWhiteSpace(profile.SystemPromptSuffix))
        {
            blocks.Add(profile.SystemPromptSuffix!);
        }

        if (profile.MemoryIndexLimit > 0)
        {
            foreach (var scope in scopes)
            {
                // v3.5 审查 P1-4：温度策略必须「先全局排序、后截断」。
                // 原实现先按时间 TakeLast(N) 再排序 —— 等于把「老而常用」的条目
                // 永远挡在索引卡之外，而「救回老而常用」正是温度策略存在的唯一理由。
                // 所以温度档取全量活跃视图去排序；Insertion 档维持原语义（最近 N 条）。
                var takeAll = profile.PriorityStrategy == MemoryPriorityStrategy.Temperature;
                var entries = await Memory
                    .LoadAsync(scope, takeAll ? int.MaxValue : profile.MemoryIndexLimit, ct)
                    .ConfigureAwait(false);
                // 按档位策略排序（Insertion = 原行为；Temperature = 热度优先）。
                // 排序在宿主做而不是存储做：同一份视图，不同模式不同取法。
                var ordered = MemoryPrioritizer.Order(entries, profile.PriorityStrategy);
                if (takeAll && ordered.Count > profile.MemoryIndexLimit)
                {
                    ordered = [.. ordered.Take(profile.MemoryIndexLimit)];
                }

                var label = scope == MemoryScope.Global
                    ? "记忆·常驻索引（全局，跨项目）"
                    : "记忆·常驻索引（本项目）";

                var card = MemoryBlocks.BuildIndexCard(ordered, profile.MemoryIndexMaxChars, label);
                if (card is not null)
                {
                    blocks.Add(card);
                }
            }
        }

        return blocks.Count == 0 ? null : string.Join("\n\n", blocks);
    }

    /// <summary>
    /// 构建**动态段**里的一块：按当轮输入检索出来的相关记忆。
    ///
    /// 它每轮都可能不同，所以**只能放尾部** —— 这正是「检索代替搬运」在实现层的落点：
    /// 不再每轮把最近 N 条记忆全量灌进上下文，而是先看这一轮说了什么，再去库里捞。
    /// 闲聊模式 <c>MemoryRecallLimit = 0</c>：一次文件扫描都不做。
    /// </summary>
    private async Task<string?> BuildRecallBlockAsync(ModeProfile profile, IReadOnlyList<string> scopes, string query, CancellationToken ct)
    {
        if (profile.MemoryRecallLimit <= 0 || string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var hits = new List<MemoryEntry>();

        foreach (var scope in scopes)
        {
            var found = await Memory
                .SearchAsync(query, scope, profile.MemoryRecallLimit, ct)
                .ConfigureAwait(false);

            hits.AddRange(found);
        }

        if (hits.Count == 0)
        {
            return null;
        }

        // 检索块本来就按相关性序返回（SearchAsync 打分排序）；
        // 这里不再按 CreatedAt 重排 —— 那会把「最相关的」换成「最新的」。
        // 只做去重（同一条记忆可能同时出现在多个 scope 的结果里）。
        var ordered = hits
            .DistinctBy(e => e.Id, StringComparer.Ordinal)
            .Take(profile.MemoryRecallLimit)
            .ToList();

        return MemoryBlocks.BuildRecallBlock(ordered, profile.MemoryRecallMaxChars);
    }

    /// <summary>
    /// 转述留痕落进**指定的会话**（B1 修复）：
    /// 自动转述跟发起回合的会话走（<c>SendAsync(session, …)</c> 传进来的那个）；
    /// 手动转述跟点击按钮时的当前会话走（调用方传 <c>_session</c>）。
    /// 此前写 <c>_sink</c> —— 回合排队期间切过会话的话，留痕会落进别人的日志。
    /// </summary>
    private ValueTask EmitRephraseRecordAsync(
        Hosting.SessionRuntime session,
        string original,
        RephraseResult result,
        string source,
        bool applied,
        CancellationToken ct)
        => session.Sink.EmitAsync(new UserInputRephrasedEvent
        {
            SessionId = session.SessionId,
            Original = original,
            Rephrased = result.Text,
            Source = source,
            Model = result.Rephrased ? result.Model : null,
            ElapsedMs = result.ElapsedMs,
            Applied = applied,
        }, ct);

    /// <summary>从事件流重建的模型上下文（可用来确认"模型看到了什么"）。</summary>
    public IReadOnlyList<LlmMessage> RebuildContext() => SessionContextBuilder.BuildFromFile(_log.Path);

    // ── debug 信息窗的内部状态读取器（v3.2 自用诊断）──────────────
    // 与 ContextStatus（WebUiServer 私有）同源：都从内存事件表现投影。
    // 拆成 4 个小属性而不是一个对象，是为了 debug 窗和 /api/status 各取所需。

    /// <summary>当前估算 token 水位。</summary>
    internal int ContextStatusTokens()
    {
        var projection = SessionContextBuilder.Project(_session.Events, EffectiveContextOptions(ModeProfile));
        return projection.EstimatedTokens;
    }

    /// <summary>水位比例（0–1+）。</summary>
    internal double ContextStatusWaterLevel()
    {
        var projection = SessionContextBuilder.Project(_session.Events, EffectiveContextOptions(ModeProfile));
        return Math.Round(projection.WaterLevel, 3);
    }

    /// <summary>逐字保留的轮次窗口（压缩后变小）。</summary>
    internal int ContextStatusKeptTurns()
    {
        var projection = SessionContextBuilder.Project(_session.Events, EffectiveContextOptions(ModeProfile));
        return projection.KeptTurns;
    }

    /// <summary>历史上发生过的自动压缩次数。</summary>
    internal int ContextStatusCompactions()
        => _session.Events.OfType<ContextCompactedEvent>().Count();

    /// <summary>从事件流投影出的会话状态。</summary>
    public SessionState CurrentState() => _projectionCache.GetOrRebuild(_log.Path);

    /// <summary>原始事件流（UI 拉取历史用）。</summary>
    public IReadOnlyList<SessionEvent> Events() => _session.Events;

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
    /// 切换「当前会话」（P1）。
    ///
    /// <para>
    /// **只换指针**：装配一份、插件不重载、SQLite 不重开、记忆不重读。
    /// 目标会话**惰性打开**（首次被选中才 Open 日志句柄），
    /// 所以会话列表翻多少遍都不会多开一个文件句柄。
    /// </para>
    /// <para>
    /// 返回 false = 当前会话有回合正在跑，或目标会话打不开。
    /// 闸的等待与归还在方法内部**配对**完成 —— 交给调用方分两步做的话，
    /// 中间一换会话就会把闸还给别的会话，那个会话从此可以并行跑两个回合。
    /// </para>
    /// </summary>
    public async ValueTask<bool> SwitchSessionAsync(string sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId)
            || string.Equals(sessionId, _session.SessionId, StringComparison.Ordinal))
        {
            return true;
        }

        await _sessionSwapGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (string.Equals(sessionId, _session.SessionId, StringComparison.Ordinal))
            {
                return true;
            }

            // 等当前会话的回合结束（拿它的闸），拿到之后立刻还 ——
            // 全程都是同一个 runtime，不可能「还给别人」。
            // 等不到就拒绝：回合跑着时切走，SSE 的增量会显示在另一个会话的界面上。
            if (!await _session.TurnGate.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false))
            {
                return false;
            }

            Hosting.SessionRuntime target;
            try
            {
                target = GetOrOpenSession(sessionId);
            }
            catch (Exception)
            {
                // 打不开（路径非法 / 权限 / 磁盘）→ 如实失败，别把宿主留在半切状态
                return false;
            }
            finally
            {
                _session.TurnGate.Release();
            }

            SwapTo(target);
            return true;
        }
        finally
        {
            _sessionSwapGate.Release();
        }
    }

    /// <summary>
    /// <summary>
    /// 从现有会话的某个事件序号之前分叉出一个新会话（F3）。
    /// 底层用 SessionForker（日志前缀复制），分叉完立刻打开 ——
    /// 「开分支试试另一条路」从此是界面上的一次点击。
    /// </summary>
    public string ForkSession(string sourceSessionId, long fromSeq)
    {
        var sourcePath = Path.Combine(Options.SessionsDir, sourceSessionId + ".jsonl");
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException($"源会话不存在：{sourceSessionId}");
        }

        var newId = $"fork-{DateTime.UtcNow:MMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}";
        var targetPath = Path.Combine(Options.SessionsDir, newId + ".jsonl");

        Data.SessionForker.Fork(sourcePath, targetPath, fromSeq, newId);

        // 打开成分叉会话的 runtime（日志已含全部前缀事件，内存表构造时自动从磁盘装载）
        GetOrOpenSession(newId);

        // 分叉继承源会话的工作模式与项目目录（分叉 = "换个决定重试"，实验前提不变）
        if (SessionMetas().TryGetValue(sourceSessionId, out var forkMeta))
        {
            SaveSessionMeta(newId, forkMeta.Mode, forkMeta.ProjectDir);
        }

        return newId;
    }

    /// <summary>重命名会话（F4）：标题属"用户可改元数据"，存 titles.json（与 rephrase.json 同类），不进事件流。</summary>
    public void RenameSession(string sessionId, string title)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        var path = Path.Combine(Options.SessionsDir, "titles.json");
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        if (File.Exists(path))
        {
            try
            {
                var loaded = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
                if (loaded is not null)
                {
                    titles = new Dictionary<string, string>(loaded, StringComparer.Ordinal);
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // 坏文件当没有
            }
        }

        titles[sessionId] = title.Trim();
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(titles));
    }

    /// <summary>读全部会话标题（列表与导出共用）。</summary>
    public IReadOnlyDictionary<string, string> LoadTitles()
    {
        var path = Path.Combine(Options.SessionsDir, "titles.json");
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>();
        }

        try
        {
            var loaded = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            return loaded is null ? new Dictionary<string, string>() : new Dictionary<string, string>(loaded, StringComparer.Ordinal);
        }
        catch (System.Text.Json.JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    /// <summary>
    /// 导出会话为 Markdown（F8）：存档系统的"分享"出口。
    /// user/assistant 全文 + 工具调用与任务状态摘要；usage/reasoning 属噪音不导。
    /// </summary>
    public string ExportSessionMarkdown(string sessionId)
    {
        var path = Path.Combine(Options.SessionsDir, sessionId + ".jsonl");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"会话不存在：{sessionId}");
        }

        var titles = LoadTitles();
        var title = titles.TryGetValue(sessionId, out var t) ? t : sessionId;
        var all = _sessions.TryGetValue(sessionId, out var rt)
            ? rt.Events
            : AgentFramework.Data.JsonlEventLog.Read(path).ToList();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.AppendLine($"> 会话 `{sessionId}` · 导出于 {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine();

        foreach (var e in all)
        {
            switch (e)
            {
                case UserMessageEvent u:
                    sb.AppendLine("## 用户").AppendLine().AppendLine(u.ModelVisibleText).AppendLine();
                    break;

                case AssistantMessageEvent a when !string.IsNullOrWhiteSpace(a.Text):
                    sb.AppendLine("## 助手").AppendLine().AppendLine(a.Text).AppendLine();
                    break;

                case ToolCallRequestedEvent req:
                    sb.AppendLine($"**调用工具** `{req.ToolName}`（call {req.CallId}）").AppendLine();
                    break;

                case ToolCallCompletedEvent done:
                    sb.AppendLine($"- 结果：{(done.Success ? "成功" : $"失败 — {done.Error}")}").AppendLine();
                    break;

                case TaskStatusChangedEvent ts:
                    sb.AppendLine($"- 任务 `{ts.TaskId}` → **{ts.Status}**").AppendLine();
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    // ── G2 技能系统 ───────────────────────────────────────────

    /// <summary>扫描到的技能清单。</summary>
    public IReadOnlyList<SkillDefinition> Skills => _state.Skills;

    /// <summary>工作区根目录（创意工坊：导入目标 = {workspaceRoot}/workshop）。</summary>
    public string WorkspaceRoot => Options.WorkspaceRoot;

    /// <summary>
    /// 重新扫描技能（导入工坊条目后调用）：skills/ 与 workshop/ 两目录重算，
    /// 同名内置优先。已启用技能集合按名字保留 —— 重扫不丢开关状态。
    /// </summary>
    public void ReloadSkills()
    {
        _state.Skills = SkillLoader.ScanWorkspace(Options.WorkspaceRoot);

        // 开关集合里已不存在的技能名清掉（导入失败重扫 / 手动删目录后的残账）
        var known = _state.Skills.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        _state.EnabledSkills.RemoveWhere(name => !known.Contains(name));
    }

    /// <summary>已启用技能名。</summary>
    public IReadOnlyCollection<string> EnabledSkills => _state.EnabledSkills;

    /// <summary>启用/停用技能（回合边界生效：VisibleTools 每轮现取，下一轮就变）。</summary>
    public bool SetSkillEnabled(string name, bool enabled)
    {
        return enabled ? _state.EnabledSkills.Add(name) : _state.EnabledSkills.Remove(name);
    }

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

    /// <summary>
    /// 包是否处于暴露态 —— 与 <c>VisibleToolsCore</c> 同谓词
    /// （模式 AllowedToolsets / AllowedTools 空集 / DisabledToolsets）。
    ///
    /// 只看 DisabledToolsets 时，闲聊模式（AllowedTools = 空集）下包仍显示为开，
    /// 界面开关与实际暴露面就对不上了。
    /// </summary>
    private bool IsToolsetExposed(string toolsetId)
    {
        var profile = _state.CurrentProfile;

        // 与 VisibleToolsCore 的 AllowedTools 三态对齐：空集 = 一个工具都不发。
        if (profile.AllowedTools is { Count: 0 })
        {
            return false;
        }

        if (profile.AllowedToolsets is not null
            && !profile.AllowedToolsets.Contains(toolsetId, StringComparer.Ordinal))
        {
            return false;
        }

        return !_state.DisabledToolsets.Contains(toolsetId);
    }

    /// <summary>
    /// 开/关一个工具包。返回 <c>false</c> = 没改成（包不存在，或是不许关的保留包）。
    ///
    /// <para>
    /// 保留包（core / meta）拒绝关闭：关掉「读文件 + 写文件 + 列目录 + 问用户」会把 agent 关成残废；
    /// 关掉「工具包开关」这个工具，就再也没有办法开回来了。
    /// </para>
    /// </summary>
    public bool SetToolsetEnabled(string toolsetId, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(toolsetId))
        {
            return false;
        }

        var descriptors = _plugins.Toolsets;
        if (!descriptors.TryGetValue(toolsetId, out var descriptor))
        {
            return false;
        }

        if (!enabled && (descriptor.Protected || Contracts.BuiltinToolsets.Protected.Contains(toolsetId)))
        {
            return false;
        }

        return enabled
            ? _state.DisabledToolsets.Remove(toolsetId)
            : _state.DisabledToolsets.Add(toolsetId);
    }

    /// <summary>
    /// 向**指定会话**落一条事件（G1 子 Agent 编排用）：从会话表取它的 Sink，
    /// 不经过「当前会话」指针 —— 派发/完成留痕必须落在父会话自己的流里。
    /// </summary>
    public async ValueTask EmitToSessionAsync(string sessionId, SessionEvent sessionEvent, CancellationToken ct = default)
    {
        Hosting.SessionRuntime? runtime;
        lock (_sessions)
        {
            _sessions.TryGetValue(sessionId, out runtime);
        }

        if (runtime is null)
        {
            throw new InvalidOperationException($"会话不存在：{sessionId}");
        }

        await runtime.Sink.EmitAsync(sessionEvent, ct).ConfigureAwait(false);
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

    // ── 会话模式的持久化（modes.json，与 titles.json 同类）───

    /// <summary>会话元数据（模式 + 项目目录）。modes.json 是旧版纯模式映射，读入时自动迁移。</summary>
    public sealed record SessionMetaInfo(string Mode, string? ProjectDir);

    private string SessionMetaPath => Path.Combine(Options.SessionsDir, "session-meta.json");
    private string LegacyModesPath => Path.Combine(Options.SessionsDir, "modes.json");

    /// <summary>读全部会话元数据（会话列表徽标 + 惰性重开时换原）。</summary>
    public IReadOnlyDictionary<string, SessionMetaInfo> SessionMetas()
    {
        var result = new Dictionary<string, SessionMetaInfo>(StringComparer.Ordinal);

        // 旧版迁移：modes.json（sessionId → mode 字符串）
        if (File.Exists(LegacyModesPath))
        {
            try
            {
                var legacy = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(LegacyModesPath));
                if (legacy is not null)
                {
                    foreach (var (id, mode) in legacy)
                    {
                        result[id] = new SessionMetaInfo(mode, null);
                    }
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // 坏文件当没有
            }
        }

        if (File.Exists(SessionMetaPath))
        {
            try
            {
                var loaded = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, SessionMetaInfo>>(File.ReadAllText(SessionMetaPath));
                if (loaded is not null)
                {
                    foreach (var (id, meta) in loaded)
                    {
                        result[id] = meta;
                    }
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // 坏文件当没有
            }
        }

        return result;
    }

    private void SaveSessionMeta(string sessionId, string? modeId, string? projectDir)
    {
        var metas = new Dictionary<string, SessionMetaInfo>(SessionMetas(), StringComparer.Ordinal);
        var existing = metas.TryGetValue(sessionId, out var m) ? m : null;
        metas[sessionId] = new SessionMetaInfo(
            modeId ?? existing?.Mode ?? AgentModes.IdOf(_mode),
            projectDir ?? existing?.ProjectDir);

        Directory.CreateDirectory(Options.SessionsDir);
        File.WriteAllText(SessionMetaPath,
            System.Text.Json.JsonSerializer.Serialize(metas));
    }

    /// <summary>
    /// 把「当前会话」指针换到 <paramref name="target"/>。
    /// 跟随字段（日志 / 事件出口 / 主循环）必须**一起**换 ——
    /// 漏一个，就会出现「投影读 A 的日志、事件写进 B 的流」这种最难查的错位。
    /// </summary>
    private void SwapTo(Hosting.SessionRuntime target)
    {
        // 一次引用赋值即完成切换：_log/_sink/_runner 都是 _session 的派生属性，
        // 「换了半边」的中间态在结构上不存在（并发读者要么全看到旧会话，要么全看到新会话）。
        _session = target;
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

/// <summary>
/// 把事件写进 JSONL，并把审批事件派发到内核事件总线。
///
/// 审批分三层，顺序固定：
///   1. 内核 / 插件的订阅者（可以置 Cancelled）
///   2. 宿主的分级策略（Allow / Deny / Ask）
///   3. Ask 时交给界面（没有界面则降级为拒绝）
/// </summary>
public sealed class HostEventSink(
    JsonlEventLog log,
    PluginHost plugins,
    Func<ToolPreExecuteEvent, ApprovalDecision> approvalPolicy,
    Func<IUserInteraction> interactionProvider,
    Action<SessionEvent>? onEvent = null,
    ISessionIndex? index = null,
    string? sessionId = null,
    Func<string, bool>? isToolAllowed = null) : IAgentEventSink
{
    // 并发安全：RequestApprovalAsync 可能被多个会话/子 agent 同时调用，
    // 无锁 List.Add 会丢条目甚至把内部数组写坏。用 ConcurrentQueue 入队，
    // 读侧每次取快照（诊断面只读，不需要跨调用的严格一致视图）。
    private readonly System.Collections.Concurrent.ConcurrentQueue<ApprovalRecord> _approvals = new();

    public IReadOnlyList<ApprovalRecord> Approvals => [.. _approvals];

    public async ValueTask EmitAsync(SessionEvent sessionEvent, CancellationToken ct)
    {
        // 真相源先落盘 —— 索引只是派生物，顺序不能反（反了就会出现"索引里有、日志里没有"）
        log.Append(sessionEvent);
        onEvent?.Invoke(sessionEvent);

        if (index is { IsAvailable: true } && sessionId is not null)
        {
            // 索引写失败绝不冒泡：它只影响"方便程度"，不影响"能不能干活"
            try
            {
                await index.IndexAsync(sessionId, sessionEvent, ct).ConfigureAwait(false);
            }
            catch
            {
                // 忽略
            }
        }
    }

    public async ValueTask RequestApprovalAsync(ToolPreExecuteEvent toolPreExecuteEvent, CancellationToken ct)
    {
        // 第 1 层：内核 / 插件订阅者表态
        await plugins.EmitAsync(toolPreExecuteEvent, ct).ConfigureAwait(false);

        // 第 2、3 层：宿主策略
        if (!toolPreExecuteEvent.Cancelled)
        {
            var decision = approvalPolicy(toolPreExecuteEvent);

            // ★ P6：本会话已经放行过这个工具 → 不再打扰。
            //   放在**策略之后**：策略永远保留最终否决权，
            //   放行集只能把 Ask 变成 Allow，翻不动 Deny。
            if (decision == ApprovalDecision.Ask
                && (isToolAllowed?.Invoke(toolPreExecuteEvent.ToolName) ?? false))
            {
                decision = ApprovalDecision.Allow;
            }

            switch (decision)
            {
                case ApprovalDecision.Allow:
                    break;

                case ApprovalDecision.Deny:
                    Deny(toolPreExecuteEvent, "策略拒绝");
                    break;

                case ApprovalDecision.Ask:
                {
                    var interaction = interactionProvider();
                    if (!interaction.CanInteract)
                    {
                        // 拿不准又没人可问 → 拒绝。安全侧的默认必须保守。
                        Deny(toolPreExecuteEvent, "无界面可确认，默认拒绝");
                    }
                    else
                    {
                        var allowed = await interaction.ConfirmAsync(toolPreExecuteEvent, ct).ConfigureAwait(false);
                        if (!allowed)
                        {
                            Deny(toolPreExecuteEvent, "用户拒绝");
                        }
                    }

                    break;
                }
            }
        }

        _approvals.Enqueue(new ApprovalRecord(
            toolPreExecuteEvent.ToolName,
            toolPreExecuteEvent.Cancelled,
            toolPreExecuteEvent.RejectReason));
    }

    private static void Deny(ToolPreExecuteEvent toolPreExecuteEvent, string reason)
    {
        toolPreExecuteEvent.Cancelled = true;
        toolPreExecuteEvent.RejectReason ??= reason;
    }
}
