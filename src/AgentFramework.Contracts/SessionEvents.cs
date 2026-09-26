using System.Text.Json.Serialization;

namespace AgentFramework.Contracts;

/// <summary>
/// 任务状态取值（字符串常量，便于向后兼容地扩展）。
/// 刻意不叫 TaskStatus —— 那会与 BCL 的 System.Threading.Tasks.TaskStatus 撞名。
/// </summary>
public static class AgentTaskStatus
{
    public const string Todo = "todo";
    public const string InProgress = "in-progress";
    public const string Done = "done";
    public const string Failed = "failed";
}

/// <summary>
/// 会话事件基类 —— 整个数据平面的唯一真相源。
///
/// 不变式（DESIGN.md 4.2）：会话状态、任务状态、UI 上看到的一切，
/// 都必须是这些事件<b>投影</b>出来的结果，<b>不得另存一份真相</b>。
/// 遵守这条，恢复 / 分叉 / 回放 / 重启还原就全是白拿的。
///
/// 仅追加：日志里的记录永不修改、永不删除。补状态靠"再追加一条事件"，不靠改历史。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SessionCreatedEvent), "session-created")]
[JsonDerivedType(typeof(UserMessageEvent), "user-message")]
[JsonDerivedType(typeof(AssistantMessageEvent), "assistant-message")]
[JsonDerivedType(typeof(ToolCallRequestedEvent), "tool-call-requested")]
[JsonDerivedType(typeof(ToolCallCompletedEvent), "tool-call-completed")]
[JsonDerivedType(typeof(TaskCreatedEvent), "task-created")]
[JsonDerivedType(typeof(TaskStatusChangedEvent), "task-status-changed")]
[JsonDerivedType(typeof(UserInputRephrasedEvent), "user-input-rephrased")]
[JsonDerivedType(typeof(ContextCompactedEvent), "context-compacted")]
[JsonDerivedType(typeof(ModelUsageEvent), "model-usage")]
[JsonDerivedType(typeof(ReasoningEvent), "reasoning")]
[JsonDerivedType(typeof(SubAgentDispatchedEvent), "subagent-dispatched")]
[JsonDerivedType(typeof(SubAgentCompletedEvent), "subagent-completed")]
[JsonDerivedType(typeof(PlanCreatedEvent), "plan-created")]
[JsonDerivedType(typeof(PlanStepUpdatedEvent), "plan-step-updated")]
[JsonDerivedType(typeof(CheckpointEvent), "checkpoint")]
public abstract class SessionEvent
{
    /// <summary>会话内单调递增序号，由日志写入时分配。</summary>
    public long Seq { get; set; }

    /// <summary>所属会话。</summary>
    public string SessionId { get; set; } = "";

    /// <summary>写入时刻（UTC）。</summary>
    public DateTimeOffset Timestamp { get; set; }
}

/// <summary>会话创建。分叉出的会话用 ParentSessionId / ForkFromSeq 记录血缘。</summary>
public sealed class SessionCreatedEvent : SessionEvent
{
    public string? ProjectId { get; set; }

    public string Title { get; set; } = "";

    /// <summary>来源会话（分叉时非空）。</summary>
    public string? ParentSessionId { get; set; }

    /// <summary>分叉点 —— 源会话中的 Seq。</summary>
    public long? ForkFromSeq { get; set; }
}

/// <summary>
/// 用户消息。
///
/// 两个文本字段各自守一条规矩：
///   - <see cref="Text"/>：**用户原话**。它永远是真相，转述不该把它抹掉。
///   - <see cref="RephrasedText"/>：**模型实际看到的文本**（转述开启且成功时才非空）。
///
/// 主循环与上下文重建统一读 <c>RephrasedText ?? Text</c> ——
/// 于是「模型可见即已记录」这条不变式在转述模式下依然成立：
/// 回放时模型重新看见的，和当时看见的一模一样。
/// </summary>
public sealed class UserMessageEvent : SessionEvent
{
    public string Text { get; set; } = "";

