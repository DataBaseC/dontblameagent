using System.Reflection;
using System.Text.Json;
using AgentFramework.Contracts;
using AgentFramework.Host;
using AgentFramework.Kernel;
using Microsoft.Extensions.Logging;

// ═══════════════════════════════════════════════════════════
//  脚本插件 + 自我升级闭环 垂直切片验证
//    写插件 → 热装载 → 下一轮可用 → 钩子把关 → 坏版本回滚 → 卸载
//  不需要 API key / 联网 / 真实模型：全程只碰插件内核与仓库。
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

var root = Path.Combine(Path.GetTempPath(), "af-plugins-verify", Guid.NewGuid().ToString("N")[..8]);
var workspace = Path.Combine(root, "workspace");
var sessions = Path.Combine(root, "sessions");
Directory.CreateDirectory(root);

Console.WriteLine("═══ 脚本插件 / 自我升级 验证 ═══");
Console.WriteLine($"工作目录：{root}");

var options = new HostOptions
{
    WorkspaceRoot = workspace,
    SessionsDir = sessions,
    SessionId = "default",
    // 本测试验的是「装卸机制」，不是审批 —— 全放行，免得把交互缝也拖进来。
    ApprovalPolicy = static _ => ApprovalDecision.Allow,
};

var host = await AgentHost.CreateAsync(options);
var kernel = host.Plugins;

Console.WriteLine("\n── 1. 自管理工具就位 ──");
string[] selfTools = ["plugin_write", "plugin_reload", "plugin_uninstall", "plugin_list"];
Check("★ 四个自管理工具已注册（agent 的自我升级入口）",
    selfTools.All(host.ToolNames.Contains),
    string.Join(",", host.ToolNames));

static Dictionary<string, string?> Args(params (string Key, string Value)[] pairs)
    => pairs.ToDictionary(p => p.Key, p => (string?)p.Value);

// ── 2. 写入仓库的边界 ──
Console.WriteLine("\n── 2. 写入边界 ──");
var evilFiles = JsonSerializer.Serialize(new Dictionary<string, string>
{
    ["plugin.json"] = """{"id":"../evil","name":"x","version":"1","apiVersion":"1","script":"main.js"}""",
    ["main.js"] = "function activate(ctx) {}",
});
var evil = await kernel.InvokeToolAsync("plugin_write", Args(("id", "../evil"), ("files", evilFiles)));
Check("★ 非法插件 id 被拒（id 直接拼路径，必须白名单化）",
    !evil.Success && evil.Error!.Contains("非法"),
    evil.Error ?? evil.Output);

// ── 3. 写一个脚本插件并热装载 ──
Console.WriteLine("\n── 3. 写插件 → 热装载 ──");
var manifest = """
    {
      "id": "demo-kit",
      "name": "演示工具包",
      "version": "1.0.0",
      "apiVersion": "1",
      "script": "main.js",
      "description": "验证用的最小脚本插件",
      "capabilities": []
    }
    """;

var script = """
    function activate(ctx) {
      ctx.log('demo-kit 正在激活');
      ctx.registerTool({
        name: 'shout',
        description: '把文本变成大写',
        parameters: { type: 'object', properties: { text: { type: 'string' } }, required: ['text'] },
        run: function (args) { return String(args.text).toUpperCase(); }
      });
      ctx.on('tools/pre-execute', function (e) {
        if (e.toolName === 'run_command' && String(e.arguments.command || '').indexOf('forbidden-marker') >= 0) {
          return '脚本钩子：这条命令不给跑';
        }
      });
    }
    function selftest() { return 'ok'; }
    """;

var files = JsonSerializer.Serialize(new Dictionary<string, string>
{
    ["plugin.json"] = manifest,
    ["main.js"] = script,
});

var write = await kernel.InvokeToolAsync("plugin_write", Args(("id", "demo-kit"), ("files", files)));
Check("plugin_write 写进统一仓库", write.Success, write.Output);

var reload = await kernel.InvokeToolAsync("plugin_reload", Args(("id", "demo-kit")));
Check("★ plugin_reload 装载成功（含 selftest 冒烟）",
    reload.Success && reload.Output.Contains("自测：ok"),
    reload.Output);
Check("★ 脚本注册的工具即时出现在工具表（下一轮模型就能看见）",
    host.ToolNames.Contains("shout"),
    string.Join(",", host.ToolNames));
Check("工具来源可追溯", host.ToolSources.GetValueOrDefault("shout") == "demo-kit",
    host.ToolSources.GetValueOrDefault("shout"));

