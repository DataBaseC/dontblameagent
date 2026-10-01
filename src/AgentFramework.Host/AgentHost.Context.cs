using System.Text.Json;
using AgentFramework.Agent;
using AgentFramework.Contracts;
using AgentFramework.Data;
using AgentFramework.Index;
using AgentFramework.Kernel;
using AgentFramework.Llm;
using AgentFramework.Tools;

namespace AgentFramework.Host;

public sealed partial class AgentHost
{

    /// <summary>
    /// 执行一次压缩：算方案 →（可选）生成摘要 → 落一条压缩事件 → 返回收紧后的投影。
    ///
    /// 注意这里**什么历史都没删** —— 只是换了个更紧的投影函数，
    /// 并把「这次遮了哪些序号」记进日志。于是可回放、可审计、可分叉。
    /// 被钉死的序号之后永远保持遮蔽，历史视图不会来回跳。
    /// </summary>
    private async Task<ContextProjection> CompactAsync(
        Hosting.SessionRuntime session,
        IReadOnlyList<SessionEvent> events,
        ContextProjection current,
        ContextOptions contextOptions,
        CancellationToken ct)
    {
        // forceCollapse:true —— 与装配用的投影同口径，Before/After 才是真实视图
        var plan = ContextCompactor.Plan(events, contextOptions, forceCollapse: true);
        if (plan is null)
        {
            // 收紧也省不下东西 → 保持现状，不写空事件污染日志
            return current;
        }

        // 自动压缩**不阻塞等摘要**（MiMo：主 agent 不维护自己的记忆）：
        // 有后台 writer 的稿就用稿，没有就沿用旧摘要 —— 0.45 水位的 checkpoint 通常早已写好；
        // 覆盖线如实记摘要真实覆盖到的轮次，超出部分由投影器标注（不假装摘要什么都有）。
        // 这次折叠掉的新轮次派给后台 writer，增量补进**下一份**检查点。
        var draft = System.Threading.Interlocked.Exchange(ref session.PendingCheckpoint, null);
        var previous = LastSummary(events);
        var summary = draft?.Summary ?? previous;
        var summaryThroughTurn = draft?.ThroughTurn ?? LastSummaryCoverage(events);

        DispatchCheckpointWriter(session, events, plan.After, contextOptions);

        await session.Sink.EmitAsync(new ContextCompactedEvent
        {
            SessionId = session.SessionId,
            Trigger = CompactionTrigger.Auto,
            PreTokens = plan.Before.EstimatedTokens,
            PostTokens = plan.After.EstimatedTokens,
            MaskedSeqs = [.. plan.MaskedSeqs],
            MaskedCount = plan.After.MaskedResults,
            CollapsedTurns = plan.CollapsedTurns,
            // ★ 把「这次收到多紧」一起落盘（P3a）：下一轮以它为起点，
            //   不必从默认窗口重新减半 —— 收到底之后每轮重算的那笔白账就此消掉。
            TightenedTurns = plan.Tightened.RecentTurnsKeptVerbatim,
            Summary = summary,
            // 覆盖线如实记摘要真实覆盖到的轮次（可能是旧摘要的覆盖线）——
            // 超出部分由投影器标注「也已折叠」，不假装摘要什么都有
            SummaryThroughTurn = summaryThroughTurn,
            TaskCard = plan.After.TaskCard,
        }, ct).ConfigureAwait(false);

        // 摘要刚落进日志 → 重新投影一次，这一轮就用得上它
        return SessionContextBuilder.Project(session.Events, plan.Tightened, forceCollapse: true);
    }

