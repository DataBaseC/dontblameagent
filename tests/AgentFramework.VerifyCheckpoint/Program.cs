using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentFramework.Contracts;
using AgentFramework.Data;
using AgentFramework.Host;
using AgentFramework.Llm;
using static AgentFramework.Harness.Suite;

// ═══════════════════════════════════════════════════════════
//  checkpoint（记忆与进化 · v3.11）契约层验证
//
//  本工程分两批：
//    A 批（现在）—— 契约层：选项默认值、事件序列化、渲染块、投影隔离、追加语义
//    B 批（实现 writer 后补）—— 水位触发、增量窗口、记忆升级、rebuild 种子注入
//
//  这里先落 A 批，保证「契约先定死，实现照着填」；B 批断言随实现一起加。
//  不需要 API key / 联网 / 真实模型。
// ═══════════════════════════════════════════════════════════


Console.WriteLine("═══ checkpoint 契约验证 ═══");

// ═══ 1. 选项默认值：写入早、裁剪晚 ═══
Console.WriteLine("\n── 1. 选项默认值 ──");

var opt = new CheckpointOptions();
var compressOpt = new ContextOptions();

Check("checkpoint 默认关闭（零回归优先）", opt.Enabled == false);
Check("水位默认 0.35", Math.Abs(opt.TriggerRatio - 0.35) < 1e-9, opt.TriggerRatio.ToString());
Check(
    "水位严格低于压缩线（写入早、裁剪晚）",
    opt.TriggerRatio < compressOpt.CompressionTriggerRatio,
    $"checkpoint {opt.TriggerRatio} < compress {compressOpt.CompressionTriggerRatio}");
Check("压缩线未被本次改动动过（仍是 0.8）", Math.Abs(compressOpt.CompressionTriggerRatio - 0.8) < 1e-9);
Check("正文上限 2000 字符（恒定大小锚点）", opt.MaxChars == 2000);
Check("防抖阈值 8 个事件", opt.MinNewEvents == 8);
Check("默认顺带升级记忆", opt.PromoteMemory);

var opt2 = opt.Clone();
opt2.TriggerRatio = 0.9;
opt2.Enabled = true;
Check("Clone 独立（改副本不影响原对象）", Math.Abs(opt.TriggerRatio - 0.35) < 1e-9 && !opt.Enabled);

Check(
    "触发来源三种且互异",
    CheckpointTrigger.Auto != CheckpointTrigger.Manual
        && CheckpointTrigger.Manual != CheckpointTrigger.Rebuild
        && CheckpointTrigger.Auto != CheckpointTrigger.Rebuild
        && CheckpointTrigger.Rebuild.Length > 0);

// ═══ 2. 事件序列化：多态往返 ═══
Console.WriteLine("\n── 2. 事件序列化 ──");

var original = new CheckpointEvent
{
    Seq = 42,
    SessionId = "s-demo",
    Timestamp = DateTimeOffset.Parse("2026-09-24T10:00:00Z"),
    Trigger = CheckpointTrigger.Auto,
    WaterLevelPermille = 364,
    PreTokens = 8_736,
    FromSeq = 10,
    ToSeq = 42,
    Intent = "把工具包接进配置层",
    NextAction = "跑全量回归",
    CurrentWork = "改 Program.cs 接线",
    Constraints = ["不许破坏前缀缓存", "core/meta 不可关"],
    FilesTouched = ["Host/Program.cs", "Host/AgentConfig.cs"],
    Discoveries = ["投影器对未知事件静默忽略"],
    ErrorsAndFixes = "CS0103：改用 _manifest.Id",
    Decisions = ["写入早、裁剪晚：拆开两个触发点"],
    Notes = "顺手修掉可见面双实现",
    PromotedMemoryIds = ["abc123", "def456"],
    Model = "test-model",
    ElapsedMs = 1_234,
};

var json = JsonSerializer.Serialize<SessionEvent>(original);
Check("序列化带类型判别符 type=checkpoint", json.Contains("\"type\":\"checkpoint\""));

var round = JsonSerializer.Deserialize<SessionEvent>(json) as CheckpointEvent;
Check("反序列化回到 CheckpointEvent", round is not null);

