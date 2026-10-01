namespace AgentFramework.Contracts;

/// <summary>Goal 终止验证的裁决（三态，与 MiMo Code / Claude Code /goal 对齐）。</summary>
public enum GoalVerdict
{
    /// <summary>目标已达成 —— 可以收尾。</summary>
    Met,

    /// <summary>目标未达成 —— 带着差距反馈继续干，不许提前收工。</summary>
    NotMet,

    /// <summary>确认无法达成（环境坏了 / 目标矛盾）—— 如实收尾，不再空转。</summary>
    Impossible,
}

/// <summary>一次目标核验的结果。<paramref name="Gap"/> 在 NotMet 时给出具体差距。</summary>
public sealed record GoalVerification(GoalVerdict Verdict, string? Gap = null)
{
    public static GoalVerification Met() => new(GoalVerdict.Met);

    public static GoalVerification NotMet(string? gap = null) => new(GoalVerdict.NotMet, gap);

    public static GoalVerification Impossible(string? gap = null) => new(GoalVerdict.Impossible, gap);
}

/// <summary>
/// Goal 终止验证器（MiMo Code 的 Goal 机制 / Claude Code 的 /goal）。
///
/// <para>
/// 解决的问题：长任务里 agent 看到已有进展就倾向**提前宣称完成**。
/// 做法：模型每次想收尾时，由一次**独立的旁路模型调用**裁决「目标真的达成了吗」——
/// 验证者不参与实际工作（无「对自己成果的认同偏差」），只看证据：
/// 未达成就把具体差距反馈回去让 agent 继续；确认做不到就判 impossible 如实收尾。
/// </para>
/// <para>
/// 纪律：验证器是**增强项**——它超时或出错时必须放行（fail-open），
/// 绝不能把收尾卡死；裁决只由调用方消费，不落进模型可见上下文（差距反馈除外）。
/// </para>
/// </summary>
public interface IGoalVerifier
{
    /// <summary>
    /// 裁决本轮收尾是否成立。
    /// <paramref name="goal"/> 是停止条件（自然语言），<paramref name="userRequest"/> 是用户原始请求，
    /// <paramref name="context"/> 是对话近况（含工具结果），<paramref name="finalText"/> 是模型想收尾的答复。
    /// </summary>
    ValueTask<GoalVerification> VerifyAsync(
        string goal,
        string userRequest,
        IReadOnlyList<LlmMessage> context,
        string finalText,
        CancellationToken ct = default);
}