    /// <summary>
    /// 手动压缩 —— 界面上「立即压缩」按钮走这里。
    /// 与自动压缩走同一条链路，只是触发源不同。
    /// </summary>
    public async Task<(bool ok, string? summary, int? preTokens, int? postTokens, string? error)> ManualCompactAsync(CancellationToken ct = default)
    {
        var session = _session;
        if (session is null) return (false, null, null, null, "没有活跃会话");

        var events = session.Events;
        var contextOptions = EffectiveContextOptions(_state.CurrentProfile);
        var projection = SessionContextBuilder.Project(events, contextOptions);

        var plan = ContextCompactor.Plan(events, contextOptions, forceCollapse: true);
        if (plan is null)
        {
            var estimated = projection.EstimatedTokens;
            var budget = contextOptions.TokenBudget;
            if (estimated > budget)
            {
                return (true, null, null, null,
                    $"当前上下文约 {estimated} tokens，已超过预算 {budget}，但压缩无法进一步缩减。"
                    + "建议在上下文设置中调大预算，或开新会话。");
            }
            return (true, null, null, null, "当前上下文不需要压缩（未达压缩阈值）");
        }

        var previous = LastSummary(events);
        var summary = await TrySummarizeAsync(
            plan.OlderMessages, plan.After.TaskCard, previous, contextOptions, ct).ConfigureAwait(false);

        // 覆盖线只如实延伸到摘要**真正吃进去**的轮次（INV-C1 的配套账目）：
        // 摘要没更新（失败/超时/无新原料）时沿用旧覆盖线 —— 把没吸收的轮次
        // 记成「已覆盖」会让下一次增量摘要静默跳过它们，信息就此蒸发。
        var cutoff = plan.After.TotalTurns - plan.After.KeptTurns;
        var producedNew = summary is not null
            && !string.Equals(summary, previous, StringComparison.Ordinal);

        await session.Sink.EmitAsync(new ContextCompactedEvent
        {
            SessionId = session.SessionId,
            Trigger = CompactionTrigger.Manual,
            PreTokens = plan.Before.EstimatedTokens,
            PostTokens = plan.After.EstimatedTokens,
            MaskedSeqs = [.. plan.MaskedSeqs],
            MaskedCount = plan.After.MaskedResults,
            CollapsedTurns = plan.CollapsedTurns,
            TightenedTurns = plan.Tightened.RecentTurnsKeptVerbatim,
            Summary = summary,
            SummaryThroughTurn = producedNew ? cutoff : LastSummaryCoverage(events),
            TaskCard = plan.After.TaskCard,
        }, ct).ConfigureAwait(false);

        return (true, summary, plan.Before.EstimatedTokens, plan.After.EstimatedTokens, null);
    }

    /// <summary>摘要调用的旁路超时 —— 慢端点上摘要本身不能拖死回合；超时按失败处理。</summary>
    private static readonly TimeSpan SummarizeTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// 增量摘要的统一入口（自动压缩 / 手动压缩 / 提前摘要共用）。
    /// 纪律：失败或超时**保留旧摘要**，绝不阻断本轮 —— 摘要是增强项，不是主链路。
    /// </summary>
    private async Task<string?> TrySummarizeAsync(
        IReadOnlyList<LlmMessage> olderMessages,
        string? taskCard,
        string? previousSummary,
        ContextOptions contextOptions,
        CancellationToken ct)
    {
        if (!contextOptions.SummarizeOlderHistory || _contextSummarizer is null)
        {
            return previousSummary;
        }

        if (olderMessages.Count == 0)
        {
            return previousSummary;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(SummarizeTimeout);

            var produced = await _contextSummarizer
                .SummarizeHistoryAsync(olderMessages, taskCard, previousSummary, timeout.Token)
                .ConfigureAwait(false);

            return string.IsNullOrWhiteSpace(produced) ? previousSummary : produced;
        }
        catch
        {
            return previousSummary;
        }
    }

    /// <summary>日志里最后一次非空摘要（没有 = null）。</summary>
    private static string? LastSummary(IReadOnlyList<SessionEvent> events)
    {
        for (var i = events.Count - 1; i >= 0; i--)
        {
            if (events[i] is ContextCompactedEvent { Summary: { Length: > 0 } summary })
            {
                return summary;
            }
        }

        return null;
    }

    /// <summary>日志里最后一次摘要覆盖到的轮次（没有 = null = 覆盖范围未知）。</summary>
    private static int? LastSummaryCoverage(IReadOnlyList<SessionEvent> events)
    {
        for (var i = events.Count - 1; i >= 0; i--)
        {
            if (events[i] is ContextCompactedEvent { Summary: { Length: > 0 }, SummaryThroughTurn: int through })
            {
                return through;
            }
        }

        return null;
    }

