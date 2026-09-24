namespace AgentFramework.Contracts;

/// <summary>
/// 一条被索引的历史记录（从事件流派生而来）。
/// 它<b>不是</b>真相源 —— 随时可以从 JSONL 重建。
/// </summary>
public sealed record IndexedEvent(
    string SessionId,
    long Seq,
    string Type,
    DateTimeOffset Timestamp,
    string Text);

/// <summary>
/// 历史检索命中。
///
/// 名字刻意不叫 SearchHit —— 那个名字是「联网搜索」的（AgentFramework.Tools），
/// 两个同名类型在同一个 using 下会打架。
/// </summary>
public sealed record HistoryHit(
    string SessionId,
    long Seq,
    string Type,
    DateTimeOffset Timestamp,
    string Snippet);

/// <summary>
/// 会话历史索引 —— 一个 <b>派生</b>存储（DESIGN.md 4.2 / 4.16）。
///
/// 它存在的理由有两条，都是长任务才暴露出来的：
///   1. <b>性能</b>：投影、水位、任务卡每轮都要算，别次次 O(n) 重放整条日志
///   2. <b>能力</b>：给模型一个「把折叠掉的历史捞回来」的口子 ——
///      这一条才是关键：没有检索，上下文裁剪就是**丢失**；有了检索，它就是**卸载**。
///
/// 不变式：**索引永远可以丢**。删了、坏了，从 JSONL 重建即可。
/// 所以任何索引故障都只能降级、不能阻断主流程。
/// </summary>
public interface ISessionIndex : IAsyncDisposable
{
    /// <summary>实现种类（"sqlite" / "none"），用于状态展示与诊断。</summary>
    string Kind { get; }

    /// <summary>索引是否真的可用（原生库加载失败时为 false，此时应降级为直接扫日志）。</summary>
    bool IsAvailable { get; }

    /// <summary>写入一条事件（幂等：同一 session+seq 重复写不会重复计数）。</summary>
    ValueTask IndexAsync(string sessionId, SessionEvent sessionEvent, CancellationToken ct = default);

    /// <summary>全量重建某个会话的索引（索引缺失、损坏、或版本升级后调用）。</summary>
    ValueTask RebuildAsync(string sessionId, IEnumerable<SessionEvent> events, CancellationToken ct = default);

    /// <summary>
    /// 全文检索历史。<paramref name="sessionId"/> 为 null 时跨会话检索。
    /// </summary>
    ValueTask<IReadOnlyList<HistoryHit>> SearchAsync(
        string query,
        string? sessionId,
        int limit,
        CancellationToken ct = default);

    /// <summary>
    /// 索引里的事件条数。<paramref name="sessionId"/> 为 null 时统计全部会话。
    /// </summary>
    ValueTask<int> CountAsync(string? sessionId = null, CancellationToken ct = default);

    /// <summary>
    /// 索引水位（已索引到的最大 Seq）。判「落后」用它，不要用 <see cref="CountAsync"/> ——
    /// 空文本事件不进 events 表，Count 永远对不齐（契约「可加不可改」）。
    /// 默认回落到 Count：老实现不必为此重编。
    /// </summary>
    async ValueTask<long> GetIndexedWatermarkAsync(string sessionId, CancellationToken ct = default)
        => await CountAsync(sessionId, ct).ConfigureAwait(false);

    /// <summary>
    /// 删掉某个会话的索引（会话被删时清派生物用）。
    /// 给了默认实现 —— 老实现不必为此重编：契约「可加不可改」。
    /// </summary>
    ValueTask RemoveSessionAsync(string sessionId, CancellationToken ct = default)
        => ValueTask.CompletedTask;
}

/// <summary>
/// 空索引 —— 原生库加载不了时的降级实现。
/// 「什么都不做」不是偷懒：它保证调用方不必到处写 null 检查。
/// </summary>
public sealed class NullSessionIndex : ISessionIndex
{
    public static readonly NullSessionIndex Instance = new();

    public string Kind => "none";

    public bool IsAvailable => false;

    public ValueTask IndexAsync(string sessionId, SessionEvent sessionEvent, CancellationToken ct = default)
        => ValueTask.CompletedTask;

    public ValueTask RebuildAsync(string sessionId, IEnumerable<SessionEvent> events, CancellationToken ct = default)
        => ValueTask.CompletedTask;

    public ValueTask<IReadOnlyList<HistoryHit>> SearchAsync(
        string query,
        string? sessionId,
        int limit,
        CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<HistoryHit>>([]);

    public ValueTask<int> CountAsync(string? sessionId = null, CancellationToken ct = default)
        => ValueTask.FromResult(0);

    public ValueTask<long> GetIndexedWatermarkAsync(string sessionId, CancellationToken ct = default)
        => ValueTask.FromResult(0L);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
