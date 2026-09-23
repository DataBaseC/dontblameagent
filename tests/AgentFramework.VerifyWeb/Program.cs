using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AgentFramework.Contracts;
using AgentFramework.Data;
using AgentFramework.Host;

// ═══════════════════════════════════════════════════════════
//  Web 对话界面垂直切片验证
//  页面 / 状态 / 历史 / 发送 / SSE 流式 / 工具卡片 / 审批 / 多会话
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

var root = Path.Combine(Path.GetTempPath(), "af-web-verify", Guid.NewGuid().ToString("N")[..8]);
var workspace = Path.Combine(root, "workspace");
var sessions = Path.Combine(root, "sessions");
Directory.CreateDirectory(root);

Console.WriteLine("═══ Web 对话界面垂直切片验证 ═══");
Console.WriteLine($"工作目录：{root}");

// 模型管理要读写配置单 —— 给一个临时文件，别碰主人真的那份
var configPath = Path.Combine(root, "agent.json");

var hostOptions = new HostOptions
{
    WorkspaceRoot = workspace,
    SessionsDir = sessions,
    SessionId = "web",
    ConfigPath = configPath,
    LlmOverride = new StreamingScriptClient(),
};

await using var host = await AgentHost.CreateAsync(hostOptions);

var port = PickFreePort();
using var server = new WebUiServer(host, hostOptions, port);
using var cts = new CancellationTokenSource();
_ = server.RunAsync(cts.Token);
await Task.Delay(500);

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
using var sseHttp = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

// ── 1. 页面与状态 ──────────────────────────────────────────
Console.WriteLine("\n── 1. 页面与状态接口 ──");
var html = await http.GetStringAsync(server.Url);
Check("首页返回对话界面", html.Contains("Agent 对话"));
Check("页面用 SSE 接收流式输出", html.Contains("EventSource('/api/stream')"));
Check("页面能渲染工具卡片", html.Contains("tool-call-requested") && html.Contains("tool-call-completed"));
Check("页面带审批卡片", html.Contains("需要你确认") && html.Contains("/api/approve"));
Check("页面带会话侧边栏", html.Contains("session-list") && html.Contains("/api/sessions"));

var statusJson = await http.GetStringAsync(server.Url + "api/status");
using (var doc = JsonDocument.Parse(statusJson))
{
    var status = doc.RootElement;
    Check("status 返回工具清单", status.GetProperty("tools").GetArrayLength() >= 6,
        $"{status.GetProperty("tools").GetArrayLength()} 个");
    Check("status 返回会话 id", status.GetProperty("sessionId").GetString() == "web");
    Check("status 字段为 camelCase", status.TryGetProperty("offlineDemo", out _));
}

Check("初始历史为空",
    JsonDocument.Parse(await http.GetStringAsync(server.Url + "api/history"))
        .RootElement.GetProperty("events").GetArrayLength() == 0);

// ── 2. 订阅 SSE，遇审批自动放行 ───────────────────────────
Console.WriteLine("\n── 2. 流式对话 + 交互式审批 ──");
var frames = new List<string>();
var autoApproved = 0;
var sseCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

_ = Task.Run(async () =>
{
    try
    {
        using var response = await sseHttp.GetAsync(
            server.Url + "api/stream", HttpCompletionOption.ResponseHeadersRead, sseCts.Token);
        using var stream = await response.Content.ReadAsStreamAsync(sseCts.Token);
        using var reader = new StreamReader(stream);

        while (!sseCts.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(sseCts.Token);
            if (line is null)
            {
                break;
            }

            if (!line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line[6..];
            lock (frames)
            {
                frames.Add(payload);
            }

            if (payload.Contains("\"type\":\"approval\""))
            {
                using var doc = JsonDocument.Parse(payload);
                var id = doc.RootElement.GetProperty("id").GetString();
                await http.PostAsync(
                    server.Url + "api/approve",
                    new StringContent($$"""{"id":"{{id}}","allow":true}""", Encoding.UTF8, "application/json"),
                    sseCts.Token);
                Interlocked.Increment(ref autoApproved);
            }
        }
    }
    catch (OperationCanceledException)
    {
        // 正常收尾
    }
    catch (Exception)
    {
        // 服务停止时忽略
    }
});

await Task.Delay(400);

var sendResponse = await http.PostAsync(
    server.Url + "api/send",
    new StringContent("""{"text":"你好"}""", Encoding.UTF8, "application/json"));
Check("发送接口返回 202", (int)sendResponse.StatusCode == 202, $"{(int)sendResponse.StatusCode}");

await Task.Delay(2500);

string[] captured;
lock (frames)
{
    captured = [.. frames];
}

Check("SSE 推送了文本增量", captured.Any(f => f.Contains("\"type\":\"delta\"")), $"{captured.Length} 帧");
Check("SSE 推送了 user-message", captured.Any(f => f.Contains("user-message")));
Check("SSE 推送了 assistant-message", captured.Any(f => f.Contains("assistant-message")));
Check("SSE 推送了工具调用请求", captured.Any(f => f.Contains("tool-call-requested")));
Check("SSE 推送了工具调用结果", captured.Any(f => f.Contains("tool-call-completed")));
Check("delta 分多次推送（真流式）",
    captured.Count(f => f.Contains("\"type\":\"delta\"")) >= 2,
    $"{captured.Count(f => f.Contains("\"type\":\"delta\""))} 次");

