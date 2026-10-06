using System.Collections.Concurrent;
using AgentFramework.Agent;
using AgentFramework.Contracts;

namespace AgentFramework.Host;

/// <summary>
/// 子 Agent 名单与生命周期 —— 宿主的**运行期状态**（不进事件流）：
/// 它回答的是「此刻谁在跑」，而事件流记的是历史。与 <c>JobManager</c> 同一取舍。
///
/// <para>
/// 管控面（对齐 dsh 的 <c>dsh-tool-subagent-control</c>）：派发 · 名单 · 状态 · 取结果 ·
/// <b>追加消息</b> · <b>打断</b> · 等待。工具只持有 <see cref="ISubAgentControl"/> 接口，
/// 编排逻辑留在宿主（它要 OpenSession / SendAsync / 多会话表）。
/// </para>
///
/// <para>
/// 「追加消息」的语义刻意做得很小：它只是**下一个回合的输入**。
/// 子 Agent 跑完当前回合时检查一次收件箱，非空就带着新指令再跑一轮 ——
/// 于是「人机协作式的追加」不必引入抢占式中断（那是另一件更复杂的事）。
/// </para>
/// </summary>
public sealed class SubAgentRegistry(AgentHost host) : ISubAgentControl
{
    /// <summary>
    /// 子 Agent 会话 id 的前缀 —— **单一来源**：生成时用它，界面列表用它把子会话排除在外
    /// （否则每派一次工，人的会话列表里就多出一条空会话）。
    /// </summary>
    public const string ChildIdPrefix = "sub-";

    /// <summary>这个会话 id 是不是子 Agent 的（界面 / 诊断面据此把它们和人的会话分开）。</summary>
    public static bool IsChildSession(string sessionId)
        => sessionId.StartsWith(ChildIdPrefix, StringComparison.Ordinal);

    private readonly ConcurrentDictionary<string, Handle> _agents = new(StringComparer.Ordinal);

    public async Task<SubAgentInfo> SpawnAsync(
        string parentSessionId,
        string task,
        bool background = false,
        int? maxSteps = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(task))
        {
            throw new ArgumentException("任务描述为空", nameof(task));
        }

        // 父会话必须活着：留痕（dispatched / completed）要落进它的流里。
        // 不先查的话，错误会从 EmitToSessionAsync 里以「会话不存在」冒出来 ——
        // 那个信息既不指向参数、也不是调用方能修的东西。
        if (host.GetSession(parentSessionId) is null)
        {
            throw new InvalidOperationException(
                $"父会话不存在或已关闭：{parentSessionId}（只有活着的会话才能派生 / 留痕）");
        }

        // 顺手清理：已结束且够老的条目。名单是**运行期状态**，不必永久保留 ——
        // 长跑宿主派几千次工就攒几千条 handle（每条还带着任务描述与摘要）。
        // 只清「已结束」的：running 的显然不能动，而「已结束的子 Agent 自己再派」
        // 这件事不存在（它没有回合在跑了），因此清掉不会让深度闸出现绕过口。
        if (_agents.Count > PruneThreshold)
        {
            var cutoff = DateTimeOffset.UtcNow - PruneAge;
            foreach (var (key, stale) in _agents)
            {
                if (stale.State != SubAgentStates.Running && stale.StartedAt < cutoff)
                {
                    _agents.TryRemove(key, out _);
                }
            }
        }

        var depth = DepthFor(parentSessionId);
        if (depth > MaxDepth)
        {
            throw new InvalidOperationException(
                $"派生层级超过上限（{MaxDepth} 层）—— 子 Agent 不该无限往下派。"
                + "确需更深时把 HostOptions.SubAgentMaxDepth 调高。");
        }

        var childId = $"{ChildIdPrefix}{DateTime.UtcNow:MMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}";
        var handle = new Handle(childId, parentSessionId, task, maxSteps, background) { Depth = depth };
        _agents[childId] = handle;

        // 留痕 1：派发（先落日志再跑 —— 与主循环同一顺序纪律）
        await host.EmitToSessionAsync(parentSessionId, new SubAgentDispatchedEvent
        {
            SessionId = parentSessionId,
            ChildSessionId = childId,
            Task = task,
        }, ct).ConfigureAwait(false);

        handle.Work = Task.Run(() => RunAsync(handle), CancellationToken.None);

        if (background)
        {
            return handle.Snapshot();
        }

