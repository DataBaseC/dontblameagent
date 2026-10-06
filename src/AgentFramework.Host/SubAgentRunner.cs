using AgentFramework.Contracts;

namespace AgentFramework.Host;

/// <summary>
/// 子 Agent 编排层（G1）。
///
/// 为什么住在 Host 层而不是 Agent 层：编排要用到 <see cref="AgentHost"/> 的
/// OpenSession / SendAsync / EmitToSessionAsync —— 而 Host 引用 Agent，
/// Agent 引用 Host 就是循环依赖（v3.4 原包把本类型放在 Agent 层，永远编译不过）。
/// 「编排宿主资源」天生是宿主侧的职责，放回 Host 层分层就顺了。
///
/// <para>
/// v3.23 起编排实体上收到 <see cref="SubAgentRegistry"/>（名单 / 状态 / 追加消息 / 打断），
/// 本类保留两样跨处共用的东西：<b>子 Agent 的系统提示词</b>与<b>前台派发的薄壳</b>。
/// </para>
/// </summary>
public static class SubAgentRunner
{
    /// <summary>派发结果：子会话 id + 最终回复摘要。</summary>
    public sealed record SubAgentResult(string ChildSessionId, bool Success, string Summary);

    /// <summary>
    /// 子 Agent 的系统提示词 —— 委派语义只在这里定义一次
    /// （提示词散成两份，子 Agent 的行为就会随调用路径漂移）。
    /// </summary>
    public const string ChildSystemPrompt =
        "你是被委派子任务的执行者。专注于完成任务本身，"
        + "完成后给出简洁的结果汇报（做了什么、结论是什么、有什么遗留）。"
        + "不要反问委派者 —— 拿不准就在汇报里标注假设。";

    /// <summary>
    /// 前台派发：等子 Agent 跑完再返回（最常见形态）。
    /// 需要并行 / 中途追加指令 / 叫停时改用 <see cref="ISubAgentControl"/>（<c>subagent</c> 工具）。
    /// </summary>
    public static async Task<SubAgentResult> RunSubAgentAsync(
        AgentHost host,
        string parentSessionId,
        string task,
        int? maxSteps = null,
        CancellationToken ct = default)
    {
        var info = await host.SubAgents
            .SpawnAsync(parentSessionId, task, background: false, maxSteps, ct)
            .ConfigureAwait(false);

        return new SubAgentResult(
            info.Id,
            info.State == SubAgentStates.Completed,
            info.Summary ?? info.State);
    }

    /// <summary>
    /// 按 UTF-16 code unit 截断，但不切断代理对（emoji 等）——
    /// 切在中间会产生非法 UTF-16 字符串，写进 JSONL 就是一个「坏行」。
    /// 与 AgentRunner 的思考截断同一条纪律；本文件私有，不跨工程抽公共。
    /// </summary>
    internal static string TruncateSafe(string text, int maxChars, string suffix)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }

        var cut = maxChars;
        while (cut > 0 && char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return text[..cut] + suffix;
    }
}
