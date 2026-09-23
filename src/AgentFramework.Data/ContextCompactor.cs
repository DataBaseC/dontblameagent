using AgentFramework.Contracts;

namespace AgentFramework.Data;

/// <summary>一次压缩的方案（还没落事件 —— 落事件是宿主的活）。</summary>
public sealed record CompactionPlan(
    ContextOptions Tightened,
    ContextProjection Before,
    ContextProjection After,
    IReadOnlyList<LlmMessage> OlderMessages)
{
    public IReadOnlyList<long> MaskedSeqs => After.MaskedSeqs;

    public int CollapsedTurns => Math.Max(0, Before.KeptTurns - After.KeptTurns);
}

/// <summary>
/// 压缩决策（L1 水位判断 + L3 遮蔽执行）。
///
/// <b>压缩 = 换一个更紧的投影函数</b>，不是删历史。
/// 具体只做一件事：把「逐字保留的轮次窗口」缩小一半 ——
/// 于是原本在窗口内、逐字保留的工具结果，自动落进窗口外被遮蔽。
///
/// 为什么不做更"聪明"的事（LLM 摘要、相关性排序）：
/// 实证显示 LLM 型 condenser 让总 token 涨 24–94%，而质量没有统计显著提升。
/// 能省的地方先靠**结构性裁剪**省掉，模型只作为可选兜底（L5，默认关）。
///
/// 幂等性：每次都从**原始配置**出发收紧，所以"压过之后再压"得到的是同一个视图，
/// 不会一轮一轮越切越狠。
/// </summary>
public static class ContextCompactor
{
    public static CompactionPlan? Plan(IEnumerable<SessionEvent> events, ContextOptions options)
    {
        var all = events as IReadOnlyList<SessionEvent> ?? [.. events];

        // ★ 起点 = 「上一次压到哪」，而不是回落到默认窗口（P3a）。
        //   窗口收到底仍超预算时，老实现每轮都从默认值重新减半 —— 全程白算，
        //   算出来的结果却与上一轮一模一样。从已收紧的位置起步，试算次数变成常数。
        var tightened = options.Clone();
        if (LastTightenedTurns(all) is int alreadyTightened
            && alreadyTightened < tightened.RecentTurnsKeptVerbatim)
        {
            tightened.RecentTurnsKeptVerbatim = Math.Max(1, alreadyTightened);
        }

        var before = SessionContextBuilder.Project(all, tightened);

        if (!before.NeedsCompression)
        {
            return null;
        }

        // 一次减半不够就继续减。
        //
        // 原先只减一次：窗口缩到 1 之后水位仍然超标的话，之后每轮都会重走一遍压缩，
        // 结果却和上一轮一模一样 —— 白算，而且预算始终悬在阈值上降不下来。
        // 现在一直收紧到「不再需要压缩」为止；收到底还超，那也只能如实记录（总比假装压过强）。
        var after = before;

        // 一步收到底（不再减半试探）：窗口越小，逐字保留的旧对话越少，
        // 预算内能装下的「有效信息密度」越高 —— 这本来就是 L3 的初衷。
        // 原减半循环（6→3→2→1）的每一步都会打断一次前缀缓存，
        // 而那几步得到的视图在下一步马上又变 —— 缓存白建，步子白走。
        // 按水位比例动态定窗口：预算越紧，窗口越小；余量充足则保底 3 轮。
        if (before.NeedsCompression)
        {
            var target = Math.Max(
                1,
                Math.Min(
                    tightened.RecentTurnsKeptVerbatim,
                    (int)Math.Ceiling(tightened.RecentTurnsKeptVerbatim * (1.0 - before.WaterLevel) * 2)));
            tightened.RecentTurnsKeptVerbatim = target;
            after = SessionContextBuilder.Project(all, tightened);
        }

        // 关于 L6 骨架化：它随 options 一起进了 tightened（Clone 保留了该开关），
        // 所以只要用户没关，第一遍投影就已经按骨架化算过水位 —— 这里不需要、也不该再「兜底强开」。
        // 用户显式关掉 L6 则全程不强开（配置表达「我就是要逐字保留」，压缩器无权推翻），
        // 此时如实返回超水位状态。
        //
        // v3.5 审查：此处原有一段 `options.SkeletonizeOldAssistant && !tightened.SkeletonizeOldAssistant`
        // 的「兜底」，但 tightened 是 options.Clone()、两者同源恒相等 → 条件恒为 false，是死代码，已删除。

        // 收紧之后什么都没变 → 这次压缩毫无意义，别写一条空事件污染日志。
        // （窗口已经很紧、或老历史本来就只有对话没有工具结果时，会出现这种情况。）
        if (after.MaskedResults == before.MaskedResults
            && after.Messages.Count == before.Messages.Count
            && after.EstimatedTokens >= before.EstimatedTokens)
        {
            return null;
        }

        return new CompactionPlan(tightened, before, after, after.OlderMessages);
    }

    /// <summary>
    /// 日志里最后一次压缩把「逐字保留窗口」收得多紧（没压过 = null）。
    ///
    /// 老日志里没有这个字段（可空），同样回落到 null —— 那正是旧行为。
    /// 从尾向前找：压缩事件总在日志尾部区间，倒着找通常一两步就命中。
    /// </summary>
    private static int? LastTightenedTurns(IReadOnlyList<SessionEvent> events)
    {
        for (var i = events.Count - 1; i >= 0; i--)
        {
            if (events[i] is ContextCompactedEvent { TightenedTurns: int turns })
            {
                return turns;
            }
        }

        return null;
    }
}
