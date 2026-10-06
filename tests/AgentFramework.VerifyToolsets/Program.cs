using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using AgentFramework.Contracts;
using AgentFramework.Host;
using AgentFramework.Host.Hosting;
using static AgentFramework.Harness.Suite;

// ═══════════════════════════════════════════════════════════
//  工具包（Toolset）与暴露面 垂直切片验证
//    包归属 → 按包开关 → 可见面变化 → 保留包拒关 → agent 工具 → HTTP 端点
//  不需要 API key / 联网 / 真实模型。
// ═══════════════════════════════════════════════════════════


var root = Path.Combine(Path.GetTempPath(), "af-toolsets-verify", Guid.NewGuid().ToString("N")[..8]);
var workspace = Path.Combine(root, "workspace");
var pluginsDir = Path.Combine(root, "plugins");
Directory.CreateDirectory(workspace);

Console.WriteLine("═══ 工具包与暴露面验证 ═══");

// ── 0. 准备插件目录（领域插件）──
(string Id, string Dll)[] binaries =
[
    ("writing-kit", "AgentFramework.Plugins.WritingKit.dll"),
    ("console-kit", "AgentFramework.Plugins.ConsoleKit.dll"),
];

foreach (var (id, dll) in binaries)
{
    var destination = Path.Combine(pluginsDir, id);
    Directory.CreateDirectory(destination);
    File.Copy(Path.Combine(AppContext.BaseDirectory, dll), Path.Combine(destination, dll), overwrite: true);

    var staticSource = Path.Combine(AppContext.BaseDirectory, "baseplugins", id);
    if (Directory.Exists(staticSource))
    {
        foreach (var file in Directory.GetFiles(staticSource))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }
    }
}

var options = new HostOptions
{
    WorkspaceRoot = workspace,
    SessionsDir = Path.Combine(root, "sessions"),
    SessionId = "default",
    PluginsDir = pluginsDir,
    Sandbox = "off",
    ApprovalPolicy = static _ => ApprovalDecision.Allow,
};

await using var host = await AgentHost.CreateAsync(options);

// ═══ 1. 包归属 ═══
Console.WriteLine("\n── 1. 工具包与归属 ──");
var ids = host.Toolsets.Select(t => t.Id).ToList();
// 2026-09 重划：日常链全收 core；可关包只留 exec/web/self + 领域插件 writing-kit
string[] expected = ["core", "meta", "exec", "web", "self", "writing-kit"];
Check("★ 内置包收敛到 5 个 + writing-kit（降工具调用压力）",
    expected.All(ids.Contains),
    string.Join(",", ids));

(string Tool, string Toolset)[] expectedOwners =
[
    ("read_file", "core"),
    ("write_file", "core"),
    ("ask_user", "core"),
    ("edit_file", "core"),
    ("read_lines", "core"),
    ("grep_files", "core"),
    ("find_files", "core"),
    ("csv_to_json", "core"),
    ("run_command", "exec"),
    ("remember", "core"),
    ("search_history", "core"),
    ("update_plan", "core"),
    ("spawn_subagent", "core"),
    ("web_search", "web"),
    ("plugin_write", "self"),
    ("skill_scaffold", "self"),
    ("skill_validate", "self"),
    ("toolsets", "meta"),
    ("use_toolset", "meta"),
    ("tool_catalog", "meta"),
    ("word_count", "writing-kit"),
];

var wrongOwners = expectedOwners
    .Where(e => host.Plugins.ToolsetOf(e.Tool) != e.Toolset)
    .Select(e => $"{e.Tool}→{host.Plugins.ToolsetOf(e.Tool) ?? "?"}（期望 {e.Toolset}）")
    .ToList();

Check("★ 每个工具都落在正确的包里", wrongOwners.Count == 0, string.Join("；", wrongOwners));

var writing = host.Toolsets.FirstOrDefault(t => t.Id == "writing-kit");
if (writing is null)
{
    // 插件 DLL 加载失败（如杀软瞬时锁文件）时，writing-kit 工具包会缺席 ——
    // 如实记 FAIL（含可见工具集清单供诊断），而不是让 First() 抛未处理异常炸掉整个套件。
    Check("★ 领域插件包的显示名来自清单（界面上看得懂）",
        false,
        $"writing-kit 未加载；现有工具集：{string.Join(",", host.Toolsets.Select(t => t.Id))}");
    return 1;
}
Check("★ 领域插件包的显示名来自清单（界面上看得懂）",
    writing.Name == "写作扩展工具包",
    writing.Name);