Console.WriteLine("\n── 2b. 审批链路 ──");
Check("★ 写文件前弹了审批请求", captured.Any(f => f.Contains("\"type\":\"approval\"")));
Check("审批请求带工具名", captured.Any(f => f.Contains("\"type\":\"approval\"") && f.Contains("write_file")));
Check("自动放行被受理", autoApproved >= 1, $"{autoApproved} 次");
Check("放行后工具真的执行了", File.Exists(Path.Combine(workspace, "web.md")));
Check("文件内容正确", File.ReadAllText(Path.Combine(workspace, "web.md")) == "来自 Web UI");
Check("审批留痕：未拒绝", host.Approvals.Any(a => a.ToolName == "write_file" && !a.Denied));

var historyJson = await http.GetStringAsync(server.Url + "api/history");
using (var doc = JsonDocument.Parse(historyJson))
{
    var events = doc.RootElement.GetProperty("events");
    Check("历史记录了完整事件链（含每轮用量事件）", events.GetArrayLength() == 7, $"{events.GetArrayLength()} 条");

    var types = new List<string>();
    foreach (var e in events.EnumerateArray())
    {
        types.Add(e.GetProperty("type").GetString() ?? "");
    }

    Check("事件顺序正确",
        types.SequenceEqual([
            "user-message", "model-usage", "assistant-message",
            "tool-call-requested", "tool-call-completed", "model-usage", "assistant-message",
        ]),
        string.Join(" → ", types));
}

// ── 3. 多会话管理 ──────────────────────────────────────────
Console.WriteLine("\n── 3. 多会话管理 ──");
var sessionsJson = await http.GetStringAsync(server.Url + "api/sessions");
using (var doc = JsonDocument.Parse(sessionsJson))
{
    Check("会话列表含当前会话",
        doc.RootElement.GetProperty("sessions").GetArrayLength() == 1,
        $"{doc.RootElement.GetProperty("sessions").GetArrayLength()} 个");
    Check("标记了当前会话", doc.RootElement.GetProperty("current").GetString() == "web");
}

var switchResponse = await http.PostAsync(server.Url + "api/sessions/switch",
    new StringContent("""{"id":"second"}""", Encoding.UTF8, "application/json"));
Check("切换会话成功", (int)switchResponse.StatusCode == 200, $"{(int)switchResponse.StatusCode}");

var statusAfter = await http.GetStringAsync(server.Url + "api/status");
Check("★ 宿主已换到新会话",
    JsonDocument.Parse(statusAfter).RootElement.GetProperty("sessionId").GetString() == "second");

// P1 之后「切换」不再重建宿主：装配只有一份，会话只是它下面的一个运行态。
// 所以这里验的是**宿主没被扔掉** —— 它照样能用，而且两个会话都挂在它名下。
// （老实现会在这里 Dispose 旧宿主，于是「旧宿主是否还被标记退休」才是关键；
//  现在没有 Dispose，那个亚秒级窗口连存在的前提都没了。）
Check("★ 切换会话不再重建宿主（P1）：旧宿主仍可用，且两个会话都在它名下",
    !host.IsRetired && host.OpenSessionIds.Contains("web") && host.OpenSessionIds.Contains("second"),
    $"retired={host.IsRetired}, open=[{string.Join(",", host.OpenSessionIds)}]");

Check("新会话历史为空（会话相互隔离）",
    JsonDocument.Parse(await http.GetStringAsync(server.Url + "api/history"))
        .RootElement.GetProperty("events").GetArrayLength() == 0);

await http.PostAsync(server.Url + "api/send",
    new StringContent("""{"text":"新会话的消息"}""", Encoding.UTF8, "application/json"));
await Task.Delay(1500);

Check("新会话写进自己的日志", File.Exists(Path.Combine(sessions, "second.jsonl")));
Check("旧会话日志未被污染",
    JsonlEventLog.Read(Path.Combine(sessions, "web.jsonl")).Count() == 7,
    $"{JsonlEventLog.Read(Path.Combine(sessions, "web.jsonl")).Count()} 条");

await http.PostAsync(server.Url + "api/sessions/switch",
    new StringContent("""{"id":"web"}""", Encoding.UTF8, "application/json"));

var backCount = JsonDocument.Parse(await http.GetStringAsync(server.Url + "api/history"))
    .RootElement.GetProperty("events").GetArrayLength();
Check("★ 切回原会话，历史完整保留", backCount == 7, $"{backCount} 条");

Check("会话列表变成 2 个",
    JsonDocument.Parse(await http.GetStringAsync(server.Url + "api/sessions"))
        .RootElement.GetProperty("sessions").GetArrayLength() == 2);

Check("拒绝删除当前会话",
    (int)(await http.PostAsync(server.Url + "api/sessions/delete",
        new StringContent("""{"id":"web"}""", Encoding.UTF8, "application/json"))).StatusCode == 400);

// B3 修复：删除非当前会话时，runtime 也一并从会话表移除并释放 ——
// 否则同 id 重建会拿到旧 runtime，内存事件表把已删除的历史“复活”。
int deleteStatus = 0; string deleteBody = "";
using (var delResp = await http.PostAsync(server.Url + "api/sessions/delete",
    new StringContent("""{"id":"second"}""", Encoding.UTF8, "application/json")))
{
    deleteStatus = (int)delResp.StatusCode;
    deleteBody = await delResp.Content.ReadAsStringAsync();
}
Check("★ 删除非当前会话成功（runtime 同步移除）", deleteStatus == 200, $"{deleteStatus} {deleteBody}");
Check("★ 已删会话不在宿主会话表里（B3）",
    !host.OpenSessionIds.Contains("second"),
    $"open=[{string.Join(",", host.OpenSessionIds)}]");