if (round is not null)
{
    Check("标量字段保真（Intent / NextAction / CurrentWork）",
        round.Intent == original.Intent
        && round.NextAction == original.NextAction
        && round.CurrentWork == original.CurrentWork);
    Check("增量窗口保真（FromSeq / ToSeq）", round.FromSeq == 10 && round.ToSeq == 42);
    Check("水位与 token 保真", round.WaterLevelPermille == 364 && round.PreTokens == 8_736);
    Check("列表字段保真（约束 / 文件 / 决策）",
        round.Constraints.Count == 2
        && round.FilesTouched.Contains("Host/Program.cs")
        && round.Decisions.Count == 1);
    Check("升级记忆的 id 保真", round.PromotedMemoryIds.Count == 2 && round.PromotedMemoryIds[0] == "abc123");
    Check("触发来源保真", round.Trigger == CheckpointTrigger.Auto);
}

var bare = JsonSerializer.Deserialize<SessionEvent>(
    JsonSerializer.Serialize<SessionEvent>(new CheckpointEvent { SessionId = "s", Seq = 1 })) as CheckpointEvent;
Check("空列表往返不变成 null", bare is not null && bare.Constraints.Count == 0 && bare.FilesTouched.Count == 0);

// ═══ 3. 渲染块：空字段不占行 ═══
Console.WriteLine("\n── 3. 渲染块 ──");

var empty = new CheckpointEvent { SessionId = "s", Seq = 1 };
Check("全空渲染为空串", empty.RenderBlock().Length == 0);

var onlyIntent = new CheckpointEvent { SessionId = "s", Seq = 1, Intent = "干这个" };
Check("只有意图时只占一行", onlyIntent.RenderBlock() == "当前意图：干这个", onlyIntent.RenderBlock());

var block = original.RenderBlock();
var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries);
Check("行数等于有值字段数（9 个）", lines.Length == 9, lines.Length.ToString());
Check("意图排在下一步之前", block.IndexOf("当前意图") < block.IndexOf("下一步"));
Check("下一步排在工作约束之前", block.IndexOf("下一步") < block.IndexOf("工作约束"));
Check("多值用分号连接", block.Contains("不许破坏前缀缓存；core/meta 不可关"));
Check("结尾无多余空行", block == block.TrimEnd() && !block.EndsWith('\n'));
Check("标签出现与否与字段是否有值一致", block.Contains("其他：") == !string.IsNullOrWhiteSpace(original.Notes));

// ═══ 4. 投影隔离：checkpoint 不进模型上下文 ═══
Console.WriteLine("\n── 4. 投影隔离 ──");

List<SessionEvent> Base() =>
[
    new SessionCreatedEvent { Seq = 1, SessionId = "s", Title = "demo" },
    new UserMessageEvent { Seq = 2, SessionId = "s", Text = "帮我看看" },
    new AssistantMessageEvent { Seq = 3, SessionId = "s", Text = "好的" },
];

var withoutCheckpoint = SessionProjector.Project(Base());
var withCheckpoint = SessionProjector.Project(
[
    .. Base(),
    new CheckpointEvent { Seq = 4, SessionId = "s", Intent = "不该出现的哨兵文本", Notes = "哨兵笔记" },
]);

Check("投影消息数不受 checkpoint 影响",
    withCheckpoint.Messages.Count == withoutCheckpoint.Messages.Count,
    $"{withoutCheckpoint.Messages.Count} → {withCheckpoint.Messages.Count}");
Check("checkpoint 文本不出现在任何消息里",
    !withCheckpoint.Messages.Any(m => (m.Text ?? "").Contains("哨兵文本") || (m.Text ?? "").Contains("哨兵笔记")));

// ═══ 5. 追加语义：旧的那条永不改 ═══
Console.WriteLine("\n── 5. 追加语义 ──");

var first = new CheckpointEvent { Seq = 10, SessionId = "s", Intent = "最初的想法" };
var second = new CheckpointEvent { Seq = 20, SessionId = "s", Intent = "改主意了", FromSeq = 11, ToSeq = 20 };
var chain = SessionProjector.Project([.. Base(), first, second]);

