using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using AgentFramework.Agent;
using AgentFramework.Contracts;
using AgentFramework.Data;
using AgentFramework.Kernel;
using AgentFramework.Llm;

// ═══════════════════════════════════════════════════════════
//  Agent 主干垂直切片验证
//  调模型 → 工具调用 → 审批 → 执行 → 落日志 → 投影
//  用脚本化假模型，不依赖任何 API key
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

var root = Path.Combine(Path.GetTempPath(), "af-agent-verify", Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(root);
Console.WriteLine("═══ Agent 主干垂直切片验证 ═══");
Console.WriteLine($"工作目录：{root}");

// ── 场景 1：一次工具调用 + 最终回答 ────────────────────────
Console.WriteLine("\n── 1. 主循环：调模型 → 工具 → 再调模型 → 收尾 ──");

const string session1 = "s-1";
var log1Path = Path.Combine(root, "s1.jsonl");
var host1 = new PluginHost(new PluginHostOptions
{
    DataRoot = Path.Combine(root, "plugin-data"),
});

var echo = new EchoTool();
var script1 = new ScriptedLlmClient(
    "cloud",
    new ScriptedTurn(
        TextPieces: ["我来调一下工具。"],
        ToolCalls: [new ToolCallRequest("call_1", "echo", """{"text":"hello"}""")],
        FinishReason: "tool_calls"),
    new ScriptedTurn(
        TextPieces: ["工具返回：", "echo:hello"],
        ToolCalls: null,
        FinishReason: "stop"));

AgentRunResult result1;
List<SessionEvent> events1;
using (var log = JsonlEventLog.Open(log1Path))
{
    var sink = new JsonlSink(log, host1);
    var runner = new AgentRunner(script1, () => [echo], sink, new AgentOptions
    {
        SessionId = session1,
        Model = "scripted-model",
        SystemPrompt = "测试用系统提示",
    });

    result1 = await runner.RunAsync("帮我回显 hello");
    events1 = JsonlEventLog.Read(log1Path).ToList();
}

Check("主循环完成", result1.Completed, $"steps={result1.Steps}");
Check("用了 2 步（工具 → 收尾）", result1.Steps == 2, $"{result1.Steps}");
Check("最终文本正确", result1.FinalText == "工具返回：echo:hello", result1.FinalText);
Check("工具真的被调用了", echo.Invocations.Count == 1 && echo.Invocations[0] == "hello");
Check("第二次请求带上了工具结果", script1.ReceivedRequests[1].Messages.Any(m => m.Role == LlmRole.Tool));
Check("第一次请求带上了工具 schema", script1.ReceivedRequests[0].Tools.Any(t => t.Name == "echo"));
Check("系统提示已下发", script1.ReceivedRequests[0].SystemPrompt == "测试用系统提示");

Console.WriteLine("\n── 1b. 事件是否完整落盘 ──");
Check("事件共 7 条（含每轮用量）", events1.Count == 7, $"{events1.Count}");
Check("顺序：user → 用量 → assistant → tool-requested → tool-completed → 用量 → assistant",
    events1[0] is UserMessageEvent
    && events1[1] is ModelUsageEvent
    && events1[2] is AssistantMessageEvent
    && events1[3] is ToolCallRequestedEvent
    && events1[4] is ToolCallCompletedEvent
    && events1[5] is ModelUsageEvent
    && events1[6] is AssistantMessageEvent,
    string.Join(" → ", events1.Select(e => e.GetType().Name.Replace("Event", ""))));

var state1 = SessionProjector.Project(events1);
Check("投影：3 条消息", state1.Messages.Count == 3, $"{state1.Messages.Count}");
Check("投影：1 次工具调用且成功", state1.ToolCalls.Count == 1 && state1.ToolCalls[0].Success == true);
Check("投影：无异常", state1.Anomalies.Count == 0, string.Join("; ", state1.Anomalies));

// ── 场景 2：审批拦截 ──────────────────────────────────────
Console.WriteLine("\n── 2. 审批拦截（分级审批就是可取消事件）──");

const string session2 = "s-2";
var log2Path = Path.Combine(root, "s2.jsonl");
var host2 = new PluginHost(new PluginHostOptions { DataRoot = Path.Combine(root, "plugin-data") });

var echo2 = new EchoTool();
var script2 = new ScriptedLlmClient(
    "cloud",
    new ScriptedTurn([], [new ToolCallRequest("call_9", "echo", """{"text":"危险操作"}""")], "tool_calls"),
    new ScriptedTurn(["我收到了错误，换个方式。"], null, "stop"));

AgentRunResult result2;
ToolCallCompletedEvent? completed2;
long blockedBefore;
using (var log = JsonlEventLog.Open(log2Path))
{
    // 内核级审批策略：凡是 text 含「危险」的一律拒绝
    var sink = new JsonlSink(log, host2, e =>
        e.Arguments.TryGetValue("text", out var v) && v?.Contains("危险") == true);

    var runner = new AgentRunner(script2, () => [echo2], sink, new AgentOptions { SessionId = session2 });
    result2 = await runner.RunAsync("执行一个危险操作");

    var events2 = JsonlEventLog.Read(log2Path).ToList();
    completed2 = events2.OfType<ToolCallCompletedEvent>().FirstOrDefault();
    blockedBefore = echo2.Invocations.Count;
}

Check("被拦截的工具没有真正执行", blockedBefore == 0, $"实际执行 {blockedBefore} 次");
Check("落盘的结果标记为失败", completed2 is { Success: false }, completed2?.Error ?? "(null)");
Check("拒绝原因已记录", completed2?.Error?.Contains("拒绝") == true, completed2?.Error ?? "");
Check("主循环仍能收尾（模型看到了错误）", result2.Completed);

// ── 场景 3：模型路由（规则打底）────────────────────────────
Console.WriteLine("\n── 3. 模型路由：规则打底 ──");

var cloudClient = new ScriptedLlmClient("cloud", new ScriptedTurn(["云端回答"], null, "stop"));
var localClient = new ScriptedLlmClient("local", new ScriptedTurn(["本地回答"], null, "stop"));
var router = new RouterLlmClient(DefaultRouting.Rule(longContextChars: 200));
router.AddTarget(cloudClient);
router.AddTarget(localClient);

// 3a：短上下文 + 带工具 → 云端
var shortRequest = new LlmRequest
{
    Model = "m",
    Messages = [new LlmMessage { Role = LlmRole.User, Content = "简短问题" }],
    Tools = [new ToolSchema("echo", "回显", """{"type":"object","properties":{}}""")],
};
await foreach (var _ in router.StreamAsync(shortRequest)) { }

// 3b：长上下文 + 不带工具 → 本地
var longRequest = new LlmRequest
{
    Model = "m",
    Messages = [new LlmMessage { Role = LlmRole.User, Content = new string('长', 500) }],
};
await foreach (var _ in router.StreamAsync(longRequest)) { }

Check("短上下文+带工具 → 路由到 cloud", router.History[0].Target == "cloud", router.History[0].Target);
Check("长上下文+无工具 → 路由到 local", router.History[1].Target == "local", router.History[1].Target);
Check("路由来源标记为 rule", router.History.All(h => h.Source == "rule"));
Check("两条路径都真实被调用", cloudClient.ReceivedRequests.Count == 1 && localClient.ReceivedRequests.Count == 1);

// ── 场景 4：手动覆盖 ──────────────────────────────────────
Console.WriteLine("\n── 4. 模型路由：手动覆盖 ──");
router.ManualOverride = "local";
await foreach (var _ in router.StreamAsync(shortRequest)) { }

Check("手动覆盖后无视规则直达 local", router.History[2].Target == "local", router.History[2].Target);
Check("路由来源标记为 manual", router.History[2].Source == "manual", router.History[2].Source);

router.ManualOverride = null;
await foreach (var _ in router.StreamAsync(shortRequest)) { }
Check("解除覆盖后回归规则", router.History[3].Target == "cloud" && router.History[3].Source == "rule",
    $"{router.History[3].Target}/{router.History[3].Source}");

// ── 4b：只配了一个端点时，规则不该把请求送到不存在的地方 ────
Console.WriteLine("\n── 4b. 只配一个端点：规则退让 ──");
var soloRouter = new RouterLlmClient(DefaultRouting.Rule(longContextChars: 200));
var soloCloud = new ScriptedLlmClient("cloud", new ScriptedTurn(["只有云端"], null, "stop"));
soloRouter.AddTarget(soloCloud);

// 按规则这个请求该走 local，但 local 根本没配 —— 应当退回云端，而不是抛异常。
// （这正是「只配云端」的用户在闲聊模式长上下文时会撞上的路径。）
var fallbackSurvived = true;
try
{
    await foreach (var _ in soloRouter.StreamAsync(longRequest)) { }
}
catch (InvalidOperationException)
{
    fallbackSurvived = false;
}

Check("★ 只配云端时，本该走本地的请求退回云端（不抛异常）", fallbackSurvived);
Check("★ 回退时真实调用了唯一那个端点", soloCloud.ReceivedRequests.Count == 1,
    $"{soloCloud.ReceivedRequests.Count} 次");

// ── 场景 5：步数上限保护 ──────────────────────────────────
Console.WriteLine("\n── 5. 死循环保护（步数上限）──");
var loopingClient = new ScriptedLlmClient(
    "cloud",
    Enumerable.Range(0, 20)
        .Select(i => new ScriptedTurn(
            [],
            [new ToolCallRequest($"c{i}", "echo", """{"text":"loop"}""")],
            "tool_calls"))
        .ToArray());

var host3 = new PluginHost(new PluginHostOptions { DataRoot = Path.Combine(root, "plugin-data") });
var echo3 = new EchoTool();
AgentRunResult result3;
using (var log = JsonlEventLog.Open(Path.Combine(root, "s3.jsonl")))
{
    var runner = new AgentRunner(loopingClient, () => [echo3], new JsonlSink(log, host3), new AgentOptions
    {
        SessionId = "s-3",
        MaxSteps = 4,
    });
    result3 = await runner.RunAsync("无限循环测试");
}

Check("步数达到上限后停止", !result3.Completed && result3.StopReason == "max-steps", result3.StopReason);
Check("步数正好 4", result3.Steps == 4, $"{result3.Steps}");
Check("工具调用未失控（4 次）", echo3.Invocations.Count == 4, $"{echo3.Invocations.Count}");

// ── 5b. max-steps 不许把正文丢掉 ───────────────────────────
Console.WriteLine("\n── 5b. max-steps 保留末步正文 ──");

// 末步模型写出了正文、但还带着工具调用没走完 —— 循环因步数上限退出时，
// 那段正文必须出现在 FinalText 里（它是「模型可见即已记录」的一部分）。
var textAndCallsClient = new ScriptedLlmClient(
    "cloud",
    new ScriptedTurn(
        ["第一步正文"],
        [new ToolCallRequest("m1", "echo", """{"text":"a"}""")],
        "tool_calls"),
    new ScriptedTurn(
        ["末步残留正文"],
        [new ToolCallRequest("m2", "echo", """{"text":"b"}""")],
        "tool_calls"),
    new ScriptedTurn(["不该出现"], null, "stop"));

var host4 = new PluginHost(new PluginHostOptions { DataRoot = Path.Combine(root, "plugin-data") });
var echo4 = new EchoTool();
AgentRunResult result4;
List<SessionEvent> events4;
using (var log = JsonlEventLog.Open(Path.Combine(root, "s4.jsonl")))
{
    var runner = new AgentRunner(textAndCallsClient, () => [echo4], new JsonlSink(log, host4), new AgentOptions
    {
        SessionId = "s-4",
        MaxSteps = 2,
    });
    result4 = await runner.RunAsync("跑满步数");
    events4 = JsonlEventLog.Read(Path.Combine(root, "s4.jsonl")).ToList();
}

Check("步数上限后停在 max-steps", !result4.Completed && result4.StopReason == "max-steps", result4.StopReason);
Check("★ max-steps 不丢文本：末步正文进了 FinalText",
    result4.FinalText == "末步残留正文", result4.FinalText);
Check("末步正文也落了盘（模型可见即已记录）",
    events4.OfType<AssistantMessageEvent>().LastOrDefault()?.Text == "末步残留正文");

// ── 6. 工具抛异常也必须补 completed ────────────────────────
Console.WriteLine("\n── 6. 工具异常仍补 ToolCallCompletedEvent ──");

// 「记录意图 → 记录结果」是一对：工具炸了也不能漏 completed，
// 否则会话里留下悬空 tool_call，下一轮请求直接 400。
var boom = new ThrowingTool();
var boomClient = new ScriptedLlmClient(
    "cloud",
    new ScriptedTurn([], [new ToolCallRequest("boom1", "boom", "{}")], "tool_calls"),
    new ScriptedTurn(["我看到了错误，换个方式。"], null, "stop"));

var host5 = new PluginHost(new PluginHostOptions { DataRoot = Path.Combine(root, "plugin-data") });
AgentRunResult result5;
List<SessionEvent> events5;
using (var log = JsonlEventLog.Open(Path.Combine(root, "s5.jsonl")))
{
    var runner = new AgentRunner(boomClient, () => [boom], new JsonlSink(log, host5), new AgentOptions
    {
        SessionId = "s-5",
    });
    result5 = await runner.RunAsync("执行一个会炸的工具");
    events5 = JsonlEventLog.Read(Path.Combine(root, "s5.jsonl")).ToList();
}

var requested5 = events5.OfType<ToolCallRequestedEvent>().ToList();
var completed5 = events5.OfType<ToolCallCompletedEvent>().ToList();
Check("主循环仍能收尾（模型看到了错误）", result5.Completed, result5.StopReason);
Check("意图事件已记录", requested5.Count == 1 && requested5[0].CallId == "boom1");
Check("★ 工具抛异常时仍有 ToolCallCompletedEvent（不悬空 tool_call）",
    completed5.Count == 1 && completed5[0].CallId == "boom1",
    $"{requested5.Count} requested / {completed5.Count} completed");
Check("异常结果落盘为失败", completed5.Count == 1 && completed5[0].Success == false,
    completed5.Count > 0 ? completed5[0].Error : "(missing)");
Check("失败原因写明是工具执行异常",
    completed5.Count == 1 && (completed5[0].Error ?? "").Contains("工具执行异常"),
    completed5.Count > 0 ? completed5[0].Error : "");
var state5 = SessionProjector.Project(events5);
Check("投影：无悬空工具调用异常", state5.Anomalies.Count == 0, string.Join("; ", state5.Anomalies));
Check("第二次请求带回了 ERROR 工具结果（tool_call 成对）",
    boomClient.ReceivedRequests[1].Messages.Any(m => m.Role == LlmRole.Tool && (m.Content ?? "").StartsWith("ERROR:")));

// ── 7. 端点断流 / 抖动不炸回合（免费端点的真实形态）────────
Console.WriteLine("\n── 7. 端点断流降级（不抛异常、不丢正文）──");

// 7a：吐了一半之后端点断流 → 不抛异常、半截正文保留、如实报 endpoint-error
var flaky = new FlakyLlmClient(new FlakyStep("我已经写了一半。", FailAfter: true));
var host6 = new PluginHost(new PluginHostOptions { DataRoot = Path.Combine(root, "plugin-data") });
var echo6 = new EchoTool();
AgentRunResult result6 = default!;
var threw6 = false;
try
{
    using var log = JsonlEventLog.Open(Path.Combine(root, "s6.jsonl"));
    var runner6 = new AgentRunner(flaky, () => [echo6], new JsonlSink(log, host6), new AgentOptions
    {
        SessionId = "s-6",
        MaxContinuations = 0,   // 本例只验降级收尾，不走续写
    });
    result6 = await runner6.RunAsync("端点断流测试");
}
catch
{
    threw6 = true;
}

Check("★ 断流不再抛异常炸掉回合", !threw6);
Check("如实报 endpoint-error（不假成功）", result6.StopReason == "endpoint-error", result6.StopReason);
Check("★ 半截正文不丢（进了 FinalText）", result6.FinalText.Contains("我已经写了一半"), result6.FinalText);

// 7b：第一次调用抛瞬时异常、第二次成功 → 重试后回合照常完成
var flakyOk = new FlakyLlmClient(
    new FlakyStep(null, FailBefore: true),
    new FlakyStep("重试之后成功了。", FinishReason: "stop"));
AgentRunResult result7;
using (var log = JsonlEventLog.Open(Path.Combine(root, "s7.jsonl")))
{
    var runner7 = new AgentRunner(flakyOk, () => [echo6], new JsonlSink(log, host6), new AgentOptions
    {
        SessionId = "s-7",
        MaxContinuations = 0,
    });
    result7 = await runner7.RunAsync("瞬时异常重试测试");
}

Check("★ 瞬时异常自动重试后回合照常完成", result7.Completed && result7.StopReason == "stop", result7.StopReason);
Check("重试后正文正确", result7.FinalText == "重试之后成功了。", result7.FinalText);

// ── 7c–7f. Goal 终止验证（MiMo Goal 机制：防提前收工）────────
Console.WriteLine("\n── 7c. Goal 验证：未达成 → 差距反馈继续 → 达成后收尾 ──");

var goalClient = new ScriptedLlmClient(
    "cloud",
    new ScriptedTurn(["我认为已经做完了。"], null, "stop"),
    new ScriptedTurn(["补完测试，现在真的完成了。"], null, "stop"));
var goalVerifier = new ScriptedGoalVerifier(
    GoalVerification.NotMet("还缺单元测试"),
    GoalVerification.Met());
var host7 = new PluginHost(new PluginHostOptions { DataRoot = Path.Combine(root, "plugin-data") });
AgentRunResult resultGoal;
using (var log = JsonlEventLog.Open(Path.Combine(root, "s8.jsonl")))
{
    var runner = new AgentRunner(goalClient, () => [echo6], new JsonlSink(log, host7), new AgentOptions
    {
        SessionId = "s-goal",
        Goal = "全部测试通过",
        GoalVerifier = goalVerifier,
    });
    resultGoal = await runner.RunAsync("完成这个任务");
}

Check("★ 未达成时不放行收尾（带差距继续干）", resultGoal.Completed && resultGoal.Steps == 2, $"{resultGoal.Steps} 步 / {resultGoal.StopReason}");
Check("★ 差距反馈被送回模型", goalClient.ReceivedRequests[1].Messages.Any(m => m.Role == LlmRole.User && (m.Content ?? "").Contains("还缺单元测试")));
Check("验证者被调用两次（想收尾就被核验）", goalVerifier.Calls.Count == 2, $"{goalVerifier.Calls.Count} 次");
Check("核验拿到的是目标与收尾答复", goalVerifier.Calls[0].Goal == "全部测试通过" && goalVerifier.Calls[0].Final.Contains("做完了"));

Console.WriteLine("\n── 7d. Goal 验证：确认做不到 → 如实收尾 ──");
var impossibleClient = new ScriptedLlmClient("cloud", new ScriptedTurn(["我尽力了。"], null, "stop"));
var impossibleVerifier = new ScriptedGoalVerifier(GoalVerification.Impossible("沙箱禁止联网"));
AgentRunResult resultImpossible;
using (var log = JsonlEventLog.Open(Path.Combine(root, "s9.jsonl")))
{
    var runner = new AgentRunner(impossibleClient, () => [echo6], new JsonlSink(log, host7), new AgentOptions
    {
        SessionId = "s-goal-imp",
        Goal = "抓取网页",
        GoalVerifier = impossibleVerifier,
    });
    resultImpossible = await runner.RunAsync("抓取网页");
}

Check("★ 确认做不到时报 goal-impossible（不空转）", resultImpossible.StopReason == "goal-impossible" && !resultImpossible.Completed, resultImpossible.StopReason);
Check("障碍写进最终答复", resultImpossible.FinalText.Contains("沙箱禁止联网"));

Console.WriteLine("\n── 7e. Goal 验证器故障时放行（fail-open）──");
var failOpenClient = new ScriptedLlmClient("cloud", new ScriptedTurn(["做完了。"], null, "stop"));
AgentRunResult resultFailOpen;
using (var log = JsonlEventLog.Open(Path.Combine(root, "s10.jsonl")))
{
    var runner = new AgentRunner(failOpenClient, () => [echo6], new JsonlSink(log, host7), new AgentOptions
    {
        SessionId = "s-goal-open",
        Goal = "随便什么",
        GoalVerifier = new ThrowingGoalVerifier(),
    });
    resultFailOpen = await runner.RunAsync("验证器故障测试");
}

Check("★ 验证器炸了也不卡收尾", resultFailOpen.Completed && resultFailOpen.StopReason == "stop", resultFailOpen.StopReason);

Console.WriteLine("\n── 7f. 验证器输出解析 ──");
Check("NOT_MET 被识别", LlmGoalVerifier.Parse("VERDICT: NOT_MET\nGAP: 缺测试").Verdict == GoalVerdict.NotMet);
Check("GAP 被提取", LlmGoalVerifier.Parse("VERDICT: NOT_MET\nGAP: 缺测试").Gap == "缺测试");
Check("IMPOSSIBLE 被识别", LlmGoalVerifier.Parse("VERDICT: IMPOSSIBLE\nGAP: -").Verdict == GoalVerdict.Impossible);
Check("MET 被识别", LlmGoalVerifier.Parse("VERDICT: MET\nGAP: -").Verdict == GoalVerdict.Met);
Check("认不出裁决时放行（fail-open）", LlmGoalVerifier.Parse("我想想……").Verdict == GoalVerdict.Met);

// ── 8. SSE 坏帧容错（P0-1）────────────────────────────────
Console.WriteLine("\n── 8. SSE 坏帧容错 ──");

var sseBody =
    "data: {\"choices\":[{\"delta\":{\"content\":\"第一段\"}}]}\n\n"
    + "data: {这一行不是合法 JSON\n\n"          // 坏帧：端点残帧 / 代理注入的垃圾
    + "data: \n\n"                              // 空帧
    + "data: {\"choices\":[{\"delta\":{\"content\":\"第二段\"},\"finish_reason\":\"stop\"}]}\n\n"
    + "data: [DONE]\n\n";

var (sseListener, ssePort) = StartSseServer(sseBody);

try
{
    var flakyClient = new OpenAiCompatibleClient("flaky", new OpenAiCompatibleOptions
    {
        BaseUrl = $"http://127.0.0.1:{ssePort}/v1",
        DefaultModel = "test-model",
    });

    var streamed = new StringBuilder();
    var completed = false;

    await foreach (var chunk in flakyClient.StreamAsync(new LlmRequest
                   {
                       Model = "auto",
                       Messages = [new LlmMessage { Role = LlmRole.User, Content = "打个招呼" }],
                       Tools = [],
                   }))
    {
        if (chunk is LlmStreamChunk.TextDelta delta)
        {
            streamed.Append(delta.Text);
        }

        if (chunk is LlmStreamChunk.Completed)
        {
            completed = true;
        }
    }

    Check("★ 坏帧被跳过后本轮仍正常完成（P0-1）", completed);
    Check("坏帧前后的正常增量都没丢", streamed.ToString() == "第一段第二段", streamed.ToString());
}
finally
{
    sseListener.Stop();
}

Console.WriteLine($"\n═══ 结果：{passes} 通过 / {failures} 失败 ═══");
return failures == 0 ? 0 : 1;

// ── 辅助：一个只会照本宣科回放 SSE 的本地端点 ──────────────
static (HttpListener Listener, int Port) StartSseServer(string body)
{
    var listener = new HttpListener();
    var port = PickFreeSsePort();
    listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    listener.Start();

    _ = Task.Run(async () =>
    {
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync();
            }
            catch
            {
                break;
            }

            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/event-stream; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    });

    return (listener, port);
}

