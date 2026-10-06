using System.Runtime.CompilerServices;
using AgentFramework.Contracts;
using AgentFramework.Tools;
using AgentFramework.Data;
using AgentFramework.Host;
using AgentFramework.Host.Hosting;
using static AgentFramework.Harness.Suite;

// ═══════════════════════════════════════════════════════════
//  宿主装配垂直切片验证
//  装配 → 对话 → 重启续接 → 审批 → 插件并入
// ═══════════════════════════════════════════════════════════


var root = Path.Combine(Path.GetTempPath(), "af-host-verify", Guid.NewGuid().ToString("N")[..8]);
var workspace = Path.Combine(root, "workspace");
var sessions = Path.Combine(root, "sessions");
Directory.CreateDirectory(root);

Console.WriteLine("═══ 宿主装配垂直切片验证 ═══");
Console.WriteLine($"工作目录：{root}");

var recording = new RecordingLlmClient { Reply = "好的，我记住了。" };

var baseOptions = new HostOptions
{
    WorkspaceRoot = workspace,
    SessionsDir = sessions,
    SessionId = "default",
    LlmOverride = recording,
};

// ── 1. 装配 ────────────────────────────────────────────────
Console.WriteLine("\n── 1. 装配 ──");
var host1 = await AgentHost.CreateAsync(baseOptions);

Check("装配成功", host1.ToolNames.Count >= 6, $"{host1.ToolNames.Count} 个工具");
Check("六个官方工具齐全",
    new[] { "read_file", "write_file", "list_dir", "run_command", "web_search", "web_fetch" }
        .All(host1.ToolNames.Contains),
    string.Join(",", host1.ToolNames));
Check("使用注入的模型客户端（非离线演示）", !host1.UsingOfflineDemo);
Check("会话日志文件已创建", File.Exists(host1.SessionLogPath), Path.GetFileName(host1.SessionLogPath));
Check("工作区目录已创建", Directory.Exists(workspace));

// ── 2. 第一轮对话 ──────────────────────────────────────────
Console.WriteLine("\n── 2. 第一轮对话 ──");
var first = await host1.SendAsync("你好，我叫 DoBoC");
Check("第一轮完成", first.Completed);
Check("模型收到了系统提示", recording.Requests[0].SystemPrompt?.Contains("桌面助手") == true);
Check("模型收到了工具 schema", recording.Requests[0].Tools.Any(t => t.Name == "write_file"));

var state1 = host1.CurrentState();
Check("事件已落盘（user + assistant）", state1.Messages.Count == 2, $"{state1.Messages.Count} 条消息");
Check("事件数正确（user + 用量 + assistant）", state1.EventCount == 3, $"{state1.EventCount}");

// ── 3. 重启续接（本轮核心）─────────────────────────────────
Console.WriteLine("\n── 3. 重启续接 ──");
await host1.DisposeAsync();  // 模拟进程退出

var host2 = await AgentHost.CreateAsync(baseOptions);  // 完全重新装配，同一个 sessionId
var restored = host2.RebuildContext();

Check("重启后从事件流恢复了上下文", restored.Count == 2, $"{restored.Count} 条");
Check("恢复内容正确",
    restored[0].Role == LlmRole.User && restored[0].Content == "你好，我叫 DoBoC",
    restored[0].Content ?? "(null)");

var second = await host2.SendAsync("我叫什么？");
Check("第二轮完成", second.Completed);

var lastRequest = recording.Requests[^1];
Check("★ 新请求带上了重启前的历史（真正的续接）",
    lastRequest.Messages.Count >= 3
    && lastRequest.Messages.Any(m => m.Content == "你好，我叫 DoBoC"),
    $"请求中共 {lastRequest.Messages.Count} 条消息");

Check("恢复的事件流继续追加（幂等，不重写历史）",
    host2.CurrentState().Messages.Count == 4,
    $"{host2.CurrentState().Messages.Count} 条消息");

// ── 4. 审批策略 ────────────────────────────────────────────
Console.WriteLine("\n── 4. 审批策略（危险动作默认拒绝）──");
var commandClient = new CommandScriptClient();
var host3 = await AgentHost.CreateAsync(new HostOptions
{
    WorkspaceRoot = workspace,
    SessionsDir = sessions,
    SessionId = "cmd",
    LlmOverride = commandClient,
});

await host3.SendAsync("帮我跑个命令");

Check("默认策略拒绝了 run_command",
    host3.Approvals.Any(a => a.ToolName == "run_command" && a.Denied),
    string.Join("; ", host3.Approvals.Select(a => $"{a.ToolName}={(a.Denied ? "拒绝" : "放行")}")));

var cleared = new ToolCallCompletedEvent?[1];
var cmdState = host3.CurrentState();
Check("被拒的工具没有真正执行",
    cmdState.ToolCalls.Count == 1 && cmdState.ToolCalls[0].Success == false,
    $"成功={cmdState.ToolCalls.FirstOrDefault()?.Success}");

Check("主循环在拒绝后仍能收尾", commandClient.Requests.Count >= 2);

await host3.DisposeAsync();

// ── 4.5 审批档位矩阵（任务 5）───────────────────────────────
Console.WriteLine("\n── 4.5 审批档位矩阵 ──");

static ToolPreExecuteEvent Call(string tool, params (string Key, string Value)[] args)
    => CallRisk(tool, ToolRisk.Execute, args);

/// <summary>
/// 带风险等级的事件（v3.23）：审批按**工具自报的风险**判定，所以测试构造事件时必须一并给上 ——
/// 不给就是最保守的执行档（这本身也是一条要钉住的行为）。
/// </summary>
static ToolPreExecuteEvent CallRisk(string tool, ToolRisk risk, params (string Key, string Value)[] args)
    => new()
    {
        ToolName = tool,
        Arguments = args.ToDictionary(a => a.Key, a => (string?)a.Value),
        Risk = risk,
    };

ApprovalDecision Decide(ApprovalTier tier, ToolPreExecuteEvent e)
    => ApprovalTiers.Decide(tier, e, workspace);

// Ask：写/执行都问，读放行
Check("Ask 档：write_file 仍问", Decide(ApprovalTier.Ask, CallRisk("write_file", ToolRisk.Write, ("path", "a.txt"))) == ApprovalDecision.Ask);
Check("Ask 档：read_file 放行", Decide(ApprovalTier.Ask, CallRisk("read_file", ToolRisk.ReadOnly, ("path", "a.txt"))) == ApprovalDecision.Allow);

// Build：区内写放行、区外写问；安全命令放行、危险命令问
Check("★ Build 档：工作区内写自动放行",
    Decide(ApprovalTier.Build, CallRisk("write_file", ToolRisk.Write, ("path", "sub/a.txt"))) == ApprovalDecision.Allow);
Check("★ Build 档：工作区外写仍问",
    Decide(ApprovalTier.Build, CallRisk("write_file", ToolRisk.Write, ("path", "../../evil.txt"))) == ApprovalDecision.Ask);
Check("★ Build 档：普通命令放行",
    Decide(ApprovalTier.Build, Call("run_command", ("command", "git status"))) == ApprovalDecision.Allow);
Check("★ Build 档：危险命令仍问",
    Decide(ApprovalTier.Build, Call("run_command", ("command", "rm -rf /"))) == ApprovalDecision.Ask);

