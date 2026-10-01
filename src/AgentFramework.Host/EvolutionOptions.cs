namespace AgentFramework.Host;

/// <summary>
/// 记忆与技能的**进化**配置（PLAN-memory-evolution 支柱四：Dream / Distill）。
///
/// 触发口径刻意用<b>累计回合数</b>而不是挂钟（MiMo 用 7 天 / 30 天）：
/// 桌面宿主不该常驻定时器，而且「攒够 N 轮经验就整理一次」比「过了一周」
/// 更贴近「经验攒够了」这个真实语义。
///
/// 两个动作的默认值不对称，是有意的：
///   · <b>Dream</b>（记忆整理）默认开 —— 它只经既有记忆事件（merge/archive）动记忆，
///     可审计、可 restore 恢复，且判据就是「巩固」按钮那套；
///   · <b>Distill</b>（技能固化）默认关 —— 它会往 skills/ 写文件，
///     属于「改变工作区」的动作，要用户显式打开。
/// </summary>
public sealed class EvolutionOptions
{
    /// <summary>Dream 周期（累计回合数）。0 = 关闭。</summary>
    public int DreamEveryTurns { get; set; } = 30;

    /// <summary>Distill 周期（累计回合数）。0 = 关闭（默认关，见类注释）。</summary>
    public int DistillEveryTurns { get; set; }

    /// <summary>一轮 Dream 最多合并多少组（防大库一次改太多；余量留给下一轮）。</summary>
    public int MaxMergeGroups { get; set; } = 20;

    /// <summary>降档清扫是否参与 Dream（默认开；判据与「巩固」按钮完全同源）。</summary>
    public bool SweepInDream { get; set; } = true;

    /// <summary>Distill：一个调用模式至少出现这么多天才算「反复出现」。</summary>
    public int DistillMinOccurrences { get; set; } = 3;

    /// <summary>Distill：一个模式至少横跨这么多会话才算「工作方式」而不是「一次巧合」。</summary>
    public int DistillMinSessions { get; set; } = 2;

    /// <summary>一轮 Distill 最多固化几个技能（宁少勿滥 —— 技能是拿来用的，不是收藏）。</summary>
    public int DistillMaxSkills { get; set; } = 3;

    /// <summary>Distill 扫描最近多少个会话日志（按写入时间取最近）。</summary>
    public int DistillRecentSessions { get; set; } = 20;

    public EvolutionOptions Clone() => new()
    {
        DreamEveryTurns = DreamEveryTurns,
        DistillEveryTurns = DistillEveryTurns,
        MaxMergeGroups = MaxMergeGroups,
        SweepInDream = SweepInDream,
        DistillMinOccurrences = DistillMinOccurrences,
        DistillMinSessions = DistillMinSessions,
        DistillMaxSkills = DistillMaxSkills,
        DistillRecentSessions = DistillRecentSessions,
    };
}
