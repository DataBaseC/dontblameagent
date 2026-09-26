using System.Runtime.CompilerServices;
using AgentFramework.Contracts;
using AgentFramework.Tools;
using AgentFramework.Data;
using AgentFramework.Host;
using AgentFramework.Host.Hosting;

// ═══════════════════════════════════════════════════════════
//  宿主装配垂直切片验证
//  装配 → 对话 → 重启续接 → 审批 → 插件并入
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
    => new()
    {
        ToolName = tool,
        Arguments = args.ToDictionary(a => a.Key, a => (string?)a.Value),
    };

ApprovalDecision Decide(ApprovalTier tier, ToolPreExecuteEvent e)
    => ApprovalTiers.Decide(tier, e, workspace);

// Ask：写/执行都问，读放行
Check("Ask 档：write_file 仍问", Decide(ApprovalTier.Ask, Call("write_file", ("path", "a.txt"))) == ApprovalDecision.Ask);
Check("Ask 档：read_file 放行", Decide(ApprovalTier.Ask, Call("read_file", ("path", "a.txt"))) == ApprovalDecision.Allow);

// Build：区内写放行、区外写问；安全命令放行、危险命令问
Check("★ Build 档：工作区内写自动放行",
    Decide(ApprovalTier.Build, Call("write_file", ("path", "sub/a.txt"))) == ApprovalDecision.Allow);
Check("★ Build 档：工作区外写仍问",
    Decide(ApprovalTier.Build, Call("write_file", ("path", "../../evil.txt"))) == ApprovalDecision.Ask);
Check("★ Build 档：普通命令放行",
    Decide(ApprovalTier.Build, Call("run_command", ("command", "git status"))) == ApprovalDecision.Allow);
Check("★ Build 档：危险命令仍问",
    Decide(ApprovalTier.Build, Call("run_command", ("command", "rm -rf /"))) == ApprovalDecision.Ask);

// Plan：逐项判定同 Ask（批量由 HostEventSink 的回合放行集处理）
Check("Plan 档：write_file 逐项仍问", Decide(ApprovalTier.Plan, Call("write_file", ("path", "a.txt"))) == ApprovalDecision.Ask);

// Yolo：全放行，但审计事件链不变（事件由主循环保证，这里只验策略）
Check("★ Yolo 档：写与执行全部放行",
    Decide(ApprovalTier.Yolo, Call("write_file", ("path", "../../evil.txt"))) == ApprovalDecision.Allow
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
