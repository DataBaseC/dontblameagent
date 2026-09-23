using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using AgentFramework.Agent;
using AgentFramework.Contracts;
using AgentFramework.Data;
using AgentFramework.Kernel;
using AgentFramework.Tools;

// ═══════════════════════════════════════════════════════════
//  工具集垂直切片验证
//  文件（含路径逃逸防护）/ 命令（超时）/ 搜索（降级链）/ 抓取（SSRF 防护）
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

var root = Path.Combine(Path.GetTempPath(), "af-tools-verify", Guid.NewGuid().ToString("N")[..8]);
var workspace = Path.Combine(root, "workspace");
Directory.CreateDirectory(workspace);
Console.WriteLine("═══ 工具集垂直切片验证 ═══");
Console.WriteLine($"工作区：{workspace}");

var options = new ToolkitOptions { WorkspaceRoot = workspace };

async ValueTask<ToolResult> Call(ITool tool, params (string Key, string Value)[] args)
{
    var dict = args.ToDictionary(a => a.Key, a => (string?)a.Value);
    return await tool.InvokeAsync(new ToolInvocation(tool.Name, dict));
}

// ── 1. 文件工具 ────────────────────────────────────────────
Console.WriteLine("\n── 1. 文件工具 ──");

var write = new WriteFileTool(options);
var read = new ReadFileTool(options);
var list = new ListDirTool(options);

var writeResult = await Call(write, ("path", "notes/todo.md"), ("content", "# 待办\n- 写框架"));
Check("write_file 写入成功", writeResult.Success, writeResult.Output);

var readResult = await Call(read, ("path", "notes/todo.md"));
Check("read_file 读回内容一致", readResult.Success && readResult.Output.Contains("写框架"), readResult.Output);

var listResult = await Call(list, ("path", "notes"));
Check("list_dir 列出条目", listResult.Success && listResult.Output.Contains("todo.md"), listResult.Output);

Console.WriteLine("\n── 1b. 路径逃逸防护 ──");
var escapeRead = await Call(read, ("path", "../../../../etc/passwd"));
Check("读越界路径被拒", !escapeRead.Success && escapeRead.Error!.Contains("越出工作区"), escapeRead.Error);

var escapeWrite = await Call(write, ("path", "../escaped.txt"), ("content", "x"));
Check("写越界路径被拒", !escapeWrite.Success && escapeWrite.Error!.Contains("越出工作区"), escapeWrite.Error);
Check("越界文件确实没被创建", !File.Exists(Path.Combine(root, "escaped.txt")));

// v3.5 审查 P2：**符号链接绕过** —— 只比字符串前缀的话，工作区里一个指向外部的链接
// 就能把读写引到区外（这是「前缀比较」这条防线唯一的结构性漏洞）。
var outsideDir = Path.Combine(root, "outside");
Directory.CreateDirectory(outsideDir);
await File.WriteAllTextAsync(Path.Combine(outsideDir, "secret.txt"), "外部机密");

var linkPath = Path.Combine(workspace, "peek");
var linkSupported = true;
try
{
    Directory.CreateSymbolicLink(linkPath, outsideDir);
}
catch (Exception)
{
    linkSupported = false;
}

if (linkSupported)
{
    var linkRead = await Call(read, ("path", "peek/secret.txt"));
    Check("★ 经符号链接读区外文件被拒（穿透链接比较）",
        !linkRead.Success && linkRead.Error!.Contains("越出工作区"), linkRead.Error);

    var linkWrite = await Call(write, ("path", "peek/planted.txt"), ("content", "x"));
    Check("★ 经符号链接写区外文件被拒", !linkWrite.Success && linkWrite.Error!.Contains("越出工作区"), linkWrite.Error);
    Check("区外文件确实没被写入", !File.Exists(Path.Combine(outsideDir, "planted.txt")));

    // 反向确认：区内正常文件仍可读（修复不能误伤合法路径）
    await Call(write, ("path", "normal.txt"), ("content", "正常内容"));
    var normalRead = await Call(read, ("path", "normal.txt"));
    Check("修复未误伤区内正常路径", normalRead.Success);
}
else
{
    Console.WriteLine("  [SKIP] 本平台不支持创建符号链接，跳过符号链接穿透测试");
}