    /// <summary>转述（澄清）后的文本；未转述时为 null。</summary>
    public string? RephrasedText { get; set; }

    /// <summary>产出转述的模型标识（仅诊断与展示用）。</summary>
    public string? RephraseModel { get; set; }

    /// <summary>
    /// 随这条消息发给模型的图（视觉输入）。空/缺省 = 纯文本。
    /// 自带 base64，JSONL 自包含 —— 回放不依赖外链文件。
    /// </summary>
    public List<LlmImage>? Images { get; set; }

    /// <summary>模型实际看到的文本。</summary>
    public string ModelVisibleText =>
        string.IsNullOrWhiteSpace(RephrasedText) ? Text : RephrasedText!;
}

/// <summary>
/// 输入转述的留痕（第 8 种事件）。
///
/// 用途：记录**手动点按钮**优化的那一次交互 —— 原文、结果、模型、耗时。
/// 它<b>不进入模型上下文</b>（<see cref="SessionContextBuilder"/> 不处理它）：
/// 手动的结果只是回填输入框，用户还可能再改；真正进上下文的是用户随后发出的那条 user 消息。
/// </summary>
public sealed class UserInputRephrasedEvent : SessionEvent
{
    public string Original { get; set; } = "";

    public string Rephrased { get; set; } = "";

    /// <summary>manual（点了按钮）/ auto（发送前自动澄清）。</summary>
    public string Source { get; set; } = "manual";

    public string? Model { get; set; }

    public long ElapsedMs { get; set; }

    /// <summary>是否已作为本轮输入真正送入模型。</summary>
    public bool Applied { get; set; }
}

/// <summary>
/// 上下文压缩留痕（第 9 种事件）。
///
/// <b>压缩不删改历史</b> —— 它只是「换一个投影函数」。
/// 这条事件记下"当时压了什么、压到多少、被遮蔽的是哪些序号"，
/// 于是两件事都能回答：
///   1. 模型当时看到了什么（可回放）
///   2. 模型当时<b>没</b>看到什么、以及为什么（可审计）
///
/// 它自己不携带任何状态：投影器遇到它只会做一件事 ——
/// 把 <see cref="MaskedSeqs"/> 里的序号**永久钉死为遮蔽态**，
/// 保证「当时遮蔽过的东西，之后不会突然又展开」（否则历史视图会来回跳）。
/// </summary>
public sealed class ContextCompactedEvent : SessionEvent
{
    /// <summary>auto（超水位）/ manual。</summary>
    public string Trigger { get; set; } = CompactionTrigger.Auto;

    /// <summary>压缩前后的估算 token 水位。</summary>
    public int PreTokens { get; set; }

    public int PostTokens { get; set; }

    /// <summary>被遮蔽的事件序号 —— 不是删除，是「视图遮蔽」。</summary>
    public List<long> MaskedSeqs { get; set; } = [];

    /// <summary>被遮蔽的条目数（便于直接读，不必再数数组）。</summary>
    public int MaskedCount { get; set; }

    /// <summary>被折叠掉的轮次数。</summary>
    public int CollapsedTurns { get; set; }

    /// <summary>L5 摘要（默认关；开启时才有）。</summary>
    public string? Summary { get; set; }

    /// <summary>压缩时的任务卡快照（便于回放"那一刻的任务状态"）。</summary>
    public string? TaskCard { get; set; }

    /// <summary>
    /// 这一次把「逐字保留的轮次窗口」收紧到了多少（P3a）。
    ///
    /// <para>
    /// 存在的理由：窗口收到底（= 1）仍然超预算时，下一轮若还是从默认窗口开始减半，
    /// 就会把整套试算**每轮重跑一遍**，而算出来的结果与上一轮一模一样 —— 纯白算。
    /// 把「已收紧到 X」落进日志，装配时读最后一条当起点，试算次数从 O(log n) 降到常数。
    /// 附带的收益：压过的会话重启后仍保持压缩视图，行为更一致。
    /// </para>
    /// <para>
    /// <b>可空</b>：老日志里没有这个字段，读出来是 null —— 那就回落到默认窗口，
    /// 与旧行为完全一致。所以这是一次向后兼容的**加字段**，不是改语义。
    /// </para>
    /// </summary>
    public int? TightenedTurns { get; set; }
}

