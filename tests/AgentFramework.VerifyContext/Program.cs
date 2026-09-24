using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentFramework.Contracts;
using AgentFramework.Data;
using AgentFramework.Host;
using AgentFramework.Index;
using AgentFramework.Tools;

// ═══════════════════════════════════════════════════════════
//  长任务上下文治理 垂直切片验证
//  Token 估算 / 投影窗口 / 遮蔽 / 任务卡 / 压缩决策 / 留痕回放 /
//  大输出引用化 / L5 摘要折叠 / SQLite 派生索引 / 历史检索 / 端到端
//
//  验的是四句承诺：
//    1. 上下文是投影，不是日志 —— 压缩不删历史，且可回放
//    2. 默认不靠 LLM 摘要 —— 靠结构性裁剪（引用化 / 遮蔽 / 任务卡）
//    3. 被遮蔽的内容**随时能查回来**（索引 + search_history）→ 无损压缩
//    4. 任何一层失败都只降级、不阻断
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

void Section(string title) => Console.WriteLine($"\n── {title} ──");

var root = Path.Combine(Path.GetTempPath(), "af-context-verify", Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(root);
Console.WriteLine("═══ 长任务上下文治理验证 ═══");
Console.WriteLine($"工作目录：{root}");

// ── 1. Token 估算 ──────────────────────────────────────────
Section("1. Token 估算（零依赖）");

Check("空文本估算为 0", TokenEstimator.Estimate("") == 0 && TokenEstimator.Estimate((string?)null) == 0);
Check("中文按字估算", TokenEstimator.Estimate("你好世界") == 4, $"{TokenEstimator.Estimate("你好世界")}");
Check("拉丁按 4 字符 1 token", TokenEstimator.Estimate("abcdefgh") == 2, $"{TokenEstimator.Estimate("abcdefgh")}");
Check("中英混排按各自规则", TokenEstimator.Estimate("你好abcd") == 3, $"{TokenEstimator.Estimate("你好abcd")}");
Check(
    "消息列表含每条协议开销",
    TokenEstimator.Estimate([new LlmMessage { Role = LlmRole.User, Content = "你好" }]) == 6);

// ── 2. 投影：窗口与遮蔽 ────────────────────────────────────
Section("2. 上下文投影（轮次窗口 + 旧结果遮蔽）");

var session = BuildLongSession(8, 3000);

var wide = SessionContextBuilder.Project(session, new ContextOptions { RecentTurnsKeptVerbatim = 100 });
Check("窗口足够大时不做遮蔽", wide.MaskedResults == 0);

var narrow = SessionContextBuilder.Project(session, new ContextOptions { RecentTurnsKeptVerbatim = 2 });
Check("超窗口的旧工具结果被遮蔽", narrow.MaskedResults == 6, $"{narrow.MaskedResults} 条");
Check("遮蔽后工具消息结构仍完整（配对不破）", narrow.Messages.Count(m => m.Role == LlmRole.Tool) == 8);
Check("遮蔽文本标明了工具名", narrow.Messages.Any(m => m.Content?.Contains("已折叠 read_file") == true));
Check("遮蔽文本保留了原长度", narrow.Messages.Any(m => m.Content?.Contains("原始 3005") == true));
Check("遮蔽文本指向了取回途径", narrow.Messages.Any(m => m.Content?.Contains("search_history") == true));
Check("窗口内的工具结果逐字保留", narrow.Messages.Last(m => m.Role == LlmRole.Tool).Content!.Contains("第8轮内容"));
Check("遮蔽不动对话文本", narrow.Messages.Count(m => m.Role == LlmRole.User && (m.Content is null || !m.Content.Contains("非用户发言"))) == 8);
Check(
    "★ 遮蔽确实省了 token",
    narrow.EstimatedTokens < wide.EstimatedTokens,
    $"{narrow.EstimatedTokens} < {wide.EstimatedTokens}");
Check(
    "关掉遮蔽开关就全保留",
    SessionContextBuilder.Project(session, new ContextOptions { RecentTurnsKeptVerbatim = 2, MaskOldToolResults = false })
        .MaskedResults == 0);
Check("全量预设不做任何裁剪", SessionContextBuilder.Project(session, ContextOptions.Full).MaskedResults == 0);

// ── 3. 任务卡 ──────────────────────────────────────────────
Section("3. 任务卡（防漂移锚点）");

var withTasks = BuildLongSession(3, 50).ToList();
long taskSeq = withTasks.Max(e => e.Seq);

void Append(SessionEvent sessionEvent)
{
    sessionEvent.Seq = ++taskSeq;
    sessionEvent.SessionId = "s";
    sessionEvent.Timestamp = DateTimeOffset.UtcNow;
    withTasks.Add(sessionEvent);
}

Append(new TaskCreatedEvent { TaskId = "t1", Title = "重构接口" });
Append(new TaskStatusChangedEvent { TaskId = "t1", Status = AgentTaskStatus.Done });
Append(new TaskCreatedEvent { TaskId = "t2", Title = "补测试" });
Append(new TaskStatusChangedEvent { TaskId = "t2", Status = AgentTaskStatus.InProgress });

var card = TaskCardBuilder.Build(withTasks);
Check("任务卡包含最初目标", card.Contains("最初目标"));
Check("任务卡包含进度", card.Contains("1/2 项已完成"), card.Split('\n').FirstOrDefault(l => l.Contains("进度")));
Check("任务卡列出进行中", card.Contains("进行中：补测试"));
Check("任务卡包含轮次", card.Contains("第 3 轮"));

var projected = SessionContextBuilder.Project(withTasks, new ContextOptions());
Check("任务卡被注入上下文", projected.TaskCardInjected);
Check(
    "★ 任务卡位于上下文末尾（近期注意力区）",
    projected.Messages[^1].Role == LlmRole.User && projected.Messages[^1].Content!.Contains("【任务卡·非用户发言】"));
Check("投影直接给出任务卡文本", projected.TaskCard is not null);

Check(
    "关掉开关就不注入任务卡",
    !SessionContextBuilder.Project(withTasks, new ContextOptions { InjectTaskCard = false }).TaskCardInjected);
Check("空会话不注入任务卡", !SessionContextBuilder.Project([], new ContextOptions()).TaskCardInjected);

var boundedCard = SessionContextBuilder.Project(withTasks, new ContextOptions { TaskCardMaxChars = 120 }).TaskCard;
Check("★ 任务卡恒定大小（超长被截断）", boundedCard!.Length <= 121, $"{boundedCard.Length}");

// ── 4. 压缩决策 ────────────────────────────────────────────
Section("4. 压缩决策（水位触发 + 窗口收紧）");

var roomy = new ContextOptions { TokenBudget = 10_000, CompressionTriggerRatio = 0.8, RecentTurnsKeptVerbatim = 2 };
Check("未超水位时不压缩", ContextCompactor.Plan(session, roomy) is null);

var tight = new ContextOptions { TokenBudget = 300, CompressionTriggerRatio = 0.8, RecentTurnsKeptVerbatim = 2 };
var plan = ContextCompactor.Plan(session, tight);
Check("超水位时给出压缩方案", plan is not null);
Check("方案把逐字窗口收紧一半", plan!.Tightened.RecentTurnsKeptVerbatim == 1, $"{plan.Tightened.RecentTurnsKeptVerbatim}");
Check(
    "★ 压缩后 token 下降",
    plan.After.EstimatedTokens < plan.Before.EstimatedTokens,
    $"{plan.Before.EstimatedTokens} → {plan.After.EstimatedTokens}");
Check("压缩后遮蔽条数增加", plan.After.MaskedResults > plan.Before.MaskedResults);
Check("压缩不改动原配置对象", tight.RecentTurnsKeptVerbatim == 2);
Check("重复压缩幂等（不会越切越狠）", ContextCompactor.Plan(session, tight)!.Tightened.RecentTurnsKeptVerbatim == 1);

// 没有可压空间时（老历史只有对话、没有工具结果）不产方案 —— 免得往日志里写空事件
var dialogueOnly = new List<SessionEvent>
{
    new UserMessageEvent { Seq = 1, SessionId = "s", Text = "问题一" },
    new AssistantMessageEvent { Seq = 2, SessionId = "s", Text = "回答一" },
    new UserMessageEvent { Seq = 3, SessionId = "s", Text = "问题二" },
    new AssistantMessageEvent { Seq = 4, SessionId = "s", Text = "回答二" },
};
var pointless = new ContextOptions
{
    TokenBudget = 10,
    CompressionTriggerRatio = 0.8,
    RecentTurnsKeptVerbatim = 1,
    MaskOldToolResults = true,
};
Check("没有可压空间时不产方案（不写空事件）", ContextCompactor.Plan(dialogueOnly, pointless) is null);

// ── 5. 压缩留痕与回放一致性 ────────────────────────────────
Section("5. 压缩留痕 + 回放一致性");

var maskedSeqs = plan.MaskedSeqs.ToList();
var withCompact = session.ToList();
withCompact.Add(new ContextCompactedEvent
{
    Seq = 9901,
    SessionId = "s",
    Timestamp = DateTimeOffset.UtcNow,
    Trigger = CompactionTrigger.Auto,
    PreTokens = plan.Before.EstimatedTokens,
    PostTokens = plan.After.EstimatedTokens,
    MaskedSeqs = maskedSeqs,
    MaskedCount = maskedSeqs.Count,
    TaskCard = "任务卡快照",
});

Check("压缩事件本身不产生新消息",
    SessionContextBuilder.Project(withCompact, new ContextOptions { RecentTurnsKeptVerbatim = 100 })
        .Messages.Count(m => m.Role == LlmRole.Tool)
    == SessionContextBuilder.Project(session, new ContextOptions { RecentTurnsKeptVerbatim = 100 })
        .Messages.Count(m => m.Role == LlmRole.Tool));

var replay = SessionContextBuilder.Project(withCompact, new ContextOptions { RecentTurnsKeptVerbatim = 100 });
Check(
    "★ 被钉死的序号即使放宽窗口也保持遮蔽（视图不来回跳）",
    replay.MaskedResults == maskedSeqs.Count,
    $"{replay.MaskedResults} / {maskedSeqs.Count}");

Check("投影器不产生异常记录", SessionProjector.Project(withCompact).Anomalies.Count == 0);

// ── 6. 早期摘要折叠（L5）──────────────────────────────────
Section("6. 早期摘要折叠（L5，默认关）");

var withSummary = session.ToList();
withSummary.Add(new ContextCompactedEvent
{
    Seq = 9902,
    SessionId = "s",
    Timestamp = DateTimeOffset.UtcNow,
    Summary = "前 6 轮：都在读 f1..f6，第 1 轮定下的接口不能改。",
});

var folded = SessionContextBuilder.Project(withSummary, new ContextOptions { RecentTurnsKeptVerbatim = 2 });
Check("摘要被放进上下文开头", folded.Messages[0].Role == LlmRole.User && folded.Messages[0].Content!.Contains("早期对话摘要·非用户发言"));
Check("摘要内容进了上下文", folded.Messages[0].Content!.Contains("接口不能改"));
Check("被折叠的老轮次不再逐条出现", folded.Messages.Count(m => m.Role == LlmRole.User && (m.Content is null || !m.Content.Contains("非用户发言"))) == 2, $"{folded.Messages.Count(m => m.Role == LlmRole.User && (m.Content is null || !m.Content.Contains("非用户发言")))}");
Check(
    "★ 折叠比单纯遮蔽更省",
    folded.EstimatedTokens < narrow.EstimatedTokens,
    $"{folded.EstimatedTokens} < {narrow.EstimatedTokens}");
Check("SummaryApplied 标记为真", folded.SummaryApplied);
Check("窗口外消息仍被收集（供滚动摘要）", folded.OlderMessages.Count > 0);

// ── 7. 大输出引用化（L2）──────────────────────────────────
Section("7. 大输出引用化（ArtifactStore）");

var wsRoot = Path.Combine(root, "ws");
Directory.CreateDirectory(wsRoot);

var store = new ArtifactStore(wsRoot);
Check("未超限时原样返回", store.Shrink("read_file", "短内容", 100, 20, 10) == "短内容");

var big = new string('A', 5000) + "尾巴";
var shrunk = store.Shrink("read_file", big, 200, 40, 20);
Check("超限时给出引用文本", shrunk.Contains("[结果过大，已落盘引用化]"));
Check("引用文本标出原始长度", shrunk.Contains($"{big.Length} 字符"));
Check("引用文本给出落盘路径", shrunk.Contains(ArtifactStore.DirectoryName + "/"));
Check("引用文本保留头尾", shrunk.Contains("—— 开头 ——") && shrunk.Contains("—— 结尾 ——"));

var relative = shrunk.Split('\n').First(l => l.StartsWith("完整内容：", StringComparison.Ordinal))
    ["完整内容：".Length..].Split('（')[0];
Check("★ 工件文件真的落了盘", File.Exists(Path.Combine(wsRoot, relative)));
Check("★ 落盘内容完整无损", File.ReadAllText(Path.Combine(wsRoot, relative)).Length == big.Length);

var toolkit = new ToolkitOptions { WorkspaceRoot = wsRoot };
File.WriteAllText(Path.Combine(wsRoot, "big.txt"), big);

var readResult = await new ReadFileTool(toolkit).InvokeAsync(
    new ToolInvocation("read_file", new Dictionary<string, string?> { ["path"] = "big.txt" }));
Check("★ read_file 大文件走引用化", readResult.Success && readResult.Output!.Contains("已落盘引用化"));

var plainRead = await new ReadFileTool(new ToolkitOptions { WorkspaceRoot = wsRoot, ArtifactsEnabled = false })
    .InvokeAsync(new ToolInvocation("read_file", new Dictionary<string, string?> { ["path"] = "big.txt" }));
Check("关掉落盘开关时原样返回", plainRead.Output!.Length == big.Length);

// Windows 的 run_command 走 cmd.exe，bash 循环跑不动 —— 按平台给等价命令。
var bigEchoCommand = OperatingSystem.IsWindows()
    ? "for /l %i in (1,1,400) do @echo line-%i-aaaaaaaaaaaaaaaaaaaaaa"
    : "for i in $(seq 1 400); do echo line-$i-aaaaaaaaaaaaaaaaaaaaaa; done";
var commandResult = await new RunCommandTool(toolkit, new AgentFramework.Sandbox.SandboxRegistry()).InvokeAsync(
    new ToolInvocation("run_command", new Dictionary<string, string?>
    {
        ["command"] = bigEchoCommand,
    }));
Check("run_command 大输出走引用化",
    commandResult.Success && commandResult.Output!.Contains("已落盘引用化"),
    commandResult.Output?.Length.ToString());

// ── 7.5 压缩的收敛：一次减半不够就继续减 ───────────────────
Section("7.5 压缩的收敛：一次减半不够就继续减");

// 工具结果要足够长 —— 否则它比折叠占位符还短，遮蔽只会更长（门槛会正确拦下）
var heavyEvents = BuildLongSession(12, 400);
var tightOptions = new ContextOptions
{
    TokenBudget = 50,
    CompressionTriggerRatio = 0.8,
    RecentTurnsKeptVerbatim = 12,
    MaskOldToolResults = true,
    MinMaskSavingChars = 0,
    InjectTaskCard = false,
};

var tightBefore = SessionContextBuilder.Project(heavyEvents, tightOptions);
var tightenPlan = ContextCompactor.Plan(heavyEvents, tightOptions);
Check("★ 超水位时给出压缩方案", tightenPlan is not null,
    $"水位 {tightBefore.EstimatedTokens}/{tightBefore.Budget}，需要压缩={tightBefore.NeedsCompression}");
Check("★ 压缩后体积确实下降",
    tightenPlan is not null && tightenPlan.After.EstimatedTokens < tightenPlan.Before.EstimatedTokens,
    tightenPlan is null ? "-" : $"{tightenPlan.Before.EstimatedTokens} → {tightenPlan.After.EstimatedTokens}");
Check("★ 一直收紧到不再超标（或收到最紧），而不是只减半一次",
    tightenPlan is not null && (!tightenPlan.After.NeedsCompression || tightenPlan.Tightened.RecentTurnsKeptVerbatim == 1),
    tightenPlan is null ? "-" : $"窗口 {tightenPlan.Tightened.RecentTurnsKeptVerbatim}，水位 {tightenPlan.After.EstimatedTokens}");

// ── 8. SQLite 派生索引 ─────────────────────────────────────
Section("8. SQLite 派生索引（可查的历史）");

await using var index = await SqliteSessionIndex.OpenAsync(Path.Combine(root, "idx", "index.db"));
Check("索引可打开", index.IsAvailable && index.Kind == "sqlite", index.Kind);

var indexed = BuildLongSession(3, 20);
foreach (var sessionEvent in indexed)
{
    await index.IndexAsync("s1", sessionEvent);
}

Check("写入后条数正确", await index.CountAsync("s1") == indexed.Count, $"{await index.CountAsync("s1")} / {indexed.Count}");

var hits = await index.SearchAsync("第2轮内容", "s1", 5);
Check("★ 中文关键词检索命中", hits.Count == 1, $"{hits.Count} 条");
Check("命中片段包含关键词", hits.Count > 0 && hits[0].Snippet.Contains("第2轮内容"));
Check("命中带会话与类型", hits.Count > 0 && hits[0].SessionId == "s1" && hits[0].Type == "tool-call-completed");
Check("无命中时返回空", (await index.SearchAsync("这个词根本不存在zzz", "s1", 5)).Count == 0);

var beforeRepeat = await index.CountAsync("s1");
await index.IndexAsync("s1", indexed[1]);
Check("幂等：同 seq 重复写入不重复计数", await index.CountAsync("s1") == beforeRepeat);

await index.RebuildAsync("s1", indexed);
Check("重建后条数等于事件数", await index.CountAsync("s1") == indexed.Count);

foreach (var sessionEvent in BuildLongSession(1, 10))
{
    await index.IndexAsync("s2", sessionEvent);
}

Check("★ 按会话隔离统计", await index.CountAsync("s1", default) > await index.CountAsync("s2", default),
    $"{await index.CountAsync("s1")} vs {await index.CountAsync("s2")}");
Check("检索可跨会话（sessionId 传 null）", (await index.SearchAsync("第1轮内容", null, 20)).Count >= 2);

await index.IndexAsync("s1", new UserMessageEvent { Seq = 7777, SessionId = "s1", Text = "折扣 100% 全免，_下划线也在里面" });
Check("★ LIKE 的 % 被正确转义", (await index.SearchAsync("100%", "s1", 5)).Count == 1);
Check("★ LIKE 的 _ 被正确转义", (await index.SearchAsync("_下划线", "s1", 5)).Count == 1);

// 多词检索 + 相关度排序：整串包含做不到「语序不同也能捞回」
await index.IndexAsync("s1", new UserMessageEvent { Seq = 8100, SessionId = "s1", Text = "报错 超时：请重试" });
await index.IndexAsync("s1", new UserMessageEvent { Seq = 8101, SessionId = "s1", Text = "这条只是超时了，另外也提到报错" });

var ranked = await index.SearchAsync("报错 超时", "s1", 10);
Check("★ 多词检索：语序不同也能命中（旧的整串包含做不到）", ranked.Count >= 2, $"{ranked.Count} 条");

var rankedSeqs = ranked.Select(h => h.Seq).ToList();
Check("★ 相关度排序：整串命中的排在只有片段命中的前面（即便它更早）",
    rankedSeqs.Contains(8100) && rankedSeqs.Contains(8101) && rankedSeqs.IndexOf(8100) < rankedSeqs.IndexOf(8101));

var broken = await SqliteSessionIndex.OpenAsync(Path.Combine(root, "bad\0path", "x.db"));
Check("★ 打不开时降级为无索引（不抛异常）", !broken.IsAvailable && broken.Kind == "none");

// 索引水位：判断「落后」用 MAX(seq)，不用 Count（空文本事件不进表，Count 永不对齐）
if (index is SqliteSessionIndex sqliteIndex)
{
    var watermarkBeforeEmpty = await sqliteIndex.GetIndexedWatermarkAsync("s1");
    await sqliteIndex.IndexAsync("s1", new SessionCreatedEvent { Seq = 9000, SessionId = "s1", Title = "" });
    var watermarkAfterEmpty = await sqliteIndex.GetIndexedWatermarkAsync("s1");
    Check("★ 空文本事件也推进 seq 水位（Count 对齐靠水位，不靠条数）",
        watermarkAfterEmpty >= 9000 && watermarkAfterEmpty > watermarkBeforeEmpty,
        $"{watermarkBeforeEmpty} → {watermarkAfterEmpty}");
    Check("水位与已索引条数分开（Count 仍是行数）",
        await sqliteIndex.CountAsync("s1") < watermarkAfterEmpty,
        $"count={await sqliteIndex.CountAsync("s1")} watermark={watermarkAfterEmpty}");

    // 新类型不再叫 unknown
    await sqliteIndex.IndexAsync("s1", new CheckpointEvent { Seq = 9100, SessionId = "s1", Intent = "水位测试 checkpoint" });
    var checkpointHit = (await sqliteIndex.SearchAsync("水位测试 checkpoint", "s1", 5)).FirstOrDefault();
    Check("★ checkpoint 等新类型进索引且 TypeName 不再是 unknown",
        checkpointHit is not null && checkpointHit.Type == "checkpoint",
        checkpointHit?.Type ?? "(null)");
}

// ── 9. search_history 工具 ─────────────────────────────────
Section("9. search_history（把折叠掉的内容捞回来）");

var historyTool = new SearchHistoryTool(toolkit, index, () => "s1");
Check("工具名正确", historyTool.Name == "search_history");

var found = await historyTool.InvokeAsync(
    new ToolInvocation("search_history", new Dictionary<string, string?> { ["query"] = "第2轮内容" }));
Check("★ 工具能捞出历史内容", found.Success && found.Output!.Contains("第2轮内容"));
Check("结果提醒「不一定仍在你当前上下文里」", found.Output!.Contains("不一定仍在你的当前上下文里"));
Check("结果按相关度排序（而非纯时间）", found.Output!.Contains("最相关的在前"));

var notFound = await historyTool.InvokeAsync(
    new ToolInvocation("search_history", new Dictionary<string, string?> { ["query"] = "zzz根本不存在" }));
Check("无命中时给出可操作提示", notFound.Success && notFound.Output!.Contains("没有找到"));

var noIndexTool = new SearchHistoryTool(toolkit, NullSessionIndex.Instance, () => "s1");
Check(
    "索引不可用时明确失败（不瞎猜）",
    !(await noIndexTool.InvokeAsync(new ToolInvocation("search_history", new Dictionary<string, string?> { ["query"] = "x" }))).Success);

// ── 10. 宿主端到端 ─────────────────────────────────────────
Section("10. 宿主端到端：长会话自动治理");

var e2eRoot = Path.Combine(root, "e2e");
var e2eWorkspace = Path.Combine(e2eRoot, "ws");
Directory.CreateDirectory(e2eWorkspace);
File.WriteAllText(Path.Combine(e2eWorkspace, "big.txt"), new string('Z', 20_000));

var main = new AlternatingToolClient();
var e2eOptions = new HostOptions
{
    WorkspaceRoot = e2eWorkspace,
    SessionsDir = Path.Combine(e2eRoot, "sessions"),
    SessionId = "long",
    LlmOverride = main,
    ApprovalPolicy = _ => ApprovalDecision.Allow,
    Context = new ContextOptions
    {
        TokenBudget = 900,
        CompressionTriggerRatio = 0.6,
        RecentTurnsKeptVerbatim = 3,
        InlineResultLimit = 2_000,
    },
};

await using (var host = await AgentHost.CreateAsync(e2eOptions))
{
    Check("宿主带上了历史检索工具", host.ToolNames.Contains("search_history"), string.Join(", ", host.ToolNames));
    Check("宿主索引可用", host.Index.IsAvailable);

    for (var i = 0; i < 10; i++)
    {
        await host.SendAsync($"第 {i + 1} 轮：读一下 big.txt 然后告诉我结果");
    }

    var events = host.Events();
    var compactions = events.OfType<ContextCompactedEvent>().ToList();

    Check("★ 长会话触发了自动压缩", compactions.Count > 0, $"{compactions.Count} 次");
    Check("压缩记录了前后水位", compactions[0].PreTokens > 0 && compactions[0].PostTokens > 0,
        $"{compactions[0].PreTokens} → {compactions[0].PostTokens}");
    Check("★ 压缩确实压低了水位", compactions[0].PostTokens < compactions[0].PreTokens);
    Check("压缩记录了被遮蔽的序号", compactions[0].MaskedSeqs.Count > 0);
    Check("压缩记录了任务卡快照", !string.IsNullOrWhiteSpace(compactions[0].TaskCard));
    Check("压缩触发方式为 auto", compactions[0].Trigger == CompactionTrigger.Auto);

    var finalProjection = SessionContextBuilder.Project(events, e2eOptions.Context);
    var fullProjection = SessionContextBuilder.Project(events, ContextOptions.Full);

    var maskedNote = finalProjection.Messages
        .First(m => m.Content?.StartsWith("[已折叠", StringComparison.Ordinal) == true).Content!;
    var rawResult = events.OfType<ToolCallCompletedEvent>().First().Output!;

    Check("★ 折叠后的体积远小于原结果",
        maskedNote.Length * 4 < rawResult.Length,
        $"{maskedNote.Length} vs {rawResult.Length} 字符");
    Check("最终上下文带任务卡", finalProjection.TaskCardInjected);
    Check("最终上下文遮蔽了历史工具结果", finalProjection.MaskedResults > 0, $"{finalProjection.MaskedResults} 条");
    Check("全量视图仍保留所有轮次（对照）", fullProjection.KeptTurns > finalProjection.KeptTurns,
        $"{fullProjection.KeptTurns} vs {finalProjection.KeptTurns}");

    var logText = JsonlEventLog.ReadRawText(host.SessionLogPath);
    Check("压缩留痕落进了日志文件", logText.Contains("context-compacted", StringComparison.Ordinal));

    Check("★ 索引条数跟上日志", await host.Index.CountAsync("long") >= events.Count - 1,
        $"{await host.Index.CountAsync("long")} / {events.Count}");
}

// ── 11. HTTP 可观测性 ──────────────────────────────────────
Section("11. HTTP：水位与索引状态可观测");

var webRoot = Path.Combine(root, "web");
var webOptions = new HostOptions
{
    WorkspaceRoot = Path.Combine(webRoot, "ws"),
    SessionsDir = Path.Combine(webRoot, "sessions"),
    SessionId = "web",
    LlmOverride = new AlternatingToolClient(),
};

await using var webHost = await AgentHost.CreateAsync(webOptions);
var port = PickFreePort();
using var server = new WebUiServer(webHost, webOptions, port);
using var serverCts = new CancellationTokenSource();
_ = server.RunAsync(serverCts.Token);
await Task.Delay(500);

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

using (var doc = JsonDocument.Parse(await http.GetStringAsync(server.Url + "api/status")))
{
    var status = doc.RootElement;
    Check("status 暴露上下文水位", status.TryGetProperty("context", out var ctx) && ctx.TryGetProperty("waterLevel", out _));
    Check("status 暴露预算与轮次", ctx.TryGetProperty("budget", out _) && ctx.TryGetProperty("turns", out _));
    Check("status 暴露索引后端", ctx.TryGetProperty("index", out var idx) && idx.TryGetProperty("kind", out _));
    Check("status 暴露压缩次数", ctx.TryGetProperty("compactions", out _));
}

var page = await http.GetStringAsync(server.Url);
Check("界面未因新增状态而损坏", page.Contains("Agent 对话", StringComparison.Ordinal) && page.Contains("/api/status", StringComparison.Ordinal));

// ── 8. 悬空工具调用：投影必须不产出非法序列（P0-2）────
Console.WriteLine("\n── 8. 悬空工具调用（回合中断）──");

var danglingProjection = SessionContextBuilder.Project(BuildDanglingSession(), new ContextOptions());

var assistantCallIds = danglingProjection.Messages
    .Where(m => m.Role == LlmRole.Assistant && m.ToolCalls is { Count: > 0 })
    .SelectMany(m => m.ToolCalls!)
    .Select(c => c.CallId)
    .ToList();

var toolResultIds = danglingProjection.Messages
    .Where(m => m.Role == LlmRole.Tool)
    .Select(m => m.ToolCallId ?? string.Empty)
    .ToHashSet(StringComparer.Ordinal);

Check("悬空的那次调用没有进上下文",
    !assistantCallIds.Contains("dangling-1"),
    string.Join(",", assistantCallIds));

Check("★ 每个 tool_calls 都有配对结果（否则端点直接 400）",
    assistantCallIds.Count > 0 && assistantCallIds.All(toolResultIds.Contains),
    $"调用 {string.Join(",", assistantCallIds)} / 结果 {string.Join(",", toolResultIds)}");

Check("中断之后的对话照常在上下文里",
    danglingProjection.Messages.Any(m => m.Role == LlmRole.User && m.Content?.Contains("中断之后") == true));

Check("有结果的工具调用不受影响",
    danglingProjection.Messages.Any(m => m.Role == LlmRole.Tool && m.ToolCallId == "ok-1"));

// ── 8.5 孤儿 ToolCallCompleted：没有 requested 的结果不得产出 role=tool ──
Console.WriteLine("\n── 8.5 孤儿 ToolCallCompleted（只有 completed）──");

var orphanEvents = new List<SessionEvent>();
long oseq = 0;

void AddOrphan(SessionEvent sessionEvent)
{
    sessionEvent.Seq = ++oseq;
    sessionEvent.SessionId = "s";
    sessionEvent.Timestamp = DateTimeOffset.UtcNow;
    orphanEvents.Add(sessionEvent);
}

AddOrphan(new UserMessageEvent { Text = "看看这个目录" });

// ← 孤儿：只有 completed、没有 requested（日志被裁剪/损坏时可能出现）
AddOrphan(new ToolCallCompletedEvent { CallId = "orphan-1", Success = true, Output = "孤儿结果正文" });

AddOrphan(new ToolCallRequestedEvent
{
    CallId = "ok-2",
    ToolName = "list_dir",
    Arguments = new Dictionary<string, string?> { ["path"] = "." },
});
AddOrphan(new ToolCallCompletedEvent { CallId = "ok-2", Success = true, Output = "a.txt" });
AddOrphan(new AssistantMessageEvent { Text = "列好了。" });

var orphanProjection = SessionContextBuilder.Project(orphanEvents, new ContextOptions());
var orphanToolIds = orphanProjection.Messages
    .Where(m => m.Role == LlmRole.Tool)
    .Select(m => m.ToolCallId ?? string.Empty)
    .ToList();

Check("★ 孤儿 tool 结果被丢弃（不产出非法 role=tool）",
    !orphanToolIds.Contains("orphan-1"),
    string.Join(",", orphanToolIds));
Check("有配对的 tool 结果不受影响", orphanToolIds.Contains("ok-2"));
Check("★ 丢弃孤儿后协议仍合法（每个 role=tool 都有对应 tool_calls）",
    orphanToolIds.Count > 0
    && orphanProjection.Messages
        .Where(m => m.Role == LlmRole.Tool)
        .All(m => orphanProjection.Messages.Any(a => a.ToolCalls?.Any(c => c.CallId == m.ToolCallId) == true)));
Check("孤儿丢弃后对话照常在上下文里",
    orphanProjection.Messages.Any(m => m.Role == LlmRole.User && m.Content?.Contains("看看这个目录") == true));

// ── 10. L6 骨架化：老轮次纯文本回复折叠（本批新增）──────────────
Section("10. L6 骨架化：老轮次纯文本回复折叠（无压力零损失，超水位才折叠）");

// 纯对话会话：只有 user/assistant，工具遮蔽无事可做 —— v3.4 里这种会话收到底也压不下去。
var dialogueTurns = 14;
var dialogueEvents = new List<SessionEvent>();
long dseq = 0;
for (var i = 1; i <= dialogueTurns; i++)
{
    dialogueEvents.Add(new UserMessageEvent
    {
        Seq = ++dseq,
        SessionId = "s",
        Timestamp = DateTimeOffset.UtcNow,
        Text = $"第 {i} 问：请展开讲讲主题 {i}。",
    });
    dialogueEvents.Add(new AssistantMessageEvent
    {
        Seq = ++dseq,
        SessionId = "s",
        Timestamp = DateTimeOffset.UtcNow,
        Text = $"关于主题 {i} 的长回答：" + new string('答', 600),
    });
}

// ── 10.1 L6 骨架化：预算内零损失，超水位才折叠 ──────────────
Section("10.1 L6 骨架化");

var dialogueOpts = new ContextOptions
{
    TokenBudget = 20_000,
    CompressionTriggerRatio = 0.8,
    RecentTurnsKeptVerbatim = 4,
    SkeletonizeOldAssistant = true,
    InjectTaskCard = false,
};

// 无压力：水位未超 → 第一遍投影直接返回，逐字保留。
var noPressure = SessionContextBuilder.Project(dialogueEvents, dialogueOpts);
Check("无压力时逐字保留（两遍投影的第一遍，零损失）",
    noPressure.Messages.Count(m => m.Role == LlmRole.Assistant && m.Content!.Contains("长回答")) == dialogueTurns,
    $"水位 {noPressure.EstimatedTokens}/{noPressure.Budget}");

// 超压力：预算收到 900 → 旧回复骨架化。
var pressured = SessionContextBuilder.Project(dialogueEvents, WithBudget(dialogueOpts, 900));
var skeletonCount = pressured.Messages.Count(m => m.Role == LlmRole.Assistant && m.Content!.StartsWith("[已骨架化"));
Check("★ 超水位时旧回复被骨架化", skeletonCount > 0, $"{skeletonCount} 条骨架");
Check("骨架化只落在窗口外（最近窗口的回复原样保留）",
    pressured.Messages.Count(m => m.Role == LlmRole.Assistant && m.Content!.Contains("长回答")) >= 1);
Check("★ 骨架化确实降低了水位", pressured.EstimatedTokens < noPressure.EstimatedTokens,
    $"{noPressure.EstimatedTokens} → {pressured.EstimatedTokens}");
Check("user 消息不被骨架化（意图锚点）",
    pressured.Messages.Count(m => m.Role == LlmRole.User) == dialogueTurns);

// 压缩器集成：纯对话会话以前返回 null（无事可做），现在能给出有效方案。
var dialoguePlan = ContextCompactor.Plan(dialogueEvents, WithBudget(dialogueOpts, 900));
Check("★ 压缩器在纯对话会话也能给出有效方案（v3.4 在这里束手无策）",
    dialoguePlan is not null && dialoguePlan.After.EstimatedTokens < dialoguePlan.Before.EstimatedTokens,
    dialoguePlan is null ? "null" : $"{dialoguePlan.Before.EstimatedTokens} → {dialoguePlan.After.EstimatedTokens}");

// 骨架化遵循钉子语义：压过的视图，放宽窗口后也不回跳。
var pinnedSkeleton = dialogueEvents.ToList();
pinnedSkeleton.Add(new ContextCompactedEvent
{
    Seq = ++dseq,
    SessionId = "s",
    Timestamp = DateTimeOffset.UtcNow,
    MaskedSeqs = [.. pressured.MaskedSeqs],
    TightenedTurns = 1,
});
var replayWide = SessionContextBuilder.Project(
    pinnedSkeleton,
    WithBudget(dialogueOpts, 900, 100));
var replaySkeletons = replayWide.Messages.Count(m => m.Role == LlmRole.Assistant && m.Content!.StartsWith("[已骨架化"));
Check("★ 被钉住的骨架序号即使放宽窗口也保持骨架", replaySkeletons >= skeletonCount, $"{replaySkeletons} / {skeletonCount}");

// 带 tool_calls 的 assistant 不受骨架化影响（协议配对安全）。
var mixed = BuildLongSession(8, 400);
var mixedProj = SessionContextBuilder.Project(mixed, WithBudget(dialogueOpts, 200));
var assistantCallIds2 = new HashSet<string>(mixedProj.Messages
    .Where(m => m.ToolCalls is { Count: > 0 })
    .SelectMany(m => m.ToolCalls!)
    .Select(c => c.CallId));
var toolResultIds2 = new HashSet<string>(mixedProj.Messages
    .Where(m => m.Role == LlmRole.Tool)
    .Select(m => m.ToolCallId!));
Check("骨架化后 tool_calls 配对仍完整（端点不 400）",
    assistantCallIds2.Count > 0 && assistantCallIds2.All(toolResultIds2.Contains));

// 关掉 L6 时行为与 v3.4 一致：什么都不骨架化。
Check("关掉 L6 后与旧行为一致（逐字保留）",
    SessionContextBuilder.Project(dialogueEvents, WithBudget(dialogueOpts, 900, skeletonize: false))
        .Messages.Count(m => m.Role == LlmRole.Assistant && m.Content!.StartsWith("[已骨架化")) == 0);

// ── 收尾 ───────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine($"═══ 结果：{passes} 通过 / {failures} 失败 ═══");
return failures == 0 ? 0 : 1;

List<SessionEvent> BuildDanglingSession()
{
    var events = new List<SessionEvent>();
    long seq = 0;

    void Add(SessionEvent sessionEvent)
    {
        sessionEvent.Seq = ++seq;
        sessionEvent.SessionId = "s";
        sessionEvent.Timestamp = DateTimeOffset.UtcNow;
        events.Add(sessionEvent);
    }

    Add(new UserMessageEvent { Text = "帮我看看这个文件" });

    // ← 就断在这儿：有 requested、没有 completed（进程被杀 / 崩溃）
    Add(new ToolCallRequestedEvent
    {
        CallId = "dangling-1",
        ToolName = "read_file",
        Arguments = new Dictionary<string, string?> { ["path"] = "a.txt" },
    });

    Add(new UserMessageEvent { Text = "中断之后我又来问一句" });

    Add(new ToolCallRequestedEvent
    {
        CallId = "ok-1",
        ToolName = "list_dir",
        Arguments = new Dictionary<string, string?> { ["path"] = "." },
    });
    Add(new ToolCallCompletedEvent { CallId = "ok-1", Success = true, Output = "a.txt" });
    Add(new AssistantMessageEvent { Text = "看完了。" });

    return events;
}


static ContextOptions WithBudget(ContextOptions src, int budget, int? keptTurns = null, bool? skeletonize = null)
{
    var c = src.Clone();
    c.TokenBudget = budget;
    if (keptTurns is int k) { c.RecentTurnsKeptVerbatim = k; }
    if (skeletonize is bool sk) { c.SkeletonizeOldAssistant = sk; }
    return c;
}

List<SessionEvent> BuildLongSession(int turns, int toolResultChars)
{
    var events = new List<SessionEvent>();
    long seq = 0;

    void Add(SessionEvent sessionEvent)
    {
        sessionEvent.Seq = ++seq;
        sessionEvent.SessionId = "s";
        sessionEvent.Timestamp = DateTimeOffset.UtcNow;
        events.Add(sessionEvent);
    }

    for (var i = 1; i <= turns; i++)
    {
        Add(new UserMessageEvent { Text = $"第 {i} 轮：请处理第 {i} 个文件，注意第 1 轮定下的接口不能改。" });
        Add(new AssistantMessageEvent { Text = $"好的，我来处理第 {i} 个。" });
        Add(new ToolCallRequestedEvent
        {
            CallId = $"c{i}",
            ToolName = "read_file",
            Arguments = new Dictionary<string, string?> { ["path"] = $"f{i}.txt" },
        });
        Add(new ToolCallCompletedEvent
        {
            CallId = $"c{i}",
            Success = true,
            Output = new string('x', toolResultChars) + $"第{i}轮内容",
        });
    }

    return events;
}

static int PickFreePort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

/// <summary>
/// 每轮先要一次工具调用、再给文本 —— 用来在验证里造出"长会话"的形状。
/// </summary>
internal sealed class AlternatingToolClient : ILlmClient
{
    private int _calls;

    public string Name => "main";

    public async IAsyncEnumerable<LlmStreamChunk> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var call = ++_calls;
        await Task.Yield();

        if (call % 2 == 1)
        {
            yield return new LlmStreamChunk.ToolCallsReady(
            [
                new ToolCallRequest($"call-{call}", "read_file", """{"path":"big.txt"}"""),
            ]);
        }
        else
        {
            yield return new LlmStreamChunk.TextDelta(
                "好的，已经读过了。这里是一段用来把上下文顶上去的说明文字，模拟真实长任务里的对话长度。");
        }

        yield return new LlmStreamChunk.Completed("stop");
    }
}