Check("删除后文件消失", !File.Exists(Path.Combine(sessions, "second.jsonl")));

// ── F3/F4/F8：分叉 · 重命名 · 导出 ──
Console.WriteLine("\n── F3/F4/F8：分叉 · 重命名 · 导出 ──");

// F4 重命名（带诊断：rename 失败时显示状态码与响应体）
string renameStatus; string renameBody;
using (var renameResp = await http.PostAsync(server.Url + "api/sessions/rename",
    new StringContent("""{"id":"web","title":"重构报告会话"}""", Encoding.UTF8, "application/json")))
{
    renameStatus = ((int)renameResp.StatusCode).ToString();
    renameBody = await renameResp.Content.ReadAsStringAsync();
}
Check("F4 重命名成功",
    renameStatus == "200"
    && (await http.GetStringAsync(server.Url + "api/sessions")).Contains("重构报告会话"),
    $"{renameStatus} {renameBody}");

// F3 分叉：从 web 会话的 lastSeq 分叉（复用 historyJson 变量，作用域内不再重复声明）
historyJson = await http.GetStringAsync(server.Url + "api/history");
using (var doc = JsonDocument.Parse(historyJson))
{
    var lastSeq = doc.RootElement.GetProperty("lastSeq").GetInt64();
    Check("F3 history 返回 lastSeq", lastSeq > 0, $"{lastSeq}");

    var forkResp = await http.PostAsync(server.Url + "api/sessions/fork",
        new StringContent("{\"id\":\"web\",\"fromSeq\":" + lastSeq + "}", Encoding.UTF8, "application/json"));
    var forkBody = await forkResp.Content.ReadAsStringAsync();
    Check("F3 分叉成功", (int)forkResp.StatusCode == 200 && forkBody.Contains("fork-"), forkBody.Length > 80 ? forkBody[..80] : forkBody);
}

// F8 导出：Markdown 含标题与用户消息
var exported = await http.GetStringAsync(server.Url + "api/sessions/export?id=web");
Check("F8 导出含标题", exported.StartsWith("# "));
Check("F8 导出含用户消息", exported.Contains("## 用户"), exported.Split('\n').Length + " 行");

// ── 4. 边界 ────────────────────────────────────────────────
Console.WriteLine("\n── 4. 边界 ──");
Check("空消息被拒绝（400）",
    (int)(await http.PostAsync(server.Url + "api/send",
        new StringContent("""{"text":"   "}""", Encoding.UTF8, "application/json"))).StatusCode == 400);

Check("未知路径返回 404",
    (int)(await http.GetAsync(server.Url + "api/nothing")).StatusCode == 404);

Check("未知审批 id 返回 404",
    (int)(await http.PostAsync(server.Url + "api/approve",
        new StringContent("""{"id":"nope","allow":true}""", Encoding.UTF8, "application/json"))).StatusCode == 404);

Check("切换不存在的会话视为新建（幂等）",
    (int)(await http.PostAsync(server.Url + "api/sessions/switch",
        new StringContent("""{"id":"brand-new"}""", Encoding.UTF8, "application/json"))).StatusCode == 200);

Check("页面重复访问正常", (await http.GetStringAsync(server.Url)).Contains("Agent 对话"));

// ── 5. 可观测性：用量账目 / 思考留痕 ────────────────────────
Console.WriteLine("\n── 5. 可观测性：模型用量与思考过程 ──");

Check("页面带用量状态栏", html.Contains("pill-usage"));
Check("页面带思考折叠块", html.Contains("thinking-body") && html.Contains("正在思考"));
Check("页面分离了 reasoning 通道", html.Contains("data.type === 'reasoning'"));
// B2：增量帧带会话归属 + 前端分流（后台回合不串台）
Check("★ 增量帧带 sessionId 并按归属分流（B2）",
    html.Contains("sessionId") && html.Contains("markSessionBusy") && html.Contains("currentSessionId"));
Check("页面渲染每轮用量行", html.Contains("addUsageLine"));

using (var doc = JsonDocument.Parse(await http.GetStringAsync(server.Url + "api/status")))
{
    Check("★ status 暴露用量账目（界面状态栏的数据源）",
        doc.RootElement.TryGetProperty("usage", out var usageNode)
        && usageNode.TryGetProperty("totalTokens", out _)
        && usageNode.TryGetProperty("calls", out _));
}

var usageRoot = Path.Combine(root, "usage");
var usageOptions = new HostOptions
{
    WorkspaceRoot = Path.Combine(usageRoot, "ws"),
    SessionsDir = Path.Combine(usageRoot, "sessions"),
    SessionId = "usage",
    LlmOverride = new UsageScriptClient(),
};
Directory.CreateDirectory(usageOptions.WorkspaceRoot);

await using var usageHost = await AgentHost.CreateAsync(usageOptions);

var reasoningSeen = new StringBuilder();
usageHost.ReasoningDelta += (_, text) => reasoningSeen.Append(text);

await usageHost.SendAsync("随便问一句");

var usageEvents = usageHost.Events().OfType<ModelUsageEvent>().ToList();
Check("★ 每轮模型调用都落一条用量事件", usageEvents.Count == 1, $"{usageEvents.Count} 条");

