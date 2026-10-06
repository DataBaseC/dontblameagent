using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AgentFramework.Contracts;

namespace AgentFramework.Agent;

public sealed class AgentOptions
{
    public string SessionId { get; set; } = "s-1";

    public string Model { get; set; } = "deepseek-chat";

    public string? SystemPrompt { get; set; } = "你是一个简洁、可靠的助手。";

    /// <summary>
    /// 最大步数（防止工具调用死循环）。
    /// 默认 60 —— 借鉴 MiMo Code 的做法：步数预算要够长任务跑完，
    /// 真正的防死循环靠「重复调用提示」+「末步预算提示」，而不是把步数掐得很死。
    /// 从前默认 12，编程任务十几步就到顶，agent 在任务中途静默收工 ——
    /// 这正是「跑一会儿就自己停了」的主因之一。
    /// </summary>
    public int MaxSteps { get; set; } = 60;

    /// <summary>
    /// 模型空响应（无正文、无思考、无工具调用）时的重试次数。
    /// 慢端点 / 网关抖动偶发吐空帧，那不是"说完了"，重试比直接收工诚实。
    /// </summary>
    public int EmptyResponseRetries { get; set; } = 2;

    /// <summary>
    /// 模型调用抛**瞬时**异常（超时 / 断流 / 5xx）时的重试次数。
    /// 只在尚无任何部分输出时重试 —— 已经吐了一半就重来，会把半截内容作废。
    /// </summary>
    public int CallRetries { get; set; } = 2;

    /// <summary>
    /// 重试的**墙钟预算**（毫秒）：失败本身来得慢（连接挂起、端点超时）就不再重试 ——
    /// 否则一次 30 秒的故障会被重试放大成 90 秒，回合迟迟不收尾。
    /// 快速失败（拒绝连接、4xx/5xx）用满次数；慢故障最多占这份预算。
    /// </summary>
    public int RetryWallClockMs { get; set; } = 2_000;

    /// <summary>回复被长度上限截断时，自动续写的最大次数。</summary>
    public int MaxContinuations { get; set; } = 3;

    /// <summary>
    /// 连续**完全相同**的工具调用达到这个次数，就注入一次「换路子」提示（doom-loop 防护）。
    /// 只提示不硬停 —— 硬停仍由 MaxSteps 兜底。
    /// </summary>
    public int RepeatCallNudge { get; set; } = 3;

    /// <summary>
    /// 回合内旧工具结果的**逐字保留条数**。超出的在超预算时折叠成占位符。
    /// 这是单回合上下文的硬护栏：回合内的工具结果不归回合外的投影管，
    /// 不在这里收，几十步的长回合就会把上下文撑爆（API 400 / 回合猝死）。
    /// </summary>
    public int InTurnKeepToolResults { get; set; } = 8;

    /// <summary>
    /// 回合内上下文预算（估算 token）。超过才启动回合内折叠 ——
    /// 与回合外治理同一条纪律：无压力不损失信息。
    /// </summary>
    public int InTurnTokenBudget { get; set; } = 24_000;

    /// <summary>回合内折叠必须至少省下这么多字符才值得做（同 ContextOptions.MinMaskSavingChars 的理由）。</summary>
    public int InTurnMinMaskSavingChars { get; set; } = 200;

    /// <summary>
    /// Goal 停止条件（自然语言，如「所有测试通过且代码已提交」）。空 = 不启用目标核验。
    /// 模型每次想收尾时，由 <see cref="GoalVerifier"/> 独立裁决「真的达成了吗」——
    /// 防止 agent 看到已有进展就提前宣称完成（MiMo Code 的 Goal 机制）。
    /// </summary>
    public string? Goal { get; set; }

    /// <summary>目标验证器（旁路模型调用）。null = 不核验，收尾即收尾。</summary>
    public IGoalVerifier? GoalVerifier { get; set; }

    /// <summary>
    /// 一次回合里最多核验/纠偏多少次（默认 3）。
    /// 每次未达成都会把差距反馈回模型继续干；到上限就放行收尾 ——
    /// 验证器不许把回合变成死循环（MiMo 实测：有上限的死循环率 &lt; 0.5%）。
    /// </summary>
    public int GoalMaxVerifications { get; set; } = 3;

