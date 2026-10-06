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
        // L5 摘要是**默认关**的（README 与契约注释都是这个口径，配了端点也不该自动开）。
        // 早摘要（EarlySummarizeRatio 水位派发的后台 writer）同属 L5，必须一并受这个开关管 ——
        // 否则「默认关」只在代码默认值上成立、在行为上不成立（安全/一致性审查 P1-1）。
        if (!contextOptions.SummarizeOlderHistory)
        {
            return false;
        }

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

        TrackBackground(Task.Run(async () =>
        {
            try
            {
                var summary = await TrySummarizeAsync(
                    olderMessages, taskCard, previous, contextOptions, LifetimeToken).ConfigureAwait(false);

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
        }));
    }

    /// <summary>
    /// checkpoint **状态锚点**的回合边界入口（v3.22 —— 兑现 v3.11 定下的契约）。
    ///
    /// <para>
    /// 与早摘要各自一条 pending 槽，互不干扰：
    /// </para>
    /// <list type="number">
    ///   <item><b>消费稿</b> —— 后台 writer 写好的 <see cref="CheckpointEvent"/> 在这里落一条事件。
    ///   <b>只追加日志，一个字节都不动模型上下文</b>（投影器不处理它）；</item>
    ///   <item><b>派发稿</b> —— 水位跨过 <see cref="CheckpointOptions.TriggerRatio"/>（默认 0.35，
    ///   远低于压缩线 0.8）且距上次至少新增 <c>MinNewEvents</c> 个事件时，派后台 writer 提取；回合不等它。</item>
    /// </list>
    /// <para>返回 true = 落了新锚点事件。</para>
    /// </summary>
    private async Task<bool> MaybeCheckpointAnchorAsync(
        Hosting.SessionRuntime session,
        IReadOnlyList<SessionEvent> events,
        ContextProjection projection,
        CancellationToken ct)
    {
        var options = Options.Checkpoint;
        var consumed = false;

        // ── 1) 消费后台 writer 的锚点稿 ────────────────────────
        if (System.Threading.Interlocked.Exchange(ref session.PendingCheckpointAnchor, null) is { } anchor)
        {
            anchor.SessionId = session.SessionId;

            // 记忆升级在**落盘之前**做：promoted id 要一并写进这条事件（可审计「这条记忆是谁写的」）。
            if (options.PromoteMemory)
            {
                anchor.PromotedMemoryIds = await PromoteCheckpointMemoryAsync(session, anchor, ct).ConfigureAwait(false);
            }

            await session.Sink.EmitAsync(anchor, ct).ConfigureAwait(false);
            TryWriteCheckpointBlock(session.SessionId, anchor);
            consumed = true;
        }

        // ── 2) 到水位就派发（不等待，单飞）────────────────────
        if (options.Enabled
            && _checkpointWriter is not null
            && projection.WaterLevel >= options.TriggerRatio
            && ShouldDispatchCheckpoint(events, options)
            && session.PendingCheckpointAnchor is null)
        {
            DispatchCheckpointAnchor(session, events, projection);
        }

        return consumed;
    }

    /// <summary>防抖：距上次锚点至少新增 <c>MinNewEvents</c> 个事件才值得再派一次。</summary>
    private static bool ShouldDispatchCheckpoint(IReadOnlyList<SessionEvent> events, CheckpointOptions options)
    {
        if (events.Count == 0)
        {
            return false;
        }

        var last = LastCheckpointEvent(events);
        var newEvents = last is null ? events.Count : events[^1].Seq - last.Seq;
        return newEvents >= Math.Max(1, options.MinNewEvents);
    }

    /// <summary>最近一条 checkpoint 事件（增量窗口与上一份渲染块的来源）。</summary>
    private static CheckpointEvent? LastCheckpointEvent(IReadOnlyList<SessionEvent> events)
    {
        for (var i = events.Count - 1; i >= 0; i--)
        {
            if (events[i] is CheckpointEvent checkpoint)
            {
                return checkpoint;
            }
        }

        return null;
    }

    /// <summary>
    /// 派后台 checkpoint writer（fire-and-forget）。
    /// 纪律同早摘要：不阻塞、single-writer、失败吞掉（writer 永不打扰主流程）。
    /// 产物进 <c>PendingCheckpointAnchor</c>，由回合边界消费 —— 日志追加仍严格单线程。
    /// </summary>
    private void DispatchCheckpointAnchor(
        Hosting.SessionRuntime session,
        IReadOnlyList<SessionEvent> events,
        ContextProjection projection)
    {
        if (_checkpointWriter is null)
        {
            return;
        }

        if (System.Threading.Interlocked.CompareExchange(ref session.CheckpointAnchorInFlight, 1, 0) != 0)
        {
            return;
        }

        var request = BuildCheckpointRequest(session, events, projection, CheckpointTrigger.Auto);
        var estimatedTokens = projection.EstimatedTokens;

        TrackBackground(Task.Run(async () =>
        {
            try
            {
                var anchor = await _checkpointWriter.WriteAsync(request, LifetimeToken).ConfigureAwait(false);
                if (anchor is not null && anchor.RenderBlock().Length > 0)
                {
                    anchor.SessionId = session.SessionId;
                    anchor.PreTokens = estimatedTokens;
                    session.PendingCheckpointAnchor = anchor;
                }
            }
            catch
            {
                // writer 永不打扰主流程
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref session.CheckpointAnchorInFlight, 0);
            }
        }));
    }

    /// <summary>组装一次 checkpoint 请求（自动 / 手动共用）。</summary>
    private CheckpointRequest BuildCheckpointRequest(
        Hosting.SessionRuntime session,
        IReadOnlyList<SessionEvent> events,
        ContextProjection projection,
        string trigger)
    {
        var last = LastCheckpointEvent(events);
        var toSeq = events.Count > 0 ? events[^1].Seq : 0;
        var notesPath = Path.Combine(Options.WorkspaceRoot, WorkNotes.DefaultFileName);

        return new CheckpointRequest
        {
            SessionId = session.SessionId,
            Messages = projection.Messages,
            TaskCard = projection.TaskCard,
            Notes = WorkNotes.TryRead(notesPath),
            PreviousBlock = last?.RenderBlock(),
            WaterLevelPermille = (int)Math.Round(projection.WaterLevel * 1000),
            Trigger = trigger,
            FromSeq = (last?.ToSeq ?? 0) + 1,
            ToSeq = toSeq,
        };
    }

    /// <summary>
    /// 手动写一次状态锚点（界面按钮 / 命令）。与自动通道同源，只是不受水位与防抖限制。
    /// 取不到回合闸（会话正忙）就如实回「稍后再试」，不硬插。
    /// </summary>
    public async Task<(bool ok, string message)> ManualCheckpointAsync(CancellationToken ct = default)
    {
        var session = _session;
        if (session is null)
        {
            return (false, "没有活跃会话");
        }

        if (_checkpointWriter is null)
        {
            return (false, "当前没有可用的模型端点，无法提取状态锚点");
        }

        if (!await session.TurnGate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return (false, "会话正忙（有回合在跑），请稍后再试");
        }

        try
        {
            var events = session.Events;
            var contextOptions = EffectiveContextOptions(_state.CurrentProfile);
            var projection = SessionContextBuilder.Project(events, contextOptions, forceCollapse: true);
            var request = BuildCheckpointRequest(session, events, projection, CheckpointTrigger.Manual);

            var anchor = await _checkpointWriter.WriteAsync(request, ct).ConfigureAwait(false);
            if (anchor is null || anchor.RenderBlock().Length == 0)
            {
                return (false, "提取没有产出有效内容（模型未给出结构化状态）");
            }

            anchor.SessionId = session.SessionId;
            anchor.PreTokens = projection.EstimatedTokens;
            anchor.PromotedMemoryIds = Options.Checkpoint.PromoteMemory
                ? await PromoteCheckpointMemoryAsync(session, anchor, ct).ConfigureAwait(false)
                : [];

            await session.Sink.EmitAsync(anchor, ct).ConfigureAwait(false);
            TryWriteCheckpointBlock(session.SessionId, anchor);
            return (true, $"已写入状态锚点（升级 {anchor.PromotedMemoryIds.Count} 条记忆）");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, $"写入失败：{ex.Message}");
        }
        finally
        {
            session.TurnGate.Release();
        }
    }

    /// <summary>
    /// 把 checkpoint 的「跨任务发现」升级进记忆（<see cref="CheckpointOptions.PromoteMemory"/>）。
    /// 只升 Discoveries —— 那是「在别处也成立的事实」；设计与决策留在笔记里，不塞进记忆。
    /// 逐条独立 try：一条写失败不影响其余，也不打扰主流程。
    /// </summary>
    private async Task<List<string>> PromoteCheckpointMemoryAsync(
        Hosting.SessionRuntime session,
        CheckpointEvent anchor,
        CancellationToken ct)
    {
        var ids = new List<string>();
        if (Memory.Kind == "none" || anchor.Discoveries.Count == 0)
        {
            return ids;
        }

        foreach (var discovery in anchor.Discoveries.Take(5))
        {
            try
            {
                var entry = await Memory.AppendAsync(
                    MemoryScope.Project,
                    discovery,
                    tags: ["checkpoint"],
                    sourceSession: session.SessionId,
                    source: "checkpoint",
                    ct: ct).ConfigureAwait(false);

                ids.Add(entry.Id);
            }
            catch
            {
                // 单条失败不影响其余
            }
        }

        return ids;
    }

    /// <summary>
    /// 状态锚点也落一份人可读文件（<c>sessions/checkpoints/&lt;id&gt;.anchor.md</c>）。
    /// 与早摘要的 <c>&lt;id&gt;.md</c> 分开 —— 两者语义不同（那个是摘要，这个是结构化状态）。
    /// 用**追加**写入以符合契约的「追加而非覆盖」。
    /// </summary>
    private void TryWriteCheckpointBlock(string sessionId, CheckpointEvent anchor)
    {
        try
        {
            var dir = Path.Combine(Options.SessionsDir, "checkpoints");
            Directory.CreateDirectory(dir);

            File.AppendAllText(
                Path.Combine(dir, sessionId + ".anchor.md"),
                $"\n## {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} · {anchor.Trigger} · "
                + $"水位 {anchor.WaterLevelPermille / 10.0:0.0}%（约 {anchor.PreTokens} tokens）· "
                + $"Seq {anchor.FromSeq}–{anchor.ToSeq}\n\n"
                + anchor.RenderBlock() + "\n");
        }
        catch
        {
            // 落文件是给人看的，失败不该影响任何流程
        }
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
    // ── 冻结段哈希诊断（任务 8 可选增强；默认关，见 HostOptions.CacheDiagnostics）──

    private readonly Dictionary<string, string> _frozenHashes = new(StringComparer.Ordinal);

    /// <summary>冻结段哈希变化次数（诊断；开关关时恒为 0）。</summary>
    public int FrozenBlockChanges { get; private set; }

    /// <summary>某会话最近一次的冻结段哈希（诊断；开关关时恒为 null）。</summary>
    public string? FrozenBlockHashOf(string sessionId)
    {
        lock (_frozenHashes)
        {
            return _frozenHashes.GetValueOrDefault(sessionId);
        }
    }

    /// <summary>
    /// 冻结段（缓存前缀那一段）的字节哈希诊断：同一会话两次装配不一致 = 前缀被打掉、缓存必 miss。
    /// 这是把「为什么 cached_tokens 偏低」从猜想到证据的那一步。
    /// </summary>
    private void ObserveFrozenBlock(string sessionId, string? frozen)
    {
        if (!Options.CacheDiagnostics)
        {
            return;
        }

        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(frozen ?? string.Empty)))[..16];

        lock (_frozenHashes)
        {
            if (_frozenHashes.TryGetValue(sessionId, out var previous)
                && !string.Equals(previous, hash, StringComparison.Ordinal))
            {
                FrozenBlockChanges++;
                Console.WriteLine($"[cache] 冻结段哈希变化（会话 {sessionId}）：{previous} → {hash} —— 前缀必 miss");
            }

            _frozenHashes[sessionId] = hash;
        }
    }

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