var firstUsage = usageEvents[0];
Check("★ 用量数字来自端点实报（而非估算）",
    firstUsage.InputTokens == 1200 && firstUsage.OutputTokens == 300,
    $"{firstUsage.InputTokens}/{firstUsage.OutputTokens}");
Check("★ 缓存命中被记下来", firstUsage.CachedTokens == 900);
Check("★ 思考 token 单列", firstUsage.ReasoningTokens == 200);
Check("★ 首字延迟与总耗时都有", firstUsage.FirstTokenMs is not null && firstUsage.ElapsedMs >= 0);
Check("★ 标出了实际服务的模型", firstUsage.Model == "fake-usage-model", firstUsage.Model);

var aggregated = usageHost.Usage;
Check("★ 会话汇总（由事件流投影而来）",
    aggregated.Calls == 1 && aggregated.InputTokens == 1200 && aggregated.CachedTokens == 900);
Check("★ 缓存命中率 = 缓存 / 输入",
    aggregated.CacheHitRate is not null && Math.Abs(aggregated.CacheHitRate.Value - 0.75) < 0.0001,
    $"{aggregated.CacheHitRate}");

Check("★ 思考流式推给了界面（边想边出）", reasoningSeen.ToString().Contains("先想想"));
Check("★ 思考落进事件流（刷新 / 重启后还能回看）",
    usageHost.Events().OfType<ReasoningEvent>().Any(r => r.Text.Contains("先想想")));
Check("★ 思考不进模型上下文（它给用户看，不喂回模型）",
    !usageHost.RebuildContext().Any(m => m.Content?.Contains("先想想") == true));

// 端点不报用量时：如实记「未知」，而不是记 0
var silentOptions = new HostOptions
{
    WorkspaceRoot = Path.Combine(usageRoot, "silent-ws"),
    SessionsDir = Path.Combine(usageRoot, "silent-sessions"),
    SessionId = "silent",
    LlmOverride = new SilentUsageClient(),
};
Directory.CreateDirectory(silentOptions.WorkspaceRoot);

await using var silentHost = await AgentHost.CreateAsync(silentOptions);
await silentHost.SendAsync("问一句");

Check("★ 端点不回报用量时调用照常跑", silentHost.Events().OfType<ModelUsageEvent>().Count() == 1);
Check("★ 记成「未知」而不是 0（假 0 会污染账本）", silentHost.Usage.UnknownCalls == 1);
Check("★ 没有输入 token 时命中率为 null，而不是 0%", silentHost.Usage.CacheHitRate is null);

// ── 6. 模型管理：启动之后也能配 ─────────────────────────────
Console.WriteLine("\n── 6. 模型管理 ──");

Check("页面带模型设置面板", html.Contains("model-panel") && html.Contains("/api/models"));

const string Secret = "sk-verify-8f31c2";

bool protectorAvailable;
using (var doc = await GetJson(http, server.Url + "api/models"))
{
    var models = doc.RootElement;
    Check("★ 有了配置单，模型管理就启用", models.GetProperty("enabled").GetBoolean());
    Check("初始没有端点", models.GetProperty("providers").GetArrayLength() == 0);

    protectorAvailable = models.GetProperty("protector").GetProperty("available").GetBoolean();
    Check("如实报告密钥加密能力（不假装加了密）",
        protectorAvailable || models.GetProperty("protector").GetProperty("kind").GetString() is { Length: > 0 },
        models.GetProperty("protector").GetProperty("kind").GetString());
}

// 新增端点（含密钥）
var addBody = $$"""
    {"id":"ollama","name":"本地 Ollama","baseUrl":"http://127.0.0.1:11434/v1",
     "apiKey":"{{Secret}}","models":["qwen2.5:7b","llama3.1:8b"]}
    """;

var addResponse = await http.PostAsync(
    server.Url + "api/models/provider",
    new StringContent(addBody, Encoding.UTF8, "application/json"));
Check("新增端点成功", addResponse.IsSuccessStatusCode, $"{(int)addResponse.StatusCode}");

var addText = await addResponse.Content.ReadAsStringAsync();
Check("★ 接口不回传密钥本身（只告诉「有没有配」）", !addText.Contains(Secret));

using (var doc = JsonDocument.Parse(addText))
{
    var models = doc.RootElement.GetProperty("models");
    var provider = models.GetProperty("providers")[0];

    Check("端点已入列表", models.GetProperty("providers").GetArrayLength() == 1);
    Check("模型列表一起保存", provider.GetProperty("models").GetArrayLength() == 2);
    Check("标出了「有没有配密钥」", provider.GetProperty("hasKey").GetBoolean());
    Check("新端点自动成为当前模型",
        models.GetProperty("active").GetProperty("providerId").GetString() == "ollama");
}

Check("配置已落盘", File.Exists(configPath));

var onDiskText = File.ReadAllText(configPath);
Check("★ 密钥落盘方式与平台能力一致（不是写死一句「必定加密」）",
    protectorAvailable ? !onDiskText.Contains(Secret) : onDiskText.Contains(Secret),
    protectorAvailable ? "已加密" : "无加密能力，如实存明文并在接口里告知");

// 缺参数要被挡下，并说清楚缺什么
var badResponse = await http.PostAsync(
    server.Url + "api/models/provider",
    new StringContent("""{"id":"","baseUrl":"http://x"}""", Encoding.UTF8, "application/json"));