/// <summary>
/// 一次模型调用的用量留痕（第 10 种事件）。
///
/// 为什么必须落进事件流：成本与缓存命中率是**事后才有意义**的数字 ——
/// 光在界面上闪一下就没了，等于没有。落进日志才能回答
/// 「这个会话一共花了多少」「缓存到底有没有起作用」。
///
/// 与「上下文水位」的区别：水位是**估算**（<c>TokenEstimator</c> 那一套，
/// 零成本、用于决策），这里是 API **实报**的数据（权威、用于记账）。
/// 两者分开、不互相冒充 —— 否则要么决策不准，要么账目不准。
/// </summary>
public sealed class ModelUsageEvent : SessionEvent
{
    /// <summary>本轮里的第几次模型调用（一次 RunAsync 可能调多轮）。</summary>
    public int Step { get; set; }

    /// <summary>实际服务的端点 / 模型名 —— 诊断「这次是谁在干活」。</summary>
    public string? Model { get; set; }

    public int? InputTokens { get; set; }

    public int? OutputTokens { get; set; }

    public int? CachedTokens { get; set; }

    public int? CacheWriteTokens { get; set; }

    public int? ReasoningTokens { get; set; }

    /// <summary>本次调用总耗时（毫秒）。</summary>
    public long ElapsedMs { get; set; }

    /// <summary>首个 token（含思考）的延迟；一个都没等到就失败时为 null。</summary>
    public long? FirstTokenMs { get; set; }

    /// <summary>本次是否产生了思考内容（界面据此决定要不要渲染思考块）。</summary>
    public bool HasReasoning { get; set; }
}

/// <summary>
/// 思考（推理）内容留痕（第 11 种事件）。
///
/// 它**不进入模型上下文**（<c>SessionContextBuilder</c> 不处理它）——
/// 思考是「模型当时怎么想的」，不是喂给模型的材料。落进日志只为两件事：
/// 界面刷新 / 重启后还能回看，以及事后复盘。
///
/// 内容按上限截断（默认 4000 字符）：思考可以非常长，日志不该被它撑爆。
/// </summary>
public sealed class ReasoningEvent : SessionEvent
{
    public string Text { get; set; } = "";

    public int Step { get; set; }

    public string? Model { get; set; }

    /// <summary>是否因为过长而被截断（界面据此提示「内容已截断」）。</summary>
    public bool Truncated { get; set; }
}

/// <summary>
/// 会话的累计用量 —— 由 <see cref="ModelUsageEvent"/> **投影**而来，不另存状态。
///
/// 「状态只能是投影」这条纪律在这里的收益很直接：重启后、换界面、事后复盘，
/// 拿到的都是同一份数字，不存在「界面显示的与日志里对不上」这种问题。
/// </summary>
public sealed record SessionUsage(
    int Calls,
    int UnknownCalls,
    int InputTokens,
    int OutputTokens,
    int CachedTokens,
    int CacheWriteTokens,
    int ReasoningTokens)
{
    /// <summary>没有任何调用记录时的空账本。</summary>
    public static SessionUsage Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);

    public int TotalTokens => InputTokens + OutputTokens;

    /// <summary>
    /// 缓存命中率；一次都没报过输入 token 时为 <c>null</c> ——
    /// 宁可显示「未知」，也不给一个看着像真的假 0%。
    /// </summary>
    public double? CacheHitRate => InputTokens > 0 ? (double)CachedTokens / InputTokens : null;

    public static SessionUsage From(IEnumerable<SessionEvent> events)
    {
        var calls = 0;
        var unknown = 0;
        var input = 0;
        var output = 0;
        var cached = 0;
        var cacheWrite = 0;
        var reasoning = 0;

        foreach (var sessionEvent in events)
        {
            if (sessionEvent is not ModelUsageEvent usage)
            {
                continue;
            }

            calls++;

            if (usage.InputTokens is null)
            {
                unknown++;
            }

            input += usage.InputTokens ?? 0;
            output += usage.OutputTokens ?? 0;
            cached += usage.CachedTokens ?? 0;
            cacheWrite += usage.CacheWriteTokens ?? 0;
            reasoning += usage.ReasoningTokens ?? 0;
        }

        return new SessionUsage(calls, unknown, input, output, cached, cacheWrite, reasoning);
    }
}

