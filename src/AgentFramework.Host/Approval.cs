namespace AgentFramework.Host;

/// <summary>对一次工具调用的处置决定。</summary>
public enum ApprovalDecision
{
    /// <summary>直接放行（只读、无副作用类操作，不打断用户）。</summary>
    Allow,

    /// <summary>直接拒绝。</summary>
    Deny,

    /// <summary>
    /// 交给用户裁决：有界面时弹确认，没界面（控制台、无人值守）则降级为拒绝。
    /// 「拿不准就拒绝」比「拿不准就放行」安全得多。
    /// </summary>
    Ask,
}

/// <summary>
/// 能向用户征求确认的界面。控制台、Web UI、将来的 WebView2 各实现一份。
/// </summary>
public interface IApprovalPrompt
{
    /// <summary>询问是否允许。返回 true = 允许。</summary>
    ValueTask<bool> AskAsync(Contracts.ToolPreExecuteEvent request, CancellationToken ct);

    /// <summary>
    /// 带「本会话记住」的审批（P6）：除了允许与否，还告诉宿主**要不要一直记住**。
    ///
    /// <para>
    /// 默认实现**退化成老的 <see cref="AskAsync"/> 且永不记住** ——
    /// 只懂「批准 / 拒绝」的老界面（控制台、将来的 WebView2）不必重编、
    /// 也不必实现任何新东西，行为与从前一模一样。
    /// </para>
    /// <para>
    /// 这是「契约可加不可改」的第三次实践（前两次：<c>IToolWithSchema</c>、
    /// <c>ISessionIndex.RemoveSessionAsync</c>）——
    /// 新增的能力写进默认实现里，老实现原地不动就是对的。
    /// </para>
    /// </summary>
    async ValueTask<ApprovalAnswer> AskDetailedAsync(Contracts.ToolPreExecuteEvent request, CancellationToken ct)
        => new ApprovalAnswer(await AskAsync(request, ct).ConfigureAwait(false), Remember: false);
}

/// <summary>
/// 一次审批的回答（P6）：允许与否 + 要不要在**本会话**里一直记住。
///
/// 「本会话记住」解决的是一个很具体的烦人：审批 Ask 每次最多挡 5 分钟，
/// 而 <c>run_command git status</c> 这种重复动作会把人点烦。
/// </summary>
public readonly record struct ApprovalAnswer(bool Allowed, bool Remember);

/// <summary>
/// 把老的 <see cref="IApprovalPrompt"/> 适配成 <see cref="Contracts.IUserInteraction"/>。
///
/// 于是现有的界面实现（控制台 / Web UI）<b>不必重写</b>就能接上新缝：
/// 它只懂「批准 / 拒绝」，那遇到「问用户」这类请求就如实回「问不出去」——
/// 而不是编一个答案。
/// </summary>
public sealed class ApprovalPromptInteraction(
    IApprovalPrompt prompt,
    Action<string>? rememberTool = null) : Contracts.IUserInteraction
{
    public bool CanInteract => true;

    public async ValueTask<bool> ConfirmAsync(Contracts.ToolPreExecuteEvent toolCall, CancellationToken ct = default)
    {
        var answer = await prompt.AskDetailedAsync(toolCall, ct).ConfigureAwait(false);

        // P6：用户在卡片上勾了「本会话允许此工具」→ 记进当前会话的放行集。
        // 此后这个工具在本会话里不再弹卡（下一回合生效）。
        if (answer is { Allowed: true, Remember: true })
        {
            rememberTool?.Invoke(toolCall.ToolName);
        }

        return answer.Allowed;
    }

    public ValueTask<Contracts.AskUserAnswer> AskAsync(Contracts.AskUserRequest request, CancellationToken ct = default)
        => ValueTask.FromResult(Contracts.AskUserAnswer.None);

    public ValueTask NotifyAsync(string text, CancellationToken ct = default)
        => ValueTask.CompletedTask;
}

/// <summary>
/// 默认分级审批策略。
///
/// 分级的依据不是"危险关键词"，而是<b>动作的性质</b>：
///   只读 / 联网检索 → 放行（不打断）
///   写文件 / 执行命令 → 询问
///   未知工具        → 询问（对新东西保持谨慎，白名单天然会漏）
/// </summary>
public static class DefaultApprovalPolicy
{
    public static ApprovalDecision Decide(Contracts.ToolPreExecuteEvent request) => request.ToolName switch
    {
        // ask_user 也放行：提问本身没有副作用，而「能不能问」还要先审批就很荒谬。
        "read_file" or "list_dir" or "web_search" or "web_fetch" or "ask_user" => ApprovalDecision.Allow,
        "write_file" or "run_command" => ApprovalDecision.Ask,
        _ => ApprovalDecision.Ask,
    };
}
