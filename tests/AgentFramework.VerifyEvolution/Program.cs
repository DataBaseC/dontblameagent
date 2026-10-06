using System.Runtime.CompilerServices;
using AgentFramework.Contracts;
using AgentFramework.Data;
using AgentFramework.Host;
using AgentFramework.Llm;
using static AgentFramework.Harness.Suite;

// ═══════════════════════════════════════════════════════════════
//  记忆与技能的进化验证（PLAN-memory-evolution 支柱四）
//    Dream   —— 记忆周期整理：去重合并 / 跨项目提升 / 频率降档 / 路径核验
//    Distill —— 技能固化：从历史挖反复模式 → skills/ 纯声明式技能草稿
// ═══════════════════════════════════════════════════════════════


void Section(string name) => Console.WriteLine($"\n── {name} ──");

var root = Path.Combine(Path.GetTempPath(), "af-evolution-verify", Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(root);
Console.WriteLine($"工作区：{root}");

// ═══════════════════════════════════════════════════════════════
Console.WriteLine("\n═══ Dream（记忆周期整理）═══");

// ── 1. 合并去重 ────────────────────────────────────────────────
Section("1. 合并去重（同槽位、规范化同文 → 折叠成一条）");

var store1 = NewStore(Path.Combine(root, "dream-dedupe"));
var scope1 = "project:" + Path.Combine(root, "projA");
var e1 = await store1.AppendAsync(scope1, "构建命令是 pnpm install");
var e2 = await store1.AppendAsync(scope1, "构建命令是  PNPM  install");
var e3 = await store1.AppendAsync(scope1, "构建命令是 pnpm INSTALL");
var eOther = await store1.AppendAsync(scope1, "测试命令是 pnpm test");
var hot1 = await store1.RecordHitAsync(scope1, e1.Id, delta: 5);

var dream1 = await DreamJob.RunAsync(store1, [scope1], DateTimeOffset.Now);
Check("★ 三条同文变体合并为一条（只压重复，不碰别的）",
    dream1.MergedGroups == 1 && dream1.MergedEntries == 3,
    $"{dream1.MergedGroups} 组 / {dream1.MergedEntries} 条");
var view1 = await store1.LoadAsync(scope1, 10);
Check("合并后主视图 = 1 条合并产物 + 1 条无关事实", view1.Count == 2, $"{view1.Count} 条");
var merged1 = view1.Single(v => v.Tags.Contains("src:" + e1.Id));
Check("审计：合并产物保留全部来源 id（可回溯）",
    merged1.Tags.Contains("src:" + e1.Id)
        && merged1.Tags.Contains("src:" + e2.Id)
        && merged1.Tags.Contains("src:" + e3.Id));
Check("热度继承：合并条取来源中的最高分",
    hot1 is not null && merged1.Score >= hot1.Score, $"{merged1.Score} >= {hot1?.Score}");
Check("无关事实一字未动", view1.Any(v => v.Id == eOther.Id || v.Text.Contains("pnpm test")));

// ── 2. 幂等 ────────────────────────────────────────────────────
Section("2. 幂等（Dream 可以随便多跑）");

var dream2 = await DreamJob.RunAsync(store1, [scope1], DateTimeOffset.Now);
Check("★ 第二轮无事可做（不重复整理、不折腾）",
    dream2 is { MergedGroups: 0, Swept: 0, Promoted: 0 },
    $"{dream2.MergedGroups}/{dream2.Swept}/{dream2.Promoted}");

// ── 3. 频率降档（与「巩固」同判据）──────────────────────────────
Section("3. 频率降档（判据是使用频率，不是年龄）");

var store3 = NewStore(Path.Combine(root, "dream-sweep"));
var scope3 = "project:" + Path.Combine(root, "projB");
var cold3 = await store3.AppendAsync(scope3, "冷知识：某个一次性结论");
var hot3 = await store3.AppendAsync(scope3, "热知识：常被引用的约定");
for (var i = 0; i < 3; i++)
{
    await store3.RecordHitAsync(scope3, hot3.Id);
}

// 同样「45 天没动」：没用过的冷条目休眠，用过三次的常青
var dream3 = await DreamJob.RunAsync(store3, [scope3], DateTimeOffset.Now.AddDays(45));
Check("★ 冷条目休眠（Swept=1，进归档层）",
    dream3.Swept == 1 && (await store3.LoadArchivedAsync(scope3, 10)).Any(a => a.Id == cold3.Id),
    $"Swept={dream3.Swept}");
Check("★ 常用条目常驻（越用越新，老而常用永不掉线）",
    (await store3.LoadAsync(scope3, 10)).Any(a => a.Id == hot3.Id));
Check("休眠可恢复（归档不是删除）",
    (await store3.RestoreAsync(scope3, cold3.Id, "test")) is not null);

// ── 4. 路径有效性核验（只报告，不改动）──────────────────────────
Section("4. 路径有效性核验（梦里不擅自改记忆）");

var store4 = NewStore(Path.Combine(root, "dream-paths"));
var scope4 = "project:" + Path.Combine(root, "projC");
var ws4 = Path.Combine(root, "ws4");
Directory.CreateDirectory(Path.Combine(ws4, "notes"));
File.WriteAllText(Path.Combine(ws4, "notes", "other.md"), "x");
var e4 = await store4.AppendAsync(scope4, "输出写进 out/report.md，约定见 notes/conventions.md");

var dream4 = await DreamJob.RunAsync(store4, [scope4], DateTimeOffset.Now, workspaceRoot: ws4);
Check("★ 失效路径被点名（out/report.md 与 notes/conventions.md）",
    dream4.StalePaths.Count == 2
        && dream4.StalePaths.Any(s => s.Contains("out/report.md"))
        && dream4.StalePaths.Any(s => s.Contains("notes/conventions.md")),
    string.Join("; ", dream4.StalePaths));
Check("只报告不改动（记忆正文一字未改）",
    (await store4.LoadAsync(scope4, 10)).Single().Text == e4.Text);

Directory.CreateDirectory(Path.Combine(ws4, "out"));
File.WriteAllText(Path.Combine(ws4, "out", "report.md"), "x");
File.WriteAllText(Path.Combine(ws4, "notes", "conventions.md"), "x");
var dream4b = await DreamJob.RunAsync(store4, [scope4], DateTimeOffset.Now, workspaceRoot: ws4);
Check("文件补齐后复跑，报告为空", dream4b.StalePaths.Count == 0, string.Join("; ", dream4b.StalePaths));

// ── 5. 跨项目提升（多项目重复事实 → 全局常识）───────────────────
Section("5. 跨项目提升（session 观察 → 更高记忆层）");

var store5 = NewStore(Path.Combine(root, "dream-promote"));
var projA = "project:" + Path.Combine(root, "projX");
var projB = "project:" + Path.Combine(root, "projY");
await store5.AppendAsync(projA, "接口约定一律用 camelCase");
await store5.AppendAsync(projB, "接口约定一律用  camelCase");
await store5.AppendAsync(projA, "本项目特有：构建在 build/ 目录");

var dream5 = await DreamJob.RunAsync(store5, [MemoryScope.Global, projA, projB], DateTimeOffset.Now);
Check("★ 多项目重复的事实提升进 global", dream5.Promoted == 1
    && (await store5.LoadAsync(MemoryScope.Global, 10)).Any(e => e.Text.Contains("camelCase")));
Check("项目副本被撤销（不重复三遍）",
    !(await store5.LoadAsync(projA, 10)).Any(e => e.Text.Contains("camelCase"))
        && !(await store5.LoadAsync(projB, 10)).Any(e => e.Text.Contains("camelCase")));
Check("项目特有事实不动",
    (await store5.LoadAsync(projA, 10)).Any(e => e.Text.Contains("build/")));
Check("★ 幂等：再跑不再提升", (await DreamJob.RunAsync(store5, [MemoryScope.Global, projA, projB], DateTimeOffset.Now)).Promoted == 0);

// ═══════════════════════════════════════════════════════════════
Console.WriteLine("\n═══ Distill（技能固化）═══");

// ── 6. 挖矿 ────────────────────────────────────────────────────
Section("6. 挖矿（反复出现 + 横跨会话 = 工作方式）");

var distillSessions = new List<(string SessionId, IReadOnlyList<SessionEvent> Events)>
{
    ("s1", ToolSession("s1", ("run_command", "dotnet build"), ("run_command", "dotnet test"), ("run_command", "git status"), ("run_command", "git status"))),
    ("s2", ToolSession("s2", ("run_command", "dotnet build"), ("run_command", "dotnet test"))),
    ("s3", ToolSession("s3", ("run_command", "dotnet build"), ("run_command", "dotnet test"))),
};

var mined = DistillJob.Mine(distillSessions);
var flow = mined.Patterns.FirstOrDefault(p => p.Kind == DistillPattern.KindFlow);
Check("★ 两步工作流排第一（先 build 再 test）",
    flow is not null
        && flow.Steps is ["run_command dotnet build", "run_command dotnet test"]
        && flow.Occurrences == 3
        && flow.Sessions == 3,
    flow?.Display ?? "(null)");
Check("单步高频操作也在（build / test 各 3 次）",
    mined.Patterns.Count(p => p.Kind == DistillPattern.KindTask && p.Occurrences == 3) == 2);
Check("低频巧合不入选（git status 只在 1 个会话出现）",
    mined.Patterns.All(p => !p.Display.Contains("git status")));
Check("阈值双判据：出现 2 次但只在 1 个会话 ≠ 工作方式",
    mined.Patterns.All(p => p.Sessions >= 2 && p.Occurrences >= 3));
Check("确定性：同输入两次挖矿逐条一致",
    string.Join("|", DistillJob.Mine(distillSessions).Patterns.Select(p => p.Display))
        == string.Join("|", mined.Patterns.Select(p => p.Display)));

// ── 7. 固化为可装载技能 ────────────────────────────────────────
Section("7. 固化（skills/ 纯声明式草稿，SkillLoader 立即可装载）");

var ws7 = Path.Combine(root, "ws7");
Directory.CreateDirectory(ws7);
var run7 = DistillJob.Run(ws7, distillSessions);
var flowId = flow is null ? null : DistillJob.SkillIdOf(flow);
Check("★ 技能落盘（skill.json + README.md）",
    run7.SkillsWritten.Count == 3
        && flowId is not null
        && File.Exists(Path.Combine(ws7, "skills", flowId, "skill.json"))
        && File.Exists(Path.Combine(ws7, "skills", flowId, "README.md")),
    string.Join(", ", run7.SkillsWritten));
Check("★ SkillLoader 立即可装载（round-trip）",
    flowId is not null && SkillLoader.ScanWorkspace(ws7).Any(s => s.Id == flowId));
var loaded7 = SkillLoader.ScanWorkspace(ws7).Single(s => s.Id == flowId);
Check("工具白名单正确（工作流只用 run_command）",
    loaded7.Tools is ["run_command"], string.Join(",", loaded7.Tools));
Check("提示词后缀含步骤与提炼依据",
    loaded7.PromptSuffix?.Contains("dotnet build") == true
        && loaded7.PromptSuffix.Contains("提炼依据")
        && loaded7.PromptSuffix.Contains("3 个会话"));
Check("id 合法（技能白名单字符）", SkillValidator.IsValidName(flowId));

// ── 8. 不覆盖已有技能 ──────────────────────────────────────────
Section("8. 不覆盖已有技能（自动作业永不顶掉人工修订）");

var manifestPath = Path.Combine(ws7, "skills", flowId!, "skill.json");
var original = File.ReadAllText(manifestPath);
File.WriteAllText(manifestPath, original.Replace("dotnet build", "dotnet build SENTINEL"));
var run8 = DistillJob.Run(ws7, distillSessions);
Check("★ 已有技能被跳过（不写、不覆盖）",
    run8.SkillsWritten.Count == 0 && run8.Notes.Any(n => n.Contains("跳过")),
    string.Join("; ", run8.Notes));
Check("人工修订原样保留（SENTINEL 还在）",
    File.ReadAllText(manifestPath).Contains("SENTINEL"));

// ── 9. 触发（累计回合数驱动，不是挂钟）─────────────────────────
Section("9. 触发（累计回合数驱动 Dream）");

var evoRoot = Path.Combine(root, "trigger");
var ws9 = Path.Combine(evoRoot, "ws");
Directory.CreateDirectory(ws9);
var store9 = NewStore(Path.Combine(evoRoot, "memory"));
var scope9 = MemoryScope.ProjectFor(ws9);
await store9.AppendAsync(scope9, "发布前要跑 smoke 测试");
await store9.AppendAsync(scope9, "发布前要跑  smoke  测试");

await using var host9 = await AgentHost.CreateAsync(new HostOptions
{
    WorkspaceRoot = ws9,
    SessionsDir = Path.Combine(evoRoot, "sessions"),
    SessionId = "evo",
    LlmOverride = new TextClient(),
    ApprovalPolicy = _ => ApprovalDecision.Allow,
    MemoryStoreOverride = store9,
    Evolution = new EvolutionOptions { DreamEveryTurns = 2, DistillEveryTurns = 0 },
});

await host9.SendAsync("第一步");
Check("不到周期不动（第 1 轮只计数）",
    host9.EvolutionTurns == 1 && (await store9.LoadAsync(scope9, 10)).Count == 2,
    $"{host9.EvolutionTurns} 轮");

await host9.SendAsync("第二步");
Check("★ 到周期自动整理（第 2 轮触发 Dream，重复记忆被合并）",
    host9.EvolutionTurns == 2 && await WaitUntil(async () => (await store9.LoadAsync(scope9, 10)).Count == 1),
    $"{host9.EvolutionTurns} 轮 / 主视图 {((await store9.LoadAsync(scope9, 10)).Count)} 条");

Console.WriteLine($"\n═══ 结果：{passes} 通过 / {failures} 失败 ═══");
return failures == 0 ? 0 : 1;

// ═══════════════════════════════════════════════════════════════
// 本地工具
// ═══════════════════════════════════════════════════════════════

static JsonlMemoryStore NewStore(string dir)
{
    Directory.CreateDirectory(dir);
    return new JsonlMemoryStore(new MemoryStoreOptions
    {
        GlobalPath = Path.Combine(dir, "global.jsonl"),
        ProjectPath = Path.Combine(dir, "project.jsonl"),
    });
}

static List<SessionEvent> ToolSession(string sessionId, params (string Tool, string Command)[] calls)
{
    var events = new List<SessionEvent>();
    long seq = 0;

    foreach (var (tool, command) in calls)
    {
        events.Add(new ToolCallRequestedEvent
        {
            Seq = ++seq,
            SessionId = sessionId,
            Timestamp = DateTimeOffset.UtcNow,
            CallId = $"c{seq}",
            ToolName = tool,
            Arguments = new Dictionary<string, string?> { ["command"] = command },
        });
    }

    return events;
}

static async Task<bool> WaitUntil(Func<Task<bool>> condition)
{
    for (var i = 0; i < 60; i++)
    {
        if (await condition())
        {
            return true;
        }

        await Task.Delay(50);
    }

    return false;
}

/// <summary>一步回固定文本的假模型 —— 给触发一节跑回合用。</summary>
internal sealed class TextClient : ILlmClient
{
    public string Name => "text";

    public async IAsyncEnumerable<LlmStreamChunk> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        yield return new LlmStreamChunk.TextDelta("好的，继续。");
        yield return new LlmStreamChunk.Completed("stop");
    }
}