    /// <summary>
    /// checkpoint 早提取的回合边界入口（MiMo Code 的 writer 子代理）：
    ///
    ///   1) <b>消费稿</b> —— 后台 writer 写好的检查点在这里落一条事件（快，只是追加日志）；
    ///   2) <b>派发稿</b> —— 水位到 <c>EarlySummarizeRatio</c> 且有未覆盖的旧轮时，
    ///      派后台 writer 去提取，**回合不等它**（主 agent 不维护自己的记忆）。
    ///
    /// 返回 true = 落了新摘要事件（调用方应重新投影）。
    /// </summary>
    private async Task<bool> MaybeEarlySummarizeAsync(
        Hosting.SessionRuntime session,
        IReadOnlyList<SessionEvent> events,
        ContextProjection projection,
        ContextOptions contextOptions,
        CancellationToken ct)
    {
        var consumed = false;

        // ── 1) 消费后台 writer 的稿子 ─────────────────────────
        //   single-writer：稿子只有 writer 写、只有这里取；在回合边界消费，
        //   于是日志追加仍严格单线程，而昂贵的模型提取完全不占回合时间。
        if (System.Threading.Interlocked.Exchange(ref session.PendingCheckpoint, null) is { } draft)
        {
            await session.Sink.EmitAsync(new ContextCompactedEvent
            {
                SessionId = session.SessionId,
                Trigger = CompactionTrigger.Early,
                PreTokens = projection.EstimatedTokens,
                PostTokens = projection.EstimatedTokens,
                MaskedSeqs = [],
                MaskedCount = 0,
                CollapsedTurns = 0,
                Summary = draft.Summary,
                // 覆盖线如实写 writer 记的那条 —— 摘要不保证覆盖之后新变旧的轮次
                SummaryThroughTurn = draft.ThroughTurn,
                TaskCard = draft.TaskCard,
            }, ct).ConfigureAwait(false);

            consumed = true;
        }

        // ── 2) 到水位就派发后台 writer（不等待）──────────────────
        if (contextOptions.EarlySummarizeRatio > 0
            && projection.WaterLevel >= contextOptions.EarlySummarizeRatio)
        {
            var cutoff = projection.TotalTurns - projection.KeptTurns;
            var uncovered = cutoff > 0
                && projection.OlderMessages.Count > 0
                && !(LastSummaryCoverage(events) is int covered && covered >= cutoff);

            if (uncovered)
            {
                DispatchCheckpointWriter(session, events, projection, contextOptions);
            }
        }

        return consumed;
    }