/// <summary>模型回复。</summary>
public sealed class AssistantMessageEvent : SessionEvent
{
    public string Text { get; set; } = "";
}

/// <summary>请求调用工具（工具调用的"意图"先进日志，再执行）。</summary>
public sealed class ToolCallRequestedEvent : SessionEvent
{
    public string CallId { get; set; } = "";

    public string ToolName { get; set; } = "";

    public Dictionary<string, string?> Arguments { get; set; } = [];
}

/// <summary>工具调用完成。</summary>
public sealed class ToolCallCompletedEvent : SessionEvent
{
    public string CallId { get; set; } = "";

    public bool Success { get; set; }

    public string Output { get; set; } = "";

    public string? Error { get; set; }

    /// <summary>
    /// 随结果进上下文的图（read_image 等）。UI 在工具卡下回显；
    /// 投影时在 tool 消息之后以 user 多模态注入（tool 角色不收图）。
    /// </summary>
    public List<LlmImage>? Images { get; set; }
}

/// <summary>任务创建（任务不是容器，只是会话产出的一个待办条目）。</summary>
public sealed class TaskCreatedEvent : SessionEvent
{
    public string TaskId { get; set; } = "";

    public string Title { get; set; } = "";
}

/// <summary>任务状态变更 —— 状态的唯一来源，不写任何独立的"任务表"。</summary>
public sealed class TaskStatusChangedEvent : SessionEvent
{
    public string TaskId { get; set; } = "";

    public string Status { get; set; } = AgentTaskStatus.Todo;

    public string? Reason { get; set; }
}

/// <summary>
/// 子 Agent 派发（G1）。父会话记录「派生了谁 + 去干什么」——
/// 子会话有独立事件流，父流只留指针，投影时不合并子流。
/// </summary>
public sealed class SubAgentDispatchedEvent : SessionEvent
{
    /// <summary>被派生的子会话 id。</summary>
    public string ChildSessionId { get; set; } = "";

    /// <summary>交给子 Agent 的任务描述。</summary>
    public string Task { get; set; } = "";
}

/// <summary>
/// 子 Agent 完成（G1）：父会话只收「结果摘要」（截断到短长度），
/// 全文在子会话自己的日志里 —— 需要细节时投影/检索子会话。
/// </summary>
public sealed class SubAgentCompletedEvent : SessionEvent
{
    public string ChildSessionId { get; set; } = "";

    public bool Success { get; set; }

    /// <summary>结果摘要（派发方截断，约 500 字符）。</summary>
    public string Summary { get; set; } = "";
}

/// <summary>
/// 计划创建（G3）：计划也是事件投影的一部分 —— 不另存"计划表"。
/// Steps 是创建时的步骤清单（有序）。
/// </summary>
public sealed class PlanCreatedEvent : SessionEvent
{
    public string PlanId { get; set; } = "";

    /// <summary>步骤描述，有序。</summary>
    public List<string> Steps { get; set; } = [];
}

/// <summary>计划步骤状态更新（G3）：pending / done / dropped。</summary>
public sealed class PlanStepUpdatedEvent : SessionEvent
{
    public string PlanId { get; set; } = "";

    /// <summary>步骤下标（从 0 起，对应 PlanCreatedEvent.Steps 的位置）。</summary>
    public int Index { get; set; }

    /// <summary>新状态：pending / done / dropped。</summary>
    public string Status { get; set; } = "pending";
}