Check("端点 id 为空被拒", (int)badResponse.StatusCode == 400, $"{(int)badResponse.StatusCode}");
Check("拒绝时说清楚原因",
    (await badResponse.Content.ReadAsStringAsync()).Contains("error"));

// 切模型：不重启
using (var doc = await PostJson(http, server.Url + "api/models/active",
    """{"providerId":"ollama","modelId":"llama3.1:8b"}"""))
{
    Check("★ 切换当前模型成功",
        doc.RootElement.GetProperty("ok").GetBoolean());
    Check("当前模型已更新",
        doc.RootElement.GetProperty("models").GetProperty("active")
            .GetProperty("modelId").GetString() == "llama3.1:8b");
}

Check("★ 换模型不需要重启：宿主照常服务",
    (await http.GetStringAsync(server.Url + "api/status")).Contains("tools"));

using (var doc = JsonDocument.Parse(File.ReadAllText(configPath)))
{
    Check("当前模型已落盘（下次启动还认得）",
        doc.RootElement.GetProperty("active").GetProperty("modelId").GetString() == "llama3.1:8b");
}

// 测试连接：分类报错，而不是笼统一句「失败」
using (var doc = await PostJson(http, server.Url + "api/models/probe",
    """{"baseUrl":"http://127.0.0.1:9/v1"}"""))
{
    Check("连不上的端点被如实报失败", !doc.RootElement.GetProperty("ok").GetBoolean());
    Check("★ 失败分了类，主人知道下一步查什么",
        doc.RootElement.GetProperty("kind").GetString() == "network",
        doc.RootElement.GetProperty("kind").GetString());
}

using (var doc = await PostJson(http, server.Url + "api/models/probe", """{"baseUrl":""}"""))
{
    Check("地址为空被挡下并说明原因",
        doc.RootElement.GetProperty("kind").GetString() == "config");
}

// 删除
using (var doc = await PostJson(http, server.Url + "api/models/remove",
    """{"providerId":"ollama"}"""))
{
    var models = doc.RootElement.GetProperty("models");
    Check("删除端点成功", models.GetProperty("providers").GetArrayLength() == 0);
    Check("★ 删掉当前端点后，当前模型不会悬空",
        !models.TryGetProperty("active", out var activeNode)
        || activeNode.ValueKind == JsonValueKind.Null);
}

// 配置单是人手写的：注释、尾逗号、别的字段 —— 界面操作都不该碰掉
var handPath = Path.Combine(root, "handwritten.json");
await File.WriteAllTextAsync(handPath, """
    {
      // 手写的说明注释
      "workspace": "我的工作区",
      "webPort": 8090,
    }
    """);

var handStore = new ModelSettingsStore(handPath, new DpapiSecretProtector());
var handSettings = handStore.Load();
handSettings.Providers.Add(new ProviderConfig
{
    Id = "mine",
    BaseUrl = "http://127.0.0.1:11434/v1",
    Models = [new ModelEntry { Id = "qwen2.5:7b" }],
});
handStore.Save(handSettings);

using (var doc = JsonDocument.Parse(
    await File.ReadAllTextAsync(handPath),
    new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }))
{
    Check("★ 带注释的手写配置单被当成「能读」，而不是「坏文件」",
        doc.RootElement.GetProperty("workspace").GetString() == "我的工作区");
    Check("★ 改模型不碰别的字段（端口还在）",
        doc.RootElement.GetProperty("webPort").GetInt32() == 8090);
}

Check("改完的端点能读回来", handStore.Load().FindProvider("mine") is not null);
Check("★ 改动前留了 .bak（手写的东西不该被不可逆地改）", File.Exists(handPath + ".bak"));
Check("备份里是最早那份（还带着注释）",
    File.ReadAllText(handPath + ".bak").Contains("// 手写的说明注释"));

// ── 9. 可注册路由（加端点不必改主分发）────────────────────
Console.WriteLine("\n── 9. 可注册路由 ──");

var toolsJson = await GetJson(http, server.Url + "api/tools");
var toolList = toolsJson.RootElement.GetProperty("tools");
Check("注册路由 /api/tools 可用（绕开主分发）",
    toolList.GetArrayLength() > 0,
    $"{toolList.GetArrayLength()} 个工具");

Check("工具清单带来源（谁挂的一眼看得出）",
    toolList.EnumerateArray().Any(t =>
        t.GetProperty("name").GetString() == "read_file"
        && (t.TryGetProperty("source", out var s) ? s.GetString() ?? "" : "").Contains("official")),
    string.Join(" | ", toolList.EnumerateArray().Take(2).Select(t => t.GetRawText())));

Check("注册自定义路由", server.Map(new ProbeRoute()));
Check("重复注册同一路径被拒（冲突当场发现，不拖到运行期）", !server.Map(new ProbeRoute()));

var probeResponse = await http.GetStringAsync(server.Url + "api/verify-probe");
Check("自定义路由真的被接住（加端点不必改主分发）",
    probeResponse.Contains("probe-ok"),
    probeResponse);

// ── 10. 边界与防护 ─────────────────────────────────────────
Console.WriteLine("\n── 10. 边界与防护 ──");

// S1：跨站 Origin 的 POST 必须被拒（否则任意网页都能 fetch localhost 替你干活）
using (var evilRequest = new HttpRequestMessage(HttpMethod.Post, server.Url + "api/send"))
{
    evilRequest.Headers.Add("Origin", "http://evil.example");
    evilRequest.Content = new StringContent("""{"text":"hi"}""", Encoding.UTF8, "application/json");
    using var evilResponse = await http.SendAsync(evilRequest);
    Check("★ 跨站 Origin 的 POST 被拒（P1-S1）",
        (int)evilResponse.StatusCode == 403,
        $"{(int)evilResponse.StatusCode}");
}

