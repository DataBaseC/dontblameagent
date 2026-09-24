using System.Net;
using System.Text;
using System.Text.Json;
using AgentFramework.Contracts;
using AgentFramework.Llm;
using AgentFramework.Tools;

// ═══════════════════════════════════════════════════════════
//  模型接入与搜索后端 垂直切片验证
//  思考参数注入（openai / qwen 风格）· Bearer 头 · HTML 搜索解析 · 降级链
//  全部离线可跑：HTTP 用本地监听器，HTML 解析用固定样本。
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

var root = Path.Combine(Path.GetTempPath(), "af-llm-verify", Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(root);

Console.WriteLine("═══ 模型接入与搜索后端验证 ═══");
Console.WriteLine($"工作目录：{root}");

// ── 1. 思考参数注入（openai 风格）──────────────────────────
Console.WriteLine("\n── 1. 思考参数注入（OpenAI 风格）──");

string? capturedPayload = null;
string? capturedAuth = null;
using var listener = new HttpListener();
var freePort = PickFreePort();
listener.Prefixes.Add($"http://127.0.0.1:{freePort}/");
listener.Start();
var serverTask = Task.Run(async () =>
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

        if (ctx.Request.Url?.AbsolutePath == "/v1/chat/completions")
        {
            using var reader = new StreamReader(ctx.Request.InputStream);
            capturedPayload = await reader.ReadToEndAsync();
            capturedAuth = ctx.Request.Headers["Authorization"];

            ctx.Response.ContentType = "text/event-stream";
            var stream = ctx.Response.OutputStream;
            // 工具名重发场景：同名在两帧里各给一次完整 function.name
            var frame = capturedPayload?.Contains("tool-name-test") == true
                ? """
                data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"c1","function":{"name":"read_file","arguments":"{\"path\"" }}]},"finish_reason":null}]}

                data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"name":"read_file","arguments":":\"a.txt\"}"}}]},"finish_reason":null}]}

                data: {"choices":[{"delta":{},"finish_reason":"tool_calls"}]}

                data: [DONE]

                """
                : """
                data: {"choices":[{"delta":{"content":"好"},"finish_reason":null}]}

                data: [DONE]

                """;
            var bytes = Encoding.UTF8.GetBytes(frame);
            await stream.WriteAsync(bytes);
            await stream.FlushAsync();
            stream.Close();
        }
        else
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
        }
    }
});

// openai 风格 + 端点默认档位：报文顶层要有 reasoning_effort
var openaiClient = new OpenAiCompatibleClient("test", new OpenAiCompatibleOptions
{
    BaseUrl = $"http://127.0.0.1:{freePort}/v1",
    ApiKey = "test-key-123",
    DefaultModel = "test-model",
    ReasoningStyle = ReasoningStyles.OpenAi,
    ReasoningEffort = "medium",
});

await DrainAsync(openaiClient, new LlmRequest { Model = "auto", Messages = [new LlmMessage { Role = LlmRole.User, Content = "hi" }] });

Check("openai 风格注入 reasoning_effort", capturedPayload?.Contains("\"reasoning_effort\":\"medium\"") == true, "报文含 reasoning_effort");
Check("Bearer 头来自配置值", capturedAuth == "Bearer test-key-123");

// 请求显式档位盖过端点默认
capturedPayload = null;
await DrainAsync(openaiClient, new LlmRequest
{
    Model = "auto",
    Messages = [new LlmMessage { Role = LlmRole.User, Content = "hi" }],
    ReasoningEffort = "high",
});
Check("请求档位盖过端点默认", capturedPayload?.Contains("\"reasoning_effort\":\"high\"") == true);

// 风格 none + 未配默认：不注入
var plainClient = new OpenAiCompatibleClient("plain", new OpenAiCompatibleOptions
{
    BaseUrl = $"http://127.0.0.1:{freePort}/v1",
    DefaultModel = "test-model",
});
capturedPayload = null;
await DrainAsync(plainClient, new LlmRequest { Model = "auto", Messages = [new LlmMessage { Role = LlmRole.User, Content = "hi" }] });
Check("未配置时不注入思考参数", capturedPayload?.Contains("reasoning_effort") != true);

// ── 2. 思考参数注入（qwen 风格）────────────────────────────
Console.WriteLine("\n── 2. 思考参数注入（Qwen 风格）──");

var qwenClient = new OpenAiCompatibleClient("qwen", new OpenAiCompatibleOptions
{
    BaseUrl = $"http://127.0.0.1:{freePort}/v1",
    DefaultModel = "qwen3",
    ReasoningStyle = ReasoningStyles.Qwen,
    ReasoningEffort = "off",
});

