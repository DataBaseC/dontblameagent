namespace AgentFramework.Contracts;

/// <summary>一次「问用户」的请求。</summary>
public sealed record AskUserRequest
{
    /// <summary>要问的问题。写得具体些 —— 含糊的问题只会换来含糊的答案。</summary>
    public required string Question { get; init; }

    /// <summary>
    /// 可选项。给了就当选择器用（界面渲染成按钮/列表），没给就是自由文本。
    /// 注意：给了选项**不等于**用户只能选它，实现方应当允许自由作答。
    /// </summary>
    public IReadOnlyList<string>? Options { get; init; }

    /// <summary>为什么问（给用户一点上下文，免得对着问题发呆）。</summary>
    public string? Context { get; init; }
}

/// <summary>用户的回答。</summary>
public sealed record AskUserAnswer(bool Answered, string? Text)
{
    /// <summary>没人可问 / 用户没答 —— 调用方必须据此降级，而不是当成空回答。</summary>
    public static AskUserAnswer None { get; } = new(false, null);

    public static AskUserAnswer Of(string text) => new(true, text);
}

/// <summary>
/// 用户交互 seam。
///
/// 与审批的关系：<b>审批是它的一个特例</b> —— 问「能不能」，答「能 / 不能」。
/// 二者共用一条缝，是为了不让「界面」这件事有两套说法：
/// 有没有人可问、问得出去问不出去，只取决于 <see cref="CanInteract"/>。
///
/// 无界面时必须**保守降级**（与审批同一纪律）：问不出去就如实说问不出去，
/// 绝不假装用户默认同意。
/// </summary>
public interface IUserInteraction
{
    /// <summary>当前有没有可以真正对话的界面。false 时不要尝试提问。</summary>
    bool CanInteract { get; }

    /// <summary>请求批准一个危险动作（工具执行前）。</summary>
    ValueTask<bool> ConfirmAsync(ToolPreExecuteEvent toolCall, CancellationToken ct = default);

    /// <summary>问用户一个问题，并等一个回答。</summary>
    ValueTask<AskUserAnswer> AskAsync(AskUserRequest request, CancellationToken ct = default);

    /// <summary>单向通知（不需要回答）。没有界面时静默丢弃即可。</summary>
    ValueTask NotifyAsync(string text, CancellationToken ct = default);
}

/// <summary>
/// 没有界面时的降级实现：问也白问，如实回答「没人可问」。
/// 审批方向默认**拒绝**（与「拿不准又没人可问 → 拒绝」同一条纪律）。
/// </summary>
public sealed class NullUserInteraction : IUserInteraction
{
    public static readonly NullUserInteraction Instance = new();

    public bool CanInteract => false;

    public ValueTask<bool> ConfirmAsync(ToolPreExecuteEvent toolCall, CancellationToken ct = default)
        => ValueTask.FromResult(false);

    public ValueTask<AskUserAnswer> AskAsync(AskUserRequest request, CancellationToken ct = default)
        => ValueTask.FromResult(AskUserAnswer.None);

    public ValueTask NotifyAsync(string text, CancellationToken ct = default)
        => ValueTask.CompletedTask;
}