static int PickFreeSsePort()
{
    var probe = new TcpListener(IPAddress.Loopback, 0);
    probe.Start();
    var port = ((IPEndPoint)probe.LocalEndpoint).Port;
    probe.Stop();
    return port;
}

// ═══════════════════════════ 测试替身 ═══════════════════════════

/// <summary>按脚本返回响应的假模型 —— 让验证不依赖网络与 API key。</summary>
internal sealed class ScriptedLlmClient : ILlmClient
{
    private readonly Queue<ScriptedTurn> _turns = new();
    private readonly List<LlmRequest> _received = [];

    public ScriptedLlmClient(string name, params ScriptedTurn[] turns)
    {
        Name = name;
        foreach (var turn in turns)
        {
            _turns.Enqueue(turn);
        }
    }

    public string Name { get; }

    public IReadOnlyList<LlmRequest> ReceivedRequests => _received;

    public async IAsyncEnumerable<LlmStreamChunk> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();

        _received.Add(request);

        if (!_turns.TryDequeue(out var turn))
        {
            yield return new LlmStreamChunk.Completed("stop");
            yield break;
        }

        foreach (var piece in turn.TextPieces)
        {
            yield return new LlmStreamChunk.TextDelta(piece);
        }

        if (turn.ToolCalls is { Count: > 0 })
        {
            yield return new LlmStreamChunk.ToolCallsReady(turn.ToolCalls);
        }

