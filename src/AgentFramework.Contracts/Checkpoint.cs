namespace AgentFramework.Contracts;

/// <summary>
/// checkpoint 的触发来源（写进事件，便于审计「这份状态是谁、为什么写的」）。
/// </summary>
public static class CheckpointTrigger
{
    /// <summary>水位跨线，运行时自动触发。</summary>
    public const string Auto = "auto";

    /// <summary>人手动按的（界面按钮 / 命令行）。</summary>
    public const string Manual = "manual";

    /// <summary>为 rebuild 做的最后一次提取 —— 窗口已经要换了。</summary>
    public const string Rebuild = "rebuild";
}

/// <summary>
/// checkpoint 配置。
///
/// <para>
/// 与 <see cref="ContextOptions"/> 的关系是本设计最要紧的一点，别混成一个触发点：
/// </para>
/// <list type="bullet">
///   <item><c>CompressionTriggerRatio</c>（0.8）—— <b>裁剪</b>上下文：换投影函数、必打断前缀缓存 → <b>宁晚不频</b>；</item>
///   <item><c>CheckpointOptions.TriggerRatio</c>（0.35）—— <b>写入</b>磁盘：上下文一个字节都不动 → <b>可以早、可以频繁</b>。</item>
/// </list>
/// <para>
/// 把「写」从「裁」里拆出来独立计时，是这一版从 MiMo Code 借来的核心一条。
/// </para>
/// </summary>
public sealed class CheckpointOptions
{
    /// <summary>
    /// 是否启用 checkpoint。
    ///
    /// <para>
    /// <b>默认关</b>：它每触发一次要花一次模型调用，不该悄悄加在所有会话上（零回归优先）。
    /// 长任务里显式打开 —— 配置项、界面开关，或 agent 自己调工具开。
    /// </para>
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// 水位：估算 token 超过 <c>TokenBudget × 这个值</c> 就写一次 checkpoint。
    ///
    /// <para>
    /// 定得比压缩线（0.8）低得多是<b>有意</b>的 —— 写入不碰上下文，早写只有好处：
    /// 一是提取本身需要空间（MiMo Code 实测口径：95% 利用率下模型已无处思考）；
    /// 二是高利用率时对中段材料的注意力下降（lost in the middle），恰好是最不该做提取的时刻。
    /// 要模型在它压缩能力正在退化时去做最关键的压缩，是桩划不来的交易。
    /// </para>
    /// </summary>
    public double TriggerRatio { get; set; } = 0.35;

    /// <summary>
    /// checkpoint 正文的字符上限。
    /// 它是**恒定大小**的锚点，不是账本 —— 超了砍，宁可少说也绝不自己长成新的上下文负担
    /// （与任务卡同一条纪律，见 <see cref="ContextOptions.TaskCardMaxChars"/>）。
    /// </summary>
    public int MaxChars { get; set; } = 2_000;

    /// <summary>
    /// 距上次 checkpoint 至少新增这么多事件，才值得再写一次（防抖）。
    /// 水位在阈值附近抖动时，不至于连着写一串几乎相同的 checkpoint。
    /// </summary>
    public int MinNewEvents { get; set; } = 8;

    /// <summary>
    /// writer 是否顺带把「稳定下来的观察」升级进记忆（<see cref="IMemoryStore"/>）。
    /// 这就是「主 agent 不维护自己的记忆」的落点：结构化那一层交给 writer。
    /// </summary>
    public bool PromoteMemory { get; set; } = true;

    public CheckpointOptions Clone() => new()
    {
        Enabled = Enabled,
        TriggerRatio = TriggerRatio,
        MaxChars = MaxChars,
        MinNewEvents = MinNewEvents,
        PromoteMemory = PromoteMemory,
    };
}

/// <summary>
/// 一次 checkpoint 请求 —— writer 的输入。
///
/// <para>
/// 刻意<b>不</b>给 writer 工具、也不给它会话的写权限：
/// 它的全部输出就是一份结构化状态。这是 CQS 的物理保证 ——
/// 一个正在调棘手 bug 的模型同时维护结构化日志，往往两件事各做差一件（MiMo Code 的观察，我们认同）。
/// </para>
/// </summary>
public sealed record CheckpointRequest
{
    /// <summary>所属会话。</summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// 迄今的对话（已投影）。调用方决定用哪种投影 ——
    /// 想要「主 agent 当时看到的」就给治理后的视图，想要完整事实就给全量投影。
    /// </summary>
    public required IReadOnlyList<LlmMessage> Messages { get; init; }

    /// <summary>当前任务卡（恒定大小的工作锚点，writer 可直接沿用）。</summary>
    public string? TaskCard { get; init; }

    /// <summary>工作小本本 <c>AGENT_NOTES.md</c> 的正文 —— 主 agent 与人共写的随手记。</summary>
    public string? Notes { get; init; }

    /// <summary>上一份 checkpoint 的渲染块 —— 增量提取的基线（没有就是首次）。</summary>
    public string? PreviousBlock { get; init; }

    /// <summary>触发时的水位（千分比，整数避免浮点序列化噪音）。</summary>
    public int WaterLevelPermille { get; init; }

    /// <summary>触发来源（<see cref="CheckpointTrigger"/>）。</summary>
    public string Trigger { get; init; } = CheckpointTrigger.Auto;

    /// <summary>本次提取覆盖的事件区间（自上次 checkpoint 之后的增量）。</summary>
    public long FromSeq { get; init; }

    public long ToSeq { get; init; }
}

/// <summary>
/// checkpoint writer —— **独立于主 agent 的提取者**。
///
/// <para>
/// 实现应当是一次**旁路模型调用**（同 <see cref="IContextSummarizer"/> 的做法），
/// 不进入主循环、不与主 agent 争注意力与 token 预算。
/// </para>
/// <para>
/// 返回 null 表示「这次不值得写」：没有新进展、或模型没能给出有效结构化输出。
/// 调用方据此跳过，不产生空事件。
/// </para>
/// </summary>
public interface ICheckpointWriter
{
    ValueTask<CheckpointEvent?> WriteAsync(CheckpointRequest request, CancellationToken ct = default);
}
