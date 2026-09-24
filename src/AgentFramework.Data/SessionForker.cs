using System.Text.Json;
using AgentFramework.Contracts;

namespace AgentFramework.Data;

public sealed record ForkResult(string SessionId, long HeaderSeq, long CopiedEvents, long ForkedFromSeq);

/// <summary>
/// 会话分叉。
///
/// 因为事件日志是「仅追加 + 可投影」，分叉变成了一件很便宜的事：
///   取源日志的一个前缀 → 复制成一份独立的自包含日志 → 换掉会话标识。
/// 不需要深拷贝运行时状态，也不需要理解业务语义。
/// </summary>
public static class SessionForker
{
    /// <summary>
    /// 从 <paramref name="sourcePath"/> 的 <paramref name="fromSeq"/>（含）之前分叉出
    /// 一个全新的、自包含的会话日志。
    /// </summary>
    public static ForkResult Fork(
        string sourcePath,
        string targetPath,
        long fromSeq,
        string newSessionId,
        string? title = null)
    {
        var prefix = JsonlEventLog.Read(sourcePath).Where(e => e.Seq <= fromSeq).ToList();
        if (prefix.Count == 0)
        {
            throw new InvalidOperationException($"源会话在 seq<={fromSeq} 范围内没有任何事件，无法分叉");
        }

        var sourceHeader = prefix.OfType<SessionCreatedEvent>().FirstOrDefault();
        var toCopy = prefix.Where(x => x is not SessionCreatedEvent).ToList();

        using var target = JsonlEventLog.Open(targetPath);

        // 建立 oldSeq→newSeq 映射（必须在落盘前算好）：
        // Append 会按 ++lastSeq 重新编号，而 ContextCompactedEvent.MaskedSeqs /
        // CheckpointEvent.FromSeq·ToSeq 钉的是**源会话的 Seq**。
        // 若只改 SessionId 不改这些引用，分叉后的压缩留痕会指向错误（或不存在）的事件。
        // 常见情况下源 Seq 恰好是 1..N 且 header 占 1，映射退化为恒等 —— 仍然安全。
        var headerSeq = target.LastSeq + 1;
        var seqMap = new Dictionary<long, long>(toCopy.Count);
        for (var i = 0; i < toCopy.Count; i++)
        {
            seqMap[toCopy[i].Seq] = headerSeq + 1 + i;
        }

        // 新会话的头部：记录血缘（从谁、从哪分叉来的）
        var header = target.Append(new SessionCreatedEvent
        {
            SessionId = newSessionId,
            ProjectId = sourceHeader?.ProjectId,
            Title = title ?? (sourceHeader is null ? "fork" : $"{sourceHeader.Title} (fork)"),
            ParentSessionId = sourceHeader?.SessionId,
            ForkFromSeq = fromSeq,
        });

        if (header.Seq != headerSeq)
        {
            throw new InvalidOperationException($"分叉 Seq 预测失准：header 期望 {headerSeq}，实际 {header.Seq}");
        }

        long copied = 0;
        foreach (var e in toCopy)
        {
            var written = target.Append(CloneWithSession(e, newSessionId, seqMap));
            if (written.Seq != seqMap[e.Seq])
            {
                throw new InvalidOperationException(
                    $"分叉 Seq 预测失准：事件原 Seq={e.Seq} 期望新 Seq={seqMap[e.Seq]}，实际 {written.Seq}");
            }

            copied++;
        }

        return new ForkResult(newSessionId, header.Seq, copied, fromSeq);
    }

    /// <summary>
    /// 用 JSON 往返克隆事件、改写会话标识，并把交叉引用的 Seq 搬到新号上。
    /// 走序列化而不是手写 switch：多态类型不会漏，将来加事件类型也不用改这里。
    /// </summary>
    private static SessionEvent CloneWithSession(
        SessionEvent source,
        string newSessionId,
        IReadOnlyDictionary<long, long> seqMap)
    {
        var json = JsonSerializer.Serialize(source, JsonlEventLog.SerializerOptions);
        var clone = JsonSerializer.Deserialize<SessionEvent>(json, JsonlEventLog.SerializerOptions)
                    ?? throw new InvalidOperationException($"事件克隆失败：{source.GetType().Name}");

        clone.SessionId = newSessionId;
        RewriteSeqRefs(clone, seqMap);
        return clone;
    }

    /// <summary>
    /// 重写事件里「指向别的事件」的 Seq 引用。
    /// 不在映射里的号（前缀之外的事件）对 MaskedSeqs 直接丢弃 ——
    /// 它们在分叉日志里并不存在，留着旧号反而可能误伤新号上的别的事件。
    /// </summary>
    private static void RewriteSeqRefs(SessionEvent clone, IReadOnlyDictionary<long, long> seqMap)
    {
        switch (clone)
        {
            case ContextCompactedEvent compacted:
                compacted.MaskedSeqs = compacted.MaskedSeqs
                    .Where(seqMap.ContainsKey)
                    .Select(s => seqMap[s])
                    .ToList();
                break;

            case CheckpointEvent checkpoint:
                if (seqMap.TryGetValue(checkpoint.FromSeq, out var from))
                {
                    checkpoint.FromSeq = from;
                }

                if (seqMap.TryGetValue(checkpoint.ToSeq, out var to))
                {
                    checkpoint.ToSeq = to;
                }

                break;
        }
    }
}
