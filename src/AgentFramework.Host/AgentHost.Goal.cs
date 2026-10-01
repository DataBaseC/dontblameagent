namespace AgentFramework.Host;

public sealed partial class AgentHost
{
    /// <summary>Goal 停止条件（自然语言）；null/空 = 不启用目标核验。</summary>
    public string? Goal => Options.Goal;

    /// <summary>
    /// 设置 Goal 停止条件（MiMo Code 的 Goal 机制 / Claude Code 的 /goal）。
    ///
    /// <para>
    /// 语义：这是「这次任务做到什么才算完」的**可测终态**（如「所有测试通过且代码已提交」）。
    /// 模型每次想收尾时，旁路验证者会按它裁决；未达成就把差距反馈回去继续干。
    /// </para>
    /// <para>
    /// 对所有打开的会话立即生效（改的是各会话主干的活选项，下一次收尾核验就按新目标走）。
    /// 传 null / 空串即关闭核验。目标本身不写进会话日志（它不是模型发言）；
    /// 要持久化就写进配置单 <c>goal</c> 字段。
    /// </para>
    /// </summary>
    public void SetGoal(string? goal)
    {
        var normalized = string.IsNullOrWhiteSpace(goal) ? null : goal.Trim();
        Options.Goal = normalized;

        lock (_sessions)
        {
            foreach (var session in _sessions.Values)
            {
                session.Runner.Options.Goal = normalized;
            }
        }

        Console.WriteLine(normalized is null
            ? "[goal] 目标核验已关闭"
            : $"[goal] 目标：{normalized}");
    }
}
