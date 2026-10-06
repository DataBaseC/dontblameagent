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

    /// <summary>按 id 取会话运行态（子 Agent 继承父会话的模式/项目目录用）；不存在返回 null。</summary>
    public Hosting.SessionRuntime? GetSession(string sessionId)
    {
        lock (_sessions)
        {
            return _sessions.TryGetValue(sessionId, out var rt) ? rt : null;
        }
    }


    // ── 会话 ───────────────────────────────────────────────

    /// <summary>
    /// 在同一套装配下开一个<b>独立会话</b>（子 agent 走这条路）。
    ///
    /// 共享：模型客户端、工具注册表、索引、记忆、交互缝。
    /// 独立：事件日志、模型上下文、回合闸。
    ///
    /// 它默认**不挂 UI 回调** —— 子会话的流式输出不该串进主人的对话窗口；
    /// 要观察它，就显式传 <paramref name="onEvent"/>（事件才是它的正经出口）。
    /// </summary>
    /// <summary>
    /// 开一个独立会话（P1）。
    ///
    /// <paramref name="relayToUi"/> 决定它的输出要不要推到界面：
    /// 子 agent 用 false（别串台），UI 自己的会话用 true（本来就是要打字出来的那个）。
    /// 开出来的会话同时登记进宿主的会话表，之后就能被「切过去」。
    /// </summary>
    public Hosting.SessionRuntime OpenSession(
        string sessionId,
        string? systemPrompt = null,
        int? maxSteps = null,
        Action<SessionEvent>? onEvent = null,
        bool relayToUi = false,
        string? modeId = null,
        string? projectDir = null)
    {
        var created = _state.OpenSession(sessionId, systemPrompt, maxSteps, onEvent, relayToUi, modeId, projectDir);

        lock (_sessions)
        {
            _sessions[sessionId] = created;
        }

        // 模式/项目目录都是会话的属性：重启后换原（session-meta.json）
        if (modeId is not null || projectDir is not null)
        {
            SaveSessionMeta(sessionId, modeId, projectDir);
        }

        return created;
    }

    /// <summary>
    /// 回合中断自愈（P0-2）：为「有意图、无结果」的悬空工具调用补写一条合成结果事件。
    ///
    /// 遵守「补状态靠再追加一条事件」的纪律 —— 不改历史，只补一条失败结果。
    /// 补完之后模型重新看到的语义是完整的：它确实调过这个工具，结果是「回合中断」。
    /// </summary>
    private async ValueTask RepairDanglingToolCallsAsync(Hosting.SessionRuntime session, CancellationToken ct)
    {
        var requested = new Dictionary<string, ToolCallRequestedEvent>(StringComparer.Ordinal);
        var completed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var e in session.Events)
        {
            switch (e)
            {
                case ToolCallRequestedEvent r:
                    requested[r.CallId] = r;
                    break;

                case ToolCallCompletedEvent c:
                    completed.Add(c.CallId);
                    break;
            }
        }

        foreach (var (callId, req) in requested)
        {
            if (completed.Contains(callId))
            {
                continue;
            }

            await session.Sink.EmitAsync(new ToolCallCompletedEvent
            {
                // ★ 归给**传进来的这个会话**（P1）：不是「当前会话」——
                //   自愈修的就是这份事件流，写错就会把事件落进别人的日志。
                SessionId = session.SessionId,
                CallId = callId,
                Success = false,
                Output = string.Empty,
                Error = $"回合中断（进程重启或崩溃），工具 {req.ToolName} 的调用从未执行完成",
            }, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 关闭并移除一个会话（B3 修复）：
    /// 等〈最多 5 秒〉回合结束 → 从会话表移除 → 释放它的运行态（日志 + 闸）→ 清派生物。
    /// 不这样做的话，「删掉再新建同名会话」会拿到**旧 runtime**——
    /// 内存事件表还是删掉前的内容，界面直接“复活”已删除的历史，而磁盘日志是新的空文件。
    /// 返回 false = 会话不存在，或有回合正在跑（用户应先等它结束）。
    /// </summary>
    public async ValueTask<bool> CloseSessionAsync(string sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        Hosting.SessionRuntime? victim;
        lock (_sessions)
        {
            if (!_sessions.TryGetValue(sessionId, out victim))
            {
                return false;
            }
        }

        // 闸的等待与释放在这里**配对**完成 —— 与 SwitchSessionAsync 同一纪律。
        // ★ 但不能在 release 之后再 Dispose 闸 —— v3.4 原版把 DisposeAsync 放在 try 内、
        //   Release 放在 finally，结果正常路径先 Dispose 再 Release →
        //   "Cannot access a disposed object"（删除端点 500 的直接原因）。
        // 正确顺序：先还闸，再释放 runtime；acquired 标志保证 finally 只在真的持有闸时才 Release。
        var acquired = false;
        try
        {
            try
            {
                // 必须用 WaitAsync 的返回值判断是否拿到闸；
                // 用 CurrentCount==0 反推是错的 —— 超时未拿到时 CurrentCount 同样是 0（回合还持着）。
                acquired = await victim.TurnGate.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                acquired = false;
            }

            if (!acquired)
            {
                return false;   // 回合在跑（或取消），不删
            }

            // v3.5 审查 P2：先**从会话表摘除**、再还闸。
            // 原顺序（先还闸、后摘除）留了一个窗口：别的线程可能在「闸已还、条目还在」之间
            // 拿到这个 runtime 并开始新回合，随后它被 DisposeAsync 掉 —— 用一个已经释放的闸。
            // 摘除在前，则新调用者不可能再拿到它。
            lock (_sessions)
            {
                // 二次确认：等待闸期间可能已被别的路径关闭
                if (!_sessions.Remove(sessionId))
                {
                    return true;    // finally 负责把闸还回去
                }
            }

            victim.TurnGate.Release();
            acquired = false;   // 已还，finally 不再重复还

            await victim.DisposeAsync().ConfigureAwait(false);
            await InvalidateDerivedDataAsync(sessionId, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            if (acquired)
            {
                victim.TurnGate.Release();
            }
        }
    }

    /// <summary>
    /// 会话被删掉时清它留下的派生物（投影缓存 + 索引）。
    ///
    /// 不清的话，「删掉再新建同名会话」会吃到旧快照 —— 界面显示的是并不存在的历史。
    /// 派生物清理失败绝不影响主流程（它们本来就是可丢的）。
    /// </summary>
    public async ValueTask InvalidateDerivedDataAsync(string sessionId, CancellationToken ct = default)
    {
        _projectionCache.Invalidate(Path.Combine(Options.SessionsDir, sessionId + ".jsonl"));

        if (!Index.IsAvailable)
        {
            return;
        }

        try
        {
            await Index.RemoveSessionAsync(sessionId, ct).ConfigureAwait(false);
        }
        catch
        {
            // 索引是派生物，清不掉也不是错
        }
    }

    // ── 以下 WaitIdleAsync / ReleaseTurn（B5）───────────────────────
    // P1 之后「会话切换不再拆除宿主」，它们不再被切换路径调用；
    // 保留给仍然会“拆掉别人还握着引用的东西”的场景：插件卸载、宿主关闭前的等待、
    // 将来的 runtime LRU 回收。注意它们操作的是**当前会话**的闸 ——
    // 针对特定会话的等待请直接拿那个 runtime 的 TurnGate（参照 CloseSessionAsync）。

    /// <summary>
    /// 等当前会话的回合结束（最多 <paramref name="maxWait"/>）。
    /// 返回 false = 回合还在跑，调用方**不得**拆除本宿主 ——
    /// 否则日志已 Dispose、插件已卸载，正在跑的回合会在写事件时炸掉。
    /// </summary>
    public async ValueTask<bool> WaitIdleAsync(TimeSpan maxWait, CancellationToken ct = default)
    {
        try
        {
            return await _session.TurnGate.WaitAsync(maxWait, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>与 <see cref="WaitIdleAsync"/> 配对：拿到的闸必须还，否则会话永远锁死。</summary>
    public void ReleaseTurn() => _session.TurnGate.Release();

    /// <summary>
    /// 切换「当前会话」（P1）。
    ///
    /// <para>
    /// **只换指针**：装配一份、插件不重载、SQLite 不重开、记忆不重读。
    /// 目标会话**惰性打开**（首次被选中才 Open 日志句柄），
    /// 所以会话列表翻多少遍都不会多开一个文件句柄。
    /// </para>
    /// <para>
    /// 返回 false = 当前会话有回合正在跑，或目标会话打不开。
    /// 闸的等待与归还在方法内部**配对**完成 —— 交给调用方分两步做的话，
    /// 中间一换会话就会把闸还给别的会话，那个会话从此可以并行跑两个回合。
    /// </para>
    /// </summary>
    public async ValueTask<bool> SwitchSessionAsync(string sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId)
            || string.Equals(sessionId, _session.SessionId, StringComparison.Ordinal))
        {
            return true;
        }

        await _sessionSwapGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (string.Equals(sessionId, _session.SessionId, StringComparison.Ordinal))
            {
                return true;
            }

            // 等当前会话的回合结束（拿它的闸），拿到之后立刻还 ——
            // 全程都是同一个 runtime，不可能「还给别人」。
            // 等不到就拒绝：回合跑着时切走，SSE 的增量会显示在另一个会话的界面上。
            if (!await _session.TurnGate.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false))
            {
                return false;
            }

            Hosting.SessionRuntime target;
            try
            {
                target = GetOrOpenSession(sessionId);
            }
            catch (Exception)
            {
                // 打不开（路径非法 / 权限 / 磁盘）→ 如实失败，别把宿主留在半切状态
                return false;
            }
            finally
            {
                _session.TurnGate.Release();
            }

            SwapTo(target);
            return true;
        }
        finally
        {
            _sessionSwapGate.Release();
        }
    }

    /// <summary>
    /// <summary>
    /// 从现有会话的某个事件序号之前分叉出一个新会话（F3）。
    /// 底层用 SessionForker（日志前缀复制），分叉完立刻打开 ——
    /// 「开分支试试另一条路」从此是界面上的一次点击。
    /// </summary>
    public string ForkSession(string sourceSessionId, long fromSeq)
    {
        var sourcePath = Path.Combine(Options.SessionsDir, sourceSessionId + ".jsonl");
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException($"源会话不存在：{sourceSessionId}");
        }

        var newId = $"fork-{DateTime.UtcNow:MMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}";
        var targetPath = Path.Combine(Options.SessionsDir, newId + ".jsonl");

        Data.SessionForker.Fork(sourcePath, targetPath, fromSeq, newId);

        // 打开成分叉会话的 runtime（日志已含全部前缀事件，内存表构造时自动从磁盘装载）
        GetOrOpenSession(newId);

        // 分叉继承源会话的工作模式与项目目录（分叉 = "换个决定重试"，实验前提不变）
        if (SessionMetas().TryGetValue(sourceSessionId, out var forkMeta))
        {
            SaveSessionMeta(newId, forkMeta.Mode, forkMeta.ProjectDir);
        }

        return newId;
    }

    /// <summary>
    /// **重建**：用 checkpoint 当种子开一个「新窗口」（v3.12 rebuild，v3.23 兑现）。
    ///
    /// <para>
    /// 与 <see cref="ForkSession"/> 的分工很清楚：
    /// 分叉复制**历史前缀**（「换个决定重试」）；重建**只带状态不带史** ——
    /// 新会话只有 header + 一条种子（checkpoint 的渲染块），
    /// 于是「上下文满了、但状态很清楚」的长任务可以轻装上阵继续跑。
    /// </para>
    /// <para>
    /// 种子用 <see cref="ContextCompactedEvent"/> 承载（触发来源标 <c>rebuild</c>）——
    /// 投影器本来就会把它的 Summary 注入成合成块，所以不必为 rebuild 新增事件类型、也不必改投影器。
    /// </para>
    /// </summary>
    /// <param name="sourceSessionId">源会话。</param>
    /// <param name="checkpointSeq">用哪一份 checkpoint；null = 用最新的一份。</param>
    public string RebuildSession(string sourceSessionId, long? checkpointSeq = null)
    {
        var sourcePath = Path.Combine(Options.SessionsDir, sourceSessionId + ".jsonl");
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException($"源会话不存在：{sourceSessionId}");
        }

        var events = JsonlEventLog.Read(sourcePath);
        var header = events.OfType<SessionCreatedEvent>().FirstOrDefault();
        var checkpoint = checkpointSeq is { } seq
            ? events.OfType<CheckpointEvent>().LastOrDefault(c => c.Seq == seq)
            : events.OfType<CheckpointEvent>().LastOrDefault();

        if (checkpoint is null)
        {
            throw new InvalidOperationException(
                "源会话没有可用的 checkpoint —— 先打开 checkpoint 开关（配置 / 界面）或手动写一次，再重建。");
        }

        var seed = checkpoint.RenderBlock();
        if (string.IsNullOrWhiteSpace(seed))
        {
            throw new InvalidOperationException("该 checkpoint 字段全空，没有可注入的状态，不重建。");
        }

        var newId = $"rebuild-{DateTime.UtcNow:MMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}";
        var targetPath = Path.Combine(Options.SessionsDir, newId + ".jsonl");

        // 不借 SessionForker：它做的是「复制前缀」，而这里要的是**零历史** ——
        // 从源日志里挑不出「空」这个前缀（且源会话未必有 header，会把第一条消息带过来）。
        // 直接建一份全新日志：header（记血缘）+ 一条种子。
        using (var log = JsonlEventLog.Open(targetPath))
        {
            log.Append(new SessionCreatedEvent
            {
                SessionId = newId,
                ProjectId = header?.ProjectId,
                Title = header is null ? "rebuild" : $"{header.Title} (rebuild)",
                ParentSessionId = sourceSessionId,
                ForkFromSeq = checkpoint.Seq,
            });

            // 种子落成一条**助手消息**（新窗口一开场先「交代」上一段的状态）。
            //
            // 为什么不用 ContextCompactedEvent 承载：摘要型事件走的是「替换被折叠的旧轮次」这条路，
            // 而新窗口**根本没有旧轮可替换** —— 用它承载种子，投影出来是空的
            // （这是本工程验证抓到的真实缺陷，不是理论推演）。
            log.Append(new AssistantMessageEvent
            {
                SessionId = newId,
                Text = seed,
            });
        }

        // 打开成新会话的 runtime（装载时已含 header + 种子）。
        GetOrOpenSession(newId);

        // 重建继承源会话的工作模式与项目目录（换了窗口，实验前提不变）。
        if (SessionMetas().TryGetValue(sourceSessionId, out var rebuildMeta))
        {
            SaveSessionMeta(newId, rebuildMeta.Mode, rebuildMeta.ProjectDir);
        }

        return newId;
    }

    /// <summary>重命名会话（F4）：标题属"用户可改元数据"，存 titles.json（与 rephrase.json 同类），不进事件流。</summary>
    public void RenameSession(string sessionId, string title)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        var path = Path.Combine(Options.SessionsDir, "titles.json");

        // v3.6 审查修复：读—改—写全程持锁 + 原子落盘 ——
        // 原先无锁读改写，并发重命名会丢更新；写中途崩溃还会留下半截 JSON（下次读被当坏文件丢弃）。
        lock (_metaGate)
        {
            var titles = new Dictionary<string, string>(StringComparer.Ordinal);
            if (File.Exists(path))
            {
                try
                {
                    var loaded = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
                    if (loaded is not null)
                    {
                        titles = new Dictionary<string, string>(loaded, StringComparer.Ordinal);
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    // 坏文件当没有
                }
            }

            titles[sessionId] = title.Trim();
            WriteJsonAtomic(path, titles);
        }
    }

    /// <summary>读全部会话标题（列表与导出共用）。</summary>
    public IReadOnlyDictionary<string, string> LoadTitles()
    {
        var path = Path.Combine(Options.SessionsDir, "titles.json");
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>();
        }

        try
        {
            var loaded = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            return loaded is null ? new Dictionary<string, string>() : new Dictionary<string, string>(loaded, StringComparer.Ordinal);
        }
        catch (System.Text.Json.JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    /// <summary>
    /// 导出会话为 Markdown（F8）：存档系统的"分享"出口。
    /// user/assistant 全文 + 工具调用与任务状态摘要；usage/reasoning 属噪音不导。
    /// </summary>
    public string ExportSessionMarkdown(string sessionId)
    {
        var path = Path.Combine(Options.SessionsDir, sessionId + ".jsonl");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"会话不存在：{sessionId}");
        }

        var titles = LoadTitles();
        var title = titles.TryGetValue(sessionId, out var t) ? t : sessionId;
        // v3.6 审查修复：读 _sessions 必须持锁 —— 别处一律持锁读写，
        // 并发导出与「切/开/删会话」相遇时，普通 Dictionary 被并发修改会抛异常。
        Hosting.SessionRuntime? runtime;
        lock (_sessions)
        {
            _sessions.TryGetValue(sessionId, out runtime);
        }

        var all = runtime is not null
            ? runtime.Events
            : AgentFramework.Data.JsonlEventLog.Read(path).ToList();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.AppendLine($"> 会话 `{sessionId}` · 导出于 {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine();

        foreach (var e in all)
        {
            switch (e)
            {
                case UserMessageEvent u:
                    sb.AppendLine("## 用户").AppendLine().AppendLine(u.ModelVisibleText).AppendLine();
                    break;

                case AssistantMessageEvent a when !string.IsNullOrWhiteSpace(a.Text):
                    sb.AppendLine("## 助手").AppendLine().AppendLine(a.Text).AppendLine();
                    break;

                case ToolCallRequestedEvent req:
                    sb.AppendLine($"**调用工具** `{req.ToolName}`（call {req.CallId}）").AppendLine();
                    break;

                case ToolCallCompletedEvent done:
                    sb.AppendLine($"- 结果：{(done.Success ? "成功" : $"失败 — {done.Error}")}").AppendLine();
                    break;

                case TaskStatusChangedEvent ts:
                    sb.AppendLine($"- 任务 `{ts.TaskId}` → **{ts.Status}**").AppendLine();
                    break;
            }
        }

        return sb.ToString();
    }

    // ── 会话模式的持久化（modes.json，与 titles.json 同类）───

    /// <summary>会话元数据（模式 + 项目目录）。modes.json 是旧版纯模式映射，读入时自动迁移。</summary>
    public sealed record SessionMetaInfo(string Mode, string? ProjectDir);

    private string SessionMetaPath => Path.Combine(Options.SessionsDir, "session-meta.json");
    private string LegacyModesPath => Path.Combine(Options.SessionsDir, "modes.json");

    /// <summary>读全部会话元数据（会话列表徽标 + 惰性重开时换原）。</summary>
    public IReadOnlyDictionary<string, SessionMetaInfo> SessionMetas()
    {
        var result = new Dictionary<string, SessionMetaInfo>(StringComparer.Ordinal);

        // 旧版迁移：modes.json（sessionId → mode 字符串）
        if (File.Exists(LegacyModesPath))
        {
            try
            {
                var legacy = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(LegacyModesPath));
                if (legacy is not null)
                {
                    foreach (var (id, mode) in legacy)
                    {
                        result[id] = new SessionMetaInfo(mode, null);
                    }
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // 坏文件当没有
            }
        }

        if (File.Exists(SessionMetaPath))
        {
            try
            {
                var loaded = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, SessionMetaInfo>>(File.ReadAllText(SessionMetaPath));
                if (loaded is not null)
                {
                    foreach (var (id, meta) in loaded)
                    {
                        result[id] = meta;
                    }
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // 坏文件当没有
            }
        }

        return result;
    }

    private void SaveSessionMeta(string sessionId, string? modeId, string? projectDir)
    {
        lock (_metaGate)
        {
            var metas = new Dictionary<string, SessionMetaInfo>(SessionMetas(), StringComparer.Ordinal);
            var existing = metas.TryGetValue(sessionId, out var m) ? m : null;
            metas[sessionId] = new SessionMetaInfo(
                modeId ?? existing?.Mode ?? AgentModes.IdOf(_mode),
                projectDir ?? existing?.ProjectDir);

            Directory.CreateDirectory(Options.SessionsDir);
            WriteJsonAtomic(SessionMetaPath, metas);
        }
    }

    /// <summary>
    /// 原子写 JSON（v3.6 审查修复）：先写同目录临时文件，再覆盖式 <see cref="File.Move(string, string, bool)"/>。
    /// 直接 <c>File.WriteAllText</c> 若在写中途崩溃/被杀，会留下半截 JSON —— 下次读被当坏文件、整份元数据丢失。
    /// 同目录 + 覆盖式 Move 在主流文件系统上是原子替换（临时文件与目标同卷，Move 不跨设备）。
    /// </summary>
    private static void WriteJsonAtomic<T>(string path, T value)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(value));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// 把「当前会话」指针换到 <paramref name="target"/>。
    /// 跟随字段（日志 / 事件出口 / 主循环）必须**一起**换 ——
    /// 漏一个，就会出现「投影读 A 的日志、事件写进 B 的流」这种最难查的错位。
    /// </summary>
    private void SwapTo(Hosting.SessionRuntime target)
    {
        // 一次引用赋值即完成切换：_log/_sink/_runner 都是 _session 的派生属性，
        // 「换了半边」的中间态在结构上不存在（并发读者要么全看到旧会话，要么全看到新会话）。
        _session = target;
    }
}