// Plan：逐项判定同 Ask（批量由 HostEventSink 的回合放行集处理）
Check("Plan 档：write_file 逐项仍问", Decide(ApprovalTier.Plan, CallRisk("write_file", ToolRisk.Write, ("path", "a.txt"))) == ApprovalDecision.Ask);

// Yolo：全放行，但审计事件链不变（事件由主循环保证，这里只验策略）
Check("★ Yolo 档：写与执行全部放行",
    Decide(ApprovalTier.Yolo, CallRisk("write_file", ToolRisk.Write, ("path", "../../evil.txt"))) == ApprovalDecision.Allow
    && Decide(ApprovalTier.Yolo, Call("run_command", ("command", "rm -rf /"))) == ApprovalDecision.Allow);

// 未知工具在 Build 档保持谨慎
Check("Build 档：未知工具仍问", Decide(ApprovalTier.Build, Call("mystery_tool")) == ApprovalDecision.Ask);

// ── 5. 插件并入 ────────────────────────────────────────────
Console.WriteLine("\n── 5. 插件并入 ──");
var pluginsDir = Path.Combine(AppContext.BaseDirectory, "plugins");
if (Directory.Exists(Path.Combine(pluginsDir, "hello")))
{
    var host4 = await AgentHost.CreateAsync(new HostOptions
    {
        WorkspaceRoot = workspace,
        SessionsDir = sessions,
        SessionId = "plugin",
        PluginsDir = pluginsDir,
        LlmOverride = recording,
    });

    Check("插件被自动加载", host4.LoadedPlugins.Count == 1, $"{host4.LoadedPlugins.Count} 个");
    Check("插件贡献的工具并入了主循环", host4.ToolNames.Contains("hello"), string.Join(",", host4.ToolNames));
    Check("官方工具与插件工具共存", host4.ToolNames.Contains("write_file") && host4.ToolNames.Contains("hello"));

    await host4.DisposeAsync();
}
else
{
    Check("插件目录存在（供加载）", false, $"未找到 {pluginsDir}；构建脚本未复制插件");
}

await host2.DisposeAsync();

// ── 6. 配置单 ──────────────────────────────────────────────
Console.WriteLine("\n── 6. 配置单（agent.json）──");

var configPath = Path.Combine(root, "agent.json");
await File.WriteAllTextAsync(configPath, """
    {
      // 配置单允许注释与尾逗号 —— 它是给人读的
      "cloud": { "baseUrl": "https://example.invalid/v1", "model": "some-model" },
      "webPort": 9001,
    }
    """);

var loadedConfig = AgentConfig.TryLoad(configPath);
Check("★ 配置单能读（含注释与尾逗号）",
    loadedConfig?.Cloud?.BaseUrl == "https://example.invalid/v1" && loadedConfig.WebPort == 9001);
Check("没写配置单时返回 null（走默认值）",
    AgentConfig.TryLoad(Path.Combine(root, "not-there.json")) is null);

await File.WriteAllTextAsync(configPath, "{ 这不是合法 json");
Check("★ 坏配置单不阻断启动（提示一声后按默认继续）", AgentConfig.TryLoad(configPath) is null);

// 生成物必须是严格 JSON（无 //、无尾逗号），并内置免费端点
var sample = AgentConfig.Sample();
var sampleOk = false;
try
{
    using var doc = System.Text.Json.JsonDocument.Parse(sample);
    sampleOk = doc.RootElement.TryGetProperty("cloud", out var cloud)
        && cloud.GetProperty("baseUrl").GetString()?.Contains("developer.amd.com.cn") == true
        && cloud.GetProperty("model").GetString() == "MiniCPM5-2B";
}
catch (System.Text.Json.JsonException)
{
    sampleOk = false;
}
Check("★ Sample() 是严格 JSON 且内置免费端点（AMD MiniCPM5-2B）", sampleOk);

// ── 7. 架构地基（模块 / 工具注册表 / 交互缝 / 会话派生）────
Console.WriteLine("\n── 7. 架构地基 ──");

var host5 = await AgentHost.CreateAsync(new HostOptions
{
    WorkspaceRoot = workspace,
    SessionsDir = sessions,
    SessionId = "infra",
    LlmOverride = recording,
});

Check("工具来源可追溯（谁挂的一眼看得出）",
    host5.ToolSources.TryGetValue("read_file", out var readSrc) && readSrc.Contains("official"),
    host5.ToolSources.GetValueOrDefault("read_file"));

// 注册表是活的：运行期挂上来的工具，下一轮就可见（技能 / 子 agent 靠这条）
var lateScope = host5.Plugins.CreateKernelScope("verify-late");
lateScope.RegisterTool(new NamedTool("late_tool"));
Check("运行期挂上的工具立刻可见", host5.ToolNames.Contains("late_tool"));
lateScope.Dispose();
Check("撤销后立刻消失（不需要重装主循环）", !host5.ToolNames.Contains("late_tool"));

// 交互缝：模型反问用户
host5.UserInteraction = new StubUserInteraction();
var asked = await host5.Plugins.InvokeToolAsync(
    "ask_user",
    new Dictionary<string, string?> { ["question"] = "用哪个方案？" });
Check("ask_user 经交互缝问到用户",
    asked.Success && asked.Output == "方案甲",
    asked.Success ? asked.Output : asked.Error);

host5.UserInteraction = null;
var askedNoUi = await host5.Plugins.InvokeToolAsync(
    "ask_user",
    new Dictionary<string, string?> { ["question"] = "在吗" });
Check("没有界面时如实回「问不出去」（绝不假装用户答过）",
    !askedNoUi.Success && (askedNoUi.Error?.Contains("问不出去") ?? false),
    askedNoUi.Error);

// 只懂审批的界面（能弹「允许/拒绝」，却答不了「用哪个方案」）：
// 必须如实说「问不出去」，而不是冒充能提问、糊成一句误导的「用户没有回答」。
host5.UserInteraction = null;
host5.ApprovalPrompt = new StubApprovalPrompt();
var askedApprovalOnly = await host5.Plugins.InvokeToolAsync(
    "ask_user",
    new Dictionary<string, string?> { ["question"] = "选哪个" });
Check("★ 只有审批界面时，ask_user 如实「问不出去」（不冒充「用户没有回答」）",
    !askedApprovalOnly.Success && (askedApprovalOnly.Error?.Contains("问不出去") ?? false),
    askedApprovalOnly.Error);
host5.ApprovalPrompt = null;

// 等待超时：文案必须与「用户主动跳过」分开（失败可诊断）。
host5.UserInteraction = new TimeoutUserInteraction();
var askedTimeout = await host5.Plugins.InvokeToolAsync(
    "ask_user",
    new Dictionary<string, string?> { ["question"] = "在吗" });
Check("★ 等待超时的文案可诊断（含「超时」，不混同「用户没有回答」）",
    !askedTimeout.Success && (askedTimeout.Error?.Contains("超时") ?? false),
    askedTimeout.Error);
host5.UserInteraction = null;

// ── 7b. 审批按「工具自报的风险」判（v3.23）──────────────────
Console.WriteLine("\n── 7b. 工具自报风险 ──");

// 默认档是 Ask；这里没有界面 → Ask 即拒绝，于是「放行 / 拦住」就是风险等级的直接读数。
host5.UserInteraction = null;
host5.ApprovalPrompt = null;