        // 前台：等它跑完。父回合被取消 → 子 Agent 一起停（它不该比派它的人活得更久）。
        await using var registration = ct.Register(() => handle.Cts.Cancel());
        await handle.Work.ConfigureAwait(false);
        return handle.Snapshot();
    }

    public IReadOnlyList<SubAgentInfo> List(string? parentSessionId = null)
        => [.. _agents.Values
            .Where(h => parentSessionId is null || h.ParentSessionId == parentSessionId)
            .OrderBy(h => h.StartedAt)
            .Select(h => h.Snapshot())];

    public SubAgentInfo? Get(string id)
        => !string.IsNullOrWhiteSpace(id) && _agents.TryGetValue(id, out var handle)
            ? handle.Snapshot()
            : null;

    public bool Post(string id, string text, bool interrupt = false)
    {
        if (string.IsNullOrWhiteSpace(text)
            || !_agents.TryGetValue(id, out var handle)
            || handle.State != SubAgentStates.Running)
        {
            return false;
        }

        // 顺序要紧：**先入队、再取消**。反过来的话，主循环可能在「队列还是空的」那一刻
        // 就被唤醒，于是它发现没指令可取 —— 这条指令就被吞掉了。
        handle.Inbox.Enqueue(text);

        if (interrupt)
        {
            try
            {
                handle.RoundCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 这一轮刚好跑完（轮级取消源已回收）：那就走普通追加的路子，下一轮取到它
            }
        }

        return true;
    }

    /// <summary>派生深度上限（默认 2：顶层派一级、子 Agent 再派一级；更深就拒）。
    /// 由宿主按配置注入（<c>HostOptions.SubAgentMaxDepth</c>）。
    /// </summary>
    public int MaxDepth { get; set; } = 2;

    /// <summary>名单累积到这么多条以上时，顺手清理「已结束且够老」的（见 SpawnAsync）。</summary>
    private const int PruneThreshold = 200;

    private static readonly TimeSpan PruneAge = TimeSpan.FromHours(1);

    /// <summary>
    /// 这个会话派出去的子 Agent 属于第几层：
    /// 它不是任何子 Agent 的会话（人 / 界面开的顶层会话）→ 1；
    /// 它是某个子 Agent 的会话 → 那个的层级 + 1。
    /// </summary>
    private int DepthFor(string sessionId)
    {
        foreach (var handle in _agents.Values)
        {
            if (handle.Id == sessionId)
            {
                return handle.Depth + 1;
            }
        }

        return 1;
    }

    public bool Stop(string id)
    {
        if (!_agents.TryGetValue(id, out var handle))
        {
            return false;
        }

        try
        {
            handle.Cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已经跑完并回收：当作成功（打断是幂等的）
        }

        return true;
    }

    public async Task<string> WaitAsync(string id, int timeoutSeconds = 300, CancellationToken ct = default)
    {
        var handle = Get(id);
        if (handle is null)
        {
            return $"没有这个子 Agent：{id}";
        }

        var work = _agents.TryGetValue(id, out var raw) ? raw.Work : null;
        if (work is null || !handle.IsRunning)
        {
            return Describe(handle);
        }

        var wait = TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds));
        var finished = await Task.WhenAny(work, Task.Delay(wait, ct)).ConfigureAwait(false);
        return finished == work
            ? Describe(handle)
            : $"{Describe(handle)} —— 等了 {timeoutSeconds} 秒仍未结束（可以继续干别的，稍后再查）";
    }

    public void StopAll()
    {
        foreach (var handle in _agents.Values)
        {
            try
            {
                handle.Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 同上
            }
        }
    }

    /// <summary>把快照渲染成一句可读状态（<c>wait</c> / <c>status</c> 共用）。</summary>
    public static string Describe(SubAgentInfo info)
    {
        var elapsed = DateTimeOffset.UtcNow - info.StartedAt;
        var head = $"[子 Agent {info.Id}] 状态={info.State} · 回合={info.Rounds} · "
            + $"已跑 {elapsed.TotalSeconds:0} 秒 · {(info.Background ? "后台" : "前台")}";

        return info.Summary is { Length: > 0 } summary
            ? $"{head}\n{summary}"
            : $"{head}\n任务：{SubAgentRunner.TruncateSafe(info.Task, 120, "…")}";
    }

    /// <summary>
    /// 子 Agent 的主循环：一个会话 + 若干回合。
    /// 每轮结束检查一次收件箱（<see cref="Post"/> 的落点），空了才收尾。
    /// </summary>
    private async Task RunAsync(Handle handle)
    {
        var parent = host.GetSession(handle.ParentSessionId);

        try
        {
            // 开子会话也放在 try 里：宿主关停 / 会话表已被收走时它会抛。
            // 从前这一抛在 try 之外 —— 条目会永远卡在 Running（状态停在初值），
            // 而后台派发的路径没人 await 这个任务，异常也就无人观察。
            var child = host.OpenSession(
                handle.Id,
                systemPrompt: SubAgentRunner.ChildSystemPrompt,
                maxSteps: handle.MaxSteps,
                onEvent: null,
                relayToUi: false,
                modeId: parent?.ModeId,
                projectDir: parent?.ProjectDir);

            var input = handle.Task;

            while (true)
            {
                // 每一轮一个**轮级**取消源（链接到 handle.Cts）：
                //   · Stop 取消 handle.Cts   → 终结整个子 Agent；
                //   · 抢占 只取消 RoundCts    → 作废这一轮，带着新指令重跑。
                using var roundCts = CancellationTokenSource.CreateLinkedTokenSource(handle.Cts.Token);
                handle.RoundCts = roundCts;

                AgentRunResult result;
                try
                {
                    result = await host.SendAsync(child, input, roundCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!handle.Cts.IsCancellationRequested)
                {
                    // 走到这里说明**只是本轮被作废**（抢占），不是终结：
                    // 有指令就带着它立刻重跑；没有就照常按取消收场。
                    if (handle.Inbox.TryDequeue(out var urgent))
                    {
                        input = urgent;
                        continue;
                    }

                    throw;
                }
                finally
                {
                    handle.RoundCts = null;
                }

                handle.Rounds++;
                handle.Summary = SubAgentRunner.TruncateSafe(
                    result.Completed
                        ? result.FinalText
                        : $"（未在 {result.Steps} 步内完成：{result.StopReason}）",
                    500,
                    $"…（已截断，全文在子会话 {handle.Id}）");

                if (handle.Inbox.TryDequeue(out var next))
                {
                    input = next;
                    continue;   // 追了指令 → 带着它再跑一轮
                }

                handle.State = result.Completed ? SubAgentStates.Completed : SubAgentStates.Failed;
                break;
            }
        }
        catch (OperationCanceledException)
        {
            handle.State = SubAgentStates.Stopped;
            handle.Summary ??= "已被打断";
        }
        catch (Exception ex)
        {
            handle.State = SubAgentStates.Failed;
            handle.Summary = $"子 Agent 异常：{ex.Message}";
        }
        finally
        {
            // 收尾**一律尽力而为**：宿主可能正在关停（StopAll → Drain → 回收会话），
            // 那时父会话或自己这个会话都可能已经被收走。
            // 收尾失败不该变成「无人观察的任务异常」—— 那是最难查的一类噪音。
            try
            {
                // 留痕 2：完成（无论成功 / 失败 / 被打断，父会话都要看到结局）
                await host.EmitToSessionAsync(handle.ParentSessionId, new SubAgentCompletedEvent
                {
                    SessionId = handle.ParentSessionId,
                    ChildSessionId = handle.Id,
                    Success = handle.State == SubAgentStates.Completed,
                    Summary = handle.Summary ?? handle.State,
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // 父会话可能已经被关掉了
            }

            try
            {
                // 只回收**运行时对象**：日志文件保留（摘要还引用着它，全文仍可从磁盘读回）。
                await host.CloseSessionAsync(handle.Id, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // 宿主关停途中可能已经清掉会话表
            }

            try
            {
                handle.Cts.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // 已经被 Stop 路径回收过
            }
        }
    }

    /// <summary>
    /// 等所有在跑的子 Agent 收尾（宿主关停时用）。
    /// 不等的话，它们的收尾会与「回收会话」并发 —— 那是一条谁都说不清的竞态。
    /// </summary>
    public async Task DrainAsync(TimeSpan timeout)
    {
        var works = _agents.Values
            .Select(h => h.Work)
            .Where(w => w is not null)
            .Cast<Task>()
            .ToArray();

        if (works.Length == 0)
        {
            return;
        }

        await Task.WhenAny(Task.WhenAll(works), Task.Delay(timeout)).ConfigureAwait(false);
    }

    /// <summary>注册表里的一条（含不可见的工作态：取消源 / 收件箱 / 后台任务）。</summary>
    private sealed class Handle(
        string id,
        string parentSessionId,
        string task,
        int? maxSteps,
        bool background)
    {
        public string Id { get; } = id;

        public string ParentSessionId { get; } = parentSessionId;

        public string Task { get; } = task;

        public int? MaxSteps { get; } = maxSteps;

        public bool Background { get; } = background;

        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

        public CancellationTokenSource Cts { get; } = new();

        public ConcurrentQueue<string> Inbox { get; } = new();

        public Task? Work { get; set; }

        /// <summary>被派发的层级（顶层会话派出的 = 1）。派生深度闸用。</summary>
        public int Depth { get; init; } = 1;

        /// <summary>
        /// 当前这一轮的取消源（只取消本轮 —— 抢占用）。null = 此刻没有轮在跑。
        /// 跨线程读写（子 Agent 循环写、Post 读），故不做包装。
        /// </summary>
        public volatile CancellationTokenSource? RoundCts;

        /// <summary>状态字符串（<see cref="SubAgentStates"/>）。跨线程读写，故为 volatile。</summary>
        public volatile string State = SubAgentStates.Running;

        /// <summary>最近一轮的摘要（volatile 同由）。</summary>
        public volatile string? Summary;

        /// <summary>已跑回合数（只在子 Agent 自己的线程里自增；读侧容忍轻微滞后）。</summary>
        public volatile int Rounds;

        public SubAgentInfo Snapshot() => new(
            Id, ParentSessionId, Task, State, StartedAt, Rounds, Summary, Background);
    }
}
