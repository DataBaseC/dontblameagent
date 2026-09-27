using System.Text.Json;
using AgentFramework.Agent;
using AgentFramework.Contracts;
using AgentFramework.Data;
using AgentFramework.Index;
using AgentFramework.Kernel;
using AgentFramework.Llm;
using AgentFramework.Tools;

namespace AgentFramework.Host;

/// <summary>
/// 把事件写进 JSONL，并把审批事件派发到内核事件总线。
///
/// 审批分三层，顺序固定：
///   1. 内核 / 插件的订阅者（可以置 Cancelled）
///   2. 宿主的分级策略（Allow / Deny / Ask）
///   3. Ask 时交给界面（没有界面则降级为拒绝）
/// </summary>
public sealed class HostEventSink(
    JsonlEventLog log,
    PluginHost plugins,
    Func<ToolPreExecuteEvent, ApprovalDecision> approvalPolicy,
    Func<IUserInteraction> interactionProvider,
    Action<SessionEvent>? onEvent = null,
    ISessionIndex? index = null,
    string? sessionId = null,
    Func<string, bool>? isToolAllowed = null,
    Func<ApprovalTier>? approvalTier = null) : IAgentEventSink
{
    // 并发安全：RequestApprovalAsync 可能被多个会话/子 agent 同时调用，
    // 无锁 List.Add 会丢条目甚至把内部数组写坏。用 ConcurrentQueue 入队，
    // 读侧每次取快照（诊断面只读，不需要跨调用的严格一致视图）。
    private readonly System.Collections.Concurrent.ConcurrentQueue<ApprovalRecord> _approvals = new();

    // 任务 5 · Plan 档：本回合内「已批准过的工具」——批准一次，本回合同类不再打扰。
    // 由回合开始（UserMessageEvent）清空；用并发字典是因为多会话可能并发跑。
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _turnApproved =
        new(StringComparer.Ordinal);

    public IReadOnlyList<ApprovalRecord> Approvals => [.. _approvals];

    public async ValueTask EmitAsync(SessionEvent sessionEvent, CancellationToken ct)
    {
        // 任务 5 · Plan 档：一个回合（以用户消息为界）开始时清空「本回合放行集」，
        // 于是「一次批准」只覆盖本回合，下一回合重新问 —— 「随时能收紧」。
        if (sessionEvent is UserMessageEvent && approvalTier?.Invoke() == ApprovalTier.Plan)
        {
            _turnApproved.Clear();
        }

        // 真相源先落盘 —— 索引只是派生物，顺序不能反（反了就会出现"索引里有、日志里没有"）
        log.Append(sessionEvent);
        onEvent?.Invoke(sessionEvent);

        if (index is { IsAvailable: true } && sessionId is not null)
        {
            // 索引写失败绝不冒泡：它只影响"方便程度"，不影响"能不能干活"
            try
            {
                await index.IndexAsync(sessionId, sessionEvent, ct).ConfigureAwait(false);
            }
            catch
            {
                // 忽略
            }
        }
    }

    public async ValueTask RequestApprovalAsync(ToolPreExecuteEvent toolPreExecuteEvent, CancellationToken ct)
    {
        // 第 1 层：内核 / 插件订阅者表态
        await plugins.EmitAsync(toolPreExecuteEvent, ct).ConfigureAwait(false);

        // 第 2、3 层：宿主策略
        if (!toolPreExecuteEvent.Cancelled)
        {
            var decision = approvalPolicy(toolPreExecuteEvent);

            // 任务 5 · Plan 档：本回合已经批准过这个工具 → 直接放行
            //（「一次批准覆盖 N 个同类写操作」）。同样放在策略之后：策略的 Deny 翻不动。
            if (decision == ApprovalDecision.Ask
                && approvalTier?.Invoke() == ApprovalTier.Plan
                && _turnApproved.ContainsKey(toolPreExecuteEvent.ToolName))
            {
                decision = ApprovalDecision.Allow;
            }

            // ★ P6：本会话已经放行过这个工具 → 不再打扰。
            //   放在**策略之后**：策略永远保留最终否决权，
            //   放行集只能把 Ask 变成 Allow，翻不动 Deny。
            if (decision == ApprovalDecision.Ask
                && (isToolAllowed?.Invoke(toolPreExecuteEvent.ToolName) ?? false))
            {
                decision = ApprovalDecision.Allow;
            }

            // 任务 5 · Plan 档：把本回合已放行的工具记下来（供本回合后续同类调用短路）。
            if (decision == ApprovalDecision.Allow && approvalTier?.Invoke() == ApprovalTier.Plan)
            {
                _turnApproved.TryAdd(toolPreExecuteEvent.ToolName, 0);
            }

            switch (decision)
            {
                case ApprovalDecision.Allow:
                    break;

                case ApprovalDecision.Deny:
                    Deny(toolPreExecuteEvent, "策略拒绝");
                    break;

                case ApprovalDecision.Ask:
                {
                    var interaction = interactionProvider();
                    if (!interaction.CanInteract)
                    {
                        // 拿不准又没人可问 → 拒绝。安全侧的默认必须保守。
                        Deny(toolPreExecuteEvent, "无界面可确认，默认拒绝");
                    }
                    else
                    {
                        // 回合被停止时 ConfirmAsync 会以取消结束 —— 不是「用户拒绝」。
                        bool allowed;
                        try
                        {
                            allowed = await interaction.ConfirmAsync(toolPreExecuteEvent, ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            throw;
                        }

                        if (!allowed)
                        {
                            Deny(toolPreExecuteEvent, ct.IsCancellationRequested ? "回合已取消" : "用户拒绝");
                        }
                        else if (approvalTier?.Invoke() == ApprovalTier.Plan)
                        {
                            // 任务 5 · Plan：批准即本回合放行同类（一次批一批）
                            _turnApproved.TryAdd(toolPreExecuteEvent.ToolName, 0);
                        }
                    }

                    break;
                }
            }
        }

        _approvals.Enqueue(new ApprovalRecord(
            toolPreExecuteEvent.ToolName,
            toolPreExecuteEvent.Cancelled,
            toolPreExecuteEvent.RejectReason));
    }

    private static void Deny(ToolPreExecuteEvent toolPreExecuteEvent, string reason)
    {
        toolPreExecuteEvent.Cancelled = true;
        toolPreExecuteEvent.RejectReason ??= reason;
    }
}