var riskScope = host5.Plugins.CreateKernelScope("verify-risk");
riskScope.RegisterTool(new RiskProbeTool("probe_readonly", ToolRisk.ReadOnly));
riskScope.RegisterTool(new RiskProbeTool("probe_exec", ToolRisk.Execute));
riskScope.RegisterTool(new NamedTool("probe_undeclared"));   // 老工具：不实现 IToolWithRisk

var probeReadOnly = await host5.Plugins.InvokeToolAsync("probe_readonly");
Check("★ 只读工具在 Ask 档放行（判据是它自己说的风险，不是一张名字清单）",
    probeReadOnly.Success, probeReadOnly.Error);

var probeExec = await host5.Plugins.InvokeToolAsync("probe_exec");
Check("★ 执行类工具在 Ask 档被拦（无界面 → 拒绝）",
    !probeExec.Success, probeExec.Error);

var probeUndeclared = await host5.Plugins.InvokeToolAsync("probe_undeclared");
Check("★ 未声明风险 → 按最保守的执行档（同样被拦）",
    !probeUndeclared.Success, probeUndeclared.Error);

riskScope.Dispose();

// 会话派生：子 agent 的地基（共享模型 / 工具 / 索引 / 记忆，独立日志与上下文）
var sub = host5.OpenSession("sub-1", systemPrompt: "你是子 agent");
Check("能开独立会话", sub.SessionId == "sub-1" && File.Exists(sub.LogPath), Path.GetFileName(sub.LogPath));
Check("子会话与主会话各有自己的日志文件", sub.LogPath != host5.SessionLogPath);

var subRun = await sub.Runner.RunAsync("子任务：回一句");
Check("子会话能独立跑一轮", subRun.Completed);

var subEvents = sub.Log.ReadAll().Count();
Check("子会话的事件进的是自己的流", subEvents >= 2, $"{subEvents} 条");

Check("子会话内容没串进主会话日志",
    !File.Exists(host5.SessionLogPath) || !JsonlEventLog.ReadRawText(host5.SessionLogPath).Contains("子任务"));

await sub.DisposeAsync();

// 加功能 = 加模块：自定义模块按自己的序号插进装配，不需要改核心
var host6 = await HostBuilder.BuildAsync(
    new HostOptions
    {
        WorkspaceRoot = workspace,
        SessionsDir = sessions,
        SessionId = "modular",
        LlmOverride = recording,
    },
    [new ProbeModule()]);

Check("自定义模块能插进装配（加功能不必改核心）",
    host6.ToolNames.Contains("module_tool"),
    string.Join(",", host6.ToolNames));
Check("内置模块照常工作",
    host6.ToolNames.Contains("read_file") && host6.ToolNames.Contains("ask_user"));

await host6.DisposeAsync();
await host5.DisposeAsync();

// ── 8. 回合中断自愈（P0-2）────────────────────────────────
Console.WriteLine("\n── 8. 回合中断自愈 ──");

const string repairSession = "repair";
var repairLogPath = Path.Combine(sessions, repairSession + ".jsonl");

// 手工造一份「有意图、无结果」的日志 —— 模拟上次进程被杀在中途
using (var brokenLog = JsonlEventLog.Open(repairLogPath))
{
    brokenLog.Append(new UserMessageEvent { SessionId = repairSession, Text = "读一下 a.txt" });
    brokenLog.Append(new ToolCallRequestedEvent
    {
        SessionId = repairSession,
        CallId = "lost-1",
        ToolName = "read_file",
        Arguments = new Dictionary<string, string?> { ["path"] = "a.txt" },
    });
}

var host7 = await AgentHost.CreateAsync(new HostOptions
{
    WorkspaceRoot = workspace,
    SessionsDir = sessions,
    SessionId = repairSession,
    LlmOverride = recording,
});

await host7.SendAsync("在吗");

var repaired = host7.Events().OfType<ToolCallCompletedEvent>().ToList();
Check("★ 悬空调用被补写了合成结果（回合中断）",
    repaired.Any(c => c.CallId == "lost-1" && !c.Success && (c.Error?.Contains("回合中断") ?? false)),
    string.Join("; ", repaired.Select(c => $"{c.CallId}={(c.Success ? "成功" : "失败")}")));

Check("补状态靠追加，历史没被改写",
    host7.Events().OfType<ToolCallRequestedEvent>().Count() == 1);

// 加固 E：删会话要能连带清掉它的索引（否则删了再建同名会话会吃到旧数据）
if (host7.Index.IsAvailable)
{
    await host7.Index.IndexAsync("victim", new UserMessageEvent { SessionId = "victim", Text = "会被删掉的会话" });
    var indexedBefore = await host7.Index.CountAsync("victim");

    await host7.Index.RemoveSessionAsync("victim");
    var indexedAfter = await host7.Index.CountAsync("victim");

    Check("★ 删会话能清掉它的索引（加固 E）",
        indexedBefore > 0 && indexedAfter == 0,
        $"{indexedBefore} → {indexedAfter}");
}

await host7.DisposeAsync();

// ── 9. 退休标志（切会话竞态防线）────────────────────────────
Console.WriteLine("\n── 9. 退休标志（切会话竞态防线）──");

var hostRetire = await AgentHost.CreateAsync(baseOptions.CloneWith("retire"));

Check("新宿主默认未退休", !hostRetire.IsRetired);

// 精确复现竞态窗口的时序：
//   切换者先拿到空闲的闸 → 一个 /api/send 起来排队 → 标记退休 → 还闸
//   → 排队者必须快速失败，而不是开始往「即将被 Dispose 的日志」里落事件
var gateHeld = await hostRetire.WaitIdleAsync(TimeSpan.FromSeconds(5));
Check("切换者拿到空闲的闸", gateHeld);

var queued = hostRetire.SendAsync("窗口期里赶到的消息");   // 闸被占着 → 只能排队
await Task.Delay(50);                                     // 让它真正排上
var eventsBefore = hostRetire.Events().Count;

hostRetire.MarkRetired();                                 // 与 SwitchSessionAsync 同序
Check("标记后 IsRetired 为真", hostRetire.IsRetired);

hostRetire.ReleaseTurn();                                 // 排队者在此刻醒来

Exception? queuedError = null;
try { await queued; } catch (Exception ex) { queuedError = ex; }

Check("★ 窗口期排队的回合被拒（InvalidOperationException）",
    queuedError is InvalidOperationException,
    queuedError?.GetType().Name ?? "竟然没抛异常");
Check("★ 被拒的回合一个事件都没落",
    hostRetire.Events().Count == eventsBefore,
    $"{eventsBefore} → {hostRetire.Events().Count}");
Check("闸已归还（宿主不会锁死）", !hostRetire.IsBusy);

Exception? directError = null;
try { await hostRetire.SendAsync("退休之后直接发一条"); } catch (Exception ex) { directError = ex; }
Check("退休宿主直接发也被拒",
    directError is InvalidOperationException,
    directError?.GetType().Name ?? "竟然没抛异常");
Check("被拒之后仍未落事件", hostRetire.Events().Count == eventsBefore);

hostRetire.MarkRetired();
Check("重复标记退休是幂等的", hostRetire.IsRetired);

await hostRetire.DisposeAsync();