var shout = await kernel.InvokeToolAsync("shout", Args(("text", "hello")));
Check("脚本工具能真正执行", shout.Success && shout.Output == "HELLO", shout.Output);

// ── 4. 脚本当"把关规则"用 ──
Console.WriteLine("\n── 4. 事件钩子（确定性把关）──");
var blocked = await kernel.InvokeToolAsync("run_command", Args(("command", "echo forbidden-marker")));
Check("★ 脚本钩子拦下了带标记的命令（不是靠模型自觉）",
    !blocked.Success && blocked.Error!.Contains("脚本钩子"),
    blocked.Error ?? blocked.Output);

var allowed = await kernel.InvokeToolAsync("run_command", Args(("command", "echo fine")));
Check("没被钩子拦的命令照常执行", allowed.Success && allowed.Output.Contains("fine"), allowed.Output);

// ── 5. 未声明能力：借不到别的工具 ──
Console.WriteLine("\n── 5. 能力声明（未声明即拒）──");
var capsManifest = """
    {"id":"sneaky","name":"越权尝试","version":"1.0.0","apiVersion":"1","script":"main.js","capabilities":[]}
    """;
var capsScript = """
    function activate(ctx) {
      ctx.registerTool({
        name: 'try_write',
        description: '试图借 write_file（但没声明能力）',
        run: function () {
          try {
            ctx.callTool('write_file', { path: 'sneaky.txt', content: 'x' });
            return '不该走到这里';
          } catch (e) {
            return '被拒：' + e.message;
          }
        }
      });
    }
    """;
var sneakyFiles = JsonSerializer.Serialize(new Dictionary<string, string>
{
    ["plugin.json"] = capsManifest,
    ["main.js"] = capsScript,
});

await kernel.InvokeToolAsync("plugin_write", Args(("id", "sneaky"), ("files", sneakyFiles)));
var sneakyReload = await kernel.InvokeToolAsync("plugin_reload", Args(("id", "sneaky")));
Check("未声明能力的插件照样能装（能力只限制借调）", sneakyReload.Success, sneakyReload.Output);

var capability = await kernel.InvokeToolAsync("try_write", Args());
var capabilityText = (capability.Output + " " + (capability.Error ?? "")).Trim();
Check("★ 未声明能力时借调被拒", capabilityText.Contains("未声明能力"), capabilityText);

// ── 5b. 同步桥可重入（callTool 借回本插件自己的工具）───────
Console.WriteLine("\n── 5b. 同步桥重入（曾经跨线程死锁）──");

// 旧实现用 Task.Run 把借调挪到线程池再 GetResult 等它：
// 本插件 InvokeScriptTool 持着 _gate，借回自己的工具时新线程在 _gate 上等本线程，
// 本线程又在等新线程 —— 对锁成环。同线程同步等待则 Monitor 可重入，不会自己卡自己。
var reenterManifest = """
    {"id":"reenter","name":"重入验证","version":"1.0.0","apiVersion":"1","script":"main.js","capabilities":["tool:inner_tool"]}
    """;
var reenterScript = """
    function activate(ctx) {
      ctx.registerTool({
        name: 'inner_tool',
        description: '内部工具',
        parameters: { type: 'object', properties: { text: { type: 'string' } } },
        run: function (args) { return 'inner:' + (args.text || ''); }
      });
      ctx.registerTool({
        name: 'outer_tool',
        description: '借调本插件自己的内部工具（重入）',
        parameters: { type: 'object', properties: { text: { type: 'string' } } },
        run: function (args) {
          var r = ctx.callTool('inner_tool', { text: args.text || '' });
          if (!r.success) { return { success: false, error: 'inner 失败：' + r.error }; }
          return 'outer(' + r.output + ')';
        }
      });
    }
    function selftest() { return 'ok'; }
    """;
var reenterFiles = JsonSerializer.Serialize(new Dictionary<string, string>
{
    ["plugin.json"] = reenterManifest,
    ["main.js"] = reenterScript,
});

await kernel.InvokeToolAsync("plugin_write", Args(("id", "reenter"), ("files", reenterFiles)));
var reenterReload = await kernel.InvokeToolAsync("plugin_reload", Args(("id", "reenter")));
Check("重入验证插件装载成功", reenterReload.Success, reenterReload.Output);

var reenter = await kernel.InvokeToolAsync("outer_tool", Args(("text", "hi")));
Check("★ callTool 借回本插件自己的工具不死锁（同步桥同线程可重入）",
    reenter.Success && reenter.Output == "outer(inner:hi)",
    reenter.Success ? reenter.Output : (reenter.Error ?? ""));

await kernel.InvokeToolAsync("plugin_uninstall", Args(("id", "reenter")));

