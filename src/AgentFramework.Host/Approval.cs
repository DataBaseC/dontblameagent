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

/// <summary>
/// 审批档位（任务 5）：从「一个一个点确定」到「全盘托管」的分级。
/// <list type="bullet">
///   <item><see cref="Ask"/>：每次敏感操作都问（接近默认）。</item>
///   <item><see cref="Plan"/>：写/执行类先问，**批准一次后本回合同类放行**（一次批一批）。</item>
///   <item><see cref="Build"/>：常规读写与执行自动放行，仅高危（出区写 / 危险命令）再问。</item>
///   <item><see cref="Yolo"/>：全部自动放行（界面有常驻横幅 + 一键收回）。</item>
/// </list>
/// </summary>
public enum ApprovalTier
{
    Ask,
    Plan,
    Build,
    Yolo,
}

/// <summary>
/// 档位 → 策略。这是「换档」的核心：改档位只需换这里的判定，主循环一行都不用动。
///
/// <para>
/// 注意：<b>策略永远只是「是否打扰用户」的判定</b>，不改变「每次工具调用都必须走
/// 完整事件链」这条铁律 —— 自动放行同样发 <c>ToolPreExecute</c> 与
/// <c>ToolCallRequested/Completed</c>，审计一点不少。
/// </para>
/// </summary>
public static class ApprovalTiers
{
    public static ApprovalTier Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "plan" => ApprovalTier.Plan,
        "build" => ApprovalTier.Build,
        "yolo" or "auto" => ApprovalTier.Yolo,
        _ => ApprovalTier.Ask,
    };

    public static string Id(ApprovalTier tier) => tier switch
    {
        ApprovalTier.Plan => "plan",
        ApprovalTier.Build => "build",
        ApprovalTier.Yolo => "yolo",
        _ => "ask",
    };

    public static string DisplayName(ApprovalTier tier) => tier switch
    {
        ApprovalTier.Plan => "先计划后执行",
        ApprovalTier.Build => "常规放行",
        ApprovalTier.Yolo => "全盘托管",
        _ => "逐项确认",
    };

    public static ApprovalDecision Decide(ApprovalTier tier, Contracts.ToolPreExecuteEvent e, string? workspaceRoot)
        => tier switch
        {
            ApprovalTier.Build => BuildDecide(e, workspaceRoot),
            ApprovalTier.Yolo => ApprovalDecision.Allow,
            // Ask 与 Plan 的逐项判定相同：Plan 的「批量」由 HostEventSink 的回合放行集处理。
            _ => DefaultApprovalPolicy.Decide(e),
        };

    /// <summary>Build：常规读/写（区内）/ 执行自动放行，仅高危再问。</summary>
    private static ApprovalDecision BuildDecide(Contracts.ToolPreExecuteEvent e, string? workspaceRoot)
    {
        switch (e.ToolName)
        {
            case "read_file" or "list_dir" or "web_search" or "web_fetch" or "ask_user"
                or "remember" or "forget" or "recall_memory" or "search_history"
                or "update_plan" or "update_notes" or "spawn_subagent"
                or "toolsets" or "use_toolset" or "tool_catalog"
                or "skill_validate" or "skill_extract" or "skill_scaffold" or "csv_to_json"
                or "grep_files" or "find_files" or "read_lines":
                return ApprovalDecision.Allow;

            case "write_file" or "edit_file" or "make_dir" or "copy_path" or "move_path":
                // 工程内的写自动放行；落到工作区之外仍然问。
                return InWorkspace(e, workspaceRoot) ? ApprovalDecision.Allow : ApprovalDecision.Ask;

            case "delete_path":
                // 删除风险高，Build 档也问（避免「批量删」被静默放行）。
                return ApprovalDecision.Ask;

            case "run_command":
                return IsRiskyCommand(e) ? ApprovalDecision.Ask : ApprovalDecision.Allow;

            default:
                // 未知工具保持谨慎 —— 白名单天然会漏，宁可多问一句。
                return ApprovalDecision.Ask;
        }
    }

    private static bool InWorkspace(Contracts.ToolPreExecuteEvent e, string? workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot)
            || !e.Arguments.TryGetValue("path", out var path)
            || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var root = Path.GetFullPath(workspaceRoot);
            var full = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(root, path));
            var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            return full.StartsWith(rootWithSep, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsRiskyCommand(Contracts.ToolPreExecuteEvent e)
    {
        if (!e.Arguments.TryGetValue("command", out var cmd) || string.IsNullOrWhiteSpace(cmd))
        {
            return false;
        }

        var c = cmd.ToLowerInvariant();
        string[] risky =
        [
            "rm -rf", "rm -r /", "del /", "format ", "mkfs", "shutdown", "reboot",
            "dd if=", "> /dev/sd", "chmod -r 777 /", "icacls", "net user", "reg delete", "takeown",
        ];
        return risky.Any(r => c.Contains(r, StringComparison.Ordinal));
    }
}
