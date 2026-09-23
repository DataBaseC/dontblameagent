namespace AgentFramework.Contracts;

/// <summary>
/// 长任务上下文治理配置（DESIGN.md 4.15）。
///
/// 分层与默认值都按「实证性价比」定的，不是按优雅程度：
///   L1 预算感知   —— 每轮算一次水位，零开销
///   L2 大输出引用化 —— 工具结果超限就落盘，零模型开销
///   L3 旧结果遮蔽  —— 窗口外的工具结果折叠成一行占位符，零模型开销（实证最有效）
///   L4 任务卡常驻  —— 恒定大小的状态卡，注入到上下文末尾（防漂移）
///   L5 早期摘要    —— **默认关**：LLM 摘要实测让总 token 涨 24–94%，质量无显著提升
/// </summary>
public sealed class ContextOptions
{
    /// <summary>上下文 token 预算（估算值）。</summary>
    public int TokenBudget { get; set; } = 24_000;

    /// <summary>
    /// 水位比例：估算 token 超过 <c>TokenBudget × 这个值</c> 才算「该压缩了」。
    /// 定得偏高是有意的 —— 压缩必然打断前缀缓存，宁晚不频。
    /// </summary>
    public double CompressionTriggerRatio { get; set; } = 0.8;

    /// <summary>最近多少轮对话逐字保留（尾部窗口）。</summary>
    public int RecentTurnsKeptVerbatim { get; set; } = 6;

    /// <summary>是否遮蔽窗口外的旧工具结果（L3）。默认开。</summary>
    public bool MaskOldToolResults { get; set; } = true;

    /// <summary>
    /// 老轮次纯文本回复骨架化（L6）。默认开。
    ///
    /// 只在水位超限时生效（两遍投影：第一遍不骨架化，超限才启用）——
    /// 与「宁晚不频」同一条纪律：无压力不损失信息。
    ///
    /// Why now：v3.4 的遮蔽只挂在工具结果上，窗口外的 user/assistant 消息逐字全保留 ——
    /// 纯对话长会话（或对话占比高的会话）上下文无界增长，压缩器收到底也降不下水位。
    /// 骨架化把「旧回答的正文」从视野里拿掉（占位符几十字符），
    /// 原文仍完整在日志里，可 search_history 检索 —— 遵循同一条「卸载而非丢失」。
    /// 用户的旧消息**不骨架化**：它们是意图锚点，体积小、信息密度高。
    /// 更彻底的折叠（连 user 一起摘要）走 L5，默认关、需本地摘要器。
    /// </summary>
    public bool SkeletonizeOldAssistant { get; set; } = true;

    /// <summary>遮蔽后占位符的字符上限。</summary>
    public int MaskedNoteMaxChars { get; set; } = 240;

    /// <summary>
    /// 遮蔽必须至少省下这么多字符才值得做。
    ///
    /// 这条门槛是被实测逼出来的：L2 引用化之后，一条旧结果往往只剩几百字符，
    /// 再折叠成占位符只省下十几个字符 —— 那不是"压缩"，是"换个说法"，白白丢掉信息。
    /// 所以宁可**不遮蔽**，也不要无谓替换。
    /// </summary>
    public int MinMaskSavingChars { get; set; } = 200;

    /// <summary>工具结果超过这个字符数就落盘（L2）。</summary>
    public int InlineResultLimit { get; set; } = 2_000;

    /// <summary>落盘后仍留在上下文里的头部片段长度。</summary>
    public int InlineHeadChars { get; set; } = 400;

    /// <summary>落盘后仍留在上下文里的尾部片段长度。</summary>
    public int InlineTailChars { get; set; } = 200;

    /// <summary>是否注入任务卡（L4）。默认开。</summary>
    public bool InjectTaskCard { get; set; } = true;

    /// <summary>任务卡字符上限 —— 它是**恒定大小**的，不随会话增长。</summary>
    public int TaskCardMaxChars { get; set; } = 1_200;

    /// <summary>是否用模型摘要早期历史（L5）。**默认关**，有实证理由。</summary>
    public bool SummarizeOlderHistory { get; set; }

