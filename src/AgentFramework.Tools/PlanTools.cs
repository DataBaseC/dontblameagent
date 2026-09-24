using AgentFramework.Contracts;

namespace AgentFramework.Tools;

/// <summary>
/// 派生并等待一个子 Agent（G1）。
///
/// 给模型的语义：任务太大/太杂时，把一块独立工作「委派出去」——
/// 子 Agent 有自己的上下文，不会污染父会话；父会话只收结果摘要。
///
/// 为什么是同步等待而不是 fire-and-forget：
///   第一版先做「委派后拿结果继续干」的形态（最常见）；
///   并行派发等真实需求出现再加（工具契约可加不可改）。
/// </summary>
public sealed class SpawnSubAgentTool(
    Func<string?> currentSessionId,
    Func<string, string, CancellationToken, Task<(string ChildId, bool Success, string Summary)>> runner) : ITool, IToolWithSchema
{
    public string Name => "spawn_subagent";

    public string Description =>
        "派生一个子 Agent 去执行一块独立任务，等待它完成后返回结果摘要。"
        + "适合：需要大量读取/检索但不想把中间过程塞进当前对话的工作；相对独立的子任务。"
        + "子 Agent 看不到当前对话历史——任务描述必须自包含（目标、约束、要汇报什么）。";

    public string ParametersJsonSchema =>
        """{"type":"object","properties":{"task":{"type":"string","description":"交给子 Agent 的完整任务描述（自包含：目标 + 约束 + 汇报要求）"}},"required":["task"]}""";

    public async ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        if (!invocation.Arguments.TryGetValue("task", out var task) || string.IsNullOrWhiteSpace(task))
        {
            return ToolResult.Fail("缺少参数 task");
        }

        var parentSessionId = currentSessionId();
        if (string.IsNullOrWhiteSpace(parentSessionId))
        {
            return ToolResult.Fail("无法确定当前会话");
        }

        try
        {
            var (childId, success, summary) = await runner(parentSessionId, task, ct).ConfigureAwait(false);
            var header = $"[子 Agent {childId} · {(success ? "完成" : "未完成")}]\n";
            return ToolResult.Ok(header + summary);
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"派生子 Agent 失败：{ex.Message}");
        }
    }
}

/// <summary>
/// 维护执行计划（G3）：模型创建计划、逐步骤更新状态。
/// 计划是事件投影 —— 不另存「计划表」，与任务状态同机制（4.2 不变式）。
/// </summary>
public sealed class UpdatePlanTool(Func<string?> currentSessionId, Func<SessionEvent, ValueTask> mutate) : ITool, IToolWithSchema
{
    public string Name => "update_plan";

    public string Description =>
        "创建或更新你的执行计划。开始一个多步骤任务时先创建计划；每完成一步就更新对应步骤状态。"
        + "计划会显示给用户，也作为你自己的工作记忆锚点（每轮都会在任务卡里复述）。";

    public string ParametersJsonSchema =>
        """{"type":"object","properties":{"action":{"type":"string","enum":["create","update"],"description":"create=新建计划（steps 必填）；update=更新某一步状态"},"steps":{"type":"array","items":{"type":"string"},"description":"action=create 时：步骤描述列表（有序）"},"index":{"type":"integer","description":"action=update 时：步骤下标（从 0 起）"},"status":{"type":"string","enum":["pending","done","dropped"],"description":"action=update 时：新状态"}},"required":["action"]}""";

    public async ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        var sessionId = currentSessionId();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return ToolResult.Fail("无法确定当前会话");
        }

        var action = invocation.Arguments.TryGetValue("action", out var a) ? a : null;
        // 同秒内连续 create 两个计划会撞 id（都是 plan-HHmmss），
        // 加 4 位随机后缀消碰撞；与 fork-/sub- 的 id 形态一致。
        var planId = invocation.Arguments.TryGetValue("planId", out var pid) && !string.IsNullOrWhiteSpace(pid)
            ? pid
            : $"plan-{DateTime.UtcNow:HHmmss}-{Guid.NewGuid().ToString("N")[..4]}";

        if (action == "create")
        {
            if (!invocation.Arguments.TryGetValue("steps", out var stepsRaw) || string.IsNullOrWhiteSpace(stepsRaw))
            {
                return ToolResult.Fail("action=create 需要 steps（步骤列表）");
            }

            // steps 允许 JSON 数组字符串或换行分隔——模型给的形态可能不同，都接住
            var steps = ParseSteps(stepsRaw);
            if (steps.Count == 0)
            {
                return ToolResult.Fail("steps 解析为空");
            }

            await mutate(new PlanCreatedEvent
            {
                SessionId = sessionId,
                PlanId = planId,
                Steps = steps,
            }).ConfigureAwait(false);

            return ToolResult.Ok($"计划 {planId} 已创建（{steps.Count} 步）：\n" +
                string.Join('\n', steps.Select((s, i) => $"{i + 1}. {s}")));
        }

        if (action == "update")
        {
            if (!invocation.Arguments.TryGetValue("index", out var idxRaw) || !int.TryParse(idxRaw, out var index))
            {
                return ToolResult.Fail("action=update 需要 index（步骤下标，从 0 起）");
            }

            var status = invocation.Arguments.TryGetValue("status", out var st) ? st : "done";
            if (status is not ("pending" or "done" or "dropped"))
            {
                return ToolResult.Fail($"status 只能是 pending / done / dropped，收到：{status}");
            }

            await mutate(new PlanStepUpdatedEvent
            {
                SessionId = sessionId,
                PlanId = planId,
                Index = index,
                Status = status,
            }).ConfigureAwait(false);

            return ToolResult.Ok($"计划 {planId} 第 {index + 1} 步 → {status}");
        }

        return ToolResult.Fail($"action 只能是 create / update，收到：{action ?? "(空)"}");
    }

    public static List<string> ParseSteps(string raw)
    {
        var steps = new List<string>();

        // 形态一：JSON 数组
        if (raw.TrimStart().StartsWith('['))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(raw);
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        if (item.ValueKind == System.Text.Json.JsonValueKind.String && item.GetString() is { Length: > 0 } v)
                        {
                            steps.Add(v);
                        }
                    }

                    return steps;
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // 落到形态二
            }
        }

        // 形态二：换行/分号分隔
        foreach (var line in raw.Split(['\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Length > 0)
            {
                steps.Add(line.TrimStart('-', '*', ' ', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.', '、').Trim());
            }
        }

        return steps.Where(x => x.Length > 0).ToList();
    }
}
