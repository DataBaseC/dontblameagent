using AgentFramework.Contracts;

namespace AgentFramework.Host;

/// <summary>
/// 计划模式的权限门（MiMo 的 plan 档）：<b>只读规划</b> —— 禁写禁执行，
/// 唯一例外是写 <c>plans/*.md</c>（计划文件是本模式的正业）。
///
/// <para>
/// 为什么要在权限层焊死而不是靠提示词：提示词说「别改文件」挡不住模型手滑，
/// 而计划模式的承诺就是「这个会话绝不会动你的代码」—— 承诺必须由机制保证。
/// </para>
/// <para>
/// 判定顺序（先例外后白名单，最后兜底拒绝）：
///   1) <c>write_file / edit_file</c> 且目标在 <c>plans/</c> 下 → 放行（唯一可写）；
///   2) 只读调研工具（读/列/搜/检索/提问/规划/子代理）→ 放行；
///   3) 其余一切（写文件、执行命令、删改、记忆写入、插件操作…）→ 拒绝，
///      拒绝理由里写明出路（切编程/工作模式）。
/// 非计划模式的会话原样走内层策略 —— 这道门对它们零影响。
/// </para>
/// </summary>
public static class PlanModePolicy
{
    /// <summary>计划模式下放行的只读调研/规划工具。</summary>
    private static readonly HashSet<string> ReadOnlyTools = new(StringComparer.Ordinal)
    {
        // 读与检索
        "read_file", "read_lines", "read_image", "read_document", "list_dir",
        "find_files", "grep_files", "search_history",
        "web_search", "web_fetch",
        "recall_memory", "tool_catalog", "toolsets", "plugin_list",
        // 规划本身
        "update_plan", "spawn_subagent", "use_toolset",
        // 提问没有副作用
        "ask_user",
    };

    /// <summary>计划模式下允许写的工具（目标还必须落在 plans/ 下）。</summary>
    private static readonly HashSet<string> PlanFileWriters = new(StringComparer.Ordinal)
    {
        "write_file", "edit_file",
    };

    public const string RejectionReason =
        "计划模式为只读规划：只允许检索/阅读与写 plans/*.md；"
        + "要修改文件或执行命令，请让用户切换到编程模式或工作模式";

    public static ApprovalDecision Decide(
        string? modeId,
        ToolPreExecuteEvent request,
        Func<ToolPreExecuteEvent, ApprovalDecision> inner,
        string? workspaceRoot)
    {
        if (!AgentModes.Resolve(modeId).ReadOnly)
        {
            return inner(request);
        }

        // 1) 唯一可写：plans/ 下的计划文件
        if (PlanFileWriters.Contains(request.ToolName) && IsPlanFile(request, workspaceRoot))
        {
            return ApprovalDecision.Allow;
        }

        // 2) 只读调研 / 规划工具
        if (ReadOnlyTools.Contains(request.ToolName))
        {
            return ApprovalDecision.Allow;
        }

        // 3) 其余一律拒绝，并写明出路
        request.RejectReason = RejectionReason;
        return ApprovalDecision.Deny;
    }

    /// <summary>
    /// 目标是否落在 <c>plans/</c> 目录下。相对路径按「plans/…」判定；
    /// 绝对路径必须真的落在 <c>&lt;workspace&gt;/plans/</c> 之内。带 <c>..</c> 的一律不认。
    /// </summary>
    private static bool IsPlanFile(ToolPreExecuteEvent request, string? workspaceRoot)
    {
        if (!request.Arguments.TryGetValue("path", out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var path = raw!.Trim().Replace('\\', '/');

        if (path.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        if (path.StartsWith("plans/", StringComparison.Ordinal))
        {
            return true;
        }

        if (Path.IsPathFullyQualified(path) && !string.IsNullOrWhiteSpace(workspaceRoot))
        {
            try
            {
                var plansRoot = Path.GetFullPath(Path.Combine(workspaceRoot!, "plans"));
                var full = Path.GetFullPath(path);
                return full.StartsWith(plansRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(full, plansRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        return false;
    }
}
