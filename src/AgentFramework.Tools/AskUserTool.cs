using System.Text.Json;
using AgentFramework.Contracts;

namespace AgentFramework.Tools;

/// <summary>
/// 问用户。给模型的是一条**正当出口**：信息不足、或有多个合理做法时，问，而不是猜。
///
/// 为什么值得一个独立工具：模型最容易犯的错之一，是把「猜」包装成「答」。
/// 与其在提示词里反复叮嘱「不要臆测」，不如给它一个便宜、明确、有反馈的问法。
///
/// 没有界面时**如实说问不出去**，并把「该自行判断 + 标注不确定」一并告诉它 ——
/// 假装用户答过，是这条缝上最不能犯的错。
/// </summary>
public sealed class AskUserTool(Func<IUserInteraction> interactionProvider) : ITool, IToolWithSchema
{
    public string Name => "ask_user";

    public string Description =>
        "当信息不足、或存在多个合理做法需要主人拍板时，直接向用户提问并等待回答。"
        + "宁可问一句，也不要把猜测当事实写进结果。";

    public string ParametersJsonSchema =>
        """{"type":"object","properties":{"question":{"type":"string","description":"要问的问题（写具体）"},"options":{"type":"array","items":{"type":"string"},"description":"可选项（可选）：给了会渲染成选择项，用户仍可自由作答"},"context":{"type":"string","description":"为什么要问（可选，给用户一点上下文）"}},"required":["question"]}""";

    public async ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        if (!invocation.Arguments.TryGetValue("question", out var question) || string.IsNullOrWhiteSpace(question))
        {
            return ToolResult.Fail("缺少参数 question");
        }

        var interaction = interactionProvider();

        // 用 CanAsk 而不是 CanInteract：只懂审批的界面（ApprovalPromptInteraction）能弹
        // 确认卡、却问不出问题 —— 对它必须如实说「问不出去」，而不是退化成一句误导的
        // 「用户没有回答」。这正是不让「审批是交互的一个特例」误导提问这条缝的地方。
        if (interaction is null || !interaction.CanAsk)
        {
            return ToolResult.Fail(
                "当前没有可交互的界面，问不出去。请基于已有信息自行判断，"
                + "并在结论里明确标出哪些地方是你不确定的。");
        }

        var request = new AskUserRequest
        {
            Question = question.Trim(),
            Options = ParseOptions(invocation.Arguments.GetValueOrDefault("options")),
            Context = invocation.Arguments.GetValueOrDefault("context"),
        };

        var answer = await interaction.AskAsync(request, ct).ConfigureAwait(false);

        if (answer.Answered)
        {
            return ToolResult.Ok(answer.Text ?? "（用户没有填写内容）");
        }

        // 超时与「用户主动跳过」分开说 —— 失败文案必须可诊断（任务 7）。
        return answer.IsTimeout
            ? ToolResult.Fail("等待回答超时：用户没有在时限内答复。请自行决断，并在结论里标注这一点。")
            : ToolResult.Fail("用户没有回答（可能直接跳过了提问）。请自行决断，并在结论里标注这一点。");
    }

    /// <summary>
    /// options 可能以 JSON 数组传来，也可能是被上游字符串化的样子。
    /// 解析不出就当成没给 —— 问题本身照样问得出去，不该因为选项格式把整次提问废掉。
    /// </summary>
    private static IReadOnlyList<string>? ParseOptions(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            var items = JsonSerializer.Deserialize<List<string>>(raw);
            return items is { Count: > 0 } ? items : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