        yield return new LlmStreamChunk.Completed(turn.FinishReason);
    }
}

internal sealed record ScriptedTurn(
    IReadOnlyList<string> TextPieces,
    IReadOnlyList<ToolCallRequest>? ToolCalls,
    string FinishReason);

/// <summary>
/// 会「抖」的假模型：按脚本逐步执行 —— 每步可以先抛异常、吐一半再抛异常、或正常回放。
/// 用来验证主循环的重试与降级（免费端点的断流 / 上游过载都是这个形态）。
/// </summary>
internal sealed class FlakyLlmClient : ILlmClient
{
    private readonly Queue<FlakyStep> _steps;

    public FlakyLlmClient(params FlakyStep[] steps)
    {
        _steps = new Queue<FlakyStep>(steps);
    }

    public string Name => "flaky";

    public async IAsyncEnumerable<LlmStreamChunk> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();

        if (!_steps.TryDequeue(out var step))
        {
            yield return new LlmStreamChunk.Completed("stop");
            yield break;
        }

        if (step.FailBefore)
        {
            throw new InvalidOperationException("端点返回错误帧：Upstream stream terminated unexpectedly");
        }

        if (step.Text is not null)
        {
            yield return new LlmStreamChunk.TextDelta(step.Text);
        }

        if (step.FailAfter)
        {
            throw new InvalidOperationException("端点返回错误帧：Upstream stream terminated unexpectedly");
        }