using (var sameOrigin = new HttpRequestMessage(HttpMethod.Post, server.Url + "api/send"))
{
    sameOrigin.Headers.Add("Origin", $"http://localhost:{port}");
    sameOrigin.Content = new StringContent("""{"text":"同源请求"}""", Encoding.UTF8, "application/json");
    using var sameResponse = await http.SendAsync(sameOrigin);
    Check("同源 Origin 正常放行", (int)sameResponse.StatusCode == 202, $"{(int)sameResponse.StatusCode}");
}

// S2：会话 id 路径穿越（switch / delete 两处都拿 id 拼路径）
using (var escapeResponse = await http.PostAsync(
    server.Url + "api/sessions/switch",
    new StringContent("""{"id":"..\\..\\evil"}""", Encoding.UTF8, "application/json")))
{
    Check("★ 路径穿越的会话 id 被拒（switch，P1-S2）",
        (int)escapeResponse.StatusCode == 400,
        $"{(int)escapeResponse.StatusCode}");
}

using (var escapeDelete = await http.PostAsync(
    server.Url + "api/sessions/delete",
    new StringContent("""{"id":"..\\..\\evil"}""", Encoding.UTF8, "application/json")))
{
    Check("★ 路径穿越的会话 id 被拒（delete，P1-S2）",
        (int)escapeDelete.StatusCode == 400,
        $"{(int)escapeDelete.StatusCode}");
}

// ── 10.5 会话创建选模式 + 插件端点（本批新增）──────────────
Console.WriteLine("\n── 10.5 会话创建选模式 + 插件端点 ──");

var modesJson = await http.GetStringAsync(server.Url + "api/modes");
Check("★ /api/modes 列出内置三档", modesJson.Contains("\"work\"") && modesJson.Contains("\"design\"") && modesJson.Contains("\"chat\""));

var newSessResp = await http.PostAsync(server.Url + "api/sessions/new",
    new StringContent("""{"mode":"chat"}""", Encoding.UTF8, "application/json"));
var newSessBody = await newSessResp.Content.ReadAsStringAsync();
Check("★ 新建会话时选定模式（chat）", (int)newSessResp.StatusCode == 200 && newSessBody.Contains("\"current\":\"chat\""), (newSessBody.Length > 80 ? newSessBody[..80] : newSessBody));

var chatStatus = await http.GetStringAsync(server.Url + "api/status");
using (var doc = JsonDocument.Parse(chatStatus))
{
    Check("★ /api/status 反映当前会话的模式", doc.RootElement.GetProperty("mode").GetProperty("current").GetString() == "chat");
    Check("★ chat 会话不暴露工具", doc.RootElement.GetProperty("mode").GetProperty("exposesTools").GetBoolean() == false);
}

var badMode = await http.PostAsync(server.Url + "api/sessions/new",
    new StringContent("""{"mode":"tavern"}""", Encoding.UTF8, "application/json"));
Check("未注册的自定义模式被 400 拒绝（插件注册后才可用）", (int)badMode.StatusCode == 400);

var pluginsJson = await http.GetStringAsync(server.Url + "api/plugins");
Check("★ /api/plugins 可用（测试环境无插件 → 空列表）", pluginsJson.Contains("\"plugins\":[]"));
var panel404 = await http.GetAsync(server.Url + "plugin-panel?id=nonexistent");
Check("无面板的插件 404", (int)panel404.StatusCode == 404);

// 切回主会话，别影响后面的用例
await http.PostAsync(server.Url + "api/sessions/switch",
    new StringContent("""{"id":"web"}""", Encoding.UTF8, "application/json"));

// ── 10.8 记忆面板 API（本批新增）───────────────────────────
Console.WriteLine("\n── 10.8 记忆面板 API ──");
var memList = await http.GetStringAsync(server.Url + "api/memory/list");
Check("★ /api/memory/list 返回双 scope 双层", memList.Contains("\"global\"") && memList.Contains("\"project\"") && memList.Contains("archived"));

var memArchive = await http.PostAsync(server.Url + "api/memory/action",
    new StringContent("""{"action":"archive","scope":"project","id":"not-exist"}""", Encoding.UTF8, "application/json"));
Check("归档不存在的记忆 → 失败但不炸", memArchive.IsSuccessStatusCode || (int)memArchive.StatusCode == 400);

var memSweep = await http.PostAsync(server.Url + "api/memory/sweep",
    new StringContent("""{"days":30,"maxScore":1,"dryRun":true}""", Encoding.UTF8, "application/json"));
var sweepBody = await memSweep.Content.ReadAsStringAsync();
Check("★ 清扫预览可用（dryRun 不写盘）", (int)memSweep.StatusCode == 200 && sweepBody.Contains("dryRun"));

var memBadAction = await http.PostAsync(server.Url + "api/memory/action",
    new StringContent("""{"action":"explode","scope":"project","id":"x"}""", Encoding.UTF8, "application/json"));
Check("未知 action → 400", (int)memBadAction.StatusCode == 400);

// v3.5 审查 P2：项目 scope 必须**精确等于本会话的项目作用域** ——
// 只查 "project:" 前缀的话，前端传 project:../../x 就能把记忆写到工作区之外。
var memBadScope = await http.PostAsync(server.Url + "api/memory/action",
    new StringContent("""{"action":"archive","scope":"project:../../evil","id":"x"}""", Encoding.UTF8, "application/json"));