    /// <summary>
    /// 派发后台 checkpoint-writer（MiMo Code 的 writer 子代理）。
    ///
    /// 纪律：
    ///   · <b>不阻塞</b> —— fire-and-forget，主 agent 继续干活；
    ///   · <b>single-writer</b> —— 每会话同一时刻至多一个 writer 在跑（防堆积），
    ///     稿子只有它写（<c>PendingCheckpoint</c>）；
    ///   · <b>不打扰</b> —— writer 任何失败都吞掉，绝不影响主流程；
    ///   · 产出同时落一份结构化检查点文件（人可读、可做下次 rebuild 的种子）。
    /// </summary>
    private void DispatchCheckpointWriter(
        Hosting.SessionRuntime session,
        IReadOnlyList<SessionEvent> events,
        ContextProjection projection,
        ContextOptions contextOptions)
    {
        if (_contextSummarizer is null)
        {
            return;
        }

        if (System.Threading.Interlocked.CompareExchange(ref session.CheckpointInFlight, 1, 0) != 0)
        {
            return;
        }

        var cutoff = projection.TotalTurns - projection.KeptTurns;
        var previous = LastSummary(events);
        var taskCard = projection.TaskCard;
        var olderMessages = projection.OlderMessages;
        var sessionId = session.SessionId;

        _ = Task.Run(async () =>
        {
            try
            {
                var summary = await TrySummarizeAsync(
                    olderMessages, taskCard, previous, contextOptions, CancellationToken.None).ConfigureAwait(false);

                if (summary is not null && !string.Equals(summary, previous, StringComparison.Ordinal))
                {
                    session.PendingCheckpoint = new Hosting.CheckpointDraft(summary, cutoff, taskCard);
                    TryWriteCheckpointFile(sessionId, summary, cutoff);
                }
            }
            catch
            {
                // writer 永不打扰主流程
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref session.CheckpointInFlight, 0);
            }
        });
    }

    /// <summary>
    /// 把检查点落成文件（<c>sessions/checkpoints/&lt;sessionId&gt;.md</c>）。
    /// single-writer 之下它是安全的：只有 writer 写这个文件；人可随时查看/清理。
    /// </summary>
    private void TryWriteCheckpointFile(string sessionId, string summary, int throughTurn)
    {
        try
        {
            var dir = Path.Combine(Options.SessionsDir, "checkpoints");
            Directory.CreateDirectory(dir);

            File.WriteAllText(
                Path.Combine(dir, sessionId + ".md"),
                "# 会话检查点（checkpoint-writer 自动生成 —— single-writer，勿手改）\n\n"
                + $"- 更新时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}\n"
                + $"- 覆盖至：第 {throughTurn} 轮\n\n"
                + summary
                + "\n");
        }
        catch
        {
            // 落盘失败不影响主流程
        }
    }

    private ContextOptions EffectiveContextOptions(ModeProfile profile)
    {
        if (profile.ContextGovernance && profile.InjectTaskCard)
        {
            return Options.Context;
        }

        var effective = Options.Context.Clone();
        effective.InjectTaskCard = profile.InjectTaskCard;

        if (!profile.ContextGovernance)
        {
            effective.MaskOldToolResults = false;
            // 从前设 TokenBudget=int.MaxValue 导致闲聊模式永不压缩；
            // 闲聊也会积累长上下文，保留默认预算（24K × 0.8 触发）。
        }

        return effective;
    }

    /// <summary>
    /// 构建**冻结段**：模式说明 + 常驻「记忆索引卡」。
    ///
    /// 它进的是请求最前面的缓存前缀，所以纪律是「**内容必须稳定**」。
    /// 这里每轮都重建，但不缓存也不打紧 —— 只要记忆没变，重建出的字符串就**逐字节相同**
    /// （<c>LoadAsync</c> 取最近 N 条，顺序确定），前缀缓存照样命中。
    /// 真正会破坏前缀的是「每轮内容都不同」，而不是「每轮都算一遍」。
    ///
    /// 「不用的那一级根本不打开文件」依然成立：按模式只读该读的层级。
    /// 而「这一轮用得上的细节」不走这里，走 <see cref="BuildRecallBlockAsync"/>（动态段）。
    /// </summary>
    private async Task<string?> BuildFrozenBlockAsync(ModeProfile profile, IReadOnlyList<string> scopes, CancellationToken ct)
    {
        var blocks = new List<string>();

        if (!string.IsNullOrWhiteSpace(profile.SystemPromptSuffix))
        {
            blocks.Add(profile.SystemPromptSuffix!);
        }

        if (profile.MemoryIndexLimit > 0)
        {
            foreach (var scope in scopes)
            {
                // v3.5 审查 P1-4：温度策略必须「先全局排序、后截断」。
                // 原实现先按时间 TakeLast(N) 再排序 —— 等于把「老而常用」的条目
                // 永远挡在索引卡之外，而「救回老而常用」正是温度策略存在的唯一理由。
                // 所以温度档取全量活跃视图去排序；Insertion 档维持原语义（最近 N 条）。
                var takeAll = profile.PriorityStrategy == MemoryPriorityStrategy.Temperature;
                var entries = await Memory
                    .LoadAsync(scope, takeAll ? int.MaxValue : profile.MemoryIndexLimit, ct)
                    .ConfigureAwait(false);
                // 按档位策略排序（Insertion = 原行为；Temperature = 热度优先）。
                // 排序在宿主做而不是存储做：同一份视图，不同模式不同取法。
                var ordered = MemoryPrioritizer.Order(entries, profile.PriorityStrategy);
                if (takeAll && ordered.Count > profile.MemoryIndexLimit)
                {
                    ordered = [.. ordered.Take(profile.MemoryIndexLimit)];
                }

                var label = scope == MemoryScope.Global
                    ? "记忆·常驻索引（全局，跨项目）"
                    : "记忆·常驻索引（本项目）";

                var card = MemoryBlocks.BuildIndexCard(ordered, profile.MemoryIndexMaxChars, label);
                if (card is not null)
                {
                    blocks.Add(card);
                }
            }
        }

        return blocks.Count == 0 ? null : string.Join("\n\n", blocks);
    }

    /// <summary>
    /// 构建**动态段**里的一块：按当轮输入检索出来的相关记忆。
    ///
    /// 它每轮都可能不同，所以**只能放尾部** —— 这正是「检索代替搬运」在实现层的落点：
    /// 不再每轮把最近 N 条记忆全量灌进上下文，而是先看这一轮说了什么，再去库里捞。
    /// 闲聊模式 <c>MemoryRecallLimit = 0</c>：一次文件扫描都不做。
    /// </summary>
    private async Task<string?> BuildRecallBlockAsync(
        ModeProfile profile, IReadOnlyList<string> scopes, string query, string? sessionId, CancellationToken ct)
    {
        if (profile.MemoryRecallLimit <= 0 || string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var hits = new List<(string Scope, MemoryEntry Entry)>();

        foreach (var scope in scopes)
        {
            var found = await Memory
                .SearchAsync(query, scope, profile.MemoryRecallLimit, ct)
                .ConfigureAwait(false);

            hits.AddRange(found.Select(e => (scope, e)));
        }

        if (hits.Count == 0)
        {
            return null;
        }

        // 检索块本来就按相关性序返回（SearchAsync 打分排序）；
        // 这里不再按 CreatedAt 重排 —— 那会把「最相关的」换成「最新的」。
        // 只做去重（同一条记忆可能同时出现在多个 scope 的结果里）。
        var ordered = hits
            .DistinctBy(x => x.Entry.Id, StringComparer.Ordinal)
            .Take(profile.MemoryRecallLimit)
            .ToList();

        // 隐式使用信号：这一轮"按当前输入捞出来用上了"，就算一次使用（续命，不动排序热度）。
        // 不做的话，降档判据只看得到显式 recall_memory 的那几次 ——
        // 「每轮都在被静默使用」的高频记忆反而会被当成冷条目，激励是反的。
        if (profile.RecordRecallAsUse)
        {
            foreach (var group in ordered.GroupBy(x => x.Scope, StringComparer.Ordinal))
            {
                try
                {
                    await Memory
                        .TouchAsync(group.Key, [.. group.Select(x => x.Entry.Id)], sessionId, ct)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // 升温是优化，绝不阻断召回（与 RecallMemoryTool 同一条纪律）。
                }
            }
        }

        return MemoryBlocks.BuildRecallBlock(ordered.Select(x => x.Entry), profile.MemoryRecallMaxChars);
    }

    /// <summary>
    /// 转述留痕落进**指定的会话**（B1 修复）：
    /// 自动转述跟发起回合的会话走（<c>SendAsync(session, …)</c> 传进来的那个）；
    /// 手动转述跟点击按钮时的当前会话走（调用方传 <c>_session</c>）。
    /// 此前写 <c>_sink</c> —— 回合排队期间切过会话的话，留痕会落进别人的日志。
    /// </summary>
    private ValueTask EmitRephraseRecordAsync(
        Hosting.SessionRuntime session,
        string original,
        RephraseResult result,
        string source,
        bool applied,
        CancellationToken ct)
        => session.Sink.EmitAsync(new UserInputRephrasedEvent
        {
            SessionId = session.SessionId,
            Original = original,
            Rephrased = result.Text,
            Source = source,
            Model = result.Rephrased ? result.Model : null,
            ElapsedMs = result.ElapsedMs,
            Applied = applied,
        }, ct);

    /// <summary>从事件流重建的模型上下文（可用来确认"模型看到了什么"）。</summary>
    public IReadOnlyList<LlmMessage> RebuildContext() => SessionContextBuilder.BuildFromFile(_log.Path);

    // ── debug 信息窗的内部状态读取器（v3.2 自用诊断）──────────────
    // 与 ContextStatus（WebUiServer 私有）同源：都从内存事件表现投影。
    // 拆成 4 个小属性而不是一个对象，是为了 debug 窗和 /api/status 各取所需。

    /// <summary>当前估算 token 水位。</summary>
    internal int ContextStatusTokens()
    {
        var projection = SessionContextBuilder.Project(_session.Events, EffectiveContextOptions(ModeProfile));
        return projection.EstimatedTokens;
    }

    /// <summary>水位比例（0–1+）。</summary>
    internal double ContextStatusWaterLevel()
    {
        var projection = SessionContextBuilder.Project(_session.Events, EffectiveContextOptions(ModeProfile));
        return Math.Round(projection.WaterLevel, 3);
    }

    /// <summary>逐字保留的轮次窗口（压缩后变小）。</summary>
    internal int ContextStatusKeptTurns()
    {
        var projection = SessionContextBuilder.Project(_session.Events, EffectiveContextOptions(ModeProfile));
        return projection.KeptTurns;
    }

    /// <summary>历史上发生过的自动压缩次数。</summary>
    internal int ContextStatusCompactions()
        => _session.Events.OfType<ContextCompactedEvent>().Count();

    /// <summary>从事件流投影出的会话状态。</summary>
    public SessionState CurrentState() => _projectionCache.GetOrRebuild(_log.Path);

    /// <summary>原始事件流（UI 拉取历史用）。</summary>
    public IReadOnlyList<SessionEvent> Events() => _session.Events;
}