// ── 2. 命令工具 ────────────────────────────────────────────
Console.WriteLine("\n── 2. 命令工具 ──");

var fastOptions = new ToolkitOptions { WorkspaceRoot = workspace, CommandTimeoutSeconds = 20 };
var run = new RunCommandTool(fastOptions);

var echoResult = await Call(run, ("command", "echo hello-from-command"));
Check("命令执行成功且拿到输出", echoResult.Success && echoResult.Output.Contains("hello-from-command"), FirstLine(echoResult.Output));

var slowOptions = new ToolkitOptions { WorkspaceRoot = workspace, CommandTimeoutSeconds = 1 };
var runSlow = new RunCommandTool(slowOptions);
var slowCommand = OperatingSystem.IsWindows() ? "ping -n 6 127.0.0.1" : "sleep 6";
var timeoutResult = await Call(runSlow, ("command", slowCommand));
Check("超时命令被终止", !timeoutResult.Success && timeoutResult.Error!.Contains("超时"), timeoutResult.Error);

// ── 3. 联网搜索 ────────────────────────────────────────────
Console.WriteLine("\n── 3. 联网搜索（provider seam + 降级链）──");

var fakeProvider = new FakeSearchProvider("fake",
[
    new SearchHit("https://example.com/a", "第一篇文章", "这是摘要 A", "2026-01-01"),
    new SearchHit("https://example.com/b", "第二篇文章", "这是摘要 B", null),
    new SearchHit("https://example.com/c", "第三篇文章", null, null),
]);

var searchTool = new WebSearchTool(options, fakeProvider);
var searchResult = await Call(searchTool, ("query", "测试关键词"));
Check("搜索返回 markdown 链接", searchResult.Success && searchResult.Output.Contains("[第一篇文章](https://example.com/a)"), FirstLine(searchResult.Output));
Check("搜索结果含摘要与日期", searchResult.Output.Contains("这是摘要 A") && searchResult.Output.Contains("2026-01-01"));
Check("搜索末尾要求引用来源", searchResult.Output.Contains("引用上述 URL"));