var memBadScopeBody = await memBadScope.Content.ReadAsStringAsync();
// 断言必须能区分「按 scope 拒」与「记忆不存在」—— 后者旧实现也会给 400，那样测不出修复。
Check("★ 越界的 project scope 在进存储前就被 400 拒绝（按 scope 校验拒的）",
    (int)memBadScope.StatusCode == 400 && memBadScopeBody.Contains("scope"),
    $"{(int)memBadScope.StatusCode} {memBadScopeBody}");

// ── 11. 创意工坊：技能导入（本批新增）──────────────────────
Console.WriteLine("\n── 11. 创意工坊：技能导入 ──");
var skillsDir = Path.Combine(workspace, "skills");
var workshopDir = Path.Combine(workspace, "workshop");
Directory.CreateDirectory(Path.Combine(skillsDir, "note-taker"));
await File.WriteAllTextAsync(Path.Combine(skillsDir, "note-taker", "skill.json"),
    """{"name":"note-taker","description":"内置示例：速记","tools":["read_file"],"version":"1.0.0","author":"local"}""");

var importSrc = Path.Combine(root, "import-src", "web-digest");
Directory.CreateDirectory(importSrc);
await File.WriteAllTextAsync(Path.Combine(importSrc, "skill.json"),
    """{"name":"web-digest","description":"工坊示例：网页摘要","tools":["read_file","web_fetch"],"version":"0.9.0","author":"someone","tags":["联网","摘要"]}""");
await File.WriteAllTextAsync(Path.Combine(importSrc, "README.md"), "分享包里的附属文件也要完整拷贝");

var importResp = await http.PostAsync(server.Url + "api/skills/import",
    new StringContent(JsonSerializer.Serialize(new { path = importSrc }), Encoding.UTF8, "application/json"));
var importBody = await importResp.Content.ReadAsStringAsync();
Check("★ 工坊技能导入成功", (int)importResp.StatusCode == 200 && importBody.Contains("web-digest"), importBody.Length > 60 ? importBody[..60].Replace("\n", "") : importBody.Replace("\n", ""));
Check("★ 导入物落在 workshop/ 且文件完整",
    File.Exists(Path.Combine(workshopDir, "web-digest", "skill.json"))
    && File.Exists(Path.Combine(workshopDir, "web-digest", "README.md")));

var skillsJson2 = await http.GetStringAsync(server.Url + "api/skills");
Check("★ 技能列表带出处（builtin + workshop）",
    skillsJson2.Contains("note-taker") && skillsJson2.Contains("web-digest")
    && skillsJson2.Contains("workshop") && skillsJson2.Contains("builtin"));
Check("清单元数据透传（version/author/tags）",
    skillsJson2.Contains("0.9.0") && skillsJson2.Contains("someone") && skillsJson2.Contains("摘要"));

var toggleWs = await http.PostAsync(server.Url + "api/skills/toggle",
    new StringContent("""{"name":"web-digest","enabled":true}""", Encoding.UTF8, "application/json"));
Check("工坊技能可启停", (int)toggleWs.StatusCode == 200);

// 导入重名内置 → 409（工坊不覆盖本地创作）
var builtinConflict = Path.Combine(root, "import-conflict");
Directory.CreateDirectory(builtinConflict);
await File.WriteAllTextAsync(Path.Combine(builtinConflict, "skill.json"),
    """{"name":"note-taker","description":"想顶掉内置的工坊包"}""");
var conflictResp = await http.PostAsync(server.Url + "api/skills/import",
    new StringContent(JsonSerializer.Serialize(new { path = builtinConflict }), Encoding.UTF8, "application/json"));
Check("★ 与内置重名的导入被 409 拒绝（不静默覆盖）", (int)conflictResp.StatusCode == 409);

// v3.5 审查 P2：与**已导入的** workshop 技能重名同样要拒绝 ——
// 原实现会 Directory.Delete 后整体替换，而 enabled 按名保留 → 新内容下一轮静默生效。
var wsConflict = Path.Combine(root, "import-ws-conflict");
Directory.CreateDirectory(wsConflict);
await File.WriteAllTextAsync(Path.Combine(wsConflict, "skill.json"),
    """{"name":"web-digest","description":"想顶掉工坊里已有的同名技能"}""");
var wsConflictResp = await http.PostAsync(server.Url + "api/skills/import",
    new StringContent(JsonSerializer.Serialize(new { path = wsConflict }), Encoding.UTF8, "application/json"));
Check("★ 与已导入的工坊技能重名被 409 拒绝（不静默覆盖）", (int)wsConflictResp.StatusCode == 409, $"{(int)wsConflictResp.StatusCode}");

// 引用未注册工具 → 400（白名单安全模型：工坊包无法凭空造工具）
var badTools = Path.Combine(root, "import-badtools");
Directory.CreateDirectory(badTools);
await File.WriteAllTextAsync(Path.Combine(badTools, "skill.json"),
    """{"name":"evil-fmt","description":"引用不存在的工具","tools":["format_disk"]}""");
var badToolsResp = await http.PostAsync(server.Url + "api/skills/import",
    new StringContent(JsonSerializer.Serialize(new { path = badTools }), Encoding.UTF8, "application/json"));
Check("★ 引用未注册工具的导入被 400 拒绝", (int)badToolsResp.StatusCode == 400);