capturedPayload = null;
await DrainAsync(qwenClient, new LlmRequest
{
    Model = "auto",
    Messages =
    [
        new LlmMessage { Role = LlmRole.System, Content = "sys" },
        new LlmMessage { Role = LlmRole.User, Content = "hi" },
    ],
});
// 注意：JsonNode 序列化会把 < 转义成 \u003C，断言前先解码回原文
var decodedOff = capturedPayload is null ? null : System.Text.RegularExpressions.Regex.Unescape(capturedPayload);
Check("qwen off 显式关闭", decodedOff?.Contains("<enable_thinking>false</enable_thinking>") == true, "末条 user 消息带关闭标记");
Check("qwen 风格不写顶层参数", decodedOff?.Contains("reasoning_effort") != true);

var qwenHigh = new OpenAiCompatibleClient("qwen-high", new OpenAiCompatibleOptions
{
    BaseUrl = $"http://127.0.0.1:{freePort}/v1",
    DefaultModel = "qwen3",
    ReasoningStyle = ReasoningStyles.Qwen,
    ReasoningEffort = "high",
});
capturedPayload = null;
await DrainAsync(qwenHigh, new LlmRequest { Model = "auto", Messages = [new LlmMessage { Role = LlmRole.User, Content = "hi" }] });
var decodedHigh = capturedPayload is null ? null : System.Text.RegularExpressions.Regex.Unescape(capturedPayload);
Check("qwen high 带预算", decodedHigh?.Contains("<enable_thinking>true</enable_thinking>") == true
    && decodedHigh?.Contains("<thinking_budget>16384</thinking_budget>") == true);

// ── 2.5 system 必须只在最前（Qwen/vLLM Jinja 硬性要求）────────
Console.WriteLine("\n── 2.5 system 位置与工具名累积 ──");

capturedPayload = null;
await DrainAsync(plainClient, new LlmRequest
{
    Model = "auto",
    SystemPrompt = "主系统提示",
    Messages =
    [
        new LlmMessage { Role = LlmRole.User, Content = "第一句" },
        new LlmMessage { Role = LlmRole.System, Content = "任务卡旧写法" },
        new LlmMessage { Role = LlmRole.Assistant, Content = "好" },
        new LlmMessage { Role = LlmRole.System, Content = "笔记旧写法" },
    ],
});

var sysCheck = capturedPayload is null ? null : JsonDocument.Parse(capturedPayload);
var msgArr = sysCheck?.RootElement.GetProperty("messages");
var systemIndexes = new List<int>();
if (msgArr is not null)
{
    for (var i = 0; i < msgArr.Value.GetArrayLength(); i++)
    {
        if (msgArr.Value[i].TryGetProperty("role", out var r) && r.GetString() == "system")
        {
            systemIndexes.Add(i);
        }
    }
}

Check("★ 全部 system 合并到一条且在最前（本地 Jinja 不再 500）",
    systemIndexes.Count == 1 && systemIndexes[0] == 0,
    systemIndexes.Count == 0 ? "无 system" : string.Join(",", systemIndexes));
Check("合并后的 system 含全部来源",
    msgArr is not null
    && msgArr.Value[0].TryGetProperty("content", out var sysContent)
    && sysContent.GetString()?.Contains("主系统提示") == true
    && sysContent.GetString()?.Contains("任务卡旧写法") == true
    && sysContent.GetString()?.Contains("笔记旧写法") == true);
Check("消息流里不再残留 system",
    msgArr is not null && systemIndexes.Count == 1);

// 工具名每帧重发全名时不得拼成 read_fileread_file
List<ToolCallRequest>? capturedCalls = null;
var toolNameClient = new OpenAiCompatibleClient("tool-name", new OpenAiCompatibleOptions
{
    BaseUrl = $"http://127.0.0.1:{freePort}/v1",
    DefaultModel = "test-model",
});
await foreach (var chunk in toolNameClient.StreamAsync(new LlmRequest
{
    Model = "auto",
    Messages = [new LlmMessage { Role = LlmRole.User, Content = "tool-name-test" }],
}, CancellationToken.None).ConfigureAwait(false))
{
    if (chunk is LlmStreamChunk.ToolCallsReady ready)
    {
        capturedCalls = ready.Calls.ToList();
    }
}

Check("★ 工具名全名重发不拼重（否则永远「工具不存在」）",
    capturedCalls is { Count: 1 } && capturedCalls[0].ToolName == "read_file",
    capturedCalls is null ? "(无调用)" : string.Join(",", capturedCalls.Select(c => c.ToolName)));
Check("工具参数跨帧拼完整",
    capturedCalls is { Count: 1 } && capturedCalls[0].ArgumentsJson.Contains("a.txt"),
    capturedCalls is { Count: 1 } ? capturedCalls[0].ArgumentsJson : "");