Check("两条 checkpoint 共存于同一条流", chain.Messages.Count == withoutCheckpoint.Messages.Count);
Check("两条各自保留自己的意图（后者不覆盖前者）",
    first.Intent == "最初的想法" && second.Intent == "改主意了");

var promoted = TaskCardBuilder.Build(chain);
Check("加入 checkpoint 后任务卡照常产出（不含 checkpoint 正文）",
    promoted.Contains("【任务卡】") && !promoted.Contains("改主意了"), promoted.Split('\n')[0]);

// ═══ 6. writer 解析与裁剪（B 批）═══
Console.WriteLine("\n── 6. writer 解析与裁剪 ──");

var parsed = LlmCheckpointWriter.Parse("""
    好的，这是结果：
    ```json
    {"intent":"接 checkpoint","next_action":"跑测试","current_work":"写 writer",
     "constraints":["零回归"],"files_touched":["Host/AgentHost.Context.cs"],
     "discoveries":["投影器忽略未知事件"],"errors_and_fixes":"CS1513：补回收尾","decisions":["写入早裁剪晚"],"notes":"顺手修文档"}
    ```
    """);
Check("能从围栏/前后废话里解析出结构化状态", parsed is not null && parsed.Intent == "接 checkpoint");
Check("列表字段就位", parsed!.Constraints.Count == 1 && parsed.FilesTouched[0] == "Host/AgentHost.Context.cs");
Check("错误与修复保真", parsed.ErrorsAndFixes == "CS1513：补回收尾");
Check("无法解析时返回 null（不写空事件）", LlmCheckpointWriter.Parse("我想了想，没什么可说的") is null);
Check("全空对象也返回 null", LlmCheckpointWriter.Parse("""{"intent":null,"notes":"无","constraints":[]}""") is null);
Check("裸 JSON（无围栏）也能解析", LlmCheckpointWriter.Parse("""{"next_action":"跑测试"}""")?.NextAction == "跑测试");

var longCheckpoint = new CheckpointEvent
{
    SessionId = "s",
    Seq = 1,
    Intent = "关键意图不能丢",
    NextAction = "下一步",
    CurrentWork = "当前工作",
    Notes = new string('z', 500),
    Constraints = [.. Enumerable.Range(0, 40).Select(i => $"约束{i}-{new string('x', 40)}")],
    Discoveries = [.. Enumerable.Range(0, 40).Select(i => $"发现{i}-{new string('y', 40)}")],
};
LlmCheckpointWriter.TrimToLimit(longCheckpoint, 400);
Check("超长被裁到上限内（恒定大小锚点）", longCheckpoint.RenderBlock().Length <= 400, longCheckpoint.RenderBlock().Length.ToString());
Check("★ 意图留到最后（锚点的锚点）", longCheckpoint.Intent == "关键意图不能丢");
Check("杂项最先被裁掉", longCheckpoint.Notes is null);

// ═══ 7. 宿主接入：手动锚点 → 事件 → 记忆升级；默认关不自动写 ═══
Console.WriteLine("\n── 7. 宿主接入 ──");

