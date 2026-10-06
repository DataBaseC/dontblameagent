namespace AgentFramework.Contracts;

/// <summary>
/// 子 Agent 的对外快照（名单 / 状态查询用）——刻意只是一份**只读视图**：
/// 控制面（停 / 发消息）走 <see cref="ISubAgentControl"/> 的方法，不让人改快照。
/// </summary>
public sealed record SubAgentInfo(
    /// <summary>子会话 id（也是派发时的句柄）。</summary>
    string Id,
    /// <summary>派发它的父会话。</summary>
    string ParentSessionId,
    /// <summary>交给它的任务描述。</summary>
    string Task,
    /// <summary>状态：<c>running</c> / <c>completed</c> / <c>failed</c> / <c>stopped</c>。</summary>
    string State,
    /// <summary>派发时刻。</summary>
    DateTimeOffset StartedAt,
    /// <summary>已跑了几个回合（追加消息会加一轮）。</summary>
    int Rounds,
    /// <summary>最终摘要（未完成时为 null）。</summary>
    string? Summary,
    /// <summary>是否后台派发（前台派发在结果返回时就已经结束）。</summary>
    bool Background)
{
    public bool IsRunning => State == SubAgentStates.Running;
}

/// <summary>子 Agent 状态的稳定字面量（事件 / 界面 / 工具三处共用一套）。</summary>
public static class SubAgentStates
{
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Stopped = "stopped";
}

/// <summary>
/// 子 Agent 管控面（对齐 dsh 的 <c>dsh-tool-subagent</c> + <c>dsh-tool-subagent-control</c>）：
/// 派发 · 名单 · 状态 · 取结果 · **追加消息** · **打断** · 等待。
///
/// <para>
/// 为什么要有它：v3.4 的子 Agent 只有「同步一次性派发」——派完只能干等，
/// 不能并行、不能中途追加指令、不能叫停。而真实的长任务正是「几块活同时推 + 谁出岔子就停谁」。
/// </para>
/// <para>
/// 实现住在宿主（它要 OpenSession / SendAsync / 会话表），工具只持有这个接口 ——
/// Tools 工程不引用 Host，分层不倒挂。
/// </para>
/// </summary>
public interface ISubAgentControl
{
    /// <summary>
    /// 派发一个子 Agent。返回句柄（快照）。
    /// <paramref name="background"/> = true 时**立即返回**（状态 running），
    /// false 时等它跑完（返回时状态已是终态）。
    /// </summary>
    Task<SubAgentInfo> SpawnAsync(
        string parentSessionId,
        string task,
        bool background = false,
        int? maxSteps = null,
        CancellationToken ct = default);

    /// <summary>名单（可只列某个父会话派出的）。</summary>
    IReadOnlyList<SubAgentInfo> List(string? parentSessionId = null);

    /// <summary>单个子 Agent 的当前快照；不存在返回 null。</summary>
    SubAgentInfo? Get(string id);

    /// <summary>
    /// 给子 Agent **追加一条指令**。
    /// 返回 false = 没有这个子 Agent，或它已经结束了（结束的子 Agent 不再收消息）。
    /// </summary>
    /// <param name="interrupt">
    /// true = <b>抢占</b>：作废它当前这一轮，**立刻**改用这条指令重跑
    /// （当前轮的产出不保留，状态仍是 running）；
    /// false = 排在当前轮跑完之后（默认，不打断它手上的活）。
    /// </param>
    bool Post(string id, string text, bool interrupt = false);

    /// <summary>打断：取消它当前（及后续）回合，状态转为 <c>stopped</c>。返回 false = 没这个子 Agent。</summary>
    bool Stop(string id);

    /// <summary>
    /// 等一个子 Agent 结束（或超时）。返回一句**人/模型可读的状态描述**（含最终摘要）。
    /// </summary>
    Task<string> WaitAsync(string id, int timeoutSeconds = 300, CancellationToken ct = default);

    /// <summary>终结所有还活着的子 Agent（宿主关停时用）。</summary>
    void StopAll();
}
