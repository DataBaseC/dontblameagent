using System.Text;
using AgentFramework.Contracts;

namespace AgentFramework.Data;

/// <summary>
/// 任务卡 —— 长任务的「工作记忆锚点」。
///
/// 为什么需要它：计划只活在模型脑内时，会漂到注意力的中部（最弱区），
/// 而且**被裁剪时第一个消失**。于是长任务跑到后面，模型就忘了最初要干什么。
///
/// 做法：从事件流**投影**出一份**恒定大小**的状态卡，每轮重算，
/// 并放在上下文**末尾**（近期注意力区）。
///
/// 恒定大小是关键：它是"摘要"，不是"账本"，不随会话增长。
/// </summary>
public static class TaskCardBuilder
{
    public static string Build(IEnumerable<SessionEvent> events, ContextOptions? options = null)
        => Build(SessionProjector.Project(events), options);

    public static string Build(SessionState state, ContextOptions? options = null)
    {
        var opt = options ?? new ContextOptions();
        var sb = new StringBuilder();

        sb.AppendLine("【任务卡】本轮工作状态（由会话事件投影得到，恒定大小）");

        var firstUser = state.Messages.FirstOrDefault(m => m.Role == "user");
        if (firstUser is not null)
        {
            sb.Append("最初目标：").AppendLine(Truncate(OneLine(firstUser.Text), 180));
        }

        // 最近一条实质性用户消息（排除纯指令/极短输入），反映当前方向
        var lastSubstantive = state.Messages
            .Where(m => m.Role == "user" && m.Text.Length > 10)
            .LastOrDefault();
        if (lastSubstantive is not null && lastSubstantive != firstUser)
        {
            sb.Append("当前方向：").AppendLine(Truncate(OneLine(lastSubstantive.Text), 180));
        }

        if (!string.IsNullOrWhiteSpace(state.Title))
        {
            sb.Append("会话标题：").AppendLine(Truncate(OneLine(state.Title), 80));
        }

        // G3：活跃计划置顶显示 —— 计划是模型的工作记忆锚点，
        // 每轮都在近期注意力区复述一遍（Manus recitation 同款机制）。
        var activePlan = state.Plans.Values
            .Where(steps => steps.Any(p => p.Status != "done" && p.Status != "dropped"))
            .OrderByDescending(p => p.Max(x => x.Index))
            .FirstOrDefault();
        if (activePlan is not null)
        {
            var marks = activePlan
                .OrderBy(p => p.Index)
                .Select(p => p.Status switch
                {
                    "done" => $"[{p.Index + 1}✓]",
                    "dropped" => $"[{p.Index + 1}×]",
                    _ => $"[{p.Index + 1} ]",
                });
            sb.Append("计划：").AppendLine(string.Join(' ', marks));

            var nextStep = activePlan.OrderBy(p => p.Index).FirstOrDefault(p => p.Status == "pending");
            if (nextStep is not null)
            {
                sb.Append("下一步：").AppendLine(Truncate(OneLine(nextStep.Text), 120));
            }
        }

        var tasks = state.Tasks.Values.ToList();
        if (tasks.Count > 0)
        {
            var done = tasks.Count(t => t.Status == AgentTaskStatus.Done);
            sb.Append("进度：").Append(done).Append('/').Append(tasks.Count).AppendLine(" 项已完成");

            AppendItems(sb, "进行中", tasks.Where(t => t.Status == AgentTaskStatus.InProgress), opt.TaskCardMaxItems);
            AppendItems(sb, "待办", tasks.Where(t => t.Status == AgentTaskStatus.Todo), opt.TaskCardMaxItems);
            AppendItems(sb, "失败", tasks.Where(t => t.Status == AgentTaskStatus.Failed), 3);
        }

        if (state.ToolCalls.Count > 0)
        {
            var recent = state.ToolCalls.TakeLast(6).Select(t => t.ToolName);
            sb.Append("最近调用：").AppendLine(string.Join(" → ", recent));

            var lastFailure = state.ToolCalls.LastOrDefault(t => t.Success == false);
            if (lastFailure is not null)
            {
                sb.Append("最近一次失败：").Append(lastFailure.ToolName).Append(" — ")
                  .AppendLine(Truncate(OneLine(lastFailure.Output ?? "无输出"), 140));
            }
        }

        sb.Append("对话轮次：第 ").Append(state.Messages.Count(m => m.Role == "user")).AppendLine(" 轮");

        var text = sb.ToString().TrimEnd();

        // 恒定大小：超了就砍 —— 这张卡宁可少说，也不能自己长成新的上下文负担
        return text.Length <= opt.TaskCardMaxChars
            ? text
            : text[..opt.TaskCardMaxChars] + "…";
    }

    private static void AppendItems(StringBuilder sb, string label, IEnumerable<TaskItem> items, int max)
    {
        var titles = items.Take(max).Select(t => Truncate(OneLine(t.Title), 60)).ToList();
        if (titles.Count == 0)
        {
            return;
        }

        sb.Append(label).Append('：').AppendLine(string.Join("、", titles));
    }

    /// <summary>压掉换行与多余空白 —— 任务卡每一行都必须信息密度高。</summary>
    private static string OneLine(string text)
        => string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";
}
