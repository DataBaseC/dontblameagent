using System.Text;
using AgentFramework.Contracts;

namespace AgentFramework.Llm;

public sealed class LlmContextSummarizerOptions
{
    public string Model { get; set; } = "auto";

    public string SystemPrompt { get; set; } =
        "你是一个精确的对话压缩器。你的输出会被另一个 AI 当作它自己的记忆使用，"
        + "所以只保留事实，不添加任何未出现过的信息。";

    /// <summary>摘要长度上限（防止小模型啰嗦成新的上下文负担）。</summary>
    public int MaxSummaryChars { get; set; } = 1_500;

    /// <summary>太少的消息不值得摘要。</summary>
    public int MinMessages { get; set; } = 4;
}

/// <summary>
/// L5：用模型摘要早期历史（**可选，默认关**）。
///
/// 为什么默认关：实证显示 LLM 型 condenser 会让**总 token 增加 24–94%**
/// （摘要本身要读完整对话 + 额外轮次），而质量没有统计显著提升。
/// 所以它是"兜底手段"：只有当结构性裁剪（L2/L3）压不下去、
/// 而用户又确实有本地小模型可以白跑时，才值得打开。
///
/// 与 4.10 的摘要器同理：应当接在**本地**端点上。
/// </summary>
public sealed class LlmContextSummarizer : IContextSummarizer
{
    private readonly ILlmClient _client;
    private readonly LlmContextSummarizerOptions _options;

    public LlmContextSummarizer(ILlmClient client, LlmContextSummarizerOptions? options = null)
    {
        _client = client;
        _options = options ?? new LlmContextSummarizerOptions();
    }

    public string Name => $"context-summarizer({_client.Name})";

    public async ValueTask<string> SummarizeHistoryAsync(
        IReadOnlyList<LlmMessage> messages,
        string? taskCard,
        string? previousSummary = null,
        CancellationToken ct = default)
    {
        // INV-C1（压缩产物再入禁止）：合成复述块（【…·非用户发言】——摘要、任务卡、
        // 计划文件等的注入块）本身就是复述/压缩产物的载体，绝不能再作为**被压缩的正文**
        // 进 transcript —— 那是「压缩压缩产物的影子」。旧摘要只以 previousSummary
        // 身份出现在背景段（见 BuildPrompt），永远不在正文里。
        var subjects = messages.Where(m => !SyntheticContent.IsSynthetic(m.Content)).ToList();

        // 增量模式下哪怕消息不多也值得跑 —— 有旧摘要要更新就不是空转
        if (subjects.Count < _options.MinMessages
            && string.IsNullOrWhiteSpace(previousSummary))
        {
            return string.Empty;
        }

        // 过滤后什么都不剩 = 没有新原料：不空转，让调用方沿用旧摘要
        if (subjects.Count == 0)
        {
            return string.Empty;
        }

        var transcript = BuildTranscript(subjects);

        var request = new LlmRequest
        {
            Model = _options.Model,
            SystemPrompt = _options.SystemPrompt,
            Messages =
            [
                new LlmMessage
                {
                    Role = LlmRole.User,
                    Content = BuildPrompt(transcript, taskCard, previousSummary),
                },
            ],
            Temperature = 0.2,
        };

        var builder = new StringBuilder();

        await foreach (var chunk in _client.StreamAsync(request, ct).ConfigureAwait(false))
        {
            if (chunk is LlmStreamChunk.TextDelta delta)
            {
                builder.Append(delta.Text);
                if (builder.Length > _options.MaxSummaryChars)
                {
                    break;
                }
            }
        }

        var summary = builder.ToString().Trim();
        if (summary.Length == 0)
        {
            throw new InvalidOperationException("上下文摘要模型返回了空结果");
        }

        return summary;
    }

    private static string BuildTranscript(IReadOnlyList<LlmMessage> messages)
    {
        var sb = new StringBuilder();

        foreach (var message in messages)
        {
            var role = message.Role switch
            {
                LlmRole.User => "用户",
                LlmRole.Assistant => "助手",
                _ => message.Role,
            };

            if (string.IsNullOrWhiteSpace(message.Content))
            {
                continue;
            }

            sb.Append('[').Append(role).Append("] ").AppendLine(message.Content.Trim());
        }

        return sb.ToString();
    }

    private static string BuildPrompt(string transcript, string? taskCard, string? previousSummary) => $"""
        把下面这段早期对话压缩成一份"工作记忆"，供另一个 AI 继续接手任务。

        必须按下面的**字段**输出（用 ## 小标题；没有内容的字段写「无」，不要省略字段）：
        ## 当前意图
        （用户的目标与硬性约束 —— 重要表述保留原话级别）
        ## 下一步动作
        （明确、可执行的下一步）
        ## 工作约束
        （不能违反的规则、失败过并**不应重试**的做法）
        ## 任务树
        （已完成 / 进行中 / 未开始 的条目）
        ## 涉及文件
        （文件路径 + 各自的角色/状态）
        ## 错误与修复
        （踩过的坑、报错与当时的修复方式）
        ## 设计决策
        （已确认的结论、决策，以及采用的**理由**）
        ## 产出与结论
        （已完成的工作与产出物：命令、结果、数值）
        ## 杂项
        （其余仍需记住的信息）

        可以丢弃：
        - 寒暄、重复表述、试错过程的中间细节（除非它揭示了约束）
        - 工具的原始输出（只留结论）

        规则：
        - 不要添加对话里没出现过的信息，不要替用户做新决定
        - 用中文、条列式、信息密度优先
        - 只输出工作记忆正文，不要任何前后缀说明
        {(string.IsNullOrWhiteSpace(previousSummary)
            ? ""
            : "- 这是**增量更新**：下面给了「已有的工作记忆」，请在其基础上合并新进展、修正过时信息，输出完整的新版工作记忆；不要逐字照抄旧记忆，也不要丢掉仍然有效的条目")}

        {(string.IsNullOrWhiteSpace(previousSummary) ? "" : "已有的工作记忆：\n" + previousSummary + "\n")}
        {(string.IsNullOrWhiteSpace(taskCard) ? "" : "当前任务卡（供你判断什么重要）：\n" + taskCard + "\n")}
        早期对话：
        {transcript}
        """;
}