listener.Stop();

// ── 3. HTML 搜索解析 ────────────────────────────────────────
Console.WriteLine("\n── 3. HTML 搜索解析（离线样本）──");

const string bingHtml = """
    <html><body>
    <li class="b_algo"><h2><a href="https://example.com/a">第一 <b>条</b>结果</a></h2><div><p class="b_lineclamp">摘要一 &amp; 说明</p></div></li>
    <li class="b_algo"><h2><a href="/search?q=x">本站跳转</a></h2><p>摘要二</p></li>
    <li class="b_algo"><h2><a href="javascript:void(0)">无效链接</a></h2><p>x</p></li>
    </body></html>
    """;

var bingHits = BingSearchProvider.Parse(bingHtml, 8);
Check("Bing 解析出 2 条有效结果", bingHits.Count == 2, $"{bingHits.Count} 条");
Check("Bing 标题剥掉了标签", bingHits.Count > 0 && bingHits[0].Title == "第一 条 结果", bingHits.FirstOrDefault()?.Title);
Check("Bing 摘要解码了实体", bingHits.Count > 0 && bingHits[0].Snippet == "摘要一 & 说明", bingHits.FirstOrDefault()?.Snippet);
Check("Bing 相对链接补全", bingHits.Count > 1 && bingHits[1].Url == "https://cn.bing.com/search?q=x", bingHits.Skip(1).FirstOrDefault()?.Url);

const string baiduHtml = """
    <html><body>
    <div class="result c-container"><h3 class="t"><a href="http://www.example.com/bai1">百度第一条</a></h3><span class="content-right_8Zs40">百度摘要内容</span></div>
    <div class="result c-container"><h3 class="t"><a href="https://www.example.com/bai2">百度第二条</a></h3></div>
    </body></html>
    """;

var baiduHits = BaiduSearchProvider.Parse(baiduHtml, 8);
Check("百度解析出 2 条结果", baiduHits.Count == 2, $"{baiduHits.Count} 条");
Check("百度摘要来自内容容器", baiduHits.Count > 0 && baiduHits[0].Snippet == "百度摘要内容", baiduHits.FirstOrDefault()?.Snippet);
Check("百度第二条没有摘要也不炸", baiduHits.Count > 1 && baiduHits[1].Snippet is null);

// ── 4. 降级链留痕 ───────────────────────────────────────────
Console.WriteLine("\n── 4. 搜索降级链 ──");

var first = new ScriptedSearch("first", []);
var second = new ScriptedSearch("second", [new SearchHit("https://example.com/s", "结果", "摘要", null)]);
var chain = new FallbackSearchProvider(first, second);

var hits = await chain.SearchAsync("q", 5, CancellationToken.None);
Check("前一个空结果时降级到下一个", hits.Count == 1 && hits[0].Url == "https://example.com/s");
Check("降级过程有留痕", chain.AttemptLog.Contains("first: ok(0)") && chain.AttemptLog.Contains("second: ok(1)"),
    string.Join(" | ", chain.AttemptLog));

var broken = new ScriptedSearch("broken", null!, throwOnCall: true);
var chain2 = new FallbackSearchProvider(broken, second);
var hits2 = await chain2.SearchAsync("q", 5, CancellationToken.None);
Check("异常后端被跳过", hits2.Count == 1);
Check("异常被记进尝试日志", chain2.AttemptLog.Any(l => l.StartsWith("broken: ")), string.Join(" | ", chain2.AttemptLog));

// ── 收尾 ───────────────────────────────────────────────────
Console.WriteLine($"\n═══ 结果：{passes} 通过 / {failures} 失败 ═══");
try { Directory.Delete(root, recursive: true); } catch { }
return failures == 0 ? 0 : 1;

static int PickFreePort()
{
    var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
    probe.Start();
    var port = ((IPEndPoint)probe.LocalEndpoint).Port;
    probe.Stop();
    return port;
}

static async Task DrainAsync(ILlmClient client, LlmRequest request)
{
    await foreach (var _ in client.StreamAsync(request, CancellationToken.None).ConfigureAwait(false))
    {
        // 消费完整流即可
    }
}

/// <summary>脚本化搜索后端：返回预置结果或抛异常。</summary>
internal sealed class ScriptedSearch(string name, IReadOnlyList<SearchHit>? results, bool throwOnCall = false) : ISearchProvider
{
    public string Name => name;

    public ValueTask<IReadOnlyList<SearchHit>> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        if (throwOnCall)
        {
            throw new HttpRequestException("模拟网络故障");
        }

        return ValueTask.FromResult(results ?? []);
    }
}