// ── G1/G3：子 Agent 编排 + 计划投影 ────────────────────────
Console.WriteLine("\n── G1/G3 子 Agent 与计划 ──");
var g3Events = new List<SessionEvent>
{
    new PlanCreatedEvent
    {
        Seq = 1, PlanId = "p1",
        Steps = ["拆解需求", "写方案", "验收"],
    },
    new PlanStepUpdatedEvent { Seq = 2, PlanId = "p1", Index = 0, Status = "done" },
};
var g3State = SessionProjector.Project(g3Events);
Check("G3 计划投影：步骤数", g3State.Plans["p1"].Count == 3);
Check("G3 计划投影：步骤状态", g3State.Plans["p1"][0].Status == "done" && g3State.Plans["p1"][1].Status == "pending");
var g3Card = TaskCardBuilder.Build(g3State);
Check("G3 任务卡显示计划标记", g3Card.Contains("[1✓]") && g3Card.Contains("[2 ]"), g3Card.Split('\n')[^2]);

Check("G3 ParseSteps：JSON 数组",
    AgentFramework.Tools.UpdatePlanTool.ParseSteps("[\"a\",\"b\"]") is ["a", "b"]);
Check("G3 ParseSteps：换行分隔带序号",
    AgentFramework.Tools.UpdatePlanTool.ParseSteps("- 1. 读文件\n2. 写总结") is ["读文件", "写总结"]);

// ── G4. 计划模式（MiMo plan 档）：只读门 + 计划文件重注入 ─────
Console.WriteLine("\n── G4. 计划模式（只读门 + 计划文件重注入）──");

var alwaysAllow = (ToolPreExecuteEvent _) => ApprovalDecision.Allow;
var rootDir = Path.Combine(Path.GetTempPath(), "af-plan-verify", Guid.NewGuid().ToString("N")[..8]);

Check("计划模式：写源码文件被拒",
    PlanModePolicy.Decide("plan", CallRisk("write_file", ToolRisk.Write, ("path", "src/a.txt")), alwaysAllow, rootDir) == ApprovalDecision.Deny);
var denied = CallRisk("write_file", ToolRisk.Write, ("path", "src/a.txt"));
PlanModePolicy.Decide("plan", denied, alwaysAllow, rootDir);
Check("拒绝理由写明出路（切模式）", denied.RejectReason?.Contains("计划模式") == true && denied.RejectReason.Contains("切换"), denied.RejectReason);
Check("计划模式：唯一可写 plans/plan.md",
    PlanModePolicy.Decide("plan", CallRisk("write_file", ToolRisk.Write, ("path", "plans/plan.md")), alwaysAllow, rootDir) == ApprovalDecision.Allow);
Check("计划模式：edit_file 计划文件也放行",
    PlanModePolicy.Decide("plan", CallRisk("edit_file", ToolRisk.Write, ("path", "plans/x.md")), alwaysAllow, rootDir) == ApprovalDecision.Allow);
Check("计划模式：plans/.. 逃逸被拒",
    PlanModePolicy.Decide("plan", CallRisk("write_file", ToolRisk.Write, ("path", "plans/../evil.txt")), alwaysAllow, rootDir) == ApprovalDecision.Deny);
Check("计划模式：执行命令被拒",
    PlanModePolicy.Decide("plan", Call("run_command", ("command", "echo hi")), alwaysAllow, rootDir) == ApprovalDecision.Deny);
Check("计划模式：删除被拒",
    PlanModePolicy.Decide("plan", CallRisk("delete_path", ToolRisk.Destructive, ("path", "a.txt")), alwaysAllow, rootDir) == ApprovalDecision.Deny);
Check("计划模式：只读工具照常放行",
    PlanModePolicy.Decide("plan", CallRisk("grep_files", ToolRisk.ReadOnly, ("pattern", "x")), alwaysAllow, rootDir) == ApprovalDecision.Allow
    && PlanModePolicy.Decide("plan", CallRisk("read_file", ToolRisk.ReadOnly, ("path", "a.txt")), alwaysAllow, rootDir) == ApprovalDecision.Allow);
Check("计划模式：update_plan / ask_user 放行",
    PlanModePolicy.Decide("plan", CallRisk("update_plan", ToolRisk.Write, ("steps", "[\"a\"]")), alwaysAllow, rootDir) == ApprovalDecision.Allow
    && PlanModePolicy.Decide("plan", CallRisk("ask_user", ToolRisk.ReadOnly, ("question", "q")), alwaysAllow, rootDir) == ApprovalDecision.Allow);
Check("非计划模式零影响（原策略透传）",
    PlanModePolicy.Decide("work", CallRisk("write_file", ToolRisk.Write, ("path", "src/a.txt")), alwaysAllow, rootDir) == ApprovalDecision.Allow);

// 端到端：真实宿主 + 会话切 plan 档 —— 权限层真的把门焊死
var planWs = Path.Combine(rootDir, "ws");
var planFake = new QueuedScriptClient(
    ("write_file", """{"path":"src/x.txt","content":"x"}"""),
    ("write_file", """{"path":"plans/plan.md","content":"# 计划内容测试\n- 步骤一"}"""),
    ("run_command", """{"command":"echo hi"}"""),
    ("write_file", """{"path":"src/y.txt","content":"y"}"""));
var planHost = await AgentHost.CreateAsync(new HostOptions
{
    WorkspaceRoot = planWs,
    SessionsDir = Path.Combine(rootDir, "sessions"),
    SessionId = "plan-e2e",
    LlmOverride = planFake,
    ApprovalPolicy = _ => ApprovalDecision.Allow,
});

planHost.SetCurrentMode("plan");

await planHost.SendAsync("想改代码");
var denyDone = planHost.Events().OfType<ToolCallCompletedEvent>().LastOrDefault();
Check("★ 端到端：plan 档写源码被拒（工具卡如实失败）",
    denyDone is { Success: false } && denyDone.Error?.Contains("计划模式") == true, denyDone?.Error);

await planHost.SendAsync("写计划");
var planDone = planHost.Events().OfType<ToolCallCompletedEvent>().LastOrDefault();
Check("★ 端到端：plan 档写 plans/plan.md 成功", planDone is { Success: true }, planDone?.Error);
Check("计划文件真的落盘", File.Exists(Path.Combine(planWs, "plans", "plan.md")));

await planHost.SendAsync("跑个命令");
var cmdDone = planHost.Events().OfType<ToolCallCompletedEvent>().LastOrDefault();
Check("★ 端到端：plan 档执行命令被拒", cmdDone is { Success: false } && cmdDone.Error?.Contains("计划模式") == true, cmdDone?.Error);

// 切回工作模式：门即刻放行；且计划文件在下一轮被重新注入（压缩冲不掉）
planHost.SetCurrentMode("work");
await planHost.SendAsync("现在动手");
var workDone = planHost.Events().OfType<ToolCallCompletedEvent>().LastOrDefault();
Check("★ 切回工作模式后写文件放行（门零影响）", workDone is { Success: true }, workDone?.Error);
var planLastRequest = planFake.Requests[^1];
Check("★ 计划文件每轮重注入（模型看得见计划）",
    planLastRequest.Messages.Any(m => (m.Content ?? "").Contains("【计划文件") && (m.Content ?? "").Contains("计划内容测试")));

// ── G5. 权限输入级匹配 + external_directory（MiMo permission rules）──
Console.WriteLine("\n── G5. 权限输入级匹配 + external_directory ──");