// ── 5c. 跨 ALC 类型同一性（契约传递依赖不许双载）───────────
Console.WriteLine("\n── 5c. 跨 ALC 类型同一性 ──");

{
    // 本应用 deps.json 把 Microsoft.Extensions.Logging.Abstractions 列为依赖，
    // DLL 也就在输出目录 ——「插件自带依赖」的发布形态。
    // 共享名单若漏了它，插件 ALC 会双载，ILogger 裂成两个 Type。
    var entryLocation = Assembly.GetEntryAssembly()!.Location;
    var probeAlc = new PluginLoadContext(entryLocation, "alc-identity-probe");
    try
    {
        var hostLogger = typeof(ILogger);
        var sharedName = hostLogger.Assembly.GetName().Name!;
        var pluginSideAsm = probeAlc.LoadFromAssemblyName(new AssemblyName(sharedName));
        Check("跨 ALC 后 typeof(ILogger).Assembly 与宿主相同",
            ReferenceEquals(pluginSideAsm, hostLogger.Assembly),
            pluginSideAsm is null ? "(null)" : pluginSideAsm.GetName().Name);

        var pluginSideType = pluginSideAsm?.GetType(hostLogger.FullName!, throwOnError: false);
        Check("跨 ALC 后 ILogger 是同一个 Type（不是同名不同身）",
            ReferenceEquals(pluginSideType, hostLogger),
            pluginSideType?.Assembly.GetName().Name ?? "(null)");

        // Contracts 自己暴露的 Log 属性类型也必须是同一份 ILogger
        var viaContracts = typeof(IPluginContext).GetProperty("Log")!.PropertyType;
        Check("Contracts 眼中的 ILogger 与宿主同一（契约传递依赖已共享）",
            ReferenceEquals(viaContracts, typeof(ILogger)),
            viaContracts.Assembly.GetName().Name);
    }
    finally
    {
        probeAlc.Unload();
    }
}

// ── 6. 重启后仓库里的插件自动装载 ──
Console.WriteLine("\n── 6. 重启续接 ──");
await host.DisposeAsync();
host = await AgentHost.CreateAsync(options);
kernel = host.Plugins;

Check("★ 重启后自写插件自动装载（仓库持久）",
    host.ToolNames.Contains("shout"),
    string.Join(",", host.ToolNames));

// ── 7. 坏版本：装载失败 + 自动回滚 ──
Console.WriteLine("\n── 7. 坏版本回滚 ──");
var brokenFiles = JsonSerializer.Serialize(new Dictionary<string, string>
{
    ["plugin.json"] = """{"id":"demo-kit","name":"演示工具包","version":"2.0.0","apiVersion":"1","script":"main.js"}""",
    ["main.js"] = "function activate(ctx) { throw new Error('故意坏的版本'); }",
});

await kernel.InvokeToolAsync("plugin_write", Args(("id", "demo-kit"), ("files", brokenFiles)));
var brokenReload = await kernel.InvokeToolAsync("plugin_reload", Args(("id", "demo-kit")));

Check("★ 坏版本被拒（不会静默成功）",
    brokenReload.Output.Contains("装载失败"),
    brokenReload.Output.Replace("\n", " | "));
Check("★ 自动回滚到上一版并重新装载",
    brokenReload.Output.Contains("已回滚"),
    brokenReload.Output.Replace("\n", " | "));
Check("★ 回滚后旧版能力没丢（工具仍在）", host.ToolNames.Contains("shout"));

var afterRollback = await kernel.InvokeToolAsync("shout", Args(("text", "still here")));
Check("★ 回滚后旧版仍可执行", afterRollback.Success && afterRollback.Output == "STILL HERE", afterRollback.Output);

// ── 8. 卸载 ──
Console.WriteLine("\n── 8. 卸载 ──");
var listBefore = await kernel.InvokeToolAsync("plugin_list", Args());
Check("plugin_list 列得出已装插件",
    listBefore.Success && listBefore.Output.Contains("demo-kit"),
    listBefore.Output.Replace("\n", " | "));

var uninstall = await kernel.InvokeToolAsync("plugin_uninstall", Args(("id", "demo-kit")));
Check("plugin_uninstall 成功", uninstall.Success, uninstall.Output);
Check("★ 卸载后工具从工具表消失（副作用被撤销）", !host.ToolNames.Contains("shout"));
Check("仓库目录已删除", !Directory.Exists(Path.Combine(workspace, "plugins", "demo-kit")));

await host.DisposeAsync();

Console.WriteLine($"\n═══ 结果：{passes} 通过 / {failures} 失败 ═══");
return failures == 0 ? 0 : 1;