        yield return new LlmStreamChunk.Completed(step.FinishReason);
    }
}

internal sealed record FlakyStep(
    string? Text,
    bool FailBefore = false,
    bool FailAfter = false,
    string FinishReason = "stop");

/// <summary>按脚本回放裁决的假目标验证器（队列耗尽后一律 Met）。</summary>
internal sealed class ScriptedGoalVerifier(params GoalVerification[] verdicts) : IGoalVerifier
{
    private readonly Queue<GoalVerification> _verdicts = new(verdicts);

    public List<(string Goal, string Final)> Calls { get; } = [];

    public ValueTask<GoalVerification> VerifyAsync(
        string goal,
        string userRequest,
        IReadOnlyList<LlmMessage> context,
        string finalText,
        CancellationToken ct = default)
    {
        Calls.Add((goal, finalText));
        return ValueTask.FromResult(_verdicts.TryDequeue(out var verdict) ? verdict : GoalVerification.Met());
    }
}

/// <summary>一调就炸的假目标验证器 —— 验证 fail-open。</summary>
internal sealed class ThrowingGoalVerifier : IGoalVerifier
{
    public ValueTask<GoalVerification> VerifyAsync(
        string goal,
        string userRequest,
        IReadOnlyList<LlmMessage> context,
        string finalText,
        CancellationToken ct = default)
        => throw new InvalidOperationException("验证器炸了");
}

