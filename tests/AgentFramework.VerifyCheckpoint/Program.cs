using System.Text.Json;
using AgentFramework.Contracts;
using AgentFramework.Data;

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

// ═══ 结果 ═══
Console.WriteLine("\n═══════════════════════════════════════");
Console.WriteLine($"结果：{passes} 通过 / {failures} 失败");
if (failures > 0)
{
    Console.WriteLine("（实现 writer 后需补 B 批断言：水位触发 / 增量窗口 / 记忆升级 / rebuild 种子）");
    Environment.ExitCode = 1;
}
