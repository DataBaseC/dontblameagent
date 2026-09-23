using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AgentFramework.Contracts;
using AgentFramework.Data;
using AgentFramework.Host;
using AgentFramework.Llm;

// ═══════════════════════════════════════════════════════════
//  输入转述（澄清模式）垂直切片验证
//  契约 / 转述器 / 宿主接入 / 数据留痕 / HTTP 接口 / 持久化
//
//  验的是四句承诺：
//    1. 便宜模型先把话说清楚，再把结果交给主模型
//    2. 转述失败、超时、没端点 —— 一律不挡发消息
//    3. 「模型可见即已记录」在转述模式下依然成立（原文也在）
//    4. 开关关掉就零开销
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

var root = Path.Combine(Path.GetTempPath(), "af-rephrase-verify", Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(root);
Console.WriteLine("═══ 输入转述（澄清模式）验证 ═══");
Console.WriteLine($"工作目录：{root}");

// ── 1. 契约与配置 ──────────────────────────────────────────
Section("1. 契约与配置");

var defaults = new RephraseOptions();
Check("默认启用转述", defaults.Enabled);
Check("默认不做发送前自动澄清", !defaults.AutoBeforeSend);
Check("默认走本地端点", defaults.Model == "local");
Check("默认低温（澄清不是创作）", Math.Abs(defaults.Temperature - 0.2) < 1e-9);
Check("默认超时 8000ms", defaults.TimeoutMs == 8000);
Check("默认 8 字符以下不转述", defaults.MinChars == 8);

Check(
    "内置提示词写明三条铁律",
    RephraseOptions.DefaultSystemPrompt.Contains("只澄清，不扩写")
    && RephraseOptions.DefaultSystemPrompt.Contains("原样保留")
    && RephraseOptions.DefaultSystemPrompt.Contains("原语言与原意"));

var plainPrompt = new RephraseOptions();
Check("无追加指令时提示词就是内置的那份", plainPrompt.BuildSystemPrompt() == plainPrompt.SystemPrompt);

var extraPrompt = new RephraseOptions { ExtraInstructions = "输出尽量用英文术语" };
Check(
    "追加指令被拼进提示词",
    extraPrompt.BuildSystemPrompt().Contains("补充要求")
    && extraPrompt.BuildSystemPrompt().Contains("输出尽量用英文术语"));

var cloned = extraPrompt.Clone();
cloned.SystemPrompt = "被改过了";
Check(
    "Clone 是独立副本",
    cloned.SystemPrompt != extraPrompt.SystemPrompt
    && cloned.ExtraInstructions == extraPrompt.ExtraInstructions);

var skipResult = RephraseResult.Skip("原文", RephraseSkip.TooShort);
Check("跳过结果的 Text 就是原文", skipResult.Text == "原文" && skipResult.Skipped && !skipResult.Rephrased);

var failResult = RephraseResult.Fail("原文", "炸了");
Check("失败结果同样回退原文", failResult.Text == "原文" && failResult.Error == "炸了" && !failResult.Rephrased);

var blankRephrase = new UserMessageEvent { Text = "原文", RephrasedText = "   " };
Check("转述文本为空白时回退原文", blankRephrase.ModelVisibleText == "原文");

// ── 2. 转述器 ──────────────────────────────────────────────
Section("2. 转述器");

const string RawInput = "那个 那个 update 方法 参数 改一下 要 double";
const string Clarified = "把 GameLoop.cs 中 Update 方法的 delta 参数改为 double 类型";

var cheap = new ScriptedLlmClient("local", Clarified);
var rephraser = new LlmUserInputRephraser(name => name == "local" ? cheap : null);

var okResult = await rephraser.RephraseAsync(RawInput, new RephraseOptions());
Check("成功转述并返回模型输出", okResult.Rephrased && okResult.Text == Clarified);
Check("记录使用的模型名", okResult.Model == "local");
Check("记录耗时", okResult.ElapsedMs >= 0, $"{okResult.ElapsedMs}ms");
Check("请求带上了系统提示词", cheap.LastRequest?.SystemPrompt?.Contains("只澄清，不扩写") == true);
Check(
    "请求只有一条 user 消息",
    cheap.LastRequest?.Messages.Count == 1 && cheap.LastRequest.Messages[0].Role == LlmRole.User);
Check("请求内容就是用户输入", cheap.LastRequest?.Messages[0].Content == RawInput);
Check("请求不带任何工具", cheap.LastRequest?.Tools.Count == 0);
Check("请求温度取自配置", Math.Abs((cheap.LastRequest?.Temperature ?? 0) - 0.2) < 1e-9);

cheap.Reset();
var disabled = await rephraser.RephraseAsync(RawInput, new RephraseOptions { Enabled = false });
Check("开关关闭时跳过", disabled.Skipped && disabled.SkipReason == RephraseSkip.Disabled);
Check("★ 开关关闭时零模型调用", cheap.CallCount == 0);

cheap.Reset();
var tooShort = await rephraser.RephraseAsync("继续", new RephraseOptions());
Check("过短输入跳过", tooShort.SkipReason == RephraseSkip.TooShort);
Check("★ 过短输入不调用模型（「继续」不该花一次调用）", cheap.CallCount == 0);

cheap.Reset();
var tooLong = await rephraser.RephraseAsync(new string('长', 2500), new RephraseOptions());
Check("过长输入跳过", tooLong.SkipReason == RephraseSkip.TooLong);
Check("★ 过长输入不调用模型", cheap.CallCount == 0);

var noEndpoint = new LlmUserInputRephraser(_ => null);
var noModel = await noEndpoint.RephraseAsync(RawInput, new RephraseOptions());
Check("无可用端点时跳过", noModel.SkipReason == RephraseSkip.NoModel);
Check("无端点时仍返回原文", noModel.Text == RawInput);

var throwing = new ScriptedLlmClient("local", null, new InvalidOperationException("连接被拒"));
var failed = await new LlmUserInputRephraser(_ => throwing).RephraseAsync(RawInput, new RephraseOptions());
Check("★ 模型抛异常时回退原文", failed.Text == RawInput && !failed.Rephrased);
Check("异常信息被记下来", failed.Error?.Contains("连接被拒") == true);

var slow = new ScriptedLlmClient("local", Clarified, delayMs: 900);
var timedOut = await new LlmUserInputRephraser(_ => slow)
    .RephraseAsync(RawInput, new RephraseOptions { TimeoutMs = 150 });
Check("★ 超时回退原文", timedOut.Text == RawInput && !timedOut.Rephrased);
Check("超时被记为错误", timedOut.Error?.Contains("超时") == true);

async Task<string> Clean(string raw)
{
    var client = new ScriptedLlmClient("local", raw);
    var result = await new LlmUserInputRephraser(_ => client).RephraseAsync(RawInput, new RephraseOptions());
    return result.Text;
}

Check("剥掉代码围栏", await Clean("```\n改好的话\n```") == "改好的话");
Check("剥掉「优化后：」前缀", await Clean("优化后：改好的话") == "改好的话");
Check("剥掉首尾引号", await Clean("\"改好的话\"") == "改好的话");

var blankClient = new ScriptedLlmClient("local", "   ");
var blankResult = await new LlmUserInputRephraser(_ => blankClient)
    .RephraseAsync(RawInput, new RephraseOptions());
Check("空输出视为失败并回退原文", !blankResult.Rephrased && blankResult.Text == RawInput);

// ── 3. 宿主接入：发送前自动澄清 ────────────────────────────
Section("3. 宿主接入（自动澄清）");

var autoRoot = Path.Combine(root, "auto");
var mainModel = new ScriptedLlmClient("main", "收到");
var autoCheap = new ScriptedLlmClient("local", Clarified);

var autoOptions = new HostOptions
{
    WorkspaceRoot = Path.Combine(autoRoot, "ws"),
    SessionsDir = Path.Combine(autoRoot, "sessions"),
    SessionId = "auto",
    LlmOverride = mainModel,
    RephraserOverride = new LlmUserInputRephraser(name => name == "local" ? autoCheap : null),
    Rephrase = new RephraseOptions { Enabled = true, AutoBeforeSend = true },
};

await using (var autoHost = await AgentHost.CreateAsync(autoOptions))
{
    Check("宿主报告转述可用", autoHost.RephraserAvailable);

    await autoHost.SendAsync(RawInput);

    var events = autoHost.Events();
    var userEvent = events.OfType<UserMessageEvent>().Single();

    Check("★ 日志保留用户原话", userEvent.Text == RawInput);
    Check("★ 日志同时记下模型实际看到的文本", userEvent.RephrasedText == Clarified);
    Check("记下转述模型", userEvent.RephraseModel == "local");

    var record = events.OfType<UserInputRephrasedEvent>().Single();
    Check("留痕记录为自动转述", record.Source == "auto" && record.Model == "local");
    Check("留痕标记已送入模型", record.Applied);

    var contextUser = autoHost.RebuildContext().First(m => m.Role == LlmRole.User);
    Check("★ 重建上下文用转述结果（回放与当时一致）", contextUser.Content == Clarified);

    var mainSaw = LastUserText(mainModel.LastRequest);
    Check("★ 主模型收到的也是转述结果", mainSaw == Clarified, mainSaw);

    var rawLog = JsonlEventLog.ReadRawText(autoHost.SessionLogPath);
    Check("落盘日志含 RephrasedText 字段", rawLog.Contains("\"RephrasedText\"", StringComparison.Ordinal));
    Check("落盘日志含留痕事件类型", rawLog.Contains("user-input-rephrased", StringComparison.Ordinal));

    var persisted = JsonlEventLog.Read(autoHost.SessionLogPath).OfType<UserMessageEvent>().Single();
    Check("★ 落盘后原文与转述文本都还在", persisted.Text == RawInput && persisted.RephrasedText == Clarified);
}

// ── 4. 宿主接入：自动澄清关闭 / 失败 ──────────────────────
Section("4. 宿主接入（关闭与失败降级）");

var offRoot = Path.Combine(root, "off");
var offMain = new ScriptedLlmClient("main", "收到");
var offCheap = new ScriptedLlmClient("local", "这段不该被产出");

var offOptions = new HostOptions
{
    WorkspaceRoot = Path.Combine(offRoot, "ws"),
    SessionsDir = Path.Combine(offRoot, "sessions"),
    SessionId = "off",
    LlmOverride = offMain,
    RephraserOverride = new LlmUserInputRephraser(_ => offCheap),
    Rephrase = new RephraseOptions { Enabled = true, AutoBeforeSend = false },
};

await using (var offHost = await AgentHost.CreateAsync(offOptions))
{
    await offHost.SendAsync(RawInput);

    Check("★ 没开自动澄清时转述模型零调用", offCheap.CallCount == 0);
    Check("日志不含转述文本", offHost.Events().OfType<UserMessageEvent>().Single().RephrasedText is null);
    var offSaw = LastUserText(offMain.LastRequest);
    Check("主模型收到原话", offSaw == RawInput, offSaw);
}

var degradeRoot = Path.Combine(root, "degrade");
var degradeMain = new ScriptedLlmClient("main", "收到");
var degradeCheap = new ScriptedLlmClient("local", null, new HttpRequestException("本地端点没起来"));

var degradeOptions = new HostOptions
{
    WorkspaceRoot = Path.Combine(degradeRoot, "ws"),
    SessionsDir = Path.Combine(degradeRoot, "sessions"),
    SessionId = "degrade",
    LlmOverride = degradeMain,
    RephraserOverride = new LlmUserInputRephraser(_ => degradeCheap),
    Rephrase = new RephraseOptions { Enabled = true, AutoBeforeSend = true },
};

await using (var degradeHost = await AgentHost.CreateAsync(degradeOptions))
{
    await degradeHost.SendAsync(RawInput);

    Check("★ 转述失败照样把话发出去", degradeHost.Events().OfType<AssistantMessageEvent>().Any());
    Check("失败时不写转述文本", degradeHost.Events().OfType<UserMessageEvent>().Single().RephrasedText is null);
    Check("失败不写留痕事件", !degradeHost.Events().OfType<UserInputRephrasedEvent>().Any());
    var degradeSaw = LastUserText(degradeMain.LastRequest);
    Check("主模型收到原话", degradeSaw == RawInput, degradeSaw);
}

// ── 5. 宿主接入：手动润色（界面按钮）──────────────────────
Section("5. 宿主接入（手动润色）");

var manualRoot = Path.Combine(root, "manual");
var manualMain = new ScriptedLlmClient("main", "收到");
var manualCheap = new ScriptedLlmClient("local", Clarified);

var manualOptions = new HostOptions
{
    WorkspaceRoot = Path.Combine(manualRoot, "ws"),
    SessionsDir = Path.Combine(manualRoot, "sessions"),
    SessionId = "manual",
    LlmOverride = manualMain,
    RephraserOverride = new LlmUserInputRephraser(_ => manualCheap),
    Rephrase = new RephraseOptions { Enabled = true },
};

await using (var manualHost = await AgentHost.CreateAsync(manualOptions))
{
    var manual = await manualHost.RephraseAsync(RawInput);

    Check("手动转述返回结果", manual.Rephrased && manual.Text == Clarified);

    var events = manualHost.Events();
    var record = events.OfType<UserInputRephrasedEvent>().Single();
    Check("留痕记录为手动润色", record.Source == "manual");
    Check("留痕标记「尚未送入模型」", !record.Applied);
    Check("手动润色不产生 user 消息（还只是草稿）", !events.OfType<UserMessageEvent>().Any());
    Check("手动润色不进模型上下文", manualHost.RebuildContext().Count == 0);
    Check("手动润色没打扰主模型", manualMain.CallCount == 0);

    var state = manualHost.CurrentState();
    Check("投影器忽略留痕事件，不报异常", state.Anomalies.Count == 0);
    Check("留痕不混进聊天消息", state.Messages.Count == 0);

    // 用户随后把润色结果发出去 —— 这才是真正进上下文的那一步
    await manualHost.SendAsync(manual.Text);
    Check("用户发送润色结果后落地为 user 消息", manualHost.Events().OfType<UserMessageEvent>().Any());
    Check("这一条没有重复转述（RephrasedText 为空）",
        manualHost.Events().OfType<UserMessageEvent>().Single().RephrasedText is null);
}

// ── 6. 无端点 ──────────────────────────────────────────────
Section("6. 无端点时的诚实降级");

var bareRoot = Path.Combine(root, "bare");
var bareOptions = new HostOptions
{
    WorkspaceRoot = Path.Combine(bareRoot, "ws"),
    SessionsDir = Path.Combine(bareRoot, "sessions"),
    SessionId = "bare",
    LlmOverride = new ScriptedLlmClient("main", "收到"),
    AllowOfflineDemo = false,
};

await using (var bareHost = await AgentHost.CreateAsync(bareOptions))
{
    Check("没有端点时报告转述不可用", !bareHost.RephraserAvailable);
    var result = await bareHost.RephraseAsync(RawInput);
    Check("手动转述给出可读原因", result.SkipReason == RephraseSkip.NoModel);
    Check("并且仍然回退原文", result.Text == RawInput);
}

// ── 7. 设置持久化 ──────────────────────────────────────────
Section("7. 设置持久化（用户偏好，不进 JSONL）");

var persistRoot = Path.Combine(root, "persist");
var persistSessions = Path.Combine(persistRoot, "sessions");
var settingsFile = Path.Combine(persistSessions, "rephrase.json");

RephraseOptions NewPersistSettings() => new()
{
    Enabled = false,
    AutoBeforeSend = true,
    Model = "cloud",
    MinChars = 12,
    ExtraInstructions = "多说人话",
};

HostOptions NewPersistOptions() => new()
{
    WorkspaceRoot = Path.Combine(persistRoot, "ws"),
    SessionsDir = persistSessions,
    SessionId = "persist",
    LlmOverride = new ScriptedLlmClient("main", "收到"),
    Rephrase = NewPersistSettings(),
};

await using (var persistHost = await AgentHost.CreateAsync(NewPersistOptions()))
{
    Check("首次装配保留调用方给的设置", persistHost.RephraseSettings.MinChars == 12);
    Check("首次装配尚未写文件", !File.Exists(settingsFile));
    persistHost.SaveRephraseSettings();
    Check("★ 保存后写出 rephrase.json", File.Exists(settingsFile));
}

await using (var reloaded = await AgentHost.CreateAsync(NewPersistOptions()))
{
    Check(
        "★ 重新装配后设置被文件读回",
        !reloaded.RephraseSettings.Enabled
        && reloaded.RephraseSettings.AutoBeforeSend
        && reloaded.RephraseSettings.Model == "cloud"
        && reloaded.RephraseSettings.MinChars == 12
        && reloaded.RephraseSettings.ExtraInstructions == "多说人话");
}

await File.WriteAllTextAsync(settingsFile, "{ 这不是合法 json");
await using (var recovered = await AgentHost.CreateAsync(NewPersistOptions()))
{
    Check("★ 配置损坏时回退调用方默认值，不炸", recovered.RephraseSettings.MinChars == 12);
}

// ── 8. HTTP 接口 ───────────────────────────────────────────
Section("8. HTTP 接口与界面");

var webRoot = Path.Combine(root, "web");
var webSessions = Path.Combine(webRoot, "sessions");
var webCheap = new ScriptedLlmClient("local", Clarified);

var webOptions = new HostOptions
{
    WorkspaceRoot = Path.Combine(webRoot, "ws"),
    SessionsDir = webSessions,
    SessionId = "web",
    LlmOverride = new ScriptedLlmClient("main", "收到"),
    RephraserOverride = new LlmUserInputRephraser(name => name == "local" ? webCheap : null),
    Rephrase = new RephraseOptions { Enabled = true },
};

await using var webHost = await AgentHost.CreateAsync(webOptions);
var port = PickFreePort();
using var server = new WebUiServer(webHost, webOptions, port);
using var serverCts = new CancellationTokenSource();
_ = server.RunAsync(serverCts.Token);
await Task.Delay(500);

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

var page = await http.GetStringAsync(server.Url);
Check("★ 页面在发送按钮旁带「✨ 优化」", page.Contains("id=\"optimize\"", StringComparison.Ordinal) && page.Contains("✨ 优化", StringComparison.Ordinal));
Check("按钮说明了它的作用", page.Contains("让转述模型把这段话改得更清楚", StringComparison.Ordinal));
Check("页面带转述设置面板", page.Contains("rp-prompt", StringComparison.Ordinal));
Check("设置面板可改提示词与模型", page.Contains("rp-model", StringComparison.Ordinal) && page.Contains("恢复默认提示词", StringComparison.Ordinal));
Check("页面提供撤销润色", page.Contains("撤销", StringComparison.Ordinal));
Check("复用同一套 /api/rephrase 接口", page.Contains("/api/rephrase/run", StringComparison.Ordinal));

var settingsJson = await http.GetStringAsync(server.Url + "api/rephrase");
using (var doc = JsonDocument.Parse(settingsJson))
{
    var settings = doc.RootElement;
    Check("GET /api/rephrase 报告可用", settings.GetProperty("available").GetBoolean());
    Check("GET /api/rephrase 返回当前开关", settings.GetProperty("enabled").GetBoolean());
    Check("GET /api/rephrase 返回内置默认提示词（供「恢复默认」用）",
        settings.GetProperty("defaultPrompt").GetString()?.Contains("只澄清，不扩写") == true);
    Check("GET /api/rephrase 字段为 camelCase", settings.TryGetProperty("autoBeforeSend", out _));
}

var runResponse = await http.PostAsync(
    server.Url + "api/rephrase/run",
    new StringContent($$"""{"text":"{{RawInput}}"}""", Encoding.UTF8, "application/json"));
using (var doc = JsonDocument.Parse(await runResponse.Content.ReadAsStringAsync()))
{
    var run = doc.RootElement;
    Check("★ POST /api/rephrase/run 返回转述结果", run.GetProperty("rephrased").GetBoolean());
    Check("返回转述后的文本", run.GetProperty("text").GetString() == Clarified);
    Check("返回原话（供撤销）", run.GetProperty("original").GetString() == RawInput);
    Check("返回模型与耗时", run.GetProperty("model").GetString() == "local");
}

var shortRun = await http.PostAsync(
    server.Url + "api/rephrase/run",
    new StringContent("""{"text":"继续"}""", Encoding.UTF8, "application/json"));
using (var doc = JsonDocument.Parse(await shortRun.Content.ReadAsStringAsync()))
{
    Check("过短输入返回跳过原因", doc.RootElement.GetProperty("skipReason").GetString() == RephraseSkip.TooShort);
    Check("跳过时 text 仍是原文", doc.RootElement.GetProperty("text").GetString() == "继续");
}

var emptyRun = await http.PostAsync(
    server.Url + "api/rephrase/run",
    new StringContent("""{"text":""}""", Encoding.UTF8, "application/json"));
Check("空文本返回 400", (int)emptyRun.StatusCode == 400, $"{(int)emptyRun.StatusCode}");

var badSettings = await http.PostAsync(
    server.Url + "api/rephrase/settings",
    new StringContent("{ not json", Encoding.UTF8, "application/json"));
Check("非法 JSON 返回 400", (int)badSettings.StatusCode == 400, $"{(int)badSettings.StatusCode}");

var patchResponse = await http.PostAsync(
    server.Url + "api/rephrase/settings",
    new StringContent("""{"enabled":false,"model":"cloud"}""", Encoding.UTF8, "application/json"));
Check("改设置返回 ok", patchResponse.IsSuccessStatusCode);

using (var doc = JsonDocument.Parse(await http.GetStringAsync(server.Url + "api/rephrase")))
{
    Check("★ 设置改动即时生效（无需重启）",
        !doc.RootElement.GetProperty("enabled").GetBoolean()
        && doc.RootElement.GetProperty("model").GetString() == "cloud");
}

Check("★ 设置改动即时落盘",
    File.Exists(Path.Combine(webSessions, "rephrase.json"))
    && (await File.ReadAllTextAsync(Path.Combine(webSessions, "rephrase.json"))).Contains("\"cloud\"", StringComparison.Ordinal));

var disabledRun = await http.PostAsync(
    server.Url + "api/rephrase/run",
    new StringContent($$"""{"text":"{{RawInput}}"}""", Encoding.UTF8, "application/json"));
using (var doc = JsonDocument.Parse(await disabledRun.Content.ReadAsStringAsync()))
{
    Check("关掉开关后接口返回跳过而非报错",
        !doc.RootElement.GetProperty("rephrased").GetBoolean()
        && doc.RootElement.GetProperty("skipReason").GetString() == RephraseSkip.Disabled);
}

var statusJson = await http.GetStringAsync(server.Url + "api/status");
using (var doc = JsonDocument.Parse(statusJson))
{
    Check("status 带转述状态（界面 pill 用）",
        doc.RootElement.TryGetProperty("rephrase", out var block)
        && block.TryGetProperty("available", out _));
}

// ── 收尾 ───────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine($"═══ 结果：{passes} 通过 / {failures} 失败 ═══");
return failures == 0 ? 0 : 1;

static string? LastUserText(LlmRequest? request)
    => request?.Messages.LastOrDefault(m => m.Role == LlmRole.User)?.Content;

static int PickFreePort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

/// <summary>可脚本化的假模型：能记调用次数与最后一次请求，也能装死、装慢、装坏。</summary>
internal sealed class ScriptedLlmClient(string name, string? reply, Exception? error = null, int delayMs = 0)
    : ILlmClient
{
    public string Name { get; } = name;

    public int CallCount { get; private set; }

    public LlmRequest? LastRequest { get; private set; }

    public void Reset()
    {
        CallCount = 0;
        LastRequest = null;
    }

    public async IAsyncEnumerable<LlmStreamChunk> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        CallCount++;
        LastRequest = request;

        if (delayMs > 0)
        {
            await Task.Delay(delayMs, ct).ConfigureAwait(false);
        }

        if (error is not null)
        {
            throw error;
        }

        if (!string.IsNullOrEmpty(reply))
        {
            yield return new LlmStreamChunk.TextDelta(reply);
        }

        yield return new LlmStreamChunk.Completed("stop");
    }
}