/// <summary>把事件写进 JSONL，并把审批事件派发到内核事件总线。</summary>
internal sealed class JsonlSink : IAgentEventSink
{
    private readonly JsonlEventLog _log;
    private readonly PluginHost _host;
    private readonly Func<ToolPreExecuteEvent, bool> _denyPolicy;

    public JsonlSink(JsonlEventLog log, PluginHost host, Func<ToolPreExecuteEvent, bool>? denyPolicy = null)
    {
        _log = log;
        _host = host;
        _denyPolicy = denyPolicy ?? (_ => false);
    }

    public ValueTask EmitAsync(SessionEvent sessionEvent, CancellationToken ct)
    {
        _log.Append(sessionEvent);
        return ValueTask.CompletedTask;
    }

    public async ValueTask RequestApprovalAsync(ToolPreExecuteEvent toolPreExecuteEvent, CancellationToken ct)
    {
        // 先让内核/插件侧的订阅者表态（它们可以置 Cancelled）
        await _host.EmitAsync(toolPreExecuteEvent, ct).ConfigureAwait(false);

        // 再套一层宿主策略
        if (!toolPreExecuteEvent.Cancelled && _denyPolicy(toolPreExecuteEvent))
        {
            toolPreExecuteEvent.Cancelled = true;
            toolPreExecuteEvent.RejectReason = "命中宿主拒绝策略";
        }
    }
}

internal sealed class EchoTool : ITool
{
    public string Name => "echo";

    public string Description => "回显给定的文本";

    public List<string> Invocations { get; } = [];

    public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        var text = invocation.Arguments.TryGetValue("text", out var value) ? value : null;
        Invocations.Add(text ?? "");
        return ValueTask.FromResult(ToolResult.Ok($"echo:{text}"));
    }
}

/// <summary>一调就炸的工具 —— 验证「工具异常也必须补 completed」。</summary>
internal sealed class ThrowingTool : ITool
{
    public string Name => "boom";

    public string Description => "总是抛异常";

    public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
        => throw new InvalidOperationException("故意炸的");
}
