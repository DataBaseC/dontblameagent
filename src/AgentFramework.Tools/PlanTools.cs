using AgentFramework.Contracts;

namespace AgentFramework.Tools;

/// <summary>
/// 派生子 Agent（G1）：把一块独立工作「委派出去」—— 子 Agent 有自己的上下文，
/// 不会污染父会话；父会话只收结果摘要。
///
/// <para>
/// 两种形态（v3.23）：
/// <list type="bullet">
///   <item><b>前台</b>（默认）：等它跑完再返回 —— 「委派后拿结果继续干」，最常见；</item>
///   <item><b>后台</b>（<c>run_in_background=true</c>）：立刻拿到句柄返回，
///   于是**几块活可以同时推**；随后用 <c>subagent</c> 工具查状态 / 取结果 / 追加指令 / 打断。</item>
/// </list>
/// </para>
/// </summary>
public sealed class SpawnSubAgentTool(
    Func<string?> currentSessionId,
    Func<ISubAgentControl?> control) : IToolWithRisk, ITool, IToolWithSchema
{
    public string Name => "spawn_subagent";

    public ToolRisk Risk => ToolRisk.Execute;

    public string Description =>
        "派生一个子 Agent 去执行一块独立任务。默认等待它完成后返回结果摘要；"
        + "传 run_in_background=true 则立刻返回句柄（多个子 Agent 可并行推进），"
        + "之后用 subagent 工具（status / result / wait / stop / message）管它。"
        + "适合：需要大量读取/检索但不想把中间过程塞进当前对话的工作；彼此独立的子任务。"
        + "子 Agent 看不到当前对话历史——任务描述必须自包含（目标、约束、要汇报什么）。";

    public string ParametersJsonSchema =>
        """
        {"type":"object","properties":{
        "task":{"type":"string","description":"交给子 Agent 的完整任务描述（自包含：目标 + 约束 + 汇报要求）"},
        "run_in_background":{"type":"boolean","description":"true = 立刻返回句柄、不阻塞当前回合（默认 false：等到跑完）"}},
        "required":["task"]}
        """;

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

        var engine = control();
        if (engine is null)
        {
            return ToolResult.Fail("子 Agent 管控面不可用（宿主未装配）");
        }

        var background = invocation.Arguments.TryGetValue("run_in_background", out var raw)
            && bool.TryParse(raw, out var parsed)
            && parsed;

        try
        {
            var info = await engine.SpawnAsync(parentSessionId, task, background, null, ct).ConfigureAwait(false);

            if (background)
            {
                var brief = task.Length > 120 ? task[..120] + "…" : task;
                return ToolResult.Ok(
                    $"[子 Agent {info.Id} 已后台派发] 状态=running\n任务：{brief}\n"
                    + "它跑它的，你可以继续干别的；需要结果时用 subagent 工具（result / wait）。");
            }

            return ToolResult.Ok($"[子 Agent {info.Id} · {StateLabel(info.State)}]\n{info.Summary}");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"派生子 Agent 失败：{ex.Message}");
        }
    }

    private static string StateLabel(string state) => state switch
    {
        SubAgentStates.Completed => "完成",
        SubAgentStates.Stopped => "被打断",
        SubAgentStates.Failed => "未完成",
        _ => state,
    };
}