/// <summary>
/// 长任务的状态锚点（checkpoint）。
///
/// <para>
/// 与 <see cref="ContextCompactedEvent"/> 的分工很清楚：
/// 压缩记的是「模型<b>看不到</b>什么了」，checkpoint 记的是「我们认定现在<b>是什么状态</b>」。
/// </para>
/// <para>
/// 三条纪律：
/// ① <b>不进模型上下文</b> —— 投影器不处理它（与 <see cref="UserInputRephrasedEvent"/> 同），
/// 只在落盘与 rebuild 时被读；
/// ② <b>追加而非覆盖</b> —— 每次 checkpoint 是一条新事件，旧的那条永不改（文件存变更，不存状态）；
/// ③ <b>增量</b> —— <see cref="FromSeq"/> / <see cref="ToSeq"/> 标明这份是从哪一段提取的，
/// 于是「改主意」在时间线上是可见的，而不是被悄悄覆盖掉。
/// </para>
/// </summary>
public sealed class CheckpointEvent : SessionEvent
{
    /// <summary>触发来源（<see cref="CheckpointTrigger"/>）。</summary>
    public string Trigger { get; set; } = CheckpointTrigger.Auto;

    /// <summary>触发时的水位（千分比）。</summary>
    public int WaterLevelPermille { get; set; }

    /// <summary>触发时的估算 token。</summary>
    public int PreTokens { get; set; }

    /// <summary>本份提取覆盖的事件区间（自上次 checkpoint 之后的增量窗口）。</summary>
    public long FromSeq { get; set; }

    public long ToSeq { get; set; }

    // —— 结构化状态 ——
    // 对照 MiMo Code 的 checkpoint 字段，砍掉我们已有事件承载的部分（任务树、计划走 TaskCreated / PlanCreated）

    /// <summary>当前意图 —— 这一路在干什么。</summary>
    public string? Intent { get; set; }

    /// <summary>下一步动作 —— 醒来第一件要做的事。</summary>
    public string? NextAction { get; set; }

    /// <summary>当前工作 —— 手上这个具体活。</summary>
    public string? CurrentWork { get; set; }

    /// <summary>工作约束（不许改的、必须遵守的）。</summary>
    public List<string> Constraints { get; set; } = [];

    /// <summary>涉及文件。</summary>
    public List<string> FilesTouched { get; set; } = [];

    /// <summary>跨任务发现 —— 在别处也成立的事实。</summary>
    public List<string> Discoveries { get; set; } = [];

    /// <summary>错误与修复。</summary>
    public string? ErrorsAndFixes { get; set; }

    /// <summary>设计决策（以及为什么）。</summary>
    public List<string> Decisions { get; set; } = [];

    /// <summary>杂项笔记 —— 上面归类不下的。</summary>
    public string? Notes { get; set; }

    /// <summary>本次顺带升级进记忆的条目 id —— 可审计「这条记忆是谁写的」。</summary>
    public List<string> PromotedMemoryIds { get; set; } = [];

    /// <summary>产出这份 checkpoint 的模型（诊断用）。</summary>
    public string? Model { get; set; }

    public long ElapsedMs { get; set; }

    /// <summary>
    /// 渲染成注入块 —— rebuild 时作为新窗口的种子。
    ///
    /// 空字段不占用行：checkpoint 的价值在于密度，不在于格式齐整。
    /// </summary>
    public string RenderBlock()
    {
        var sb = new System.Text.StringBuilder();

        void Line(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                sb.Append(label).Append('：').AppendLine(value!.Trim());
            }
        }

        void Items(string label, List<string> values)
        {
            if (values.Count > 0)
            {
                sb.Append(label).Append('：').AppendLine(string.Join('；', values));
            }
        }

        Line("当前意图", Intent);
        Line("下一步", NextAction);
        Line("当前工作", CurrentWork);
        Items("工作约束", Constraints);
        Items("涉及文件", FilesTouched);
        Items("跨任务发现", Discoveries);
        Line("错误与修复", ErrorsAndFixes);
        Items("设计决策", Decisions);
        Line("其他", Notes);

        return sb.ToString().TrimEnd();
    }
}