Check("writing-kit 带着 6 个写作工具（含 read_document）",
    writing.Tools.Count == 6 && writing.Tools.Contains("word_count") && writing.Tools.Contains("read_document"),
    $"{writing.Tools.Count} 个");
Check("core 收齐文件链（关掉插件不断手）",
    host.Toolsets.First(t => t.Id == "core").Tools.Contains("edit_file")
    && host.Toolsets.First(t => t.Id == "core").Tools.Contains("read_lines")
    && host.Toolsets.First(t => t.Id == "core").Tools.Contains("grep_files"),
    string.Join(",", host.Toolsets.First(t => t.Id == "core").Tools.Where(t =>
        t is "edit_file" or "read_lines" or "grep_files" or "find_files" or "make_dir")));

var coreView = host.Toolsets.First(t => t.Id == "core");
Check("保留包被标出来（core / meta）",
    coreView.Protected && host.Toolsets.First(t => t.Id == "meta").Protected);

// ═══ 2. 默认全开 ═══
Console.WriteLine("\n── 2. 默认全开（行为与从前一致）──");
Check("★ 默认一个包都没关",
    host.DisabledToolsets.Count == 0 && host.Toolsets.All(t => t.Enabled));
Check("默认把所有工具都暴露给模型（与未引入工具包时一致）",
    host.ExposedToolNames.Count == host.ToolNames.Count,
    $"{host.ExposedToolNames.Count} / {host.ToolNames.Count}");

// ═══ 3. 按包收起来 ═══
Console.WriteLine("\n── 3. 收起来（主人要的「有选择地开」）──");
Check("★ 关掉「联网」包成功", host.SetToolsetEnabled("web", false));
Check("★ 该包的工具立刻移出可见面，别的包不受影响",
    !host.ExposedToolNames.Contains("web_search")
    && !host.ExposedToolNames.Contains("web_fetch")
    && host.ExposedToolNames.Contains("read_file")
    && host.ToolNames.Contains("web_search"),
    $"可见 {host.ExposedToolNames.Count} / 注册 {host.ToolNames.Count}");

Check("★ 关掉「执行命令」包 → run_command 不可见（但读写文件还在）",
    host.SetToolsetEnabled("exec", false)
    && !host.ExposedToolNames.Contains("run_command")
    && host.ExposedToolNames.Contains("write_file"));

Check("★ 关掉写作包 → 6 个写作工具全不可见，core 文件链不受影响",
    host.SetToolsetEnabled("writing-kit", false)
    && !host.ExposedToolNames.Contains("word_count")
    && !host.ExposedToolNames.Contains("read_document")
    && !host.ExposedToolNames.Contains("outline")
    && host.ExposedToolNames.Contains("edit_file"),
    $"可见 {host.ExposedToolNames.Count} 个");

Check("工具仍然注册着（只是不暴露）—— 再打开就能用，无需重装",
    host.ToolNames.Contains("word_count") && host.ToolNames.Contains("web_search"));

var visibleAfterClose = host.ExposedToolNames.Count;
var registeredAfterClose = host.ToolNames.Count;
Check("★ 关掉三个包后，可见面明显收窄",
    visibleAfterClose < registeredAfterClose - 5,
    $"可见 {visibleAfterClose} / 注册 {registeredAfterClose}");

// ═══ 4. 保留包 ═══
Console.WriteLine("\n── 4. 保留包不许关 ──");
Check("★ 关 core 被拒（关掉读文件/写文件会成残废）",
    !host.SetToolsetEnabled("core", false) && host.DisabledToolsets.All(d => d != "core"));
Check("★ 关 meta 被拒（关了工具包开关就再也开不回来）",
    !host.SetToolsetEnabled("meta", false) && host.DisabledToolsets.All(d => d != "meta"));
Check("未知包名被拒",
    !host.SetToolsetEnabled("no-such-toolset", false));

// ═══ 5. 再打开 ═══
Console.WriteLine("\n── 5. 打开回来 ──");
Check("打开「联网」包后恢复",
    host.SetToolsetEnabled("web", true) && host.ExposedToolNames.Contains("web_search"));
Check("打开「执行命令」包后恢复",
    host.SetToolsetEnabled("exec", true) && host.ExposedToolNames.Contains("run_command"));
