using AgentFramework.Contracts;
using AgentFramework.Data;

// ═══════════════════════════════════════════════════════════
//  数据平面垂直切片验证
//  追加 → 读取 → 投影 → 重启恢复 → 分叉 → 崩溃自修复
// ═══════════════════════════════════════════════════════════

var passes = 0;
var failures = 0;

void Check(string name, bool ok, string? detail = null)
{
    var suffix = detail is null ? "" : $"  ({detail})";
    if (ok)
    {
        passes++;
        Console.WriteLine($"  [PASS] {name}{suffix}");
    }
    else
    {
        failures++;
        Console.WriteLine($"  [FAIL] {name}{suffix}");
    }
}

var root = Path.Combine(Path.GetTempPath(), "af-data-verify", Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(root);
Console.WriteLine("═══ 数据平面垂直切片验证 ═══");
Console.WriteLine($"工作目录：{root}");

const string sessionId = "s-main";
var logPath = Path.Combine(root, "session-main.jsonl");

// ── 1. 追加与读取 ──────────────────────────────────────────
Console.WriteLine("\n── 1. 追加与读取 ──");
using (var log = JsonlEventLog.Open(logPath))
{
    log.Append(new SessionCreatedEvent { SessionId = sessionId, ProjectId = "p-1", Title = "记事本项目" });
    log.Append(new UserMessageEvent { SessionId = sessionId, Text = "帮我做一个记事本" });
    log.Append(new AssistantMessageEvent { SessionId = sessionId, Text = "好的，先搭骨架" });
    log.Append(new TaskCreatedEvent { SessionId = sessionId, TaskId = "t-1", Title = "搭骨架" });
    log.Append(new ToolCallRequestedEvent
    {
        SessionId = sessionId,
        CallId = "c-1",
        ToolName = "write_file",
        Arguments = new Dictionary<string, string?> { ["path"] = "main.cs" },
    });
    log.Append(new ToolCallCompletedEvent { SessionId = sessionId, CallId = "c-1", Success = true, Output = "written" });
    log.Append(new TaskCreatedEvent { SessionId = sessionId, TaskId = "t-2", Title = "写界面" });
    log.Append(new TaskStatusChangedEvent
    {
        SessionId = sessionId,
        TaskId = "t-1",
        Status = AgentTaskStatus.Done,
        Reason = "骨架已生成",
    });
}

var events = JsonlEventLog.Read(logPath).ToList();
Check("写 8 条、读回 8 条", events.Count == 8, $"{events.Count} 条");
Check("Seq 严格递增 1..8", events.Select(e => e.Seq).SequenceEqual(Enumerable.Range(1, 8).Select(i => (long)i)));
Check("多态反序列化正确（事件类型保真）", events[1] is UserMessageEvent && events[4] is ToolCallRequestedEvent);

// ── 2. 投影 ────────────────────────────────────────────────
Console.WriteLine("\n── 2. 投影（状态只能来自事件）──");
var state = SessionProjector.Project(events);
Check("投影出 2 条消息", state.Messages.Count == 2, $"{state.Messages.Count}");
Check("投影出 2 个任务", state.Tasks.Count == 2, $"{state.Tasks.Count}");
Check("任务 t-1 = done", state.Tasks["t-1"].Status == AgentTaskStatus.Done, state.Tasks["t-1"].Status);
Check("任务 t-2 = todo", state.Tasks["t-2"].Status == AgentTaskStatus.Todo, state.Tasks["t-2"].Status);
Check("工具调用结果已归并", state.ToolCalls.Count == 1 && state.ToolCalls[0].Success == true);
Check("投影无异常", state.Anomalies.Count == 0, string.Join("; ", state.Anomalies));

// ── 3. 状态的唯一来源就是事件 ──────────────────────────────
Console.WriteLine("\n── 3. 追加事件即可改变状态（状态没有独立存储）──");
using (var log = JsonlEventLog.Open(logPath))
{
    log.Append(new TaskStatusChangedEvent
    {
        SessionId = sessionId,
        TaskId = "t-2",
        Status = AgentTaskStatus.InProgress,
    });
}

var state2 = SessionProjector.ProjectFile(logPath);
Check("重投影后 t-2 = in-progress", state2.Tasks["t-2"].Status == AgentTaskStatus.InProgress, state2.Tasks["t-2"].Status);
Check("事件总数 9", state2.EventCount == 9, $"{state2.EventCount}");

// ── 4. 重启恢复 ────────────────────────────────────────────
Console.WriteLine("\n── 4. 重启恢复（关闭 → 重开 → 现场还在）──");
var beforeRestart = SessionProjector.ProjectFile(logPath);
using (var log = JsonlEventLog.Open(logPath))
{
    Check("重启后序号接续（LastSeq=9）", log.LastSeq == 9, $"{log.LastSeq}");
    Check("重启后条数正确（9）", log.Count == 9, $"{log.Count}");
    log.Append(new UserMessageEvent { SessionId = sessionId, Text = "继续" });
    Check("重启后追加序号为 10", log.LastSeq == 10, $"{log.LastSeq}");
}

var afterRestart = SessionProjector.ProjectFile(logPath);
Check("重开后可继续追加并被读到", afterRestart.Messages.Count == beforeRestart.Messages.Count + 1,
    $"{beforeRestart.Messages.Count} → {afterRestart.Messages.Count}");
Check("重启后任务状态保持", afterRestart.Tasks["t-2"].Status == AgentTaskStatus.InProgress);
Check("重启后历史消息完整", afterRestart.Messages[0].Text == "帮我做一个记事本");

// ── 5. 分叉 ────────────────────────────────────────────────
Console.WriteLine("\n── 5. 分叉（从 seq=4 分叉）──");
var forkPath = Path.Combine(root, "session-fork.jsonl");
var fork = SessionForker.Fork(logPath, forkPath, fromSeq: 4, newSessionId: "s-fork");
var forkState = SessionProjector.ProjectFile(forkPath);

Check("分叉会话 Id 已改写", forkState.SessionId == "s-fork", forkState.SessionId ?? "(null)");
Check("分叉记录了血缘", forkState.ParentSessionId == sessionId, forkState.ParentSessionId ?? "(null)");
Check("分叉点记录正确", forkState.ForkFromSeq == 4, $"{forkState.ForkFromSeq}");
Check("复制了前缀事件 3 条（不含源 created）", fork.CopiedEvents == 3, $"{fork.CopiedEvents}");
Check("分叉会话消息 2 条", forkState.Messages.Count == 2, $"{forkState.Messages.Count}");
Check("分叉会话任务 1 个（只含前缀里的）", forkState.Tasks.Count == 1, $"{forkState.Tasks.Count}");
Check("分叉后 t-1 回到 todo（后续状态变更未带入）", forkState.Tasks["t-1"].Status == AgentTaskStatus.Todo,
    forkState.Tasks["t-1"].Status);

using (var forkLog = JsonlEventLog.Open(forkPath))
{
    forkLog.Append(new UserMessageEvent { SessionId = "s-fork", Text = "在分叉上继续" });
}

Check("分叉日志可独立追加（5 条）", JsonlEventLog.Read(forkPath).Count() == 5);
Check("源会话未受分叉影响（10 条）", JsonlEventLog.Read(logPath).Count() == 10);

// ── 6. 可重建（投影幂等）──────────────────────────────────
Console.WriteLine("\n── 6. 可重建 ──");
var rebuildA = SessionProjector.ProjectFile(forkPath);
var rebuildB = SessionProjector.ProjectFile(forkPath);
Check("同一日志两次投影结果一致（可重建）",
    rebuildA.EventCount == rebuildB.EventCount
    && rebuildA.Messages.Count == rebuildB.Messages.Count
    && rebuildA.Tasks.Count == rebuildB.Tasks.Count
    && rebuildA.Tasks["t-1"].Status == rebuildB.Tasks["t-1"].Status);

// ── 7. 崩溃安全与自修复 ────────────────────────────────────
Console.WriteLine("\n── 7. 崩溃安全与自修复 ──");
var crashPath = Path.Combine(root, "session-crash.jsonl");
using (var log = JsonlEventLog.Open(crashPath))
{
    log.Append(new SessionCreatedEvent { SessionId = "s-crash", Title = "崩溃测试" });
    log.Append(new UserMessageEvent { SessionId = "s-crash", Text = "第一条" });
}

// 模拟进程在写半行时被杀
File.AppendAllText(crashPath, "{\"type\":\"user-message\",\"Seq\":3,\"SessionId\":\"s-cr");

var afterCrash = JsonlEventLog.Read(crashPath).ToList();
Check("坏行被安全跳过（读到 2 条）", afterCrash.Count == 2, $"{afterCrash.Count} 条");
Check("崩溃后投影仍可用", SessionProjector.Project(afterCrash).Messages.Count == 1);

var lengthBeforeRepair = new FileInfo(crashPath).Length;
using (var log = JsonlEventLog.Open(crashPath))
{
    var lengthAfterRepair = new FileInfo(crashPath).Length;
    Check("Open 触发自修复（截断到最后一条完整记录）",
        lengthAfterRepair < lengthBeforeRepair,
        $"{lengthBeforeRepair} → {lengthAfterRepair} 字节");

    Check("修复后序号接续（LastSeq=2）", log.LastSeq == 2, $"{log.LastSeq}");

    log.Append(new UserMessageEvent { SessionId = "s-crash", Text = "崩溃后继续" });
}

var finalState = SessionProjector.ProjectFile(crashPath);
Check("修复后追加的事件可被读到（3 条）", finalState.EventCount == 3, $"{finalState.EventCount}");
Check("修复后消息完整（第一条 + 崩溃后继续）",
    finalState.Messages.Count == 2 && finalState.Messages[1].Text == "崩溃后继续");

// ── 投影快照缓存 ───────────────────────────────────────────
Console.WriteLine("\n── 投影快照缓存 ──");

var cacheRoot = Path.Combine(Path.GetTempPath(), "af-cache-verify", Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(cacheRoot);
var cacheLog = Path.Combine(cacheRoot, "s1.jsonl");

using (var log = JsonlEventLog.Open(cacheLog))
{
    log.Append(new UserMessageEvent { SessionId = "s1", Text = "一" });
    log.Append(new AssistantMessageEvent { SessionId = "s1", Text = "回一" });
}

var cacheDir = Path.Combine(cacheRoot, ".cache");
var cache = new SnapshotProjectionCache(cacheDir);

var first = cache.GetOrRebuild(cacheLog);
Check("首次读取走全量重放", cache.FullRebuilds == 1 && cache.IncrementalHits == 0);
Check("投影结果正确", first.Messages.Count == 2, $"{first.Messages.Count} 条");

var second = cache.GetOrRebuild(cacheLog);
Check("★ 二次读取命中快照（增量）", cache.IncrementalHits == 1);
Check("快照结果与全量一致",
    second.Messages.Count == first.Messages.Count && second.EventCount == first.EventCount);

using (var log = JsonlEventLog.Open(cacheLog))
{
    log.Append(new UserMessageEvent { SessionId = "s1", Text = "二" });
}

var third = cache.GetOrRebuild(cacheLog);
Check("★ 只重放快照之后的尾部事件",
    third.Messages.Count == 3 && third.Messages[2].Text == "二",
    $"{third.Messages.Count} 条");
Check("增量结果 == 全量结果",
    third.Messages.Count == SessionProjector.ProjectFile(cacheLog).Messages.Count
    && third.EventCount == SessionProjector.ProjectFile(cacheLog).EventCount);

var snapshotFile = Directory.GetFiles(cacheDir).Single();
File.WriteAllText(snapshotFile, "{ 半个 json");
var afterCorrupt = cache.GetOrRebuild(cacheLog);
Check("★ 快照损坏可自愈（退回全量重放）",
    afterCorrupt.Messages.Count == 3, $"{afterCorrupt.Messages.Count} 条");

cache.Invalidate(cacheLog);
Check("可主动失效快照", !File.Exists(snapshotFile));

Console.WriteLine($"\n═══ 结果：{passes} 通过 / {failures} 失败 ═══");
return failures == 0 ? 0 : 1;