var alwaysAllow5 = (ToolPreExecuteEvent _) => ApprovalDecision.Allow;
var alwaysAsk5 = (ToolPreExecuteEvent _) => ApprovalDecision.Ask;
var rootDir5 = Path.Combine(Path.GetTempPath(), "af-rules-verify", Guid.NewGuid().ToString("N")[..8]);

// 签名与通配
Check("签名：run_command 拼命令",
    ApprovalRuleSet.Signature(Call("run_command", ("command", "git status"))) == "run_command git status");
Check("签名：write_file 拼路径（内容噪音不进签名）",
    ApprovalRuleSet.Signature(CallRisk("write_file", ToolRisk.Write, ("path", "docs/a.md"), ("content", "很长的内容"))) == "write_file docs/a.md");
Check("通配：* 跨词匹配、? 单字符、大小写不敏感",
    ApprovalRuleSet.IsMatch("run_command git *", "run_command git status")
        && ApprovalRuleSet.IsMatch("edit_file *.mdx", "edit_file docs/a.mdx")
        && ApprovalRuleSet.IsMatch("RUN_COMMAND GIT *", "run_command git push origin main"));

// 输入级规则：命中即定，last-match-wins
var gitAllow = new List<ApprovalRule> { new("run_command git *", ApprovalDecision.Allow) };
Check("★ 规则放行盖过档位询问（git * = allow）",
    ApprovalPolicyChain.Decide("work", Call("run_command", ("command", "git status")), DefaultApprovalPolicy.Decide, rootDir5, gitAllow) == ApprovalDecision.Allow);
Check("规则未命中仍走档位（run_command 默认询问）",
    ApprovalPolicyChain.Decide("work", Call("run_command", ("command", "npm test")), DefaultApprovalPolicy.Decide, rootDir5, gitAllow) == ApprovalDecision.Ask);

var denyRm = new List<ApprovalRule>
{
    new("run_command *", ApprovalDecision.Allow),
    new("run_command rm *", ApprovalDecision.Deny),
};
var rmCall = Call("run_command", ("command", "rm -rf build"));
Check("★ 规则拒绝盖过档位放行（后写覆盖先写）",
    ApprovalPolicyChain.Decide("work", rmCall, alwaysAllow5, rootDir5, denyRm) == ApprovalDecision.Deny);
Check("规则拒绝理由写明命中的模式", rmCall.RejectReason?.Contains("run_command rm *") == true, rmCall.RejectReason);

Check("顺序反过来就是放行（last-match-wins，顺序即语义）",
    ApprovalPolicyChain.Decide("work", Call("run_command", ("command", "rm -rf build")), alwaysAsk5, rootDir5,
        [new ApprovalRule("run_command rm *", ApprovalDecision.Deny), new ApprovalRule("run_command *", ApprovalDecision.Allow)]) == ApprovalDecision.Allow);
Check("规则可以收紧到询问（allow → ask）",
    ApprovalPolicyChain.Decide("work", CallRisk("write_file", ToolRisk.Write, ("path", "a.md")), alwaysAllow5, rootDir5,
        [new ApprovalRule("write_file *", ApprovalDecision.Ask)]) == ApprovalDecision.Ask);

// external_directory：越界写不许静默放行
Check("★ external_directory：越界写升级为询问（档位放行也一样）",
    ApprovalPolicyChain.Decide("work", CallRisk("write_file", ToolRisk.Write, ("path", Path.Combine(Path.GetTempPath(), "af-outside.txt"))), alwaysAllow5, rootDir5) == ApprovalDecision.Ask);
Check("区内写不受影响", ApprovalPolicyChain.Decide("work", CallRisk("write_file", ToolRisk.Write, ("path", "src/a.txt")), alwaysAllow5, rootDir5) == ApprovalDecision.Allow);
Check("★ .. 逃逸视为越界",
    ApprovalPolicyChain.Decide("work", CallRisk("write_file", ToolRisk.Write, ("path", "../evil.txt")), alwaysAllow5, rootDir5) == ApprovalDecision.Ask);
Check("★ 越界闸压过规则放行（规则不是越界授权）",
    ApprovalPolicyChain.Decide("work", CallRisk("write_file", ToolRisk.Write, ("path", Path.Combine(Path.GetTempPath(), "af-outside.txt"))), alwaysAllow5, rootDir5,
        [new ApprovalRule("write_file *", ApprovalDecision.Allow)]) == ApprovalDecision.Ask);
Check("★ 显式授予 external_directory 后放行",
    ApprovalPolicyChain.Decide("work", CallRisk("write_file", ToolRisk.Write, ("path", Path.Combine(Path.GetTempPath(), "af-outside.txt"))), alwaysAllow5, rootDir5,
        allowExternalDirectory: true) == ApprovalDecision.Allow);
Check("越界读不管（读不是伤害向量，仍按档位）",
    ApprovalPolicyChain.Decide("work", CallRisk("read_file", ToolRisk.ReadOnly, ("path", Path.Combine(Path.GetTempPath(), "af-outside.txt"))), alwaysAllow5, rootDir5) == ApprovalDecision.Allow);
Check("没有工作区概念时不瞎拦",
    ApprovalPolicyChain.Decide("work", CallRisk("write_file", ToolRisk.Write, ("path", "x.txt")), alwaysAllow5, workspaceRoot: null,
        allowExternalDirectory: false) == ApprovalDecision.Allow);

// 计划模式闸最高优先：规则推不翻模式的承诺
Check("★ 计划模式闸最高优先（规则 write_file * = allow 也推不翻）",
    ApprovalPolicyChain.Decide("plan", CallRisk("write_file", ToolRisk.Write, ("path", "src/a.txt")), alwaysAllow5, rootDir5,
        [new ApprovalRule("write_file *", ApprovalDecision.Allow)]) == ApprovalDecision.Deny);
Check("计划模式下规则放行命令也不行（只读承诺）",
    ApprovalPolicyChain.Decide("plan", Call("run_command", ("command", "git status")), alwaysAllow5, rootDir5, gitAllow) == ApprovalDecision.Deny);

// 端到端：真实宿主 + agent.json 等价的规则配置 —— 链条真的接进了工具执行
var ruleWs = Path.Combine(rootDir5, "ws");
var ruleFake = new QueuedScriptClient(
    ("run_command", """{"command":"echo hi"}"""),
    ("run_command", """{"command":"rm -rf x"}"""));
var ruleHost = await AgentHost.CreateAsync(new HostOptions
{
    WorkspaceRoot = ruleWs,
    SessionsDir = Path.Combine(rootDir5, "sessions"),
    SessionId = "rules-e2e",
    LlmOverride = ruleFake,
    ApprovalPolicy = DefaultApprovalPolicy.Decide,
    ApprovalRules = [new ApprovalRule("run_command echo *", ApprovalDecision.Allow)],
});

await ruleHost.SendAsync("打个招呼");
var echoDone = ruleHost.Events().OfType<ToolCallCompletedEvent>().LastOrDefault();
Check("★ 端到端：规则放行的命令成功（echo * = allow）", echoDone is { Success: true }, echoDone?.Error);

await ruleHost.SendAsync("把 x 删掉");
var rmDone = ruleHost.Events().OfType<ToolCallCompletedEvent>().LastOrDefault();
Check("★ 端到端：未获规则放行的命令被拒（默认询问→无界面拒绝）",
    rmDone is { Success: false } && rmDone.Error?.Contains("拒绝") == true, rmDone?.Error);

