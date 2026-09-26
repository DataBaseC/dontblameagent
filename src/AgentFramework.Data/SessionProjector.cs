using AgentFramework.Contracts;

namespace AgentFramework.Data;

public sealed class TaskItem
{
    public required string TaskId { get; init; }

    public required string Title { get; init; }

    public string Status { get; set; } = AgentTaskStatus.Todo;

    public string? LastReason { get; set; }
}

public sealed class ChatMessage
{
    public required string Role { get; init; }

    public required string Text { get; init; }

    public long Seq { get; init; }
}

/// <summary>计划步骤（G3）：投影产物，Index 对应 PlanCreatedEvent.Steps 的位置。</summary>
public sealed class PlanStep
{
    public required string PlanId { get; init; }

    public required int Index { get; init; }

    public required string Text { get; init; }

    public string Status { get; set; } = "pending";
}

public sealed class ToolCallRecord
{
    public required string CallId { get; init; }

    public required string ToolName { get; init; }

    public bool? Success { get; set; }

    public string? Output { get; set; }
}

/// <summary>
/// 会话状态 —— <b>完全由事件投影得到</b>，自身不承载任何独立真相。
/// 这意味着：删掉它、重启进程、换个地方重算，结果都一样。
/// 分叉、重放、重启还原之所以能白拿，就是因为它不存东西。
/// </summary>
public sealed class SessionState
{
    public string? SessionId { get; set; }

    public string? ProjectId { get; set; }

    public string Title { get; set; } = "";

    public string? ParentSessionId { get; set; }

    public long? ForkFromSeq { get; set; }

    public List<ChatMessage> Messages { get; set; } = [];

    public Dictionary<string, TaskItem> Tasks { get; set; } = [];

    public List<ToolCallRecord> ToolCalls { get; set; } = [];

    /// <summary>活跃计划（G3）：planId → 步骤状态列表。null 状态 = pending。</summary>
    public Dictionary<string, List<PlanStep>> Plans { get; set; } = new(StringComparer.Ordinal);

    public long LastSeq { get; set; }

    public int EventCount { get; set; }

    /// <summary>投影过程中发现的异常（不静默吞掉，便于暴露数据问题）。</summary>
    public List<string> Anomalies { get; set; } = [];
}

/// <summary>把事件流投影成会话状态。</summary>
public static class SessionProjector
{
    /// <summary>
    /// 把事件流投影成会话状态。
    /// <paramref name="seed"/> 非空时从已有状态继续 ——
    /// 这是「快照 + 增量重放」的基础：不必每次从头重放整条日志。
    /// </summary>
    public static SessionState Project(IEnumerable<SessionEvent> events, SessionState? seed = null)
    {
        var state = seed ?? new SessionState();

        // 工具调用的**侧表**（P3b）：结果事件要按 CallId 回填对应的请求记录。
        // 原先用 FirstOrDefault 在列表里线性找 —— 事件一多就是 O(n²)，
        // 几千事件的长会话里这一步会明显拖慢**每一次**投影。
        // 侧表建一次，之后每次回填都是 O(1)。
        // 增量投影（seed 非空）时把已有记录先填进去，语义与老实现完全一致。
        var toolCallsById = new Dictionary<string, ToolCallRecord>(StringComparer.Ordinal);
        foreach (var existing in state.ToolCalls)
        {
            toolCallsById[existing.CallId] = existing;
        }

        foreach (var e in events)
        {
            state.EventCount++;
            state.LastSeq = e.Seq;

            switch (e)
            {
                case SessionCreatedEvent createdEvent:
                    state.SessionId = createdEvent.SessionId;
                    state.ProjectId = createdEvent.ProjectId;
                    state.Title = createdEvent.Title;
                    state.ParentSessionId = createdEvent.ParentSessionId;
                    state.ForkFromSeq = createdEvent.ForkFromSeq;
                    break;

                case UserMessageEvent user:
                    state.Messages.Add(new ChatMessage { Role = "user", Text = user.Text, Seq = user.Seq });
                    break;

                case AssistantMessageEvent assistant:
                    state.Messages.Add(new ChatMessage { Role = "assistant", Text = assistant.Text, Seq = assistant.Seq });
                    break;

                case TaskCreatedEvent taskCreated:
                    state.Tasks[taskCreated.TaskId] = new TaskItem
                    {
                        TaskId = taskCreated.TaskId,
                        Title = taskCreated.Title,
                    };
                    break;

                case TaskStatusChangedEvent statusChanged:
                    if (state.Tasks.TryGetValue(statusChanged.TaskId, out var item))
                    {
                        item.Status = statusChanged.Status;
                        item.LastReason = statusChanged.Reason;
                    }
                    else
                    {
                        state.Anomalies.Add($"seq={statusChanged.Seq}: 任务 {statusChanged.TaskId} 状态变更，但该任务尚未创建");
                    }

                    break;

                case PlanCreatedEvent planCreated:
                    state.Plans[planCreated.PlanId] = planCreated.Steps
                        .Select((text, i) => new PlanStep { PlanId = planCreated.PlanId, Index = i, Text = text })
                        .ToList();
                    break;

                case PlanStepUpdatedEvent stepUpdated when state.Plans.TryGetValue(stepUpdated.PlanId, out var planSteps):
                    if (stepUpdated.Index >= 0 && stepUpdated.Index < planSteps.Count)
                    {
                        planSteps[stepUpdated.Index].Status = stepUpdated.Status;
                    }
                    else
                    {
                        state.Anomalies.Add($"seq={stepUpdated.Seq}: 计划 {stepUpdated.PlanId} 步骤下标越界 {stepUpdated.Index}");
                    }

                    break;

                case ToolCallRequestedEvent requested:
                    var created = new ToolCallRecord
                    {
                        CallId = requested.CallId,
                        ToolName = requested.ToolName,
                    };
                    state.ToolCalls.Add(created);
                    toolCallsById[created.CallId] = created;
                    break;

                case ToolCallCompletedEvent completed:
                    if (toolCallsById.TryGetValue(completed.CallId, out var record))
                    {
                        record.Success = completed.Success;
                        record.Output = completed.Output;
                        // 失败时 Error 字段才是错误正文；Output 可能为空。
                        // 投影进 Output 以便任务卡等消费方能直接看到失败原因。
                        if (!completed.Success && string.IsNullOrWhiteSpace(record.Output) && !string.IsNullOrWhiteSpace(completed.Error))
                        {
                            record.Output = completed.Error;
                        }
                    }
                    else
                    {
                        state.Anomalies.Add($"seq={completed.Seq}: 工具结果 {completed.CallId} 找不到对应的请求");
                    }

                    break;
            }
        }

        return state;
    }

    /// <summary>从日志文件直接投影。</summary>
    public static SessionState ProjectFile(string path) => Project(JsonlEventLog.Read(path));
}
