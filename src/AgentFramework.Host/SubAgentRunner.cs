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
/// 为什么值得一个独立类型而不是散在工具里：
///   「派生一个子会话 → 跑任务 → 收结果 → 在父会话留痕」是一个**完整的编排动作**，
///   主模型通过 <c>spawn_subagent</c> 工具触发它，未来并行派发、多级派生也都长在这上面。
/// </summary>
public static class SubAgentRunner
{
    /// <summary>派发结果：子会话 id + 最终回复摘要。</summary>
    public sealed record SubAgentResult(string ChildSessionId, bool Success, string Summary);

    /// <summary>
    /// 跑一个子 Agent：开子会话 → 发任务 → 收最终文本 → 父会话留痕（先 dispatched 再 completed）。
    /// 「模型可见即已记录」对父会话依然成立：父上下文里只出现这两条事件的投影。
    /// </summary>
    public static async Task<SubAgentResult> RunSubAgentAsync(
        AgentHost host,
        string parentSessionId,
        string task,
        int? maxSteps = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(task))
        {
            return new SubAgentResult(string.Empty, false, "任务描述为空");
        }

        var childId = $"sub-{DateTime.UtcNow:MMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}";

        // 留痕 1：派发（先落日志再跑 —— 顺序纪律与主循环一致）
        await host.EmitToSessionAsync(parentSessionId, new SubAgentDispatchedEvent
        {
            SessionId = parentSessionId,
            ChildSessionId = childId,
            Task = task,
        }, ct).ConfigureAwait(false);

        // 开子会话：独立事件流；继承父会话的工作模式与项目目录 ——
        // 委派出去的任务在同一份项目记忆与文件沙箱里干活，汇报才对得上号。
        var parent = host.GetSession(parentSessionId);

        var child = host.OpenSession(
            childId,
            systemPrompt: "你是被委派子任务的执行者。专注于完成任务本身，"
                + "完成后给出简洁的结果汇报（做了什么、结论是什么、有什么遗留）。"
                + "不要反问委派者——拿不准就在汇报里标注假设。",
            maxSteps: maxSteps,
            onEvent: null,
            relayToUi: false,
            modeId: parent?.ModeId,
            projectDir: parent?.ProjectDir);

        try
        {
            var result = await host.SendAsync(child, task, ct).ConfigureAwait(false);

            var summary = result.Completed
                ? result.FinalText
                : $"（未在 {result.Steps} 步内完成：{result.StopReason}）";

            // 摘要截断：父上下文只需要「结论级」信息，全文在子会话日志里
            summary = TruncateSafe(summary, 500, $"…（已截断，全文在子会话 {childId}）");

            // 留痕 2：完成
            await host.EmitToSessionAsync(parentSessionId, new SubAgentCompletedEvent
            {
                SessionId = parentSessionId,
                ChildSessionId = childId,
                Success = result.Completed,
                Summary = summary,
            }, ct).ConfigureAwait(false);

            return new SubAgentResult(childId, result.Completed, summary);
        }
        catch (Exception ex)
        {
            await host.EmitToSessionAsync(parentSessionId, new SubAgentCompletedEvent
            {
                SessionId = parentSessionId,
                ChildSessionId = childId,
                Success = false,
                Summary = $"子 Agent 异常：{ex.Message}",
            }, ct).ConfigureAwait(false);

            return new SubAgentResult(childId, false, $"子 Agent 异常：{ex.Message}");
        }
        finally
        {
            // v3.5 审查 P2：子会话用完即弃 —— 原先它永久留在宿主会话表里
            //（内存事件表 + 日志句柄都不释放），派工越多泄漏越多。
            // 只回收**运行时对象**：日志文件保留（上面的摘要还引用着它），全文仍可从磁盘读回。
            // 用 CancellationToken.None：即使父回合被取消，也要把子会话收干净。
            await host.CloseSessionAsync(childId, CancellationToken.None).ConfigureAwait(false);
        }
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
