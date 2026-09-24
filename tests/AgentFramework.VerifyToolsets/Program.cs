using System.Net;
using System.Net.Sockets;
using AgentFramework.Contracts;
using AgentFramework.Host;

// ═══════════════════════════════════════════════════════════
//  工具包（Toolset）与暴露面 垂直切片验证
//    包归属 → 按包开关 → 可见面变化 → 保留包拒关 → agent 工具 → HTTP 端点
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

var root = Path.Combine(Path.GetTempPath(), "af-toolsets-verify", Guid.NewGuid().ToString("N")[..8]);
var workspace = Path.Combine(root, "workspace");
var pluginsDir = Path.Combine(root, "plugins");
Directory.CreateDirectory(workspace);

Console.WriteLine("═══ 工具包与暴露面验证 ═══");

// ── 0. 准备插件目录（基石插件 → 各自的包）──
(string Id, string Dll)[] binaries =
[
    ("devkit", "AgentFramework.Plugins.DevKit.dll"),
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
string[] expected = ["core", "meta", "exec", "memory", "search", "plan", "web", "self", "devkit", "writing-kit"];
Check("★ 内置 8 个包 + 基石插件 2 个包都在",
    expected.All(ids.Contains),
    string.Join(",", ids));

(string Tool, string Toolset)[] expectedOwners =
[
    ("read_file", "core"),
    ("write_file", "core"),
    ("ask_user", "core"),
    ("run_command", "exec"),
    ("remember", "memory"),
    ("search_history", "search"),
    ("update_plan", "plan"),
    ("web_search", "web"),
    ("plugin_write", "self"),
    ("toolsets", "meta"),
    ("use_toolset", "meta"),
    ("edit_file", "devkit"),
    ("word_count", "writing-kit"),
];

var wrongOwners = expectedOwners
    .Where(e => host.Plugins.ToolsetOf(e.Tool) != e.Toolset)
    .Select(e => $"{e.Tool}→{host.Plugins.ToolsetOf(e.Tool) ?? "?"}（期望 {e.Toolset}）")
    .ToList();

Check("★ 每个工具都落在正确的包里", wrongOwners.Count == 0, string.Join("；", wrongOwners));

var devkit = host.Toolsets.First(t => t.Id == "devkit");
Check("★ 插件包的显示名来自清单（界面上看得懂）",
    devkit.Name == "编程扩展工具包",
    devkit.Name);
Check("插件包的工具数与实际一致",
    devkit.Tools.Count == 7 && devkit.Tools.Contains("edit_file"),
    $"{devkit.Tools.Count} 个");

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

Check("★ 关掉写作包 → 5 个写作工具全不可见，编程包不受影响",
    host.SetToolsetEnabled("writing-kit", false)
    && !host.ExposedToolNames.Contains("word_count")
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
    DisabledToolsets = ["web", "plan", "core"],
    ApprovalPolicy = static _ => ApprovalDecision.Allow,
};

await using var configuredHost = await AgentHost.CreateAsync(configured);
Check("★ 配置里写的包启动就是关的",
    !configuredHost.ExposedToolNames.Contains("web_search")
    && !configuredHost.ExposedToolNames.Contains("update_plan"),
    $"关了 {configuredHost.DisabledToolsets.Count} 个");
Check("★ 配置里写保留包也不会生效（core 照旧暴露）",
    !configuredHost.DisabledToolsets.Contains("core")
    && configuredHost.ExposedToolNames.Contains("read_file"),
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