    /// <summary>单次核验的超时（毫秒）。超时按「达成」放行 —— 验证器是增强项，绝不卡住收尾。</summary>
    public int GoalVerifierTimeoutMs { get; set; } = 60_000;

    public double Temperature { get; set; } = 0.7;

    /// <summary>文本增量回调（用于 UI 流式显示）。</summary>
    public Action<string>? OnTextDelta { get; set; }

    /// <summary>思考增量回调 —— 让界面能显示「模型正在往哪个方向想」。</summary>
    public Action<string>? OnReasoningDelta { get; set; }

    /// <summary>
    /// 回调异常的可观测出口 —— UI 回调抛错不该拖垮回合，但也不该被无声吞掉。
    /// 宿主可挂上它做计数 / 记日志；不挂则维持「静默但回合不受影响」。
    /// </summary>
    public Action<Exception>? OnCallbackError { get; set; }

    /// <summary>是否要求端点回报用量（个别本地端点不认该字段时关掉）。</summary>
    public bool IncludeUsage { get; set; } = true;

    /// <summary>思考留痕的字符上限 —— 思考可以很长，日志不该被它撑爆。</summary>
    public int ReasoningMaxChars { get; set; } = 4_000;
}

public sealed record AgentRunResult(bool Completed, string FinalText, int Steps, string StopReason);

/// <summary>
/// Agent 主干对外的两个出口。
/// 刻意用接口而不是直接依赖 Kernel / Data —— 主干只负责「编排」，
/// 事件写到哪里、审批由谁裁决，都交由宿主决定。
/// </summary>
public interface IAgentEventSink
{
    /// <summary>写入一条会话事件（宿主持久化到 JSONL）。</summary>
    ValueTask EmitAsync(SessionEvent sessionEvent, CancellationToken ct);

    /// <summary>请求审批。返回时若 <c>evt.Cancelled</c> 为 true 则拦截本次调用。</summary>
    ValueTask RequestApprovalAsync(ToolPreExecuteEvent toolPreExecuteEvent, CancellationToken ct);
}

/// <summary>
/// Agent 主循环。把四条线合流：
///   调模型 → 工具调用 → 审批 → 执行 → 落日志
///
/// 全过程遵守「模型可见即已记录」：凡是进入模型上下文的内容，
/// 都先落到事件日志里（顺序严格：先落日志，再进上下文）。
///
/// 长任务不死（借鉴 MiMo Code / Claude Code 的成熟做法）：
///   · 空响应重试      —— 端点抖动吐空帧 ≠ 「说完了」
///   · 瞬时异常重试    —— 超时 / 断流 / 5xx 不直接判回合死刑
///   · 截断自动续写    —— finish=length 是「没写完」，不是「答完了」
///   · 回合内上下文护栏 —— 几十步工具结果无界增长是长回合猝死的真正根源
///   · 重复调用提示    —— 同一工具同一参数反复调（doom loop）时叫醒模型
///   · 末步预算提示    —— 步数将尽时要求输出交接摘要，优雅降级而不是硬截断
/// </summary>
public sealed class AgentRunner
{
    private readonly ILlmClient _llm;
    private readonly Func<IReadOnlyCollection<ITool>> _toolsProvider;
    private readonly IAgentEventSink _sink;
    private readonly AgentOptions _options;

    public AgentRunner(
        ILlmClient llm,
        Func<IReadOnlyCollection<ITool>> toolsProvider,
        IAgentEventSink sink,
        AgentOptions? options = null)
    {
        _llm = llm;
        _toolsProvider = toolsProvider;
        _sink = sink;
        _options = options ?? new AgentOptions();
    }

    /// <summary>
    /// 本主干的运行期选项（活引用）——宿主可在运行期改 Goal 等字段，下一次收尾核验立即生效。
    /// </summary>
    public AgentOptions Options => _options;

