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

    /// <summary>最大步数（防止工具调用死循环）。</summary>
    public int MaxSteps { get; set; } = 12;

    public double Temperature { get; set; } = 0.7;

    /// <summary>文本增量回调（用于 UI 流式显示）。</summary>
    public Action<string>? OnTextDelta { get; set; }

    /// <summary>思考增量回调 —— 让界面能显示「模型正在往哪个方向想」。</summary>
    public Action<string>? OnReasoningDelta { get; set; }

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
    /// 跑一轮对话。
    /// <paramref name="priorContext"/> 用于「会话续接」：由宿主从事件流重建历史后传进来，
    /// 模型便能接着上次继续 —— 这也是「重启后现场还原」在模型侧的实现。
    /// </summary>
    public async Task<AgentRunResult> RunAsync(
        string userInput,
        IReadOnlyList<LlmMessage>? priorContext = null,
        CancellationToken ct = default,
        string? rephrasedText = null,
        string? rephraseModel = null)
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
            }, ct).ConfigureAwait(false);

        history.Add(new LlmMessage { Role = LlmRole.User, Content = modelVisibleText });

        for (var step = 1; step <= _options.MaxSteps; step++)
        {
            var tools = _toolsProvider();
            var schemas = tools
                .Select(t => new ToolSchema(t.Name, t.Description, ToolSchemas.For(t)))
                .ToList();

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

            var startedAt = Stopwatch.GetTimestamp();
            long? firstTokenMs = null;

            await foreach (var chunk in _llm.StreamAsync(request, ct).ConfigureAwait(false))
            {
                switch (chunk)
                {
                    case LlmStreamChunk.TextDelta delta:
                        firstTokenMs ??= ElapsedMs(startedAt);
                        text.Append(delta.Text);
                        _options.OnTextDelta?.Invoke(delta.Text);
                        break;

                    // 思考与正文是两条通道：思考不喂回模型，但可以实时给用户看
                    case LlmStreamChunk.ReasoningDelta reasoningDelta:
                        firstTokenMs ??= ElapsedMs(startedAt);
                        reasoning.Append(reasoningDelta.Text);
                        _options.OnReasoningDelta?.Invoke(reasoningDelta.Text);
                        break;

                    case LlmStreamChunk.UsageReady usageReady:
                        usage = usageReady.Usage;
                        servedModel = usageReady.Model;
                        break;

                    case LlmStreamChunk.ToolCallsReady ready:
                        calls = ready.Calls;
                        break;
                }
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

            // 没有工具调用 → 本轮任务结束
            if (calls is null || calls.Count == 0)
            {
                return new AgentRunResult(true, assistantText, step, "stop");
            }

            // 有工具调用 → 逐个走「记录意图 → 审批 → 执行 → 记录结果」
            foreach (var call in calls)
            {
                var arguments = ParseArguments(call.ArgumentsJson);

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
                };
                await _sink.RequestApprovalAsync(approval, ct).ConfigureAwait(false);

                ToolResult result;
                if (approval.Cancelled)
                {
                    result = ToolResult.Fail($"已被拒绝：{approval.RejectReason ?? "无理由"}");
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
                        // 回合被叫停：结果照样落盘，否则会留下悬空 tool_call，下一轮直接 400
                        result = ToolResult.Fail("已取消");
                    }
                    catch (Exception ex)
                    {
                        // 工具抛异常也必须补上 completed —— 「记录意图 → 记录结果」是一对，
                        // 缺了 completed 会话会从下一轮起每轮 400（悬空 tool_call）。
                        result = ToolResult.Fail($"工具执行异常：{ex.GetType().Name}: {ex.Message}");
                    }
                }

                await _sink.EmitAsync(new ToolCallCompletedEvent
                {
                    SessionId = _options.SessionId,
                    CallId = call.CallId,
                    Success = result.Success,
                    Output = result.Output,
                    Error = result.Error,
                }, ct).ConfigureAwait(false);

                history.Add(new LlmMessage
                {
                    Role = LlmRole.Tool,
                    ToolCallId = call.CallId,
                    Content = result.Success ? result.Output : $"ERROR: {result.Error}",
                });
            }
        }

        // 末步可能已经写出正文，只是还带着工具调用没走完 —— 不要把它丢掉
        return new AgentRunResult(false, lastAssistantText, _options.MaxSteps, "max-steps");
    }

    /// <summary>毫秒计时 —— 用 Stopwatch 时间戳，避开 DateTime 的精度与时钟回拨问题。</summary>
    private static long ElapsedMs(long startedAt)
        => (long)((Stopwatch.GetTimestamp() - startedAt) * 1000.0 / Stopwatch.Frequency);

    private static Dictionary<string, string?> ParseArguments(string json)
    {
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
            // 模型偶尔会给出不合法 JSON —— 当作空参数处理，让工具自己去报错
        }

        return result;
    }
}