    /// <summary>压缩后最多保留多少条任务卡级别的进度条目。</summary>
    public int TaskCardMaxItems { get; set; } = 8;

    public ContextOptions Clone() => new()
    {
        TokenBudget = TokenBudget,
        CompressionTriggerRatio = CompressionTriggerRatio,
        RecentTurnsKeptVerbatim = RecentTurnsKeptVerbatim,
        MaskOldToolResults = MaskOldToolResults,
        SkeletonizeOldAssistant = SkeletonizeOldAssistant,
        MaskedNoteMaxChars = MaskedNoteMaxChars,
        MinMaskSavingChars = MinMaskSavingChars,
        InlineResultLimit = InlineResultLimit,
        InlineHeadChars = InlineHeadChars,
        InlineTailChars = InlineTailChars,
        InjectTaskCard = InjectTaskCard,
        TaskCardMaxChars = TaskCardMaxChars,
        SummarizeOlderHistory = SummarizeOlderHistory,
        TaskCardMaxItems = TaskCardMaxItems,
    };

    /// <summary>
    /// 全量投影：什么都不裁。
    /// 给「老接口」用 —— <c>SessionContextBuilder.Build(events)</c> 保持原语义，
    /// 老调用方与既有验证不受影响；治理只在新入口 <c>Project</c> 上默认生效。
    /// </summary>
    public static ContextOptions Full => new()
    {
        TokenBudget = int.MaxValue,
        RecentTurnsKeptVerbatim = int.MaxValue,
        MaskOldToolResults = false,
        SkeletonizeOldAssistant = false,
        InjectTaskCard = false,
        SummarizeOlderHistory = false,
    };
}

/// <summary>
/// 装配好的上下文，**显式区分「冻结段」与「动态段」**。
///
/// 为什么要有这个类型，而不是直接拼一个 <c>List&lt;LlmMessage&gt;</c>：
/// 前缀缓存的规则是「从请求开头到断点必须逐字节一致」——
/// 只要有一处把**每轮会变**的内容混进前缀，整段缓存就白建。
/// 所以「哪些进前缀、哪些不进」不能只靠注释约定，得靠**结构**表达。
///
/// 请求的真实顺序（OpenAI / Anthropic 一致）：<c>tools → system → messages</c>。
/// 于是 messages 内部分两段：
///   <c>[0, FrozenCount)</c>  —— 冻结段：会话内不变，是缓存前缀的一部分
///   <c>[FrozenCount, …)</c>  —— 动态段：每轮可变，必须排在冻结段之后
///
/// 一句话：**静态在前、动态在后，且不让动态内容的位置影响静态内容。**
/// </summary>
public sealed record AssembledContext(IReadOnlyList<LlmMessage> Messages, int FrozenCount)
{
    /// <summary>冻结段（缓存前缀的一部分）。</summary>
    public IReadOnlyList<LlmMessage> Frozen => [.. Messages.Take(FrozenCount)];

    /// <summary>动态段（每轮可变）。</summary>
    public IReadOnlyList<LlmMessage> Dynamic => [.. Messages.Skip(FrozenCount)];

    /// <summary>冻结段里一行说明都没有时的退化装配（全部视为动态）。</summary>
    public static AssembledContext AllDynamic(IReadOnlyList<LlmMessage> messages) => new(messages, 0);
}

/// <summary>压缩触发方式（写进 <see cref="ContextCompactedEvent"/>，便于审计）。</summary>
public static class CompactionTrigger
{
    public const string Auto = "auto";
    public const string Manual = "manual";
}

/// <summary>
/// 上下文摘要器（L5）。
///
/// 只在 <see cref="ContextOptions.SummarizeOlderHistory"/> 显式打开时才被调用，
/// 且应当接在**本地小模型**上（同 4.10 的摘要器）。
/// </summary>
public interface IContextSummarizer
{
    /// <summary>把一段历史对话压成一段摘要（保持任务目标、约束、已完成结论）。</summary>
    ValueTask<string> SummarizeHistoryAsync(
        IReadOnlyList<LlmMessage> messages,
        string? taskCard,
        CancellationToken ct = default);
}