    /// <summary>
    /// 跑一轮对话。
    /// <paramref name="priorContext"/> 用于「会话续接」：由宿主从事件流重建历史后传进来，
    /// 模型便能接着上次继续 —— 这也是「重启后现场还原」在模型侧的实现。
    /// </summary>
    public async Task<AgentRunResult> RunAsync(
        string userInput,
        IReadOnlyList<LlmMessage>? priorContext = null,
        CancellationToken ct = default,
        string? rephrasedText = null,
        string? rephraseModel = null,
        IReadOnlyList<LlmImage>? images = null)
    {
        var history = priorContext is null
            ? new List<LlmMessage>()
            : [.. priorContext];
        var lastAssistantText = string.Empty;

        // 转述模式下：日志里存「原文 + 转述结果」，模型看到的是转述结果。
        // 「模型可见即已记录」不受影响 —— 谁看见了什么，日志里都写清楚了。
        var modelVisibleText = string.IsNullOrWhiteSpace(rephrasedText) ? userInput : rephrasedText;

        // 用户输入先落日志，再进上下文
        await _sink.EmitAsync(
            new UserMessageEvent
            {
                SessionId = _options.SessionId,
                Text = userInput,
                RephrasedText = rephrasedText,
                RephraseModel = rephraseModel,
                Images = images is { Count: > 0 } ? [.. images] : null,
            }, ct).ConfigureAwait(false);

        history.Add(new LlmMessage
        {
            Role = LlmRole.User,
            Content = modelVisibleText,
            Images = images is { Count: > 0 } ? images : null,
        });

        var continuationsUsed = 0;
        var goalVerifications = 0;
        string? lastCallSignature = null;
        var repeatCount = 0;

        for (var step = 1; step <= _options.MaxSteps; step++)
        {
            var tools = _toolsProvider();
            var schemas = tools
                .Select(t => new ToolSchema(t.Name, t.Description, ToolSchemas.For(t)))
                .ToList();

            // 末步预算提示（MiMo Code 的优雅降级）：步数将尽时明确要求交接摘要，
            // 而不是把工具调用拦腰砍断后什么都不留。提示走 user 消息（system 只能在最前）。
            if (step == _options.MaxSteps)
            {
                history.Add(new LlmMessage
                {
                    Role = LlmRole.User,
                    Content = "【步数预算提示·非用户发言】本轮步数预算即将用尽（这是最后一步）。"
                        + "请不要再发起新的工具调用，立即输出交接摘要：已完成的工作、关键结论、文件与命令清单、以及剩余步骤。",
                });
            }

            // 回合内上下文护栏：几十步的工具结果无界增长会把上下文撑爆（长回合猝死的根源）。
            // 超预算才动手 —— 无压力不损失信息；折叠只是「换投影」，原文仍在会话日志里。
            await PruneInTurnHistoryAsync(history, ct).ConfigureAwait(false);

            var request = new LlmRequest
            {
                Model = _options.Model,
                SystemPrompt = _options.SystemPrompt,
                // 传快照而不是 history 本身：否则请求发出后，history 被本轮的后续追加改动，
                // 会连带篡改「这次请求当时长什么样」—— 诊断与验收都会被误导。
                Messages = [.. history],
                Tools = schemas,
                Temperature = _options.Temperature,
                IncludeUsage = _options.IncludeUsage,
            };

            var text = new StringBuilder();
            var reasoning = new StringBuilder();
            IReadOnlyList<ToolCallRequest>? calls = null;
            LlmUsage? usage = null;
            string? servedModel = null;
            string? stopReason = null;

            var startedAt = Stopwatch.GetTimestamp();
            long? firstTokenMs = null;
            Exception? streamError = null;

            // 空响应 / 瞬时异常的重试都在这一层 —— 重试的是「这一次模型调用」，不消耗步数。
            var maxAttempts = 1 + Math.Max(0, Math.Max(_options.CallRetries, _options.EmptyResponseRetries));
            for (var attempt = 0; ; attempt++)
            {
                text.Clear();
                reasoning.Clear();
                calls = null;
                usage = null;
                servedModel = null;
                stopReason = null;
                firstTokenMs = null;
                var streamStartedAt = Stopwatch.GetTimestamp();

                try
                {
                    await foreach (var chunk in _llm.StreamAsync(request, ct).ConfigureAwait(false))
                    {
                        switch (chunk)
                        {
                            case LlmStreamChunk.TextDelta delta:
                                firstTokenMs ??= ElapsedMs(streamStartedAt);
                                text.Append(delta.Text);
                                // v3.6 审查修复：UI 增量回调隔离 —— 原先内联直调，
                                // 回调抛异常会中断整轮，而此刻 assistant 文本尚未落账 → 该轮输出丢失。
                                try { _options.OnTextDelta?.Invoke(delta.Text); } catch (Exception ex) { _options.OnCallbackError?.Invoke(ex); }
                                break;

                            // 思考与正文是两条通道：思考不喂回模型，但可以实时给用户看
                            case LlmStreamChunk.ReasoningDelta reasoningDelta:
                                firstTokenMs ??= ElapsedMs(streamStartedAt);
                                reasoning.Append(reasoningDelta.Text);
                                try { _options.OnReasoningDelta?.Invoke(reasoningDelta.Text); } catch (Exception ex) { _options.OnCallbackError?.Invoke(ex); }
                                break;

                            case LlmStreamChunk.UsageReady usageReady:
                                usage = usageReady.Usage;
                                servedModel = usageReady.Model;
                                break;

                            case LlmStreamChunk.ToolCallsReady ready:
                                calls = ready.Calls;
                                break;

                            case LlmStreamChunk.Completed completed:
                                // v3.6 审查修复：消费 finish_reason —— 原先这一支被丢弃，
                                // 于是被 max_tokens 截断（length）/ 内容过滤（content_filter）也被当作成功。
                                stopReason = completed.FinishReason;
                                break;
                        }
                    }
                }
                catch (Exception ex) when (
                    ex is not OperationCanceledException
                    && attempt + 1 < maxAttempts
                    && ElapsedMs(startedAt) < _options.RetryWallClockMs
                    && text.Length == 0
                    && reasoning.Length == 0
                    && calls is null
                    && !ct.IsCancellationRequested)
                {
                    // 瞬时故障（超时 / 断流 / 5xx）且尚无任何部分输出 —— 退避重试。
                    // 有任何部分输出就不重试：重来会作废已经流出来的内容。
                    await Task.Delay(TimeSpan.FromMilliseconds(400 * (attempt + 1)), ct).ConfigureAwait(false);
                    continue;
                }
                catch (Exception ex) when (
                    ex is not OperationCanceledException
                    && !ct.IsCancellationRequested)
                {
                    // 重试用尽（或已有部分输出）→ **不再抛出去炸掉整个回合**。
                    // 免费/慢端点的「上游断流」错误帧就落在这条路径：回合应当
                    // 降级为「截断续写」或如实收尾，而不是让进程直接崩掉。
                    streamError = ex;
                    break;
                }

                // 有输出（正文 / 思考 / 工具调用）→ 本次调用有效，出循环。
                if (text.Length > 0 || reasoning.Length > 0 || calls is not null)
                {
                    break;
                }

                // 空响应：端点抖动吐空帧 ≠ 「说完了」。content_filter 是裁决不是抖动，不重试。
                if (string.Equals(stopReason, "content_filter", StringComparison.Ordinal)
                    || attempt + 1 >= maxAttempts)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(400 * (attempt + 1)), ct).ConfigureAwait(false);
            }

            // 落账：用量与思考都进事件流。
            // 不这么做，成本与缓存命中率就只是界面上一闪而过的数字 —— 事后什么都查不到。
            await _sink.EmitAsync(new ModelUsageEvent
            {
                SessionId = _options.SessionId,
                Step = step,
                Model = servedModel ?? _llm.Name,
                InputTokens = usage?.InputTokens,
                OutputTokens = usage?.OutputTokens,
                CachedTokens = usage?.CachedTokens,
                CacheWriteTokens = usage?.CacheWriteTokens,
                ReasoningTokens = usage?.ReasoningTokens,
                ElapsedMs = ElapsedMs(startedAt),
                FirstTokenMs = firstTokenMs,
                HasReasoning = reasoning.Length > 0,
            }, ct).ConfigureAwait(false);

            if (reasoning.Length > 0)
            {
                var fullReasoning = reasoning.ToString();
                var cut = _options.ReasoningMaxChars;
                var truncated = fullReasoning.Length > cut;

                if (truncated)
                {
                    // 不要从代理对（emoji 等）中间切 —— 那会产生非法 UTF-16 字符串，
                    // 写进 JSONL 时就变成一个「坏行」（加固 D）。
                    while (cut > 0 && char.IsHighSurrogate(fullReasoning[cut - 1]))
                    {
                        cut--;
                    }
                }

                await _sink.EmitAsync(new ReasoningEvent
                {
                    SessionId = _options.SessionId,
                    Step = step,
                    Model = servedModel ?? _llm.Name,
                    Text = truncated ? fullReasoning[..cut] : fullReasoning,
                    Truncated = truncated,
                }, ct).ConfigureAwait(false);
            }

            var assistantText = text.ToString();
            lastAssistantText = assistantText;

            // 工具后偶发：端点把最终答复只放进 reasoning_content（content 为空）。
            // 那是给用户看的答复，必须进对话框 —— 只躺在思考块里等于「答了但看不见」。
            if (assistantText.Length == 0
                && reasoning.Length > 0
                && (calls is null || calls.Count == 0))
            {
                assistantText = reasoning.ToString();
                lastAssistantText = assistantText;
            }

            // 端点异常且毫无输出：如实以 endpoint-error 收尾，绝不抛异常炸掉回合/进程
            // （一次性 CLI 模式曾在这里整个崩掉，界面上就表现为「突然停了」）。
            if (streamError is not null
                && assistantText.Length == 0
                && reasoning.Length == 0
                && (calls is null || calls.Count == 0))
            {
                return new AgentRunResult(
                    false,
                    $"（端点异常，回合中止：{Truncate(streamError.Message, 200)}。可直接重发，历史不受影响。）",
                    step,
                    "endpoint-error");
            }

            // 空响应兜底：重试用尽后仍然「什么都没有」—— 如实报 empty-response，
            // 而不是返回一个空的成功（那正是「界面显示模型什么都没说却算这一轮成功」）。
            if (assistantText.Length == 0
                && reasoning.Length == 0
                && (calls is null || calls.Count == 0))
            {
                var emptyReason = string.Equals(stopReason, "content_filter", StringComparison.Ordinal)
                    ? "content_filter"
                    : "empty-response";
                return new AgentRunResult(false, string.Empty, step, emptyReason);
            }

            history.Add(new LlmMessage
            {
                Role = LlmRole.Assistant,
                Content = assistantText.Length > 0 ? assistantText : null,
                ToolCalls = calls,
            });

            if (assistantText.Length > 0)
            {
                await _sink.EmitAsync(
                    new AssistantMessageEvent { SessionId = _options.SessionId, Text = assistantText }, ct)
                    .ConfigureAwait(false);
            }

            // 没有工具调用 → 本轮任务结束。
            // v3.6 审查修复：如实传递 finish_reason —— 被 max_tokens 截断（length）或内容过滤
            // （content_filter）不该再报「成功」，否则用户看到的是「半截答案 + 一切正常」。
            if (calls is null || calls.Count == 0)
            {
                var reason = streamError is not null
                    ? "endpoint-error"
                    : string.IsNullOrEmpty(stopReason) ? "stop" : stopReason;

                // finish=length / 端点中断都是「没写完」，不是「答完了」——
                // 接着写，而不是把半截答案当收尾。
                if (reason is "length" or "endpoint-error" && continuationsUsed < _options.MaxContinuations)
                {
                    continuationsUsed++;
                    history.Add(new LlmMessage
                    {
                        Role = LlmRole.User,
                        Content = reason == "length"
                            ? "（上一条回复因长度上限被截断。请从中断处继续，不要重复已写内容、不要重新开头。）"
                            : "（上一条回复因端点中断被截断。请从中断处继续，不要重复已写内容、不要重新开头。）",
                    });
                    continue;
                }

                // Goal 终止验证（MiMo Code 的 Goal 机制）：模型想以「我说完了」收尾时，
                // 先由旁路验证者问一句「目标真的达成了吗」——它不参与干活、无认同偏差，只看证据。
                // 未达成 → 把差距反馈回去继续干；确认做不到 → 如实报 goal-impossible。
                // 只在自然收尾（stop）且非末步强制收尾时核验；验证器超时/出错一律放行（fail-open）。
                if (_options.GoalVerifier is not null
                    && !string.IsNullOrWhiteSpace(_options.Goal)
                    && reason == "stop"
                    && step < _options.MaxSteps
                    && goalVerifications < _options.GoalMaxVerifications)
                {
                    var verification = await VerifyGoalAsync(userInput, history, assistantText, ct).ConfigureAwait(false);

                    if (verification is { Verdict: GoalVerdict.NotMet })
                    {
                        goalVerifications++;
                        history.Add(new LlmMessage
                        {
                            Role = LlmRole.User,
                            Content = "【目标核验·非用户发言】你刚才想收尾，但核验显示目标尚未达成。差距："
                                + (string.IsNullOrWhiteSpace(verification.Gap)
                                    ? "（未给出具体差距，请对照目标逐条自查并用工具验证）"
                                    : verification.Gap)
                                + "\n请继续用工具完成并验证剩余部分，达成后再收尾；"
                                + "若存在客观上无法克服的障碍，请在最后的答复里明确说明障碍。",
                        });
                        continue;
                    }

                    if (verification is { Verdict: GoalVerdict.Impossible })
                    {
                        return new AgentRunResult(
                            false,
                            assistantText
                                + (string.IsNullOrWhiteSpace(verification.Gap)
                                    ? "\n\n[目标核验] 确认无法达成目标。"
                                    : $"\n\n[目标核验] 确认无法达成目标：{verification.Gap}"),
                            step,
                            "goal-impossible");
                    }

                    // Met / 验证器缺席（fail-open）→ 正常收尾
                }

                var incomplete = reason is "length" or "content_filter" or "endpoint-error";
                return new AgentRunResult(!incomplete, assistantText, step, reason);
            }

            continuationsUsed = 0;

            // 有工具调用 → 逐个走「记录意图 → 审批 → 执行 → 记录结果」
            foreach (var call in calls)
            {
                // v3.6 审查修复：参数解析失败要**显式失败**，而不是静默当空参执行工具 ——
                // 前者模型能看到错误并重试；后者会用空/错参数跑一个带副作用的工具。
                var arguments = ParseArguments(call.ArgumentsJson, out var argumentError);

                await _sink.EmitAsync(new ToolCallRequestedEvent
                {
                    SessionId = _options.SessionId,
                    CallId = call.CallId,
                    ToolName = call.ToolName,
                    Arguments = arguments,
                }, ct).ConfigureAwait(false);

                var approval = new ToolPreExecuteEvent
                {
                    ToolName = call.ToolName,
                    Arguments = arguments,
                    // 风险等级由**工具自报**（IToolWithRisk）。未声明的一律按最保守的执行档 ——
                    // 于是「新工具默认要问」是安全的默认，而审批不必再维护一张工具名清单。
                    Risk = tools.FirstOrDefault(t => string.Equals(t.Name, call.ToolName, StringComparison.Ordinal))
                        is IToolWithRisk risky ? risky.Risk : ToolRisk.Execute,
                };

                ToolResult result;
                var completedEmitted = false;
                try
                {
                    if (string.IsNullOrWhiteSpace(call.ToolName))
                    {
                        result = ToolResult.Fail("工具名为空：模型未给出有效的 function.name，无法执行");
                    }
                    else if (argumentError is not null)
                    {
                        result = ToolResult.Fail(argumentError);
                    }
                    else
                    {
                        await _sink.RequestApprovalAsync(approval, ct).ConfigureAwait(false);

                        if (approval.Cancelled)
                        {
                            var why = approval.RejectReason ?? "无理由";
                            result = ToolResult.Fail(
                                why.Contains("取消", StringComparison.Ordinal)
                                    ? $"已取消：{why}"
                                    : $"已被拒绝：{why}");
                        }
                        else
                        {
                            var tool = tools.FirstOrDefault(t => string.Equals(t.Name, call.ToolName, StringComparison.Ordinal));
                            try
                            {
                                result = tool is null
                                    ? ToolResult.Fail($"工具不存在：{call.ToolName}")
                                    : await tool.InvokeAsync(new ToolInvocation(call.ToolName, arguments), ct).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (ct.IsCancellationRequested)
                            {
                                result = ToolResult.Fail("已取消");
                            }
                            catch (Exception ex)
                            {
                                // 工具抛异常也必须补 completed —— 「记录意图 → 记录结果」是一对，
                                // 缺了 completed 会话会从下一轮起每轮 400（悬空 tool_call）。
                                result = ToolResult.Fail($"工具执行异常：{ex.GetType().Name}: {ex.Message}");
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // 审批等待被停止键打断：仍要落 completed，否则悬空 tool_call 会弄坏会话
                    result = ToolResult.Fail("已取消：回合被停止");
                    await EmitToolCompletedAsync(call, result, CancellationToken.None).ConfigureAwait(false);
                    completedEmitted = true;
                    history.Add(new LlmMessage
                    {
                        Role = LlmRole.Tool,
                        ToolCallId = call.CallId,
                        Content = $"ERROR: {result.Error}",
                    });
                    throw;
                }
                catch (Exception ex)
                {
                    // 审批/订阅者抛出的非取消异常：也必须落 completed。
                    // 否则 UI 工具卡永远停在「执行中…」，下一轮还会因悬空 tool_call 被端点 400。
                    result = ToolResult.Fail($"工具流程异常：{ex.GetType().Name}: {ex.Message}");
                }

                if (!completedEmitted)
                {
                    await EmitToolCompletedAsync(call, result, ct).ConfigureAwait(false);
                }

                history.Add(new LlmMessage
                {
                    Role = LlmRole.Tool,
                    ToolCallId = call.CallId,
                    Content = result.Success ? result.Output : $"ERROR: {result.Error}",
                });

                // 视觉工具（read_image 等）随结果带图：OpenAI 系 tool 角色不收图，
                // 紧跟一条 user 多模态消息注入 —— 文本标明来源，图进 content 数组。
                if (result.Attachments is { Count: > 0 })
                {
                    history.Add(new LlmMessage
                    {
                        Role = LlmRole.User,
                        Content = $"（上一步工具「{call.ToolName}」附带了 {result.Attachments.Count} 张图，请结合图像继续）",
                        Images = result.Attachments,
                    });
                }
            }

            // doom-loop 防护（只提示不硬停）：同一工具同一参数反复调，结果不会变，
            // 那是模型在原地打转 —— 叫醒它换路子，硬停仍由 MaxSteps 兜底。
            var signature = string.Join("|", calls.Select(c => $"{c.ToolName}:{c.ArgumentsJson}"));
            if (string.Equals(signature, lastCallSignature, StringComparison.Ordinal))
            {
                repeatCount++;
            }
            else
            {
                lastCallSignature = signature;
                repeatCount = 1;
            }

            if (repeatCount == _options.RepeatCallNudge)
            {
                history.Add(new LlmMessage
                {
                    Role = LlmRole.User,
                    Content = "【系统提示·非用户发言】你已连续多次发起完全相同的工具调用，结果不会改变。"
                        + "请换一种方法，或直接基于已有结果给出结论，不要重复相同调用。",
                });
            }
        }

        // 末步可能已经写出正文，只是还带着工具调用没走完 —— 不要把它丢掉
        return new AgentRunResult(false, lastAssistantText, _options.MaxSteps, "max-steps");
    }

    /// <summary>
    /// Goal 终止核验（旁路调用）。
    ///
    /// 纪律：验证器是**增强项** —— 超时、出错、取消（非用户停止）一律返回 null 放行收尾，
    /// 绝不把「收尾」卡死在核验上。用户停止（ct 取消）照常上抛。
    /// 只喂对话近况（尾部若干条）：验证者要的是证据，不是把整段历史再读一遍。
    /// </summary>
    private async ValueTask<GoalVerification?> VerifyGoalAsync(
        string userInput,
        IReadOnlyList<LlmMessage> history,
        string finalText,
        CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.GoalVerifierTimeoutMs);

            var tail = history.Count <= 24
                ? history
                : history.Skip(history.Count - 24).ToList();

            return await _options.GoalVerifier!.VerifyAsync(
                _options.Goal!,
                userInput,
                tail,
                finalText,
                timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 回合内上下文护栏（MiMo Code 的 prune 旧工具输出）。
    ///
    /// 回合外的投影治理管不到**回合内**的增长 —— 一个几十步的长回合，
    /// 工具结果全堆在内存历史里，直到 API 400 / 回合猝死。这里把它兜住：
    ///   · 超过预算才动手（无压力不损失信息）；
    ///   · 保留最近 N 条工具结果逐字，更早的折叠成一行占位符（原文都在会话日志里）；
    ///   · 折叠必须真的省下可观空间，否则不值得换（同 L3 的门槛纪律）；
    ///   · 折叠发生时落一条压缩留痕事件 —— 「模型可见即已记录」同样约束回合内。
    /// </summary>
    private async ValueTask PruneInTurnHistoryAsync(List<LlmMessage> history, CancellationToken ct)
    {
        if (_options.InTurnKeepToolResults <= 0 || history.Count == 0)
        {
            return;
        }

        var before = EstimateTokens(history);
        if (before <= _options.InTurnTokenBudget)
        {
            return;
        }

        // 从尾向前数：最近 N 条 tool 消息保留；更早的折叠（内容替换，ToolCallId 不动 → 协议不破）
        var keep = _options.InTurnKeepToolResults;
        var seen = 0;
        var newlyMasked = 0;

        for (var i = history.Count - 1; i >= 0; i--)
        {
            if (history[i].Role != LlmRole.Tool)
            {
                continue;
            }

            seen++;
            if (seen <= keep)
            {
                continue;
            }

            var message = history[i];
            var raw = message.Content ?? string.Empty;

            // 幂等：已折叠过的不再换（占位符本来就很短，门槛也会拦下）
            if (raw.StartsWith("[已折叠历史工具结果", StringComparison.Ordinal))
            {
                continue;
            }

            var note = $"[已折叠历史工具结果：原始 {raw.Length} 字符；完整输出在会话日志中，"
                + "可用 search_history 检索，或重新调用该工具]";

            if (note.Length + _options.InTurnMinMaskSavingChars > raw.Length)
            {
                continue;
            }

            history[i] = message with { Content = note };
            newlyMasked++;
        }

        if (newlyMasked == 0)
        {
            return;
        }

        var after = EstimateTokens(history);

        await _sink.EmitAsync(new ContextCompactedEvent
        {
            SessionId = _options.SessionId,
            Trigger = CompactionTrigger.InTurn,
            PreTokens = before,
            PostTokens = after,
            MaskedSeqs = [],
            MaskedCount = newlyMasked,
            CollapsedTurns = 0,
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 粗算 token（与 Data 层 TokenEstimator 同一量级的启发式）：
    /// 只用于「超没超预算」的趋势判断，不用于计费。CJK 按 1 字 1 token，拉丁按 4 字符 1 token。
    /// 主干刻意不依赖 Data 层 —— 就地实现这一点点算术，比引入一条项目依赖便宜。
    /// </summary>
    private static int EstimateTokens(IReadOnlyList<LlmMessage> messages)
    {
        var total = 0;

        foreach (var message in messages)
        {
            total += 4; // 每条消息的协议开销

            foreach (var text in new[] { message.Content, message.ToolCallId })
            {
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                var cjk = 0;
                var other = 0;
                foreach (var ch in text)
                {
                    if (ch >= 0x2E80 && ch <= 0x9FFF)
                    {
                        cjk++;
                    }
                    else
                    {
                        other++;
                    }
                }

                total += cjk + (other + 3) / 4;
            }
        }

        return total;
    }

    /// <summary>毫秒计时 —— 用 Stopwatch 时间戳，避开 DateTime 的精度与时钟回拨问题。</summary>
    private static long ElapsedMs(long startedAt)
        => (long)((Stopwatch.GetTimestamp() - startedAt) * 1000.0 / Stopwatch.Frequency);

    /// <summary>截断长文本（错误消息进对话框时用）。</summary>
    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";

    /// <summary>
    /// 落一条工具完成事件。「请求 → 完成」必须成对，缺 completed 会让
    /// UI 卡在「执行中…」、下一轮因悬空 tool_call 被端点 400。
    /// </summary>
    private async ValueTask EmitToolCompletedAsync(ToolCallRequest call, ToolResult result, CancellationToken ct)
        => await _sink.EmitAsync(new ToolCallCompletedEvent
        {
            SessionId = _options.SessionId,
            CallId = call.CallId,
            Success = result.Success,
            Output = result.Output,
            Error = result.Error,
            Images = result.Attachments is { Count: > 0 } ? [.. result.Attachments] : null,
        }, ct).ConfigureAwait(false);

    /// <summary>
    /// 把模型给的参数 JSON 解析成字典。<paramref name="error"/> 非空表示解析失败 ——
    /// 调用方据此**显式失败**，而不再静默当空参执行工具（v3.6 审查修复）。
    /// 空串合法（视为无参数、error 为 null）；非对象或非法 JSON 视为失败。
    /// </summary>
    private static Dictionary<string, string?> ParseArguments(string json, out string? error)
    {
        error = null;
        var result = new Dictionary<string, string?>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "工具参数不是 JSON 对象，已拒绝执行（避免用错误参数调用工具）";
                return result;
            }

            foreach (var property in doc.RootElement.EnumerateObject())
            {
                result[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()
                    : property.Value.GetRawText();
            }
        }
        catch (JsonException)
        {
            error = "工具参数不是合法 JSON，已拒绝执行（避免用错误参数调用工具）";
        }

        return result;
    }
}
