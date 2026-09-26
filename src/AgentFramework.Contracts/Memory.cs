namespace AgentFramework.Contracts;

/// <summary>记忆的层级（DESIGN.md 4.16）。</summary>
public static class MemoryScope
{
    /// <summary>
    /// 全局：跨项目、跨会话的常识与偏好。
    /// **只在非工作模式（闲聊模式）加载** —— 工作时不把"我是谁"塞进每一轮。
    /// </summary>
    public const string Global = "global";

    /// <summary>项目：本工作区的历史、约定、踩过的坑。工作模式加载。</summary>
    public const string Project = "project";

    /// <summary>多项目作用域前缀：project:{绝对目录}。会话钉了项目目录后，
    /// 项目记忆走这个作用域 —— 路径自包含在 id 里，无需注册表即可解析。</summary>
    public const string ProjectPrefix = "project:";

    /// <summary>某项目目录对应的作用域 id（绝对路径归一化后内嵌）。</summary>
    public static string ProjectFor(string projectDir)
        => ProjectPrefix + System.IO.Path.GetFullPath(projectDir).TrimEnd(System.IO.Path.DirectorySeparatorChar);

    /// <summary>从作用域 id 解出项目目录；非项目作用域返回 null。</summary>
    public static string? ProjectDirOf(string scope)
        => scope is not null && scope.StartsWith(ProjectPrefix, StringComparison.Ordinal)
            ? scope[ProjectPrefix.Length..]
            : null;
}

/// <summary>
/// 记忆事件的种类。
///
/// 关键取舍：<b>记忆文件里存的不是「当前有哪些记忆」，而是「记忆发生过哪些变更」</b>。
/// 于是「改主意」与「撤销」都变成一次追加 —— 历史一个字不改，
/// 当前状态由**折叠（fold）**算出来。
///
/// 这与会话事件流是同一套纪律，也正是 Zep/Graphiti 那条「失效而非删除」的路子：
/// <i>回滚是查询，不是重建</i>。好处很实在：可审计、可回放，
/// 而且能同时回答「现在是什么」与「当时是什么」。
/// </summary>
public static class MemoryKinds
{
    /// <summary>记下一条事实。</summary>
    public const string Assert = "assert";

    /// <summary>撤销某条事实（按 id）。</summary>
    public const string Retract = "retract";

    /// <summary>给某条事实升温 —— 被检索命中（recall）或被用户手动置顶时追加。</summary>
    public const string Score = "score";

    /// <summary>降级：移出主视图进归档层（长时间未用且低热度，或用户手动）。可被 restore 撤销。</summary>
    public const string Archive = "archive";

    /// <summary>升级：从归档层恢复到主视图。</summary>
    public const string Restore = "restore";
}