var limitedOptions = new ToolkitOptions { WorkspaceRoot = workspace, SearchMaxResults = 2 };
var limitedSearch = new WebSearchTool(limitedOptions, fakeProvider);
var limitedResult = await Call(limitedSearch, ("query", "q"));
Check("SearchMaxResults 生效（只返回 2 条）",
    limitedResult.Output.Split("- [").Length - 1 == 2,
    $"{limitedResult.Output.Split("- [").Length - 1} 条");

var failing = new FakeSearchProvider("broken", []);
failing.ThrowOnSearch = true;
var fallback = new FallbackSearchProvider(failing, fakeProvider);
var fallbackTool = new WebSearchTool(options, fallback);
var fallbackResult = await Call(fallbackTool, ("query", "q"));
Check("首个后端失败时降级到下一个", fallbackResult.Success && fallbackResult.Output.Contains("第一篇文章"));
Check("降级过程有记录（不静默失败）", fallback.AttemptLog.Count >= 2, string.Join(" | ", fallback.AttemptLog));

// ── 4. 网页抓取 ────────────────────────────────────────────
Console.WriteLine("\n── 4. 网页抓取（SSRF 防护）──");

const string sampleHtml = """
<html><head><title>测试页</title><style>p{color:red}</style></head>
<body><h1>标题一</h1><p>正文段落 &amp; 实体</p><script>alert('bad')</script></body></html>
""";

var (listener, port) = StartLocalServer(sampleHtml);
var localUrl = $"http://127.0.0.1:{port}/page";

try
{
    // 4a：默认拒绝私有/回环
    var strictOptions = new ToolkitOptions { WorkspaceRoot = workspace };
    var strictFetch = new WebFetchTool(strictOptions);
    var blocked = await Call(strictFetch, ("url", localUrl));
    Check("默认拒绝访问本地地址（SSRF 防护）",
        !blocked.Success && blocked.Error!.Contains("SSRF"), blocked.Error);

    // 4b：放开后能抓，并做 HTML → 文本
    var looseOptions = new ToolkitOptions { WorkspaceRoot = workspace, AllowPrivateNetworks = true };
    var looseFetch = new WebFetchTool(looseOptions);
    var fetched = await Call(looseFetch, ("url", localUrl));
    Check("放开私有网段后可抓取", fetched.Success, FirstLine(fetched.Output));
    Check("HTML 已转纯文本（含标题与正文）",
        fetched.Output.Contains("标题一") && fetched.Output.Contains("正文段落 & 实体"));
    Check("脚本与样式已剔除",
        !fetched.Output.Contains("alert") && !fetched.Output.Contains("color:red"));

    // 4c：非 http(s) 被拒
    var badScheme = await Call(looseFetch, ("url", "file:///etc/passwd"));
    Check("非 http(s) 协议被拒", !badScheme.Success && badScheme.Error!.Contains("只支持 http"), badScheme.Error);

    // 4d：地址归一化（P1-S3）—— 这几种写法从前会落进 IPv6 分支被直接放行，
    //     而操作系统连它们时访问的其实是回环 / 内网地址。
    foreach (var target in new[]
             {
                 "http://[::ffff:127.0.0.1]/",
                 "http://[::ffff:a00:1]/",
                 "http://[fc00::1]/",
             })
    {
        var probe = await Call(strictFetch, ("url", target));
        Check($"归一化后仍拒绝 {target}", !probe.Success && probe.Error!.Contains("SSRF"), probe.Error);
    }
}
finally
{
    listener.Stop();
}

// ── 5. 工具集接进主循环 ────────────────────────────────────
Console.WriteLine("\n── 5. 工具集 + Agent 主干端到端 ──");

var tools = new List<ITool> { write, read, list, run };
var scripted = new ScriptedLlmClient(
    "scripted",
    new ScriptedTurn(
        [],
        [new ToolCallRequest("c1", "write_file", """{"path":"from-agent.md","content":"由 agent 写入"}""")],
        "tool_calls"),
    new ScriptedTurn(["已写入文件。"], null, "stop"));

var host = new PluginHost(new PluginHostOptions { DataRoot = Path.Combine(root, "plugin-data") });
var logPath = Path.Combine(root, "agent.jsonl");
AgentRunResult runResult;
List<SessionEvent> events;

using (var log = JsonlEventLog.Open(logPath))
{
    var sink = new JsonlSink(log, host);
    var runner = new AgentRunner(scripted, () => tools, sink, new AgentOptions { SessionId = "s-tools" });
    runResult = await runner.RunAsync("把内容写进 from-agent.md");
    events = JsonlEventLog.Read(logPath).ToList();
}

Check("主循环完成", runResult.Completed);
Check("工具 schema 真的下发给模型了",
    scripted.ReceivedRequests[0].Tools.Any(t => t.Name == "write_file" && t.ParametersJsonSchema.Contains("content")),
    string.Join(",", scripted.ReceivedRequests[0].Tools.Select(t => t.Name)));
Check("工具真的写进了文件", File.Exists(Path.Combine(workspace, "from-agent.md")));
Check("文件内容正确", File.ReadAllText(Path.Combine(workspace, "from-agent.md")) == "由 agent 写入");
Check("事件链完整（user→用量→tool-requested→tool-completed→用量→assistant）",
    events.Count == 6 && events[2] is ToolCallRequestedEvent && events[3] is ToolCallCompletedEvent,
    string.Join(" → ", events.Select(e => e.GetType().Name.Replace("Event", ""))));

// 核心不变式：凡是进过模型上下文的内容，都要能从事件流原样重建
var rebuilt = SessionContextBuilder.Build(events);
Check("从事件流重建出模型上下文（模型可见即已记录）",
    rebuilt.Count == 4
    && rebuilt[0].Role == LlmRole.User
    && rebuilt[1].Role == LlmRole.Assistant
    && rebuilt[1].ToolCalls is { Count: 1 } rebuiltCalls
    && rebuiltCalls[0].ToolName == "write_file"
    && rebuilt[2].Role == LlmRole.Tool
    && rebuilt[2].ToolCallId == "c1"
    && rebuilt[3].Role == LlmRole.Assistant,
    string.Join(" → ", rebuilt.Select(m => m.ToolCalls is { Count: > 0 } ? $"{m.Role}(+tool_call)" : m.Role)));

Check("重建的工具参数与原始一致",
    rebuilt[1].ToolCalls![0].ArgumentsJson.Contains("from-agent.md"),
    rebuilt[1].ToolCalls![0].ArgumentsJson);

// ── 会话级工作区沙箱（多项目，本批新增）────────────────────
Console.WriteLine("\n── 会话级工作区沙箱 ──");

var projDir = Path.Combine(root, "session-project");
Directory.CreateDirectory(projDir);
File.WriteAllText(Path.Combine(projDir, "inner.txt"), "项目内的文件");
File.WriteAllText(Path.Combine(root, "host-only.txt"), "宿主工作区的文件");

var sessionToolkit = new ToolkitOptions
{
    WorkspaceRoot = root,                                  // 宿主级根
    WorkspaceRootResolver = () => projDir,                 // 会话钉住的项目目录
};

var sessionRead = await new ReadFileTool(sessionToolkit).InvokeAsync(
    new ToolInvocation("read_file", new Dictionary<string, string?> { ["path"] = "inner.txt" }));
Check("★ 会话钉了项目目录后，文件工具锚定项目目录", sessionRead.Success && sessionRead.Output!.Contains("项目内的文件"));

var sessionEscape = await new ReadFileTool(sessionToolkit).InvokeAsync(
    new ToolInvocation("read_file", new Dictionary<string, string?> { ["path"] = "../host-only.txt" }));
Check("★ 项目目录外的文件同样被拒（越界检查按会话根计算）", !sessionEscape.Success && sessionEscape.Error!.Contains("越出工作区"));

Console.WriteLine($"\n═══ 结果：{passes} 通过 / {failures} 失败 ═══");
return failures == 0 ? 0 : 1;

// ═══════════════════════════ 辅助 ═══════════════════════════

static string FirstLine(string text)
{
    var line = text.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "";
    return line.Length > 80 ? line[..80] : line;
}

/// <summary>起一个只回一份 HTML 的极简 HTTP 服务器（用于验证抓取链路）。</summary>
static (TcpListener Listener, int Port) StartLocalServer(string html)
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;

    _ = Task.Run(async () =>
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync();
            }
            catch
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    using (client)
                    using (var stream = client.GetStream())
                    {
                        var buffer = new byte[8192];
                        _ = await stream.ReadAsync(buffer);

                        var body = Encoding.UTF8.GetBytes(html);
                        var head = Encoding.ASCII.GetBytes(
                            "HTTP/1.1 200 OK\r\n"
                            + "Content-Type: text/html; charset=utf-8\r\n"
                            + $"Content-Length: {body.Length}\r\n"
                            + "Connection: close\r\n\r\n");

                        await stream.WriteAsync(head);
                        await stream.WriteAsync(body);
                        await stream.FlushAsync();
                    }
                }
                catch
                {
                    // 测试服务器，忽略
                }
            });
        }
    });

    return (listener, port);
}

// ═══════════════════════════ 测试替身 ═══════════════════════════

internal sealed class FakeSearchProvider(string name, IReadOnlyList<SearchHit> hits) : ISearchProvider
{
    public string Name => name;

    public bool ThrowOnSearch { get; set; }

    public ValueTask<IReadOnlyList<SearchHit>> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        if (ThrowOnSearch)
        {
            throw new HttpRequestException("模拟后端故障");
        }

        return ValueTask.FromResult<IReadOnlyList<SearchHit>>(hits.Take(maxResults).ToList());
    }
}

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

internal sealed class JsonlSink(JsonlEventLog log, PluginHost host) : IAgentEventSink
{
    public ValueTask EmitAsync(SessionEvent sessionEvent, CancellationToken ct)
    {
        log.Append(sessionEvent);
        return ValueTask.CompletedTask;
    }

    public async ValueTask RequestApprovalAsync(ToolPreExecuteEvent toolPreExecuteEvent, CancellationToken ct)
        => await host.EmitAsync(toolPreExecuteEvent, ct).ConfigureAwait(false);
}
