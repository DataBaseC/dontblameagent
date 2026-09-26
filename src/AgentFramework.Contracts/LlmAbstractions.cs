namespace AgentFramework.Contracts;

/// <summary>消息角色。</summary>
public static class LlmRole
{
    public const string System = "system";
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string Tool = "tool";
}

/// <summary>
/// 一张进模型上下文的图（视觉模型的多模态输入）。
/// 自带 base64 正文 —— 事件真相源与请求体都不依赖外链，回放时「模型可见即已记录」。
/// </summary>
public sealed record LlmImage
{
    /// <summary>MIME：image/png · image/jpeg · image/webp · image/gif。</summary>
    public required string MediaType { get; init; }

    /// <summary>base64 正文（不含 data: 前缀）。</summary>
    public required string Base64Data { get; init; }

    /// <summary>可选文件名（展示用）。</summary>
    public string? FileName { get; init; }

    /// <summary>OpenAI 系的 <c>image_url</c> data URL 形态。</summary>
    public string ToDataUrl() => $"data:{MediaType};base64,{Base64Data}";

    /// <summary>从 data URL 解析；失败返回 null。</summary>
    public static LlmImage? TryParseDataUrl(string? dataUrl, string? fileName = null)
    {
        if (string.IsNullOrWhiteSpace(dataUrl))
        {
            return null;
        }

        const string prefix = "data:";
        if (!dataUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var comma = dataUrl.IndexOf(',');
        if (comma <= prefix.Length)
        {
            return null;
        }

        var meta = dataUrl[prefix.Length..comma];
        var base64 = dataUrl[(comma + 1)..];
        var mediaType = meta.Split(';')[0].Trim();
        if (mediaType.Length == 0)
        {
            mediaType = "image/png";
        }

        if (!mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new LlmImage { MediaType = mediaType, Base64Data = base64, FileName = fileName };
    }
}

/// <summary>模型调用请求。</summary>
public sealed record LlmMessage
{
    public required string Role { get; init; }

    public string? Content { get; init; }

    /// <summary>
    /// 随本条消息进上下文的图（视觉输入）。空 = 纯文本消息。
    /// 请求体里会展开成 OpenAI 多模态 content 数组。
    /// </summary>
    public IReadOnlyList<LlmImage>? Images { get; init; }

    /// <summary>role=tool 时，指的是哪次工具调用的结果。</summary>
    public string? ToolCallId { get; init; }

    /// <summary>role=assistant 时，模型请求的工具调用。</summary>
    public IReadOnlyList<ToolCallRequest>? ToolCalls { get; init; }
}

/// <summary>一次工具调用请求（模型产出）。</summary>
public sealed record ToolCallRequest(string CallId, string ToolName, string ArgumentsJson);

/// <summary>暴露给模型的工具 schema。</summary>
public sealed record ToolSchema(string Name, string Description, string ParametersJsonSchema);

/// <summary>
/// 一次模型调用的**用量账目**（API 实报，不是估算）。
///
/// 所有字段可空 —— 这是刻意的：<b>没有就是「不知道」，不是 0</b>。
/// 各家协议给的字段并不一样（OpenAI 的 cached 是 prompt 的子集，
/// DeepSeek 的 hit/miss 是 prompt 的拆分，Anthropic 的 input 干脆不含缓存），
/// 用 0 冒充缺失会把成本统计悄悄算错。
/// </summary>
public sealed record LlmUsage
{
    /// <summary>输入 token 总数（含缓存命中部分）。</summary>
    public int? InputTokens { get; init; }

    public int? OutputTokens { get; init; }

    /// <summary>命中前缀缓存的输入 token。</summary>
    public int? CachedTokens { get; init; }

    /// <summary>写入缓存的输入 token（Anthropic 专有；OpenAI 系为 null）。</summary>
    public int? CacheWriteTokens { get; init; }

    /// <summary>思考（推理）token。</summary>
    public int? ReasoningTokens { get; init; }

    /// <summary>
    /// 缓存命中率（0–1）。算不出就是 null —— 宁可显示「未知」，也不显示一个假的 0%。
    /// </summary>
    public double? CacheHitRate =>
        InputTokens is > 0 && CachedTokens is not null
            ? (double)CachedTokens.Value / InputTokens.Value
            : null;
}

/// <summary>LLM 请求。</summary>
public sealed record LlmRequest
{
    public required string Model { get; init; }

    public string? SystemPrompt { get; init; }

    public required IReadOnlyList<LlmMessage> Messages { get; init; }

    public IReadOnlyList<ToolSchema> Tools { get; init; } = [];

    public double Temperature { get; init; } = 0.7;

    /// <summary>
    /// 思考强度档位（null = 不注入，端点用自己的默认）。
    ///
    /// 注入方式由端点的「参数风格」决定（见 <see cref="OpenAiCompatibleOptions.ReasoningStyle"/>）：
    /// OpenAI 系是 <c>reasoning_effort</c>；Qwen 系本地端点是消息里的 <c>enable_thinking / thinking_budget</c>。
    /// 端点不认的参数大多忽略或报 400 —— 所以风格选择权交给每个端点的配置。
    /// </summary>
    public string? ReasoningEffort { get; init; }

    /// <summary>
    /// 是否要求端点回报用量。
    ///
    /// 默认开：OpenAI 系必须显式带 <c>stream_options.include_usage</c> 才会报；
    /// DeepSeek 无论开关都会报。要它不要它，都在客户端按此开关决定。
    /// </summary>
    public bool IncludeUsage { get; init; } = true;
}

/// <summary>
/// 流式返回块。
/// 设计取舍：<b>文本真流式，工具调用等流结束再一次性给出</b> ——
/// 工具调用必须凑齐完整 JSON 才能执行，逐片吐出去对上层没有价值。
/// </summary>
public abstract record LlmStreamChunk
{
    /// <summary>文本增量。</summary>
    public sealed record TextDelta(string Text) : LlmStreamChunk;

    /// <summary>
    /// 思考（推理）增量 —— 与正文分开的一条通道。
    ///
    /// 只有提供 reasoning 通道的模型才会给（如 DeepSeek 的 <c>reasoning_content</c>）。
    /// 它<b>不进模型上下文</b>，只给界面看：让用户知道「模型在往哪个方向想」。
    /// </summary>
    public sealed record ReasoningDelta(string Text) : LlmStreamChunk;

    /// <summary>本次请求的用量账目（出现时机各家不同，见客户端实现）。</summary>
    public sealed record UsageReady(LlmUsage Usage, string? Model = null) : LlmStreamChunk;

    /// <summary>本轮模型请求的全部工具调用（流结束时给出）。</summary>
    public sealed record ToolCallsReady(IReadOnlyList<ToolCallRequest> Calls) : LlmStreamChunk;

    /// <summary>本轮结束。</summary>
    public sealed record Completed(string FinishReason) : LlmStreamChunk;
}

/// <summary>
/// LLM 客户端 —— 一个 capability seam。
/// 云端模型与本地模型（LM Studio）都实现它，因而可以互换、可以路由。
/// </summary>
public interface ILlmClient
{
    /// <summary>目标名（如 cloud / local）。</summary>
    string Name { get; }

    IAsyncEnumerable<LlmStreamChunk> StreamAsync(LlmRequest request, CancellationToken ct = default);
}