/// <summary>
/// 一条记忆**事件**。
///
/// 与事件的区别：事件是「发生了什么」（可回放），记忆是「我认定的事实」（不可重建）。
/// 所以记忆必须**显式写入**、单独持久化 —— 写错了会一直误导，这比丢一条事件严重得多。
/// </summary>
public sealed record MemoryEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>
    /// 事件种类，见 <see cref="MemoryKinds"/>。
    /// 旧文件里缺这个字段时按 <c>assert</c> 处理（向后兼容，不必迁移）。
    /// </summary>
    public string Kind { get; init; } = MemoryKinds.Assert;

    /// <summary>所属层级（<see cref="MemoryScope"/>）。</summary>
    public required string Scope { get; init; }

    /// <summary>事实内容；<c>retract</c> 事件为空串。</summary>
    public required string Text { get; init; }

    /// <summary>
    /// 可选**槽位键**：同一槽位上的新事实会自动**取代**旧事实。
    ///
    /// 这是「改主意」的结构性表达（零模型参与）：
    /// 比如槽位「构建命令」先记 npm、后记 pnpm —— 后者一写，前者即退出当前视图，
    /// 既不需要模型判断谁更新，也不会留下互相矛盾的两条。
    /// </summary>
    public string? Slot { get; init; }

    /// <summary><c>retract</c> 事件指向哪条（被撤销那条的 <see cref="Id"/>）。</summary>
    public string? TargetId { get; init; }

    public List<string> Tags { get; init; } = [];

    /// <summary>系统记下这条的时间 —— 双时间戳里的「我知道的时间」。</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>来源会话（便于回溯「这话是在哪儿说的」）。</summary>
    public string? SourceSession { get; init; }

    /// <summary>user = 用户明说的；agent = agent 自己记的。</summary>
    public string Source { get; init; } = "agent";

    /// <summary>
    /// 温度分 —— 「被用过的次数」。这是**事件**（只增不减），当前热度由 Fold 折叠出：
    /// assert 带初始分，score 事件累加，被 retract / 同 slot 取代的条目整体退出视图。
    /// 与 ContextCompactedEvent.MaskedSeqs 同一个设计观：文件存变更，不存状态。
    /// 旧文件缺这个字段按 0 处理 —— 向后兼容，不必迁移。
    /// </summary>
    public int Score { get; init; }

    /// <summary>这条是否曾被用户显式标记为重要（score 事件携带，随条目进入视图）。</summary>
    public bool IsImportant { get; init; }

    /// <summary>
    /// **视图字段**（由 <c>Fold</c> 折叠填充，<b>绝不落盘、不进事件流</b>）：
    /// 最近一次「被使用」的时间 —— 显式召回命中，或被本轮检索消费。
    ///
    /// Why：旧降档判据只看 <see cref="CreatedAt"/>（创建时间），于是「31 天前创建、昨天刚用过」
    /// 的记忆会被误扫。价值守恒的是「还用不用」，不是「存了多久」。这个字段让「很久没用」可被表达，
    /// 由 score 事件的时间戳折叠得出 —— 零新增落盘字段。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public DateTimeOffset? LastUsedAt { get; init; }

    /// <summary>
    /// **视图字段**（由 <c>Fold</c> 折叠填充，不落盘）：被使用的累计次数（频率）—— 降档判据的主信号。
    ///
    /// 与 <see cref="Score"/> 的分工：Score 是「排序热度」（谁常驻索引卡），
    /// UseCount 是「使用频率」（谁该继续活着）。隐式消费（delta=0）只涨 UseCount、不涨 Score，
    /// 所以两者必须分开算 —— 否则常驻条目会自我强化、把索引卡锁死。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int UseCount { get; init; }

    /// <summary>这条是否是一次「撤销」事件。</summary>
    public bool IsRetraction => string.Equals(Kind, MemoryKinds.Retract, StringComparison.Ordinal);
}

/// <summary>
/// 记忆降档策略 —— 判据是「调用频率」，时间是衰减曲线而非门槛。
///
/// 有效热度：<c>(1 + 使用次数) × 0.5^(闲置天数 / 半衰期)</c>。
///   · 使用次数来自「使用事件」（显式召回 delta&gt;0 + 本轮检索消费 delta=0），由 Fold 折叠得出；
///   · 起点 1 是"当初认定值得记"的先验，让新条目按半衰期自然衰减，而不是一创建就判死；
///   · 每用一次：次数 +1、闲置时钟归零 → 热度只增不减 → <b>老而常用永不掉线</b>。
/// 寿命因此是<b>频率的函数</b>（用一次推后一个半衰期），而不是固定的"30 天"。
/// 时间只在衰减指数里出现一次，绝不单独作为"该不该降档"的门槛。
/// </summary>
public sealed record SweepPolicy
{
    /// <summary>有效热度低于此值即休眠（归档）。默认 0.5 —— 约等于「半个先验已被衰减光」。</summary>
    public double MinEffectiveScore { get; init; } = 0.5;