Check("打开写作包后恢复",
    host.SetToolsetEnabled("writing-kit", true) && host.ExposedToolNames.Contains("word_count"));
Check("全部打开后回到初始状态",
    host.DisabledToolsets.Count == 0
    && host.ExposedToolNames.Count == host.ToolNames.Count,
    $"{host.ExposedToolNames.Count} / {host.ToolNames.Count}");

// ═══ 6. agent 自己的两个工具 ═══
Console.WriteLine("\n── 6. agent 手上的 toolsets / use_toolset ──");
static Dictionary<string, string?> Args(params (string Key, string Value)[] pairs)
    => pairs.ToDictionary(p => p.Key, p => (string?)p.Value);

var list = await host.Plugins.InvokeToolAsync("toolsets", Args());
Check("★ toolsets 列出所有包与状态",
    list.Success && list.Output.Contains("core") && list.Output.Contains("writing-kit"),
    list.Output.Split('\n')[0]);

var close = await host.Plugins.InvokeToolAsync("use_toolset", Args(("toolset", "writing-kit"), ("enabled", "false")));
Check("★ use_toolset 关包后可见面真的变了（agent 能给自己减负）",
    close.Success && !host.ExposedToolNames.Contains("word_count"),
    close.Output);

var open = await host.Plugins.InvokeToolAsync("use_toolset", Args(("toolset", "writing-kit"), ("enabled", "true")));
Check("use_toolset 再打开也生效",
    open.Success && host.ExposedToolNames.Contains("word_count"),
    open.Output);

var locked = await host.Plugins.InvokeToolAsync("use_toolset", Args(("toolset", "core"), ("enabled", "false")));
Check("★ 关保留包被拒，且理由说清楚了",
    !locked.Success && locked.Error!.Contains("保留包"),
    locked.Error);

var unknown = await host.Plugins.InvokeToolAsync("use_toolset", Args(("toolset", "nope"), ("enabled", "true")));
Check("未知包被拒并列出可选项",
    !unknown.Success && unknown.Error!.Contains("core"),
    unknown.Error);

// ═══ 6.5 技能工坊工具（任务 2）═══
Console.WriteLine("\n── 6.5 技能工坊：scaffold / validate / catalog ──");

var scaffold = await host.Plugins.InvokeToolAsync("skill_scaffold",
    Args(("id", "demo"), ("description", "演示技能")));
Check("★ skill_scaffold 生成技能骨架", scaffold.Success, scaffold.Error);

// 写盘后清单必须自己刷新（SkillsReloader）—— 否则界面/下一轮还用启动时旧列表。
Check("★ skill_scaffold 后技能清单已刷新",
    host.Skills.Any(s => s.Name == "demo"),
    string.Join(",", host.Skills.Select(s => s.Name)));

var validateOk = await host.Plugins.InvokeToolAsync("skill_validate", Args(("id", "demo")));
Check("★ skill_scaffold → skill_validate 闭环通过", validateOk.Success, validateOk.Error);

var validateBad = await host.Plugins.InvokeToolAsync("skill_validate", Args(("id", "no-such-skill")));
Check("skill_validate 对不存在的技能报错", !validateBad.Success && validateBad.Error!.Contains("没找到"));

var catalog = await host.Plugins.InvokeToolAsync("tool_catalog", Args(("toolset", "self")));
Check("tool_catalog 按包列出工具（skill 工坊并入 self）",
    catalog.Success && catalog.Output.Contains("skill_scaffold"), catalog.Output.Split('\n')[0]);

var lab = await host.Plugins.InvokeToolAsync("csv_to_json", Args(("csv", "a,b\n1,2")));
Check("★ csv_to_json（结构化转换）解析成功且归属 core", lab.Success && lab.Output.Contains("\"a\""), lab.Output.Replace("\n", " "));

// ═══ 7. 配置层面 ═══
Console.WriteLine("\n── 7. 配置层面（启动即收起）──");
var configured = new HostOptions
{
    WorkspaceRoot = workspace,
    SessionsDir = Path.Combine(root, "sessions2"),
    SessionId = "default",
    PluginsDir = pluginsDir,
    Sandbox = "off",
    // 故意把保留包一起写进去：它必须被忽略
    DisabledToolsets = ["web", "exec", "core"],
    ApprovalPolicy = static _ => ApprovalDecision.Allow,
};

