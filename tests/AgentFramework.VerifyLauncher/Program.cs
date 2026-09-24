using System.Net;
using System.Net.Http;
using System.Text;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using AgentFramework.Contracts;
using AgentFramework.Host;
using AgentFramework.Launcher;

// ═══════════════════════════════════════════════════════════
//  启动器垂直切片验证
//  扫描 → 勾选 → 档案 → Host 按勾选装配 → 启动器 HTTP 服务
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

var root = Path.Combine(Path.GetTempPath(), "af-launcher-verify", Guid.NewGuid().ToString("N")[..8]);
var pluginsDir = Path.Combine(root, "plugins");
var workspace = Path.Combine(root, "workspace");
var sessions = Path.Combine(root, "sessions");
Directory.CreateDirectory(root);

Console.WriteLine("═══ 启动器垂直切片验证 ═══");
Console.WriteLine($"工作目录：{root}");

// 准备插件目录：一个真插件 + 一个坏清单
var builtPlugin = Path.Combine(AppContext.BaseDirectory, "plugins", "hello");
if (!Directory.Exists(builtPlugin))
{
    Console.WriteLine($"构建脚本没把示例插件复制到 {builtPlugin}");
    return 99;
}

CopyDirectory(builtPlugin, Path.Combine(pluginsDir, "hello"));

var brokenDir = Path.Combine(pluginsDir, "broken");
Directory.CreateDirectory(brokenDir);
File.WriteAllText(Path.Combine(brokenDir, "plugin.json"), """{"id":"broken","name":"坏插件"}""");

var pluginDirNoManifest = Path.Combine(pluginsDir, "just-a-folder");
Directory.CreateDirectory(pluginDirNoManifest);

// ── 1. 扫描 ────────────────────────────────────────────────
Console.WriteLine("\n── 1. 插件目录扫描 ──");
var catalog = PluginCatalog.Scan(pluginsDir);

Check("扫描出 1 个可用插件", catalog.Plugins.Count == 1, $"{catalog.Plugins.Count} 个");
Check("插件元信息正确",
    catalog.Plugins[0].Id == "hello" && catalog.Plugins[0].Version == "1.0.0" && catalog.Plugins[0].Injects.Count == 1,
    $"{catalog.Plugins[0].Id}@{catalog.Plugins[0].Version}");
Check("坏清单被记录而非静默跳过", catalog.Errors.Count == 1 && catalog.Errors[0].Contains("broken"),
    string.Join("; ", catalog.Errors));
Check("没有清单的目录被忽略（不计入错误）", !catalog.Errors.Any(e => e.Contains("just-a-folder")));

// ── 2. 档案读写 ────────────────────────────────────────────
Console.WriteLine("\n── 2. 装配档案（profile）──");
var profilePath = Path.Combine(root, "profiles", "default.json");
var profile = new PluginProfile { Name = "default" };
profile.EnabledPlugins.Add("hello");
profile.Save(profilePath);

Check("档案已落盘", File.Exists(profilePath));
var reloaded = PluginProfile.Load(profilePath);
Check("档案可读回", reloaded.IsEnabled("hello") && !reloaded.IsEnabled("other"));
Check("档案是纯 JSON（人可读）", File.ReadAllText(profilePath).Contains("\"EnabledPlugins\""));

// 坏档案不拖垮启动
File.WriteAllText(profilePath, "{ this is not json");
Check("坏档案退回空档案（不抛异常）", PluginProfile.Load(profilePath).EnabledPlugins.Count == 0);

// 正式存档（供后面用）
var finalProfile = new PluginProfile();
finalProfile.EnabledPlugins.Add("hello");
finalProfile.Save(profilePath);

// ── 3. Host 按勾选装配（本轮核心）────────────────────────
Console.WriteLine("\n── 3. Host 按勾选装配 ──");
var llm = new SilentLlmClient();

