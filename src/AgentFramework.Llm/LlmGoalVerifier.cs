using System.Text;
using AgentFramework.Contracts;

namespace AgentFramework.Llm;

public sealed class LlmGoalVerifierOptions
{
    public string Model { get; set; } = "auto";

    public string SystemPrompt { get; set; } =
        "你是一个严格但公正的目标核验员。你不参与实际工作，只根据证据判断「目标是否达成」。"
        + "你没有认同偏差：不要因为答复看起来自信就放行，也不要吹毛求疵。";

    /// <summary>喂给验证者的对话近况上限（字符）——它是旁路小模型，不需要整段历史。</summary>
    public int MaxContextChars { get; set; } = 12_000;

    public double Temperature { get; set; } = 0.0;
}

/// <summary>
/// 用模型实现的 Goal 终止验证器（MiMo Code 的 Goal 机制 / Claude Code 的 /goal）。
///
/// <para>
/// 调用形态：旁路模型调用，不进主对话；输入是「停止条件 + 用户请求 + 对话近况 + 想收尾的答复」，
/// 输出严格三行格式（VERDICT / EVIDENCE / GAP），解析容错：
/// 认不出裁决时按 **Met 放行**（fail-open）——验证器永远不许卡住收尾。
/// </para>
/// </summary>
public sealed class LlmGoalVerifier : IGoalVerifier
{
    private readonly ILlmClient _client;
    private readonly LlmGoalVerifierOptions _options;

    public LlmGoalVerifier(ILlmClient client, LlmGoalVerifierOptions? options = null)
    {
        _client = client;
        _options = options ?? new LlmGoalVerifierOptions();
    }

    public string Name => $"goal-verifier({_client.Name})";

    public async ValueTask<GoalVerification> VerifyAsync(
        string goal,
        string userRequest,
        IReadOnlyList<LlmMessage> context,
        string finalText,
        CancellationToken ct = default)
    {
        var prompt = BuildPrompt(goal, userRequest, TruncateContext(context), finalText);
        var first = await AskAsync(prompt, ct).ConfigureAwait(false);

        if (HasVerdictLine(first))
        {
            return Parse(first);
        }

        // 第一次没读出裁决 —— 这正是原先 fail-open 生效的地方。改法：先给模型**一次纠正机会**
        // （格式歪掉多半是被自由表述带跑），带上强约束再问一次。
        var retryPrompt = prompt
            + "\n\n（上一次输出无法解析。请严格只输出三行，不要任何其它文字：\n"
            + "VERDICT: MET 或 NOT_MET 或 IMPOSSIBLE\nEVIDENCE: …\nGAP: … 或 -）";
        var second = await AskAsync(retryPrompt, ct).ConfigureAwait(false);

        // 两次都读不出裁决 → 放行。验证器不许卡死收尾（fail-open 保持不变，只是多给一次机会）。
        return HasVerdictLine(second) ? Parse(second) : GoalVerification.Met();
    }

    /// <summary>旁路提问一次并收集输出（截断到 2000 字符，防止跑飞）。</summary>
    private async Task<string> AskAsync(string prompt, CancellationToken ct)
    {
        var request = new LlmRequest
        {
            Model = _options.Model,
            SystemPrompt = _options.SystemPrompt,
            Messages = [new LlmMessage { Role = LlmRole.User, Content = prompt }],
            Temperature = _options.Temperature,
        };

        var builder = new StringBuilder();

        await foreach (var chunk in _client.StreamAsync(request, ct).ConfigureAwait(false))
        {
            if (chunk is LlmStreamChunk.TextDelta delta)
            {
                builder.Append(delta.Text);
                if (builder.Length > 2_000)
                {
                    break;
                }
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// 输出里是否存在一行合法裁决 —— 用来区分「解析失败（该重试一次）」与「模型真的判了 MET」。
    /// 没有这个判别，fail-open 就会把「格式歪掉」静默当成「通过」。
    /// </summary>
    private static bool HasVerdictLine(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Any(line => line.Trim().StartsWith("VERDICT:", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 解析验证者的输出。严格找三行格式；认不出 → Met（fail-open）。
    /// 公开给验收测试做确定性回归。
    /// </summary>
    public static GoalVerification Parse(string output)
    {
        foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();

            if (!line.StartsWith("VERDICT:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var verdict = line["VERDICT:".Length..].Trim().ToUpperInvariant();
            var gap = ExtractGap(output);

            if (verdict.Contains("NOT_MET") || verdict.Contains("NOT MET") || verdict.Contains("未达成"))
            {
                return GoalVerification.NotMet(gap);
            }

            if (verdict.Contains("IMPOSSIBLE") || verdict.Contains("无法达成"))
            {
                return GoalVerification.Impossible(gap);
            }

            return GoalVerification.Met();
        }

        // 认不出裁决：放行（fail-open）
        return GoalVerification.Met();
    }

    private static string? ExtractGap(string output)
    {
        foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();

            if (line.StartsWith("GAP:", StringComparison.OrdinalIgnoreCase))
            {
                var gap = line["GAP:".Length..].Trim();
                return string.IsNullOrWhiteSpace(gap) || gap == "-" ? null : gap;
            }
        }

        return null;
    }

    private string TruncateContext(IReadOnlyList<LlmMessage> context)
    {
        var sb = new StringBuilder();
        var budget = _options.MaxContextChars;

        // 从尾部往前收集：近况优先，装不下就停
        for (var i = context.Count - 1; i >= 0 && budget > 0; i--)
        {
            var message = context[i];
            if (string.IsNullOrWhiteSpace(message.Content))
            {
                continue;
            }

            var role = message.Role switch
            {
                LlmRole.User => "用户",
                LlmRole.Assistant => "助手",
                LlmRole.Tool => "工具结果",
                _ => message.Role,
            };

            var entry = $"[{role}] {message.Content.Trim()}\n";
            if (entry.Length > budget)
            {
                entry = entry[..budget];
            }

            sb.Insert(0, entry);
            budget -= entry.Length;
        }

        return sb.ToString();
    }

    private static string BuildPrompt(string goal, string userRequest, string context, string finalText) => $"""
        请核验：下面这个 AI 助手想结束任务并给出最终答复，但它声称完成的目标真的达成了吗？

        目标（停止条件）：
        {goal}

        用户的原始请求：
        {userRequest}

        对话近况（含工具结果，越靠后越新）：
        {context}

        助手想收尾时说：
        {finalText}

        核验规则：
        - 只依据证据：目标里的可测终态必须在对话/工具结果里有对应证据，助手的自信不算证据
        - 目标包含多条要求时**逐条**核对，缺一条就是 NOT_MET
        - 若因环境损坏（如命令根本无法运行、资源缺失）导致客观上无法达成，判 IMPOSSIBLE
        - 不要新增目标之外的要求，不要因为文笔或格式问题扣分

        严格按三行格式回答（不要输出别的内容）：
        VERDICT: MET 或 NOT_MET 或 IMPOSSIBLE
        EVIDENCE: 一句话证据（指向对话/工具结果里的具体内容）
        GAP: 未达成时列出具体差距（逐条）；已达成则写 -
        """;
}