// 缺 skill.json / 目录不存在 → 400
var noManifest = await http.PostAsync(server.Url + "api/skills/import",
    new StringContent(JsonSerializer.Serialize(new { path = Path.Combine(root, "nowhere") }), Encoding.UTF8, "application/json"));
Check("目录不存在导入被 400 拒绝", (int)noManifest.StatusCode == 400);


// P0-3：回合进行中切会话，不许崩（200 = 切成，409 = 还在跑，都不能是 500）
using (var busySwitch = await http.PostAsync(
    server.Url + "api/sessions/switch",
    new StringContent("""{"id":"switch-during-turn"}""", Encoding.UTF8, "application/json")))
{
    Check("★ 回合进行中切会话不崩（P0-3）",
        (int)busySwitch.StatusCode is 200 or 409,
        $"{(int)busySwitch.StatusCode}");
}

// F3：挂一个不读数据的 SSE 客户端，请求不该被它拖住
using (var lazyHttp = new HttpClient { Timeout = Timeout.InfiniteTimeSpan })
{
    using var lazyStream = await lazyHttp.GetAsync(
        server.Url + "api/stream", HttpCompletionOption.ResponseHeadersRead);

    var sw = System.Diagnostics.Stopwatch.StartNew();
    using var sendUnderSlowClient = await http.PostAsync(
        server.Url + "api/send",
        new StringContent("""{"text":"慢客户端在也不该卡"}""", Encoding.UTF8, "application/json"));
    sw.Stop();

    Check("★ 不读数据的 SSE 客户端不拖住请求（P1-F3）",
        (int)sendUnderSlowClient.StatusCode == 202 && sw.ElapsedMilliseconds < 3000,
        $"{(int)sendUnderSlowClient.StatusCode} / {sw.ElapsedMilliseconds}ms");
}

sseCts.Cancel();
cts.Cancel();
server.Stop();

Console.WriteLine($"\n═══ 结果：{passes} 通过 / {failures} 失败 ═══");

static async Task<JsonDocument> GetJson(HttpClient client, string url)
    => JsonDocument.Parse(await client.GetStringAsync(url));

static async Task<JsonDocument> PostJson(HttpClient client, string url, string body)
    => JsonDocument.Parse(await (await client.PostAsync(
        url, new StringContent(body, Encoding.UTF8, "application/json")))
        .Content.ReadAsStringAsync());
return failures == 0 ? 0 : 1;

// ═══════════════════════════ 辅助 ═══════════════════════════

static int PickFreePort()
{
    var probe = new TcpListener(IPAddress.Loopback, 0);
    probe.Start();
    var port = ((IPEndPoint)probe.LocalEndpoint).Port;
    probe.Stop();
    return port;
}

/// <summary>先吐两段文本、再调工具、最后收尾 —— 用来验证流式与工具卡片。</summary>
internal sealed class StreamingScriptClient : ILlmClient
{
    public string Name => "streaming-script";

    public async IAsyncEnumerable<LlmStreamChunk> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();

        var usedTool = request.Messages.Any(m => m.Role == LlmRole.Tool);

        if (!usedTool)
        {
            yield return new LlmStreamChunk.TextDelta("我先");
            yield return new LlmStreamChunk.TextDelta("写个文件。");
            yield return new LlmStreamChunk.ToolCallsReady(
            [
                new ToolCallRequest("w1", "write_file", """{"path":"web.md","content":"来自 Web UI"}"""),
            ]);
            yield return new LlmStreamChunk.Completed("tool_calls");
            yield break;
        }

        yield return new LlmStreamChunk.TextDelta("写好了。");
        yield return new LlmStreamChunk.Completed("stop");
    }
}

/// <summary>
/// 假装一个「会思考、会报用量」的端点 —— 用来验证可观测链路。
/// DeepSeek 一类的模型就是这个形状：先 reasoning_content，再 content，最后带 usage。
/// </summary>
internal sealed class UsageScriptClient : ILlmClient
{
    public string Name => "usage-endpoint";

    public async IAsyncEnumerable<LlmStreamChunk> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();

        yield return new LlmStreamChunk.ReasoningDelta("先想想这个问题");
        yield return new LlmStreamChunk.ReasoningDelta("…嗯，这样就行。");
        yield return new LlmStreamChunk.TextDelta("好的，我来说说。");
        yield return new LlmStreamChunk.UsageReady(
            new LlmUsage
            {
                InputTokens = 1200,
                OutputTokens = 300,
                CachedTokens = 900,
                ReasoningTokens = 200,
            },
            "fake-usage-model");
        yield return new LlmStreamChunk.Completed("stop");
    }
}

/// <summary>一个不报用量的端点 —— 验证「未知」不会被记成 0。</summary>
internal sealed class SilentUsageClient : ILlmClient
{
    public string Name => "silent-endpoint";

    public async IAsyncEnumerable<LlmStreamChunk> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();

        yield return new LlmStreamChunk.TextDelta("收到。");
        yield return new LlmStreamChunk.Completed("stop");
    }
}

/// <summary>自定义路由桩：验证「加端点 = 加一个类 + 注册一行」，不必改主分发。</summary>
internal sealed class ProbeRoute : IWebRoute
{
    public string Method => "GET";

    public string Path => "/api/verify-probe";

    public ValueTask HandleAsync(WebUiRequest request, CancellationToken ct)
    {
        request.Text("probe-ok");
        return ValueTask.CompletedTask;
    }
}