HostOptions Make(string session, IReadOnlyCollection<string>? enabled) => new()
{
    WorkspaceRoot = workspace,
    SessionsDir = sessions,
    PluginsDir = pluginsDir,
    SessionId = session,
    LlmOverride = llm,
    EnabledPlugins = enabled,
};

await using (var hostOn = await AgentHost.CreateAsync(Make("on", ["hello"])))
{
    Check("勾选后插件被加载", hostOn.LoadedPlugins.Count == 1, $"{hostOn.LoadedPlugins.Count} 个");
    Check("插件工具并入主循环", hostOn.ToolNames.Contains("hello"), string.Join(",", hostOn.ToolNames));
}

await using (var hostOff = await AgentHost.CreateAsync(Make("off", [])))
{
    Check("未勾选的插件不被加载", hostOff.LoadedPlugins.Count == 0, $"{hostOff.LoadedPlugins.Count} 个");
    Check("未加载的插件被登记为跳过", hostOff.SkippedPlugins.Contains("hello"),
        string.Join(",", hostOff.SkippedPlugins));
    Check("跳过插件后工具集仍完整（官方工具在）",
        hostOff.ToolNames.Contains("write_file") && !hostOff.ToolNames.Contains("hello"));
}

await using (var hostAll = await AgentHost.CreateAsync(Make("all", null)))
{
    Check("EnabledPlugins=null 时尝试全量加载", hostAll.LoadedPlugins.Count == 1, $"{hostAll.LoadedPlugins.Count} 个成功");
    Check("★ 坏插件不拖垮整体启动（记录原因后继续）",
        hostAll.FailedPlugins.Count == 1 && hostAll.FailedPlugins[0].Contains("broken"),
        string.Join("; ", hostAll.FailedPlugins));
    Check("坏插件失败后官方工具仍可用", hostAll.ToolNames.Contains("write_file"));
}

// ── 4. 启动器 HTTP 服务 ────────────────────────────────────
Console.WriteLine("\n── 4. 启动器页面与接口 ──");
var state = new LauncherState
{
    PluginsDir = pluginsDir,
    ProfilePath = profilePath,
    Profile = new PluginProfile(),
    Catalog = catalog.Plugins,
    Errors = catalog.Errors,
};

var port = PickFreePort();
var server = new LauncherServer(state, port);
using var cts = new CancellationTokenSource();
_ = server.RunAsync(cts.Token);
await Task.Delay(400);

using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
{
    Timeout = TimeSpan.FromSeconds(10),
};