    /// <summary>
    /// 热度半衰期（天）：闲置这么久，有效热度减半。
    /// 时间**只在此处出现** —— 作为衰减曲线的参数，不是门槛。
    /// </summary>
    public double HalfLifeDays { get; init; } = 30;

    public static SweepPolicy Default { get; } = new();
}

/// <summary>
/// 记忆存储。
///
/// 与 <see cref="ISessionIndex"/> 的根本差别：**索引可以丢，记忆不能丢**。
/// 索引坏了重新从日志建；记忆文件坏了就是真丢了 —— 所以它是真相源，写入只追加。
/// </summary>
public interface IMemoryStore
{
    string Kind { get; }

    /// <summary>
    /// 追加一条记忆（<c>assert</c> 事件）。
    ///
    /// <paramref name="slot"/> 是可选**槽位键**：同一槽位上的新事实会自动取代旧的，
    /// 于是「改主意」不必先删再写，也不会留下互相矛盾的两条。
    /// </summary>
    ValueTask<MemoryEntry> AppendAsync(
        string scope,
        string text,
        IEnumerable<string>? tags = null,
        string? sourceSession = null,
        string source = "agent",
        string? slot = null,
        CancellationToken ct = default);

    /// <summary>
    /// 撤销一条记忆 —— 追加一条 <c>retract</c> 事件，**不改动原有记录**。
    /// 目标不存在时返回 null（已撤销过、或 id 写错，都不算错误）。
    /// </summary>
    ValueTask<MemoryEntry?> RetractAsync(
        string scope,
        string id,
        string source = "user",
        CancellationToken ct = default);

    /// <summary>按层级读取最近的若干条。</summary>
    ValueTask<IReadOnlyList<MemoryEntry>> LoadAsync(string scope, int limit, CancellationToken ct = default);

    /// <summary>按关键词检索（<paramref name="scope"/> 为 null 时跨层级）。</summary>
    ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(
        string query,
        string? scope,
        int limit,
        CancellationToken ct = default);

    ValueTask<int> CountAsync(string scope, CancellationToken ct = default);

    /// <summary>
    /// 给一条记忆「升温」—— 追加一条 <c>score</c> 事件，只增不减（事件化，同 append-only 纪律）。
    /// 目标不存在或已被撤销/取代时返回 null，不算错误。返回 null 时调用方**不得**把正文写进上下文。
    /// </summary>
    ValueTask<MemoryEntry?> RecordHitAsync(
        string scope,
        string id,
        int delta = 1,
        bool markImportant = false,
        CancellationToken ct = default);

    /// <summary>
    /// 降级：把主视图里的一条记忆移入归档层（追加 <c>archive</c> 事件，原文永不删）。
    /// 归档条目不进索引卡、不进主检索 —— 降级的意义就是降噪。目标不在主视图时返回 null。
    /// </summary>
    ValueTask<MemoryEntry?> ArchiveAsync(
        string scope,
        string id,
        string source = "user",
        CancellationToken ct = default);

    /// <summary>升级：把归档条目恢复到主视图（追加 <c>restore</c> 事件）。目标不在归档层时返回 null。</summary>
    ValueTask<MemoryEntry?> RestoreAsync(
        string scope,
        string id,
        string source = "user",
        CancellationToken ct = default);

    /// <summary>
    /// 记忆合并：把同主题的 K 条旧记忆折叠成一条新记忆 ——
    /// 原条目逐条 retract + 追加一条新 assert（tags 里保留全部来源 id，可审计可回滚）。
    /// 热度继承：新条目取来源中的最高分（它们是被一起合并的一组）。
    /// 任一 id 不在主视图时抛 ArgumentException —— 调用方应先做预检。
    /// </summary>
    ValueTask<MemoryEntry> MergeAsync(
        string scope,
        IReadOnlyList<string> ids,
        string text,
        string? sourceSession = null,
        string source = "user",
        CancellationToken ct = default);