// ── MCP 资源展平（v3.22）──
Console.WriteLine("\n── MCP 资源展平 ──");
{
    using var resourceDoc = System.Text.Json.JsonDocument.Parse("""
        {"contents":[{"uri":"file:///a.txt","mimeType":"text/plain","text":"内容甲"},{"uri":"file:///b.png","blob":"AAECAw=="}]}
        """);
    var flat = AgentFramework.Host.Mcp.McpContent.FlattenResource(resourceDoc.RootElement);
    Check("文本资源原样带出（带 uri 前缀）", flat.Contains("file:///a.txt") && flat.Contains("内容甲"));
    Check("★ 二进制只报大小（不往上下文灌 base64）",
        flat.Contains("已省略") && !flat.Contains("AAECAw=="));

    using var emptyDoc = System.Text.Json.JsonDocument.Parse("""{"contents":[]}""");
    Check("空 contents 返回空串",
        AgentFramework.Host.Mcp.McpContent.FlattenResource(emptyDoc.RootElement).Length == 0);
}

// ── 10. 子 Agent 管控面（v3.23）：后台派发 / 名单 / 追加指令 / 打断 ──
Console.WriteLine("\n── 10. 子 Agent 管控 ──");

var gate = new GatedLlmClient();
var subRoot = Path.Combine(root, "subagents");
var subHost = await AgentHost.CreateAsync(new HostOptions
{
    WorkspaceRoot = Path.Combine(subRoot, "workspace"),
    SessionsDir = Path.Combine(subRoot, "sessions"),
    SessionId = "main",
    LlmOverride = gate,
});

// 10a 后台派发：立刻拿句柄，不阻塞派它的人
var background = await subHost.SubAgents.SpawnAsync("main", "慢任务", background: true);
Check("★ 后台派发立刻返回句柄（不阻塞当前回合）",
    background.State == SubAgentStates.Running && background.Id.StartsWith("sub-"),
    background.State);
await Task.Delay(200);
Check("名单里看得到（且只列本会话派出的）",
    subHost.SubAgents.List("main").Any(a => a.Id == background.Id)
    && subHost.SubAgents.List("someone-else").Count == 0);

// 10b 追加指令 → 跑完当前回合就接着做（而不是被丢弃）
Check("★ 往运行中的子 Agent 追加了一条指令", subHost.SubAgents.Post(background.Id, "做完顺手看一眼日志"));
Check("对不存在的子 Agent 追加 = 如实失败", !subHost.SubAgents.Post("sub-nope", "在吗"));

gate.Release(2);   // 放行两轮（第一轮 + 追加带来的第二轮）
await Task.Delay(500);
var afterPost = subHost.SubAgents.Get(background.Id)!;
Check("★ 追加的指令真的带来了第二轮", afterPost.Rounds >= 2, $"回合={afterPost.Rounds}");
Check("跑完后有摘要（父会话拿到的就是它）", afterPost.Summary is { Length: > 0 });

// 10c 打断：运行中的子 Agent 可以被叫停
var slow = await subHost.SubAgents.SpawnAsync("main", "会被打断的任务", background: true);
await Task.Delay(200);
Check("对运行中的子 Agent 请求打断", subHost.SubAgents.Stop(slow.Id));
await Task.Delay(500);
Check("★ 打断后状态如实变 stopped", subHost.SubAgents.Get(slow.Id)!.State == SubAgentStates.Stopped,
    subHost.SubAgents.Get(slow.Id)!.State);

// 10d 等待：已结束的立刻返回状态描述
var waited = await subHost.SubAgents.WaitAsync(background.Id, 5);
Check("wait 对已结束的子 Agent 立刻返回状态", waited.Contains(background.Id), waited.Split('\n')[0]);

// 10e 父会话留痕：派发与完成都进父事件流（模型可见即已记录）
var parentEvents = AgentFramework.Data.JsonlEventLog.Read(
    Path.Combine(subRoot, "sessions", "main.jsonl")).ToList();
Check("父会话记了派发与完成（各一条以上）",
    parentEvents.OfType<SubAgentDispatchedEvent>().Count() >= 2
    && parentEvents.OfType<SubAgentCompletedEvent>().Count() >= 2,
    $"dispatched={parentEvents.OfType<SubAgentDispatchedEvent>().Count()} completed={parentEvents.OfType<SubAgentCompletedEvent>().Count()}");

// 10f 工具面：模型能用的入口
var spawnTool = new AgentFramework.Tools.SpawnSubAgentTool(() => "main", () => subHost.SubAgents);
Check("spawn_subagent 的 schema 暴露 run_in_background（并行入口）",
    spawnTool.ParametersJsonSchema.Contains("run_in_background"));

var controlTool = new AgentFramework.Tools.SubAgentControlTool(() => "main", () => subHost.SubAgents);
var listOut = await controlTool.InvokeAsync(new ToolInvocation("subagent",
    new Dictionary<string, string?> { ["action"] = "list" }));
Check("subagent list 列得出派过的子 Agent", listOut.Success && listOut.Output.Contains(background.Id), listOut.Output);

var stopOut = await controlTool.InvokeAsync(new ToolInvocation("subagent",
    new Dictionary<string, string?> { ["action"] = "stop", ["id"] = "sub-nope" }));
Check("对不存在的 id 操作如实失败", !stopOut.Success, stopOut.Error);

var badAction = await controlTool.InvokeAsync(new ToolInvocation("subagent",
    new Dictionary<string, string?> { ["action"] = "nope" }));
Check("未知 action 如实报错（不静默当成功）", !badAction.Success, badAction.Error);

// 10g 抢占：作废当前这一轮，立刻改用新指令重跑（不是「打断」）
var preempt = await subHost.SubAgents.SpawnAsync("main", "会被抢占的任务", background: true);
await Task.Delay(200);
Check("★ 抢占请求被接受（interrupt=true）", subHost.SubAgents.Post(preempt.Id, "改做另一件事", interrupt: true));
await Task.Delay(300);

var preempted = subHost.SubAgents.Get(preempt.Id)!;
Check("★ 抢占后仍是 running（没被当成「打断」收场）", preempted.State == SubAgentStates.Running, preempted.State);
Check("★ 被作废的那一轮不计入回合数（回合仍为 0）", preempted.Rounds == 0, $"回合={preempted.Rounds}");

gate.Release(1);   // 放行抢占后的那一轮
await Task.Delay(400);
Check("抢占后带着新指令跑完了", subHost.SubAgents.Get(preempt.Id)!.State == SubAgentStates.Completed,
    subHost.SubAgents.Get(preempt.Id)!.State);

// 10h 派生深度闸：子 Agent 再派也有限度（默认 2 层）
// 用**运行中**的子 Agent 当父级 —— 它的会话还活着，这才是「子 Agent 自己再派」的真实形态。
var runningParent = await subHost.SubAgents.SpawnAsync("main", "父级任务", background: true);
await Task.Delay(200);

var level2 = await subHost.SubAgents.SpawnAsync(runningParent.Id, "第二层任务", background: true);
Check("运行中的子 Agent 再派一级：允许（第 2 层）", level2.State == SubAgentStates.Running);