await using var configuredHost = await AgentHost.CreateAsync(configured);
Check("★ 配置里写的包启动就是关的",
    !configuredHost.ExposedToolNames.Contains("web_search")
    && !configuredHost.ExposedToolNames.Contains("run_command"),
    $"关了 {configuredHost.DisabledToolsets.Count} 个");
Check("★ 配置里写保留包也不会生效（core 照旧暴露）",
    !configuredHost.DisabledToolsets.Contains("core")
    && configuredHost.ExposedToolNames.Contains("read_file")
    && configuredHost.ExposedToolNames.Contains("edit_file"),
    string.Join(",", configuredHost.DisabledToolsets));

// ═══ 8. HTTP 端点 ═══
Console.WriteLine("\n── 8. 界面用的 HTTP 端点 ──");
var port = PickFreePort();
using var server = new WebUiServer(host, options, port);
using var serverCts = new CancellationTokenSource();
_ = server.RunAsync(serverCts.Token);
await Task.Delay(500);

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
var baseUrl = server.Url.TrimEnd('/');

var listed = await http.GetStringAsync(baseUrl + "/api/toolsets");
Check("★ GET /api/toolsets 返回包清单",
    listed.Contains("\"id\":\"core\"") && listed.Contains("\"locked\":true"),
    $"{listed.Length} 字符");

var toggleBody = await http.PostAsync(
    baseUrl + "/api/toolsets/toggle",
    new StringContent("""{"id":"web","enabled":false}""", System.Text.Encoding.UTF8, "application/json"));
Check("★ POST /api/toolsets/toggle 生效",
    toggleBody.StatusCode == HttpStatusCode.OK && !host.ExposedToolNames.Contains("web_search"));

var lockedBody = await http.PostAsync(
    baseUrl + "/api/toolsets/toggle",
    new StringContent("""{"id":"core","enabled":false}""", System.Text.Encoding.UTF8, "application/json"));
Check("★ 关保留包返回 400（不假装成功）",
    lockedBody.StatusCode == HttpStatusCode.BadRequest);

var missingBody = await http.PostAsync(
    baseUrl + "/api/toolsets/toggle",
    new StringContent("""{"id":"ghost","enabled":false}""", System.Text.Encoding.UTF8, "application/json"));
Check("未知包返回 404",
    missingBody.StatusCode == HttpStatusCode.NotFound);

_ = http.PostAsync(
    baseUrl + "/api/toolsets/toggle",
    new StringContent("""{"id":"web","enabled":true}""", System.Text.Encoding.UTF8, "application/json")).Result;

// ═══ 9. Tool Search：延迟工具按需拉起 ═══
// 「延迟」不是新通路 —— 就是现成的 Eager=false 包（装配末自动进 DisabledToolsets）。
// 本节验的是补上的那半条：模型用 tool_search 把**单件**工具拉进本会话。
Console.WriteLine("\n── 9. Tool Search（工具级按需拉起）──");

var scripted = new ScriptedSearchClient(
    ("tool_search", """{"query":"deferred"}"""),
    ("tool_search", """{"action":"list"}"""),
    ("tool_search", """{"action":"reset"}"""));

var searchOptions = new HostOptions
{
    WorkspaceRoot = Path.Combine(root, "search", "workspace"),
    SessionsDir = Path.Combine(root, "search", "sessions"),
    SessionId = "search",
    LlmOverride = scripted,
    Sandbox = "off",
    ApprovalPolicy = static _ => ApprovalDecision.Allow,
    PluginsDir = Path.Combine(root, "search", "plugins"),
};

await using var searchHost = await HostBuilder.BuildAsync(searchOptions, [new DeferredProbeModule()]);

Check("★ 延迟包的工具注册着、但默认不进工具表",
    searchHost.ToolNames.Contains("deferred_echo")
    && !searchHost.ExposedToolNames.Contains("deferred_echo"),
    $"注册 {searchHost.ToolNames.Count} / 可见 {searchHost.ExposedToolNames.Count}");
Check("★ tool_search 自己常驻（发现入口不能被延迟）",
    searchHost.ExposedToolNames.Contains("tool_search"));

await searchHost.SendAsync("看看有没有能回显的工具");

var rounds = scripted.Requests;
var toolNamesAt = (int index) => rounds[index].Tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
var toolMessage = rounds.Count > 1
    ? rounds[1].Messages.LastOrDefault(m => m.Role == LlmRole.Tool)
    : null;