    /// <summary>读归档层的条目（供管理面板；主检索不读这里 —— 降噪是归档的意义）。</summary>
    ValueTask<IReadOnlyList<MemoryEntry>> LoadArchivedAsync(string scope, int limit, CancellationToken ct = default);

    /// <summary>
    /// 给一批记忆记一次「使用」—— 追加 score 事件（<c>delta = 0</c>）：
    /// 只刷新「使用频率 / 最近使用」（决定谁该继续活着），**不动排序热度**。
    ///
    /// 用于「本轮检索命中」这类**隐式使用** —— 让降档判据看到真实的调用频率，
    /// 而不是只看到显式 recall 的那几次。一条 batch 落盘，一轮只付一次 fsync。
    /// 返回实际记上的条数。
    /// </summary>
    ValueTask<int> TouchAsync(
        string scope,
        IReadOnlyCollection<string> ids,
        string? sourceSession = null,
        CancellationToken ct = default);

    /// <summary>
    /// 降档**预览**：按与 <see cref="SweepAsync"/> **完全相同**的判据列出将休眠的条目（只读不写）。
    /// 预览与执行共用一份判据，杜绝"预览说 N 条、执行扫 M 条"的口径漂移。
    /// </summary>
    ValueTask<IReadOnlyList<MemoryEntry>> PreviewSweepAsync(
        string scope,
        DateTimeOffset now,
        SweepPolicy policy,
        CancellationToken ct = default);

    /// <summary>
    /// 降档清扫：把主视图里「**使用频率低于阈值**（按半衰期衰减后）」且未置顶的条目归档，返回条数。
    ///
    /// 判据是<b>调用频率</b>，不是创建时间：一条越用越新的记忆永远不会掉到线下，无论它多老；
    /// 时间只以半衰期的形式参与衰减计算，不作为独立门槛。详见 <see cref="SweepPolicy"/>。
    /// 这是记忆的"睡眠巩固"—— 建议低频调用（面板打开时 / 周期巩固时）。
    /// </summary>
    ValueTask<int> SweepAsync(
        string scope,
        DateTimeOffset now,
        SweepPolicy policy,
        string source = "system",
        CancellationToken ct = default);
}

/// <summary>空记忆存储 —— 未启用记忆时的降级实现，省掉调用方的 null 检查。</summary>
public sealed class NullMemoryStore : IMemoryStore
{
    public static readonly NullMemoryStore Instance = new();

    public string Kind => "none";

    public ValueTask<MemoryEntry> AppendAsync(
        string scope,
        string text,
        IEnumerable<string>? tags = null,
        string? sourceSession = null,
        string source = "agent",
        string? slot = null,
        CancellationToken ct = default)
        => ValueTask.FromResult(new MemoryEntry
        {
            Scope = scope,
            Text = text,
            Tags = tags is null ? [] : [.. tags],
            SourceSession = sourceSession,
            Source = source,
            Slot = slot,
        });

    public ValueTask<MemoryEntry?> RetractAsync(
        string scope,
        string id,
        string source = "user",
        CancellationToken ct = default)
        => ValueTask.FromResult<MemoryEntry?>(null);