var overDepthBlocked = false;
try
{
    await subHost.SubAgents.SpawnAsync(level2.Id, "第三层任务", background: true);
}
catch (InvalidOperationException)
{
    overDepthBlocked = true;
}

Check("★ 派生深度超过上限时如实拒绝（默认 2 层）", overDepthBlocked);

// 已关闭 / 不存在的父会话：如实拒绝，而不是从留痕深处冒一个费解的错
var badParentBlocked = false;
var badParentMessage = "";
try
{
    await subHost.SubAgents.SpawnAsync("no-such-session", "孤儿任务", background: true);
}
catch (InvalidOperationException ex)
{
    badParentBlocked = true;
    badParentMessage = ex.Message;
}

Check("★ 父会话不存在时如实拒绝（错指向参数，不含糊）",
    badParentBlocked && badParentMessage.Contains("父会话"), badParentMessage);

// 10i 反复派发 / 叫停 / 追加（找竞态）：幂等路径不该抛，也不该卡在 running
var stressOk = true;
for (var i = 0; i < 20 && stressOk; i++)
{
    try
    {
        var churn = await subHost.SubAgents.SpawnAsync("main", $"压测任务 {i}", background: true);
        subHost.SubAgents.Stop(churn.Id);
        subHost.SubAgents.Post(churn.Id, "追加（可能已结束）");   // 竞态路径：允许 false，不许抛
        subHost.SubAgents.Stop(churn.Id);                        // 二次叫停：幂等
    }
    catch (Exception)
    {
        stressOk = false;
    }
}

await Task.Delay(800);
Check("★ 反复派发 / 叫停 / 追加不抛异常（幂等与竞态路径稳）", stressOk);

// 把前面故意卡住的两个（父级 / 第二层）也停掉，再看有没有残留的 running
subHost.SubAgents.StopAll();
await Task.Delay(500);
Check("★ 全部叫停后没有卡在 running 的（取消真的落实）",
    subHost.SubAgents.List("main").All(a => !a.IsRunning),
    string.Join(",", subHost.SubAgents.List("main").Select(a => a.State).Distinct()));

// 10j 并发回报：多个子 Agent 同时完成 → 父会话事件表仍按 Seq 严格有序
//（落盘的 Seq 由日志锁分配、入表是另一把锁 —— 两步之间可能交错，这正是要钉住的）
var concurrent = Enumerable.Range(0, 8)
    .Select(i => subHost.SubAgents.SpawnAsync("main", $"并发任务 {i}", background: true))
    .ToArray();
var concurrentInfos = await Task.WhenAll(concurrent);
gate.Release(8);
await Task.Delay(900);

var mainEvents = subHost.GetSession("main")!.Events;
var ordered = mainEvents.Zip(mainEvents.Skip(1)).All(pair => pair.First.Seq < pair.Second.Seq);
Check("★ 并发回报后父会话事件表仍按 Seq 严格有序", ordered,
    string.Join(",", mainEvents.TakeLast(6).Select(e => e.Seq)));
Check("8 个并发子 Agent 都收尾了",
    concurrentInfos.All(a => subHost.SubAgents.Get(a.Id) is { IsRunning: false }));

// 10k 关停纪律：退休后拒绝新回合（这道防线从前建好了却没有任何调用点）
var retiredHost = await AgentHost.CreateAsync(new HostOptions
{
    WorkspaceRoot = Path.Combine(root, "retired", "workspace"),
    SessionsDir = Path.Combine(root, "retired", "sessions"),
    SessionId = "retired",
    LlmOverride = new RecordingLlmClient { Reply = "不该被调用" },
});

retiredHost.MarkRetired();
var refusedAfterRetire = false;
var retireMessage = "";
try
{
    await retiredHost.SendAsync("还能跑吗");
}
catch (InvalidOperationException ex)
{
    refusedAfterRetire = true;
    retireMessage = ex.Message;
}

Check("★ 退休后的宿主拒绝新回合（关停防线真的接线了）",
    refusedAfterRetire && retireMessage.Contains("已关闭"), retireMessage);

await retiredHost.DisposeAsync();
Check("关停流程本身走完（等闸不把自己卡死）", retiredHost.IsRetired);

// 10l 关停纪律：后台作业（checkpoint writer）随宿主叫停 —— 不许「宿主已拆、它还在往盘上写」
var blockedWriter = new BlockingCheckpointWriter();
var bgHost = await AgentHost.CreateAsync(new HostOptions
{
    WorkspaceRoot = Path.Combine(root, "bg", "workspace"),
    SessionsDir = Path.Combine(root, "bg", "sessions"),
    SessionId = "bg",
    LlmOverride = new RecordingLlmClient { Reply = "收到" },
    Checkpoint = new CheckpointOptions { Enabled = true, TriggerRatio = 0.0, MinNewEvents = 1 },
    CheckpointWriterOverride = blockedWriter,
});

// 跑两轮：第一轮事件表还空（防抖过不去），第二轮才够得着派发条件
await bgHost.SendAsync("第一句");
await bgHost.SendAsync("第二句");

var writerDispatched = false;
for (var i = 0; i < 40 && !writerDispatched; i++)
{
    writerDispatched = blockedWriter.Started;
    if (!writerDispatched)
    {
        await Task.Delay(50);
    }
}

Check("★ checkpoint writer 被派发（后台作业确实在跑）", writerDispatched);

await bgHost.DisposeAsync();
Check("★ 宿主关闭时后台作业被叫停（writer 收到取消，不会关停后还在写盘）",
    blockedWriter.Cancelled);

// 10m 子 Agent 异常要有结局（不许卡 Running）——
//     后台派发的路径没人 await 这个任务，正因如此更不该留下一片静默的空白
var failingHost = await AgentHost.CreateAsync(new HostOptions
{
    WorkspaceRoot = Path.Combine(root, "failing", "workspace"),
    SessionsDir = Path.Combine(root, "failing", "sessions"),
    SessionId = "failing",
    LlmOverride = new ThrowingLlmClient(),
});

var failingInfo = await failingHost.SubAgents.SpawnAsync("failing", "会失败的任务", background: true);
var failedSnapshot = failingHost.SubAgents.Get(failingInfo.Id);

// 模型异常可能先走内部重试 / 退避 —— 给它足够时间收场，别把「还在重试」错判成「卡住」
for (var i = 0; i < 120 && failedSnapshot is { IsRunning: true }; i++)
{
    await Task.Delay(100);
    failedSnapshot = failingHost.SubAgents.Get(failingInfo.Id);
}

Check("★ 子 Agent 异常时有结局（不卡 Running）",
    failedSnapshot is { IsRunning: false }, failedSnapshot?.State);
Check("★ 失败如实记为 Failed",
    failedSnapshot?.State == SubAgentStates.Failed, failedSnapshot?.State);

await failingHost.DisposeAsync();

await subHost.DisposeAsync();

Console.WriteLine($"\n═══ 结果：{passes} 通过 / {failures} 失败 ═══");
return failures == 0 ? 0 : 1;

// ═══════════════════════════ 测试替身 ═══════════════════════════

/// <summary>记录全部请求的假模型 —— 用来检查"模型到底看到了什么"。</summary>
internal sealed class RecordingLlmClient : ILlmClient
{
    private readonly List<LlmRequest> _requests = [];

    public string Name => "recording";