Check("第一轮：模型看不到延迟工具",
    rounds.Count > 0 && !toolNamesAt(0).Contains("deferred_echo"),
    rounds.Count > 0 ? $"{rounds[0].Tools.Count} 个工具" : "无请求");

Check("★ 检索结果把完整定义交回模型（名称 + 描述 + 参数）",
    toolMessage?.Content is { } content && content.Contains("deferred_echo") && content.Contains("参数"),
    toolMessage?.Content is { } shown ? shown[..Math.Min(70, shown.Length)] : "没有 tool 消息");

Check("★ 检索后下一轮就进了工具表（按需拉起生效）",
    rounds.Count > 1 && toolNamesAt(1).Contains("deferred_echo"),
    rounds.Count > 1 ? $"第 2 轮 {rounds[1].Tools.Count} 个工具" : "只有一轮");

Check("★ reset 卸下后，再下一轮它又不在工具表里",
    rounds.Count > 3 && !toolNamesAt(3).Contains("deferred_echo"),
    rounds.Count > 3 ? $"共 {rounds.Count} 轮 / 第 4 轮 {rounds[3].Tools.Count} 个工具" : $"只有 {rounds.Count} 轮");

Check("★ 拉起只活在会话回合内（回合外可见面不外溢）",
    !searchHost.ExposedToolNames.Contains("deferred_echo"));

Console.WriteLine($"\n结果：{passes} 通过 / {failures} 失败");

try
{
    Directory.Delete(root, recursive: true);
}
catch (Exception)
{
    // 临时目录偶尔删不掉，不影响结论
}

return failures == 0 ? 0 : 1;

static int PickFreePort()
{
    var probe = new TcpListener(IPAddress.Loopback, 0);
    probe.Start();
    var port = ((IPEndPoint)probe.LocalEndpoint).Port;
    probe.Stop();
    return port;
}

/// <summary>只用于 Tool Search 验收的延迟包：工具注册着，但声明 Eager=false → 默认不进上下文。</summary>
internal sealed class DeferredProbeModule : IHostModule
{
    public string Name => "deferred-probe";

    public int Order => 900;

    public ValueTask ConfigureAsync(HostState state, CancellationToken ct = default)
    {
        state.Kernel.CreateKernelScope("probe-deferred").RegisterTool(new DeferredEchoTool());

        // 包名由内核按 scope 生成（kernel:probe-deferred）—— 注册后取真名再描述，
        // 否则描述会落在另一个空包上，工具所在的那个仍是「常驻」（延迟了个寂寞）。
        state.Kernel.DescribeToolset(new ToolsetDescriptor
        {
            Id = state.Kernel.ToolsetOf("deferred_echo") ?? "kernel:probe-deferred",
            Name = "延迟探针",
            Description = "只用于 Tool Search 验收的延迟包",
            Eager = false,
            Source = "probe",
        });

        return ValueTask.CompletedTask;
    }
}

/// <summary>延迟包里的样例工具（自带 schema，好让检索结果里能带上参数）。</summary>
internal sealed class DeferredEchoTool : ITool, IToolWithSchema
{
    public string Name => "deferred_echo";

    public string Description => "把输入的文本原样回显（延迟包样例：默认不进上下文）。";

    public string ParametersJsonSchema => """{"type":"object","properties":{"text":{"type":"string"}}}""";

    public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
        => ValueTask.FromResult(ToolResult.Ok("echo: ok"));
}

/// <summary>按队列逐轮发工具调用的假模型（队列空则收尾）—— 每个请求都入档。</summary>
internal sealed class ScriptedSearchClient(params (string Tool, string ArgsJson)[] turns) : ILlmClient
{
    private readonly Queue<(string Tool, string ArgsJson)> _turns = new(turns);

    public string Name => "scripted-search";

    public List<LlmRequest> Requests { get; } = [];

    public async IAsyncEnumerable<LlmStreamChunk> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();

        int index;
        lock (Requests)
        {
            Requests.Add(request);
            index = Requests.Count;
        }

        if (_turns.TryDequeue(out var spec))
        {
            yield return new LlmStreamChunk.ToolCallsReady(
            [
                new ToolCallRequest($"ss-{index}", spec.Tool, spec.ArgsJson),
            ]);
            yield return new LlmStreamChunk.Completed("tool_calls");
            yield break;
        }

        yield return new LlmStreamChunk.TextDelta("好，处理完了。");
        yield return new LlmStreamChunk.Completed("stop");
    }
}