    public ValueTask<IReadOnlyList<MemoryEntry>> LoadAsync(string scope, int limit, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<MemoryEntry>>([]);

    public ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(
        string query,
        string? scope,
        int limit,
        CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<MemoryEntry>>([]);

    public ValueTask<int> CountAsync(string scope, CancellationToken ct = default) => ValueTask.FromResult(0);

    public ValueTask<MemoryEntry?> RecordHitAsync(
        string scope,
        string id,
        int delta = 1,
        bool markImportant = false,
        CancellationToken ct = default)
        => ValueTask.FromResult<MemoryEntry?>(null);

    public ValueTask<MemoryEntry?> ArchiveAsync(
        string scope, string id, string source = "user", CancellationToken ct = default)
        => ValueTask.FromResult<MemoryEntry?>(null);

    public ValueTask<MemoryEntry?> RestoreAsync(
        string scope, string id, string source = "user", CancellationToken ct = default)
        => ValueTask.FromResult<MemoryEntry?>(null);

    public ValueTask<MemoryEntry> MergeAsync(
        string scope, IReadOnlyList<string> ids, string text,
        string? sourceSession = null, string source = "user", CancellationToken ct = default)
        => throw new InvalidOperationException("空记忆存储不支持合并");

    public ValueTask<IReadOnlyList<MemoryEntry>> LoadArchivedAsync(
        string scope, int limit, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<MemoryEntry>>([]);

    public ValueTask<int> TouchAsync(
        string scope, IReadOnlyCollection<string> ids, string? sourceSession = null, CancellationToken ct = default)
        => ValueTask.FromResult(0);

    public ValueTask<IReadOnlyList<MemoryEntry>> PreviewSweepAsync(
        string scope, DateTimeOffset now, SweepPolicy policy, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<MemoryEntry>>([]);

    public ValueTask<int> SweepAsync(
        string scope, DateTimeOffset now, SweepPolicy policy,
        string source = "system", CancellationToken ct = default)
        => ValueTask.FromResult(0);
}

/// <summary>工作模式。对应 dsh 的 session mode 概念（DESIGN.md 决策记录 2026-09-20）。</summary>
public enum AgentMode
{
    /// <summary>干活：全工具、全治理、项目记忆。</summary>
    Work,

    /// <summary>闲聊：不挂工具、不治理、只读全局记忆 —— 图的就是"少调用、少花钱"。</summary>
    Chat,

    /// <summary>
    /// 设计对话（G4）：全工具 + 项目记忆 + 加大记忆索引。
    /// 为「设计对话可拉出来单独一档」的原始诉求留的档位：
    /// 与工作模式的差异在提示词基调（先澄清视觉意图再动手）与更大的记忆常驻。
    /// </summary>
    Design,
}

/// <summary>
/// 一个模式的档位：决定"这一轮暴露什么"。
///
/// 关键取舍：模式**不改变装配**（插件、工具、索引都照常注册），
/// 只决定这一轮给模型看什么 —— 所以切换无需重启。
/// </summary>
public sealed class ModeProfile
{
    /// <summary>内置三档的模式枚举。插件注册的自定义档位不使用此字段（用 <see cref="CustomId"/>），保留默认值仅为兼容。</summary>
    public AgentMode Mode { get; init; } = AgentMode.Work;

    /// <summary>插件注册的自定义模式 id（如 “tavern”）。内置三档为 null。会话以**字符串 id** 钉住模式，新增档位无需改枚举。</summary>
    public string? CustomId { get; init; }

    public required string Name { get; init; }

    /// <summary>要加载哪些记忆层级。空数组 = 一条记忆都不加载。</summary>
    public required IReadOnlyList<string> MemoryScopes { get; init; }

    /// <summary>暴露给模型的工具名白名单；<c>null</c> = 全部工具。</summary>
    public IReadOnlyList<string>? AllowedTools { get; init; }

    /// <summary>
    /// 这一档位只允许哪些<b>工具包</b>；<c>null</c> = 不限（默认）。
    ///
    /// <para>
    /// 与 <see cref="AllowedTools"/> 的关系：两者取交集 ——
    /// 包是「一档模式大概要哪几类能力」的粗筛，逐工具名是细调，两个维度都留着。
    /// </para>
    /// </summary>
    public IReadOnlyList<string>? AllowedToolsets { get; init; }

    public bool InjectTaskCard { get; init; } = true;

    /// <summary>
    /// 是否把「工作小本本」的摘要复述进上下文（DESIGN.md 4.17）。
    /// 它走动态段 —— 与任务卡并列，都是「每轮都给、但绝不许进前缀」的东西。
    /// </summary>
    public bool InjectNotes { get; init; } = true;

    /// <summary>小本本摘要的字符上限 —— 与任务卡一样，恒定大小，不随本子变厚而增长。</summary>
    public int NotesSummaryMaxChars { get; init; } = 600;

    /// <summary>是否启用上下文治理（预算/遮蔽/压缩）。</summary>
    public bool ContextGovernance { get; init; } = true;

    /// <summary>
    /// **常驻索引卡**条数上限（0 = 不常驻）。进「冻结段」。
    ///
    /// 索引卡回答「我是谁」：少量、稳定、硬限长 —— 变动它会打掉前缀缓存，
    /// 所以条数刻意小，剩下的交给按需检索。
    /// </summary>
    public int MemoryIndexLimit { get; init; } = 8;

    /// <summary>索引卡字符上限 —— 恒定大小，不随记忆总量增长。</summary>
    public int MemoryIndexMaxChars { get; init; } = 800;

    /// <summary>
    /// **每轮按当前输入检索**、注入尾部的条数上限（0 = 不检索）。进「动态段」。
    /// </summary>
    public int MemoryRecallLimit { get; init; } = 5;

    /// <summary>检索块字符上限。</summary>
    public int MemoryRecallMaxChars { get; init; } = 1_200;

    /// <summary>
    /// 是否把「本轮检索命中」计为一次**使用**（续命信号，供降档判据看到真实调用频率）。
    ///
    /// 默认开。不这样做的话，降档只看得到显式 <c>recall_memory</c> 的那几次 ——
    /// 「每轮都在被静默使用」的高频记忆反而会被当成冷条目，激励是反的。
    /// 实现上只写一条 batch（delta=0），<b>不动排序热度</b>，一轮一次 fsync。
    /// </summary>
    public bool RecordRecallAsUse { get; init; } = true;

    /// <summary>
    /// 常驻索引卡与「本轮召回」的**排序策略**（默认仍是插入序，行为与 v3.4 一致）。
    ///
    /// Why now：排序属于装配决策，放进档位让「闲聊仍省、工作更聪明」成为一行配置，
    /// 而不是在存储层里悄悄改语义。
    /// </summary>
    public MemoryPriorityStrategy PriorityStrategy { get; init; } = MemoryPriorityStrategy.Insertion;

    /// <summary>拼到系统提示词末尾的模式说明。</summary>
    public string? SystemPromptSuffix { get; init; }

    /// <summary>这种模式是否把工具暴露给模型。</summary>
    public bool ExposesTools => AllowedTools is null || AllowedTools.Count > 0;
}

/// <summary>
/// 记忆进上下文的排序策略。
///
/// Insertion（默认）：按写入序取最近 N 条 —— 行为与 v3.4 完全一致，测试可回归。
/// Temperature：热度优先（Score 降序），同分按时间新者先 ——
///   让「老而常用」挤掉「新而冷」；初始分让新条目不至于永远排在老热门后面。
/// </summary>
public enum MemoryPriorityStrategy
{
    Insertion,
    Temperature,
}

/// <summary>内置模式档。</summary>
public static class AgentModes
{
    /// <summary>工作模式：全工具、六层治理、项目记忆、热度排序。</summary>
    public static ModeProfile Work { get; } = new()
    {
        Mode = AgentMode.Work,
        Name = "工作模式",
        MemoryScopes = [MemoryScope.Project],
        AllowedTools = null,
        InjectTaskCard = true,
        ContextGovernance = true,
        MemoryIndexLimit = 8,
        MemoryRecallLimit = 5,
        PriorityStrategy = MemoryPriorityStrategy.Temperature,
        SystemPromptSuffix =
            "当前是工作模式。上下文里只有最近几轮对话的原文，更早的内容已折叠为摘要或占位符 —— " +
            "原文永远在会话日志里，需要细节时用 search_history 检索，或重新调用工具，不要凭印象编造。\n" +
            "遇到预计超过 5 次工具调用的检索/阅读/比对任务：用 spawn_subagent 派子 Agent，" +
            "任务描述写清目标、约束与汇报格式；你只消化它返回的结论，不要让中间过程灌进当前对话。\n" +
            "多步骤任务先 update_plan 建计划，每完成一步就更新状态。",
    };

    /// <summary>
    /// 闲聊模式：**不挂工具**、不注入任务卡、不做治理，只读全局记忆。
    ///
    /// 省在四处：工具 schema（最贵的一项固定开销）、任务卡、每轮投影与水位比较、项目记忆检索。
    /// </summary>
    public static ModeProfile Chat { get; } = new()
    {
        Mode = AgentMode.Chat,
        Name = "闲聊模式",
        MemoryScopes = [MemoryScope.Global],
        AllowedTools = [],
        InjectTaskCard = false,
        InjectNotes = false,
        ContextGovernance = false,
        MemoryIndexLimit = 6,
        MemoryRecallLimit = 0,
        SystemPromptSuffix = "当前是闲聊模式：不要调用工具，直接对话；回答简短、自然，不必给方案。",
    };

    /// <summary>设计对话模式（G4）：全工具、项目记忆、更大常驻索引、设计基调提示词、热度排序。</summary>
    public static ModeProfile Design { get; } = new()
    {
        Mode = AgentMode.Design,
        Name = "设计对话",
        MemoryScopes = [MemoryScope.Project],
        AllowedTools = null,
        InjectTaskCard = true,
        ContextGovernance = true,
        MemoryIndexLimit = 12,
        MemoryRecallLimit = 6,
        PriorityStrategy = MemoryPriorityStrategy.Temperature,
        SystemPromptSuffix = "当前是设计对话模式：先澄清视觉意图（风格/受众/参考），再给出方案；" +
            "描述界面时使用具体的层次语言（结构 → 布局 → 组件 → 状态），避免空泛形容词。" +
            "预计超过 5 次工具调用的检索/阅读任务，先派子 Agent，只消化结论。",
    };

    /// <summary>
    /// 编程模式（任务 4）：提示词偏「读代码 → 改代码 → 跑测试」；
    /// 文件链/记忆/计划已收进 core，这里只额外开 exec。
    /// 用 <see cref="ModeProfile.CustomId"/> 标识（字符串 id 钉会话），**不改 <see cref="AgentMode"/> 枚举**。
    /// </summary>
    public static ModeProfile Code { get; } = new()
    {
        CustomId = "code",
        Name = "编程模式",
        MemoryScopes = [MemoryScope.Project],
        AllowedTools = null,
        AllowedToolsets =
        [
            BuiltinToolsets.Core, BuiltinToolsets.Exec,
        ],
        InjectTaskCard = true,
        ContextGovernance = true,
        MemoryIndexLimit = 8,
        MemoryRecallLimit = 5,
        PriorityStrategy = MemoryPriorityStrategy.Temperature,
        SystemPromptSuffix =
            "当前是编程模式：先读懂相关代码再改，改动尽量局部化，改完跑测试验证。\n" +
            "预计超过 5 次工具调用的检索/阅读/比对任务：用 spawn_subagent 派子 Agent。\n" +
            "多步骤任务先 update_plan 建计划，每完成一步就更新状态。",
    };

    /// <summary>
    /// 写作模式（任务 4）：提示词偏「结构与改写」；**默认收起 exec**（跑命令不是写作的常态），
    /// 对接 WritingKit 工具包。工具面与编程模式真实不同 —— 不是只换提示词。
    /// </summary>
    public static ModeProfile Write { get; } = new()
    {
        CustomId = "write",
        Name = "写作模式",
        MemoryScopes = [MemoryScope.Project],
        AllowedTools = null,
        AllowedToolsets =
        [
            BuiltinToolsets.Core, BuiltinToolsets.WritingKit,
        ],
        InjectTaskCard = true,
        ContextGovernance = true,
        MemoryIndexLimit = 8,
        MemoryRecallLimit = 4,
        PriorityStrategy = MemoryPriorityStrategy.Temperature,
        SystemPromptSuffix =
            "当前是写作模式：先定结构（提纲 / 骨架）再落笔；改写重在重新表达，而不是堆砌辞藻。\n" +
            "长文分段推进，每段写完回看与上文的衔接。",
    };

    public static ModeProfile For(AgentMode mode) => mode switch
    {
        AgentMode.Chat => Chat,
        AgentMode.Design => Design,
        _ => Work,
    };

    // ── 创意工坊 / 插件扩展点：自定义工作模式 ─────────────
    // dsh 式插件（酒馆、宠物、纯聊天皮肤…）的本质都是"另一种档位"：
    // 暴露什么工具、读哪层记忆、什么提示词基调。注册即插拔，无需改枚举。
    private static readonly Dictionary<string, ModeProfile> Custom = new(StringComparer.Ordinal);

    /// <summary>
    /// 注册一个自定义模式（插件装配期调用）。
    /// id 与内置档位冲突时**抛异常**（不静默覆盖内置）；插件之间重名则后注册者覆盖。
    /// </summary>
    public static void Register(ModeProfile profile)
    {
        var id = profile.CustomId?.Trim();
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("自定义模式必须设置 CustomId", nameof(profile));
        }

        if (IsBuiltinId(id))
        {
            throw new ArgumentException(
                $"模式 id「{id}」与内置档位冲突（work / chat / design / code / write 不可覆盖）", nameof(profile));
        }

        Custom[id] = profile;
    }

    /// <summary>注销一个自定义模式（插件卸载 / 热重载时调用，避免串档与泄漏）。</summary>
    public static bool Unregister(string id) => Custom.Remove(id.Trim());

    /// <summary>按字符串 id 解析模式档位：内置（work/chat/design/code/write）→ 插件注册表 → 工作模式兜底。</summary>
    public static bool TryResolve(string? id, out ModeProfile profile)
    {
        profile = Work;
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        switch (id.Trim())
        {
            case "work": profile = Work; return true;
            case "chat": profile = Chat; return true;
            case "design": profile = Design; return true;
            case "code": profile = Code; return true;
            case "write": profile = Write; return true;
            default:
                if (Custom.TryGetValue(id.Trim(), out var custom))
                {
                    profile = custom;
                    return true;
                }
                return false;
        }
    }

    public static ModeProfile Resolve(string? id)
        => TryResolve(id, out var profile) ? profile : Work;

    /// <summary>内置档位 id（插件不得覆盖 —— 冲突时显式报错）。</summary>
    public static bool IsBuiltinId(string id) =>
        id is "work" or "chat" or "design" or "code" or "write";

    /// <summary>全部可用档位（内置五档 + 插件注册）—— 新建会话选择器与 /api/modes 的数据源。</summary>
    public static IReadOnlyList<ModeProfile> Available
    {
        get
        {
            var list = new List<ModeProfile> { Work, Design, Chat, Code, Write };
            list.AddRange(Custom.Values);
            return list;
        }
    }

    /// <summary>内置枚举 → 字符串 id（线上协议与存储统一用字符串）。</summary>
    public static string IdOf(AgentMode mode) => mode switch
    {
        AgentMode.Chat => "chat",
        AgentMode.Design => "design",
        _ => "work",
    };
}

/// <summary>
/// 模式注册的撤销句柄（任务 4）：注销时从 <see cref="AgentModes"/> 移除。
/// 由 <see cref="IPluginContext.RegisterMode"/> 返回，被内核作用域收集、卸载时逆序撤销。
/// </summary>
public sealed class ModeRegistration(string id) : IDisposable
{
    public void Dispose() => AgentModes.Unregister(id);
}