    public IReadOnlyList<LlmRequest> Requests => _requests;

    public string Reply { get; set; } = "好的。";

    public async IAsyncEnumerable<LlmStreamChunk> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        _requests.Add(request);
        yield return new LlmStreamChunk.TextDelta(Reply);
        yield return new LlmStreamChunk.Completed("stop");
    }
}

/// <summary>第一轮请求执行命令，第二轮收尾 —— 用来验证审批拦截。</summary>
internal sealed class CommandScriptClient : ILlmClient
{
    private readonly List<LlmRequest> _requests = [];

    public string Name => "command-script";

    public IReadOnlyList<LlmRequest> Requests => _requests;

    public async IAsyncEnumerable<LlmStreamChunk> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        _requests.Add(request);

        var alreadyTried = request.Messages.Any(m => m.Role == LlmRole.Tool);
        if (!alreadyTried)
        {
            yield return new LlmStreamChunk.ToolCallsReady(
            [
                new ToolCallRequest("cmd_1", "run_command", """{"command":"echo hi"}"""),
            ]);
            yield return new LlmStreamChunk.Completed("tool_calls");
            yield break;
        }

        yield return new LlmStreamChunk.TextDelta("命令被拒绝了，我换别的方式。");
        yield return new LlmStreamChunk.Completed("stop");
    }
}

/// <summary>
/// 按队列逐轮回放工具调用的假模型：每次回合的第一次调用发队列里的工具调用、第二次收尾；
/// 全部请求入档（用来检查「模型到底看到了什么」）。
/// </summary>
internal sealed class QueuedScriptClient(params (string Tool, string ArgsJson)[] turns) : ILlmClient
{
    private readonly Queue<(string Tool, string ArgsJson)> _turns = new(turns);
    private int _calls;

    public string Name => "queued-script";

    public List<LlmRequest> Requests { get; } = [];

    public async IAsyncEnumerable<LlmStreamChunk> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();

        lock (Requests)
        {
            Requests.Add(request);
        }

        if (++_calls % 2 == 1 && _turns.TryDequeue(out var spec))
        {
            yield return new LlmStreamChunk.ToolCallsReady(
            [
                new ToolCallRequest($"qc-{_calls}", spec.Tool, spec.ArgsJson),
            ]);
            yield return new LlmStreamChunk.Completed("tool_calls");
            yield break;
        }

        yield return new LlmStreamChunk.TextDelta("好的，这一步处理完了。");
        yield return new LlmStreamChunk.Completed("stop");
    }
}

/// <summary>能真正回答的交互缝替身 —— 用来验证 ask_user 这条路是通的。</summary>
internal sealed class StubUserInteraction : IUserInteraction
{
    public bool CanInteract => true;

    public ValueTask<bool> ConfirmAsync(ToolPreExecuteEvent toolCall, CancellationToken ct = default)
        => ValueTask.FromResult(true);

    public ValueTask<AskUserAnswer> AskAsync(AskUserRequest request, CancellationToken ct = default)
        => ValueTask.FromResult(AskUserAnswer.Of("方案甲"));

    public ValueTask NotifyAsync(string text, CancellationToken ct = default)
        => ValueTask.CompletedTask;
}

/// <summary>只懂审批的界面替身 —— 验证「审批是交互的一个特例」不会冒充「能提问」。</summary>
internal sealed class StubApprovalPrompt : IApprovalPrompt
{
    public ValueTask<bool> AskAsync(ToolPreExecuteEvent request, CancellationToken ct)
        => ValueTask.FromResult(true);
}

/// <summary>等待超时的交互缝替身 —— 验证超时与「用户跳过」文案可区分。</summary>
internal sealed class TimeoutUserInteraction : IUserInteraction
{
    public bool CanInteract => true;

    public ValueTask<bool> ConfirmAsync(ToolPreExecuteEvent toolCall, CancellationToken ct = default)
        => ValueTask.FromResult(true);

    public ValueTask<AskUserAnswer> AskAsync(AskUserRequest request, CancellationToken ct = default)
        => ValueTask.FromResult(AskUserAnswer.Timeout);

    public ValueTask NotifyAsync(string text, CancellationToken ct = default)
        => ValueTask.CompletedTask;
}

/// <summary>名字可指定的桩工具（运行期注册用）。</summary>
internal sealed class NamedTool(string name) : ITool
{
    public string Name => name;

    public string Description => "验收用桩工具";

    public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
        => ValueTask.FromResult(ToolResult.Ok("ok"));
}

/// <summary>会自报风险等级的桩工具（v3.23：审批按它判，而不是按名字）。</summary>
internal sealed class RiskProbeTool(string name, ToolRisk risk) : IToolWithRisk, ITool
{
    public string Name => name;

    public string Description => "验收用风险桩工具";

    public ToolRisk Risk => risk;

    public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
        => ValueTask.FromResult(ToolResult.Ok("ok"));
}

/// <summary>
/// 会「卡住」的假模型：每次调用都等一次 Release（或被取消）——
/// 把「后台派发 / 追加指令 / 打断」这些**时序**行为变成确定可断言的东西。
/// </summary>
internal sealed class GatedLlmClient : ILlmClient
{
    private readonly SemaphoreSlim _gate = new(0);
    private int _calls;

    public string Name => "gated";

    public int Calls => Volatile.Read(ref _calls);

    public void Release(int count = 1) => _gate.Release(count);

    public async IAsyncEnumerable<LlmStreamChunk> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var round = Interlocked.Increment(ref _calls);

        // 卡在这里，直到测试放行（或回合被取消 —— 打断就是这条路径）
        await _gate.WaitAsync(ct).ConfigureAwait(false);

        yield return new LlmStreamChunk.TextDelta($"第 {round} 轮完成");
        yield return new LlmStreamChunk.Completed("stop");
    }
}

/// <summary>自定义宿主模块：验证「加功能 = 加模块，不必改核心」。</summary>
internal sealed class ProbeModule : IHostModule
{
    public string Name => "probe";

    /// <summary>排在工具模块（300）之后 —— 序号就是它的插队位置。</summary>
    public int Order => 450;

    public ValueTask ConfigureAsync(HostState state, CancellationToken ct = default)
    {
        state.Kernel.CreateKernelScope("probe").RegisterTool(new NamedTool("module_tool"));
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// 会一直挂着、直到被取消的 checkpoint writer ——
/// 用来验证「宿主关闭会叫停后台作业」（它的 token 若被取消，说明关闭真的接线了）。
/// </summary>
internal sealed class BlockingCheckpointWriter : ICheckpointWriter
{
    private volatile bool _started;
    private volatile bool _cancelled;

    public bool Started => _started;

    public bool Cancelled => _cancelled;

    public async ValueTask<CheckpointEvent?> WriteAsync(CheckpointRequest request, CancellationToken ct = default)
    {
        _started = true;
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
            _cancelled = true;
        }

        return null;
    }
}

/// <summary>每次调用都炸的假模型 —— 用来验证「子 Agent 异常有结局」。</summary>
internal sealed class ThrowingLlmClient : ILlmClient
{
    public string Name => "throwing";

    public async IAsyncEnumerable<LlmStreamChunk> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        throw new InvalidOperationException("模型炸了（验收桩）");
#pragma warning disable CS0162
        yield return new LlmStreamChunk.Completed("stop");
#pragma warning restore CS0162
    }
}
