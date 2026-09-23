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

        using var target = JsonlEventLog.Open(targetPath);

        // 新会话的头部：记录血缘（从谁、从哪分叉来的）
        var header = target.Append(new SessionCreatedEvent
        {
            SessionId = newSessionId,
            ProjectId = sourceHeader?.ProjectId,
            Title = title ?? (sourceHeader is null ? "fork" : $"{sourceHeader.Title} (fork)"),
            ParentSessionId = sourceHeader?.SessionId,
            ForkFromSeq = fromSeq,
        });

        long copied = 0;
        foreach (var e in prefix.Where(x => x is not SessionCreatedEvent))
        {
            target.Append(CloneWithSession(e, newSessionId));
            copied++;
        }

        return new ForkResult(newSessionId, header.Seq, copied, fromSeq);
    }

    /// <summary>
    /// 用 JSON 往返克隆事件并改写会话标识。
    /// 走序列化而不是手写 switch：多态类型不会漏，将来加事件类型也不用改这里。
    /// </summary>
    private static SessionEvent CloneWithSession(SessionEvent source, string newSessionId)
    {
        var json = JsonSerializer.Serialize(source, JsonlEventLog.SerializerOptions);
        var clone = JsonSerializer.Deserialize<SessionEvent>(json, JsonlEventLog.SerializerOptions)
                    ?? throw new InvalidOperationException($"事件克隆失败：{source.GetType().Name}");

        clone.SessionId = newSessionId;
        return clone;
    }
}