var hostRoot = Path.Combine(Path.GetTempPath(), "af-cp-verify", Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(hostRoot);

var countingWriter = new CountingCheckpointWriter();
var hostOptions = new HostOptions
{
    WorkspaceRoot = Path.Combine(hostRoot, "workspace"),
    SessionsDir = Path.Combine(hostRoot, "sessions"),
    SessionId = "cp",
    LlmOverride = new StaticReplyLlmClient("好的。"),
    CheckpointWriterOverride = countingWriter,
    Checkpoint = new CheckpointOptions { Enabled = false },
};

await using (var host = await AgentHost.CreateAsync(hostOptions))
{
    await host.SendAsync("你好");
    await host.SendAsync("继续");
    await Task.Delay(200);

    Check("默认关时不自动写锚点（零回归）", host.Events().All(e => e is not CheckpointEvent));
    Check("默认关时也不调用 writer", countingWriter.Calls == 0);

    var (ok, message) = await host.ManualCheckpointAsync();
    Check("手动写锚点成功", ok, message);

    var anchor = host.Events().OfType<CheckpointEvent>().LastOrDefault();
    Check("锚点事件已追加进日志", anchor is not null);
    Check("触发来源是 manual", anchor?.Trigger == CheckpointTrigger.Manual);
    Check("增量窗口被记录（FromSeq/ToSeq）", anchor is { ToSeq: > 0 } && anchor.FromSeq >= 1);
    Check("writer 收到了任务卡与消息投影", countingWriter.LastRequest is { Messages.Count: > 0 });
    Check("★ 升级记忆的 id 写进事件（可审计）",
        anchor?.PromotedMemoryIds.Count == 1,
        string.Join(",", anchor?.PromotedMemoryIds ?? []));
    Check("人可读锚点文件已落盘（追加语义）",
        File.Exists(Path.Combine(hostOptions.SessionsDir, "checkpoints", "cp.anchor.md")));

    // ── 重建：用 checkpoint 当种子开新窗口（只带状态不带史）──
    var rebuiltId = host.RebuildSession("cp");
    Check("重建出新会话（新 id、与原会话不同）", rebuiltId.StartsWith("rebuild-") && rebuiltId != "cp", rebuiltId);

    var rebuiltEvents = AgentFramework.Data.JsonlEventLog.Read(
        Path.Combine(hostOptions.SessionsDir, rebuiltId + ".jsonl")).ToList();
    Check("★ 新窗口只带 header + 一条种子 —— 不带历史",
        rebuiltEvents.Count == 2
        && rebuiltEvents[0] is SessionCreatedEvent
        && rebuiltEvents[1] is AssistantMessageEvent,
        $"{rebuiltEvents.Count} 条事件");
    Check("header 记了血缘（父会话 + 从哪份 checkpoint 重建）",
        rebuiltEvents.OfType<SessionCreatedEvent>().Single().ParentSessionId == "cp");

    var seedEvent = rebuiltEvents.OfType<AssistantMessageEvent>().Single();
    Check("★ 种子就是 checkpoint 的渲染块（意图/下一步都在里面）",
        seedEvent.Text?.Contains("验证 checkpoint 接入") == true && seedEvent.Text.Contains("当前意图"),
        seedEvent.Text?[..Math.Min(40, seedEvent.Text.Length)]);

    var rebuiltProjection = AgentFramework.Data.SessionContextBuilder.Project(rebuiltEvents);
    Check("★ 种子进了新会话的模型可见上下文（一开场就带着状态）",
        rebuiltProjection.Messages.Any(m => m.Content?.Contains("验证 checkpoint 接入") == true),
        $"{rebuiltProjection.Messages.Count} 条消息");
}

// ═══ 结果 ═══
Console.WriteLine("\n═══════════════════════════════════════");
Console.WriteLine($"结果：{passes} 通过 / {failures} 失败");
if (failures > 0)
{
    Environment.ExitCode = 1;
}

/// <summary>记录调用次数与最近一次请求的假 writer —— 验证宿主有没有把请求组装对（B 批确定性的关键）。</summary>
internal sealed class CountingCheckpointWriter : ICheckpointWriter
{
    public int Calls { get; private set; }

    public CheckpointRequest? LastRequest { get; private set; }

    public ValueTask<CheckpointEvent?> WriteAsync(CheckpointRequest request, CancellationToken ct = default)
    {
        Calls++;
        LastRequest = request;

        return ValueTask.FromResult<CheckpointEvent?>(new CheckpointEvent
        {
            Trigger = request.Trigger,
            WaterLevelPermille = request.WaterLevelPermille,
            FromSeq = request.FromSeq,
            ToSeq = request.ToSeq,
            Intent = "验证 checkpoint 接入",
            NextAction = "跑全量回归",
            Discoveries = ["投影器忽略未知事件类型"],
        });
    }
}

/// <summary>固定回一句的假模型：宿主自身跑得起来就行（writer 是覆盖注入的）。</summary>
internal sealed class StaticReplyLlmClient(string reply) : ILlmClient
{
    public string Name => "static";

    public async IAsyncEnumerable<LlmStreamChunk> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        yield return new LlmStreamChunk.TextDelta(reply);
        yield return new LlmStreamChunk.Completed("stop");
    }
}
