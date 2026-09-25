using AgentFramework.Agent;
using AgentFramework.Contracts;
using AgentFramework.Data;

namespace AgentFramework.Host.Hosting;

/// <summary>
/// 一个会话的运行态：事件日志 + 事件出口 + 主循环 + 它自己的回合闸。
///
/// 「一次装配 = 一个会话」曾经是硬约束（日志在装配时就打开了，宿主因此成了不可变体）。
/// 把它拆出来，是为了让「同一套缝、多个会话」变成可能 —— 子 agent 要的正是这个：
/// <b>共享</b>模型客户端、工具注册表、索引、记忆；<b>独立</b>事件流、上下文、回合闸。
///
/// 回合闸做成**每会话一把**而不是全局一把，是因为「不许两个回合同时跑」这条纪律
/// 本来就是针对同一个会话的（同一个事件流不能被两个回合交错写入）；
/// 不同会话之间没有这层关系，不该被互相挡住。
/// </summary>
public sealed class SessionRuntime : IAsyncDisposable
{
    private readonly List<SessionEvent> _events;
    private readonly object _eventsGate = new();

    internal SessionRuntime(string sessionId, JsonlEventLog log, HostEventSink sink, AgentRunner runner, string? modeId = null, string? projectDir = null)
    {
        SessionId = sessionId;
        Log = log;
        Sink = sink;
        Runner = runner;
        ModeId = modeId;
        ProjectDir = projectDir;

        // 启动时从磁盘加载**一次** —— 之后本轮进程里就不再重读这个文件了（见 Events）。
        _events = [.. JsonlEventLog.Read(log.Path)];
    }

    public string SessionId { get; }

    /// <summary>
    /// 本会话钉住的模式 id（创建时选定；null = 未钉，跟随宿主默认 —— 向后兼容旧装配路径）。
    /// 「工作模式在创建会话时选定」就落在这一字段：模式是会话的属性，不是宿主的全局开关。
    /// </summary>
    public string? ModeId { get; private set; }

    /// <summary>切换本会话的模式 id（任务 4：in-session 切换、无需重启；下一轮生效）。</summary>
    public void SetModeId(string? modeId) => ModeId = modeId;

    /// <summary>
    /// 本会话钉住的项目目录（创建时选定；null = 宿主工作区）。
    /// 文件工具沙箱与项目记忆都锚定这里 —— 多项目时各会话各看各的目录、各记各的账。
    /// </summary>
    public string? ProjectDir { get; }

    public JsonlEventLog Log { get; }

    public HostEventSink Sink { get; }

    public AgentRunner Runner { get; }

    /// <summary>
    /// 本会话的完整事件表 —— **内存持有**（P2）。
    ///
    /// <para>
    /// 原先每一轮都要 <c>_log.ReadAll()</c> 全量重读 JSONL、再全量重投影；
    /// 触发压缩时还要再重投影 2–3 次（while 减半窗口）。
    /// 长会话几千事件之后，这是**每轮固定**的 O(n) 文件 I/O + O(n) 投影 ——
    /// 而快照缓存只服务 UI 状态投影，热路径一点没沾到。
    /// </para>
    /// <para>
    /// 现在落盘时顺手进内存表，文件退化成**纯持久化**。
    /// 投影函数一行没改 —— 它们本来就是纯函数，「喂什么算什么」是它们的全部契约；
    /// 这里变的只是「喂进去的东西不再来自磁盘」。
    /// </para>
    /// <para>返回副本：调用方拿到的应该是快照，不是活表。</para>
    /// </summary>
    public IReadOnlyList<SessionEvent> Events
    {
        get
        {
            lock (_eventsGate)
            {
                return [.. _events];
            }
        }
    }

    /// <summary>
    /// 事件出口把刚落盘的事件交给这里（P2）。
    ///
    /// 只该由 <see cref="HostEventSink"/> 在**写盘之后**调用 ——
    /// 顺序反了就会出现「内存里有、日志里没有」，
    /// 而真相源与视图不一致是最难查的一类 bug。
    /// </summary>
    public void Track(SessionEvent sessionEvent)
    {
        lock (_eventsGate)
        {
            _events.Add(sessionEvent);
        }
    }

    /// <summary>本会话的回合闸：同一会话不允许两个回合同时跑。</summary>
    public SemaphoreSlim TurnGate { get; } = new(1, 1);

    /// <summary>本会话的日志路径（诊断与 UI 用）。</summary>
    public string LogPath => Log.Path;

    /// <summary>
    /// 悬空工具调用是否已修过一遍（幂等，一次会话只修一次就够）。
    /// 放在 runtime 上而不是宿主上 —— 修的是**这个会话**的事件流（P1）。
    /// </summary>
    internal bool DanglingRepaired { get; set; }

    /// <summary>
    /// 本会话的**审批放行集**（P6）：主人勾过「本会话允许此工具」的工具名。
    ///
    /// <para>
    /// 为什么挂在会话上：那是主人的一次表态，语义是「**这一段工作里**别再问我了」。
    /// 换一个会话就是换一件事，没理由沿用。
    /// </para>
    /// <para>
    /// 生命周期跟着 runtime 走，宿主关掉就没了 —— **不做持久化**：
    /// 「永久允许」是个危险承诺，重启之后该重新问一次。
    /// </para>
    /// </summary>
    private readonly HashSet<string> _allowedTools = new(StringComparer.Ordinal);

    /// <summary>这个工具在本会话里是否已被放行（P6）。</summary>
    public bool IsToolAllowed(string toolName)
    {
        lock (_allowedTools)
        {
            return _allowedTools.Contains(toolName);
        }
    }

    /// <summary>记住「本会话允许这个工具」（P6）。返回 true = 这次是新记的。</summary>
    public bool AllowTool(string toolName)
    {
        lock (_allowedTools)
        {
            return _allowedTools.Add(toolName);
        }
    }

    /// <summary>本会话已放行的工具（诊断 / UI 用）。</summary>
    public IReadOnlyList<string> AllowedTools
    {
        get
        {
            lock (_allowedTools)
            {
                return [.. _allowedTools];
            }
        }
    }

    /// <summary>会话累计用量（由事件流投影而来，不另存状态）。
    /// B4：改读**内存事件表** —— 此前每次都全量重读磁盘，而 UI 每刷一次状态栏就调一次。</summary>
    public SessionUsage Usage => SessionUsage.From(Events);

    public ValueTask DisposeAsync()
    {
        Log.Dispose();
        TurnGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