/// <summary>
/// 子 Agent 管控工具（对齐 dsh 的 <c>dsh-tool-subagent-control</c>）：
/// 名单 / 状态 / 取结果 / 追加指令 / 打断 / 等待 —— 一个工具多动作（与 <c>job</c> 同一手法）。
/// </summary>
public sealed class SubAgentControlTool(
    Func<string?> currentSessionId,
    Func<ISubAgentControl?> control) : IToolWithRisk, ITool, IToolWithSchema
{
    public string Name => "subagent";

    public ToolRisk Risk => ToolRisk.Execute;

    /// <summary>wait 的秒数硬上限（模型越不过它）。</summary>
    private const int MaxWaitSeconds = 600;

    public string Description =>
        "管理由本会话派生的子 Agent。action："
        + "list（名单）/ status（状态）/ result（取最终摘要）/ wait（等它结束）/ "
        + "message（追加一条指令，它跑完当前回合就接着做）/ stop（打断）。";

    public string ParametersJsonSchema =>
        """
        {"type":"object","properties":{
        "action":{"type":"string","description":"list / status / result / wait / message / stop"},
        "id":{"type":"string","description":"子 Agent 句柄（list 的输出里给）"},
        "text":{"type":"string","description":"message 动作要追加的指令"},
        "interrupt":{"type":"boolean","description":"message 专用：true = 作废它当前这一轮、立刻改用这条指令重跑"},
        "timeout":{"type":"integer","description":"wait 的秒数上限（默认 300）"}},
        "required":["action"]}
        """;

    public async ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        var engine = control();
        if (engine is null)
        {
            return ToolResult.Fail("子 Agent 管控面不可用（宿主未装配）");
        }

        if (!invocation.Arguments.TryGetValue("action", out var action) || string.IsNullOrWhiteSpace(action))
        {
            return ToolResult.Fail("缺少参数 action");
        }

        var parentSessionId = currentSessionId() ?? string.Empty;
        var id = invocation.Arguments.TryGetValue("id", out var rawId) ? rawId?.Trim() : null;

        switch (action.Trim().ToLowerInvariant())
        {
            case "list":
            {
                var agents = engine.List(string.IsNullOrWhiteSpace(parentSessionId) ? null : parentSessionId);
                if (agents.Count == 0)
                {
                    return ToolResult.Ok("本会话没有派生过子 Agent。");
                }

                var lines = agents.Select(a =>
                    $"{a.Id} · {a.State} · 回合 {a.Rounds} · 已跑 {(DateTimeOffset.UtcNow - a.StartedAt).TotalSeconds:0} 秒"
                    + $"{(a.Background ? " · 后台" : string.Empty)}\n    任务：{Brief(a.Task)}");
                return ToolResult.Ok(string.Join("\n", lines));
            }

            case "status":
            case "result":
            case "wait":
            {
                if (string.IsNullOrWhiteSpace(id))
                {
                    return ToolResult.Fail($"action={action} 需要参数 id");
                }

                if (action.Trim().Equals("wait", StringComparison.OrdinalIgnoreCase))
                {
                    // 夹一个上限：模型误传一个巨大的值会把回合挂到天荒地老
                    //（与 run_command 的运维硬上限同一思路 —— 上限进代码、当前取值进参数）。
                    var timeout = invocation.Arguments.TryGetValue("timeout", out var rawTimeout)
                        && int.TryParse(rawTimeout, out var parsedTimeout)
                        ? Math.Clamp(parsedTimeout, 1, MaxWaitSeconds)
                        : 300;

                    return ToolResult.Ok(await engine.WaitAsync(id!, timeout, ct).ConfigureAwait(false));
                }

                var info = engine.Get(id!);
                return info is null
                    ? ToolResult.Fail($"没有这个子 Agent：{id}")
                    : ToolResult.Ok(Describe(info));
            }

            case "message":
            {
                if (string.IsNullOrWhiteSpace(id) || !invocation.Arguments.TryGetValue("text", out var text)
                    || string.IsNullOrWhiteSpace(text))
                {
                    return ToolResult.Fail("action=message 需要参数 id 与 text");
                }

                var interrupt = invocation.Arguments.TryGetValue("interrupt", out var rawInterrupt)
                    && bool.TryParse(rawInterrupt, out var parsedInterrupt)
                    && parsedInterrupt;

                return engine.Post(id!, text!, interrupt)
                    ? ToolResult.Ok(interrupt
                        ? $"已作废 {id} 当前这一轮，改用这条指令重跑。"
                        : $"已把指令交给 {id}（它跑完当前回合就接着做）。")
                    : ToolResult.Fail($"{id} 不在运行中（或不存在）—— 已结束的子 Agent 不再收指令。");
            }

            case "stop":
            {
                if (string.IsNullOrWhiteSpace(id))
                {
                    return ToolResult.Fail("action=stop 需要参数 id");
                }

                return engine.Stop(id!)
                    ? ToolResult.Ok($"已打断 {id}。")
                    : ToolResult.Fail($"没有这个子 Agent：{id}");
            }

            default:
                return ToolResult.Fail($"未知 action：{action}（可用：list / status / result / wait / message / stop）");
        }
    }

    private static string Describe(SubAgentInfo info)
    {
        var head = $"[子 Agent {info.Id}] 状态={info.State} · 回合={info.Rounds} · "
            + $"已跑 {(DateTimeOffset.UtcNow - info.StartedAt).TotalSeconds:0} 秒";

        return info.Summary is { Length: > 0 } summary
            ? $"{head}\n{summary}"
            : $"{head}\n任务：{Brief(info.Task)}";
    }

    private static string Brief(string text) => text.Length > 120 ? text[..120] + "…" : text;
}

/// <summary>
/// 维护执行计划（G3）：模型创建计划、逐步骤更新状态。
/// 计划是事件投影 —— 不另存「计划表」，与任务状态同机制（4.2 不变式）。
/// </summary>
public sealed class UpdatePlanTool(Func<string?> currentSessionId, Func<SessionEvent, ValueTask> mutate) : IToolWithRisk, ITool, IToolWithSchema
{
    public string Name => "update_plan";

    public ToolRisk Risk => ToolRisk.Write;

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