try
{
    var html = await http.GetStringAsync(server.Url);
    Check("页面可访问", html.Contains("Agent 启动器"));
    Check("页面显示构建版本（便于确认编译成功）", html.Contains(BuildInfo.Stamp), BuildInfo.Stamp);
    Check("页面列出插件", html.Contains("Hello Plugin"));
    Check("页面提示损坏插件", html.Contains("broken"), "坏清单提示已展示");
    Check("页面初始为未勾选", html.Contains("未启用"));

    var toggleViaGet = await http.GetAsync($"{server.Url}toggle?id=hello");
    Check("★ GET 写端点被 405 拒绝（安全契约：写操作必须 POST）", (int)toggleViaGet.StatusCode == 405);

    var toggleResponse = await http.PostAsync($"{server.Url}toggle?id=hello", new StringContent(""));
    Check("toggle 返回重定向", (int)toggleResponse.StatusCode == 302, $"{(int)toggleResponse.StatusCode}");
    Check("勾选状态已改变", state.Profile.IsEnabled("hello"));

    var htmlAfter = await http.GetStringAsync(server.Url);
    Check("页面反映勾选结果", htmlAfter.Contains("已启用"));

    await http.PostAsync($"{server.Url}save", new StringContent(""));
    Check("保存接口把档案写盘", File.Exists(profilePath) && PluginProfile.Load(profilePath).IsEnabled("hello"));

    IReadOnlyList<string>? captured = null;
    state.Launcher = launchArgs =>
    {
        captured = launchArgs;
        return "（验证中不真的拉起进程）";
    };

    await http.PostAsync($"{server.Url}launch", new StringContent(""));
    Check("启动接口带上 --plugins-enabled",
        captured is not null && captured.Contains("--plugins-enabled") && captured.Contains("hello"),
        captured is null ? "(未调用)" : string.Join(' ', captured));

    var toggleOff = await http.PostAsync($"{server.Url}toggle?id=hello", new StringContent(""));
    Check("再次点击可取消勾选", (int)toggleOff.StatusCode == 302 && !state.Profile.IsEnabled("hello"));

    // ── 5. v2：加载顺序（LoadOrder）───────────────────────────────
    Console.WriteLine("\n── 5. v2 加载顺序 ──");
    // 重新启用并建立手写顺序
    await http.PostAsync($"{server.Url}toggle?id=hello", new StringContent(""));
    state.Profile.EnsureLoadOrderCovers(state.Catalog.Select(p => p.Id).ToList());
    state.Profile.Save(profilePath);
    Check("LoadOrder 已补齐覆盖目录", state.Profile.LoadOrder is { Count: >= 1 } && state.Profile.LoadOrder.Contains("hello"),
        state.Profile.LoadOrder is null ? "(null)" : string.Join(',', state.Profile.LoadOrder));

    // move 端点：单插件目录里移动到自己前面 = 无变化但不报错
    var moveResp = await http.PostAsync($"{server.Url}move?from=hello&to=hello", new StringContent(""));
    Check("move 端点可用（同 id 自移返回 302）", (int)moveResp.StatusCode == 302);

    // OrderedEnabled：手写顺序优先，未列出的启用插件按目录序补后
    var profile2 = new PluginProfile();
    profile2.EnabledPlugins.AddRange(["b", "a", "c"]);
    profile2.LoadOrder = ["c", "b"];
    var ordered = profile2.OrderedEnabled(["a", "b", "c"]);
    Check("OrderedEnabled: 手写序优先 + 未列的按目录序补后",
        ordered is ["c", "b", "a"], string.Join(',', ordered));

    // 老档案（LoadOrder=null）退回目录序
    var legacyProfile = new PluginProfile();
    legacyProfile.EnabledPlugins.Add("c");
    var legacyOrdered = legacyProfile.OrderedEnabled(["a", "b", "c"]);
    Check("老档案无 LoadOrder 仍可装配（向后兼容）", legacyOrdered is ["c"], string.Join(',', legacyOrdered));

    // LoadOrder 持久化到 JSON
    var profile3 = new PluginProfile { LoadOrder = ["hello"] };
    profile3.EnabledPlugins.Add("hello");
    profile3.Save(profilePath);
    Check("LoadOrder 落盘并可读回", PluginProfile.Load(profilePath).LoadOrder is ["hello"]);

    // ── 6. v2：预检 ────────────────────────────────────────────
    Console.WriteLine("\n── 6. v2 启动预检 ──");
    // 场景 A：正常单插件 → 只有目录层错误（broken 清单）
    var issuesA = Preflight.Run(catalog.Plugins, ["hello"], catalog.Errors);
    Check("预检：坏清单被归为 CatalogError", issuesA.Any(i => i.Kind == PreflightKind.CatalogError),
        string.Join(", ", issuesA.Select(i => i.Message)));

    // 场景 B：构造前置顺序违规（插件 A inject B，但 B 排在 A 后面）
    var pa = new PluginDescriptor("pa", "PA", "1.0.0", "1", "/x/pa", ["pb"]);
    var pb = new PluginDescriptor("pb", "PB", "1.0.0", "1", "/x/pb", []);
    var issuesB = Preflight.Run([pa, pb], ["pa", "pb"], []);
    Check("预检发现前置排在依赖者之后",
        issuesB.Any(i => i.Kind == PreflightKind.DependencyAfterDependent && i.PluginId == "pa"),
        string.Join(", ", issuesB.Select(i => i.Message)));

    // 场景 C：前置未启用
    var issuesC = Preflight.Run([pa, pb], ["pa"], []);
    Check("预检发现前置未启用且给出修法",
        issuesC.Any(i => i.Kind == PreflightKind.MissingDependency && i.FixHint is not null),
        string.Join(", ", issuesC.Select(i => i.Message)));

    // 场景 D：合法顺序（pb 在 pa 前）→ 无前置类问题
    var issuesD = Preflight.Run([pa, pb], ["pb", "pa"], []);
    Check("预检通过合法顺序", issuesD.All(i => i.Kind is not (PreflightKind.MissingDependency or PreflightKind.DependencyAfterDependent)),
        string.Join(", ", issuesD.Select(i => i.Message)));

    // 场景 E：重复 id
    var pc = new PluginDescriptor("pa", "PA2", "2.0.0", "1", "/x/pa2", []);
    var issuesE = Preflight.Run([pa, pc], [], []);
    Check("预检发现重复 id", issuesE.Any(i => i.Kind == PreflightKind.DuplicateId),
        string.Join(", ", issuesE.Select(i => i.Message)));

    // 页面上有预检与 debug 窗入口
    var htmlV2 = await http.GetStringAsync(server.Url);
    Check("管理页含预检结果窗", htmlV2.Contains("预检"));
    Check("管理页含加载序号列", htmlV2.Contains("class=\"num\""));
    Check("管理页含 debug 窗入口", htmlV2.Contains("/debug"));

    var debugHtml = await http.GetStringAsync($"{server.Url}debug");
    Check("debug 信息窗可访问", debugHtml.Contains("debug 信息窗") && debugHtml.Contains("装配序"));

    // F1：跨站 Origin 的 POST 被拒（launch/stop 能真操作进程，必须挡）
    using (var evil = new HttpRequestMessage(HttpMethod.Get, server.Url + "toggle?id=hello"))
    {
        evil.Headers.Add("Origin", "http://evil.example");
        using var evilResp = await http.SendAsync(evil);
        Check("F1 跨站 Origin 被拒（403）", (int)evilResp.StatusCode == 403, $"{(int)evilResp.StatusCode}");
    }

    // F6：debug 窗含宿主输出段
    var debugHtml2 = await http.GetStringAsync($"{server.Url}debug");
    Check("F6 debug 窗含宿主输出段", debugHtml2.Contains("宿主输出"));

    var stateJson = await http.GetStringAsync($"{server.Url}api/state");
    Check("api/state 返回 loadOrder 与 issues",
        stateJson.Contains("\"loadOrder\"") && stateJson.Contains("\"issues\"") && stateJson.Contains("\"running\""),
        stateJson.Length.ToString());
}
finally
{
    cts.Cancel();
    server.Stop();
}

Console.WriteLine($"\n═══ 结果：{passes} 通过 / {failures} 失败 ═══");
return failures == 0 ? 0 : 1;

// ═══════════════════════════ 辅助 ═══════════════════════════

static void CopyDirectory(string source, string target)
{
    Directory.CreateDirectory(target);
    foreach (var file in Directory.EnumerateFiles(source))
    {
        File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
    }

    foreach (var dir in Directory.EnumerateDirectories(source))
    {
        CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
    }
}

static int PickFreePort()
{
    var probe = new TcpListener(IPAddress.Loopback, 0);
    probe.Start();
    var port = ((IPEndPoint)probe.LocalEndpoint).Port;
    probe.Stop();
    return port;
}

internal sealed class SilentLlmClient : ILlmClient
{
    public string Name => "silent";

    public async IAsyncEnumerable<LlmStreamChunk> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        yield return new LlmStreamChunk.TextDelta("ok");
        yield return new LlmStreamChunk.Completed("stop");
    }
}
