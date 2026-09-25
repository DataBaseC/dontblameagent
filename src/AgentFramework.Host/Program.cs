using System.Text;
using AgentFramework.Contracts;
using AgentFramework.Host;

// ═══════════════════════════════════════════════════════════
//  Agent Framework 宿主入口
//  用法：
//    dotnet run --project src/AgentFramework.Host          （交互模式）
//    dotnet run --project src/AgentFramework.Host -- --prompt "你好"   （一次性）
//
//  配置单：当前目录下的 agent.json（可用 --config 指定别的）
//          —— 端点、目录、端口、模式都写在里面，样例见 agent.json.example
//  也可以用环境变量（优先级高于配置单）：
//    AGENT_CLOUD_BASEURL / AGENT_CLOUD_KEY / AGENT_CLOUD_MODEL
//    AGENT_LOCAL_BASEURL / AGENT_LOCAL_MODEL
//  两者都没有则进入离线演示模式（能跑通，但端点不报用量）
// ═══════════════════════════════════════════════════════════

try
{
    Console.OutputEncoding = Encoding.UTF8;
}
catch
{
    // 某些终端不支持设置编码，忽略即可
}

string? GetArg(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

bool HasFlag(string name) => Array.IndexOf(args, name) >= 0;

var cwd = Directory.GetCurrentDirectory();

// 配置单：默认找「当前目录下的 agent.json」，也可以 --config 指定别的
var configPath = GetArg("--config") ?? Path.Combine(cwd, "agent.json");
var config = AgentConfig.TryLoad(configPath);

// 优先级：命令行 > 环境变量 > 配置单。越显式的越优先，临时覆盖很方便。
string? Pick(string? arg, string envName, string? fromConfig)
{
    if (!string.IsNullOrWhiteSpace(arg))
    {
        return arg;
    }

    var env = Environment.GetEnvironmentVariable(envName);
    return !string.IsNullOrWhiteSpace(env) ? env : fromConfig;
}

// --init-config：写出一份带注释的配置单，主人改完直接用
if (HasFlag("--init-config"))
{
    var target = Path.Combine(cwd, "agent.json");
    File.WriteAllText(target, AgentConfig.Sample());
    Console.WriteLine($"已写出配置单：{target}");
    Console.WriteLine("改完之后重新运行即可（也可用 --config 指定别的路径）。");
    return 0;
}

var options = new HostOptions
{
    WorkspaceRoot = Pick(GetArg("--workspace"), "AGENT_WORKSPACE", config?.Workspace)
        ?? Path.Combine(cwd, "agent-workspace"),
    SessionsDir = Pick(GetArg("--sessions"), "AGENT_SESSIONS", config?.Sessions)
        ?? Path.Combine(cwd, "agent-sessions"),
    PluginsDir = Pick(GetArg("--plugins"), "AGENT_PLUGINS", config?.Plugins),
    // 命令沙箱档位：auto（默认）/ off / process / job，或插件注册的后端名
    Sandbox = Pick(GetArg("--sandbox"), "AGENT_SANDBOX", config?.Sandbox),
    // 启动时就收起的工具包（逗号分隔）。留空 = 全部开启；保留包写了不生效。
    DisabledToolsets = (IReadOnlyCollection<string>?)GetArg("--disable-toolsets")?.Split(
        ',',
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        ?? config?.DisabledToolsets,
    EnabledPlugins = (IReadOnlyCollection<string>?)GetArg("--plugins-enabled")?.Split(
        ',',
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        ?? config?.EnabledPlugins,
    // 外部 MCP server（agent.json 的 mcpServers）：子进程 + stdio JSON-RPC，默认延迟。
    McpServers = config?.McpServers ?? [],
    SessionId = Pick(GetArg("--session"), "AGENT_SESSION", config?.SessionId) ?? "default",
    // 模型管理要读写这份配置单（界面里改端点 / 换模型走它）
    ConfigPath = configPath,
    Cloud = new LlmEndpointOptions
    {
        BaseUrl = Pick(null, "AGENT_CLOUD_BASEURL", config?.Cloud?.BaseUrl) ?? string.Empty,
        ApiKey = Pick(null, "AGENT_CLOUD_KEY", config?.Cloud?.ApiKey) ?? string.Empty,
        Model = Pick(null, "AGENT_CLOUD_MODEL", config?.Cloud?.Model) ?? string.Empty,
    },
    Local = new LlmEndpointOptions
    {
        BaseUrl = Pick(null, "AGENT_LOCAL_BASEURL", config?.Local?.BaseUrl) ?? string.Empty,
        Model = Pick(null, "AGENT_LOCAL_MODEL", config?.Local?.Model) ?? string.Empty,
    },
    Mode = string.Equals(config?.Mode, "chat", StringComparison.OrdinalIgnoreCase)
        ? AgentMode.Chat
        : AgentMode.Work,
    Temperature = config?.Temperature ?? 0.7,
    MaxSteps = config?.MaxSteps ?? 12,
    // 端点不认 stream_options（会直接 400）时，用 --no-usage 或配置单关掉用量回报
    IncludeUsage = HasFlag("--no-usage") ? false : config?.IncludeUsage ?? true,
    SearchBackends = Pick(GetArg("--search"), "AGENT_SEARCH_BACKENDS", config?.SearchBackends),
    SearxngBaseUrl = Pick(null, "AGENT_SEARXNG", config?.SearxngBaseUrl),
    // 分级审批：只读操作放行；写文件 / 执行命令需确认（Web 模式弹卡片，无界面则拒绝）
    ApprovalPolicy = HasFlag("--allow-command")
        ? static _ => ApprovalDecision.Allow
        : DefaultApprovalPolicy.Decide,
};

// 配置单里写了才覆盖 —— 这几项 HostOptions 自带默认值，不能拿 null 盖掉
if (!string.IsNullOrWhiteSpace(config?.SystemPrompt))
{
    options.SystemPrompt = config.SystemPrompt;
}

if (config?.Context is { } contextConfig)
{
    if (contextConfig.TokenBudget is { } budget)
    {
        options.Context.TokenBudget = budget;
    }

    if (contextConfig.CompressionTriggerRatio is { } ratio)
    {
        options.Context.CompressionTriggerRatio = ratio;
    }

    if (contextConfig.RecentTurnsKeptVerbatim is { } kept)
    {
        options.Context.RecentTurnsKeptVerbatim = kept;
    }

    if (contextConfig.InjectTaskCard is { } taskCard)
    {
        options.Context.InjectTaskCard = taskCard;
    }
}

await using var host = await AgentHost.CreateAsync(options);

Console.WriteLine("═══ Agent Framework ═══");
Console.WriteLine($"配置单  ：{(config is null ? $"{configPath}（没有，按默认值 / 环境变量跑）" : configPath)}");
Console.WriteLine($"工作区  ：{options.WorkspaceRoot}");
Console.WriteLine($"会话日志：{host.SessionLogPath}");
Console.WriteLine($"模型    ：{(host.UsingOfflineDemo
    ? "离线演示模式（未配置端点，只回显并演示一次工具调用）"
    : "已启用云端 / 本地端点与路由")}");
Console.WriteLine($"工具    ：{string.Join(", ", host.ToolNames)}");
Console.WriteLine($"插件    ：{(host.LoadedPlugins.Count == 0
    ? "无"
    : string.Join(", ", host.LoadedPlugins.Select(p => $"{p.Id}@{p.Version}")))}");

var state = host.CurrentState();
if (state.EventCount > 0)
{
    Console.WriteLine($"历史    ：已从事件流恢复 {state.EventCount} 条事件、{state.Messages.Count} 条消息");
}

Console.WriteLine();

// Web 对话界面模式
if (HasFlag("--web"))
{
    var webPort = int.TryParse(GetArg("--port"), out var parsedWebPort)
        ? parsedWebPort
        : config?.WebPort ?? 8090;
    using var server = new WebUiServer(host, options, webPort);
    host.ApprovalPrompt = server;   // 把审批交给界面：写文件 / 执行命令会弹卡片让你确认

    Console.WriteLine($"对话界面：{server.Url}");
    Console.WriteLine("（Ctrl+C 退出）");

    if (!HasFlag("--no-open"))
    {
        TryOpenBrowser(server.Url);
    }

    using var webCts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        webCts.Cancel();
    };

    try
    {
        await server.RunAsync(webCts.Token);
    }
    finally
    {
        server.Stop();
    }

    return 0;
}

// 一次性模式（便于脚本化与验收）
var oneShot = GetArg("--prompt");
if (oneShot is not null)
{
    var result = await host.SendAsync(oneShot);
    Console.WriteLine($"[完成] {result.FinalText}");
    Console.WriteLine($"[统计] 步数={result.Steps} 停止原因={result.StopReason}");
    return result.Completed ? 0 : 1;
}

// 交互模式
Console.WriteLine("输入消息开始对话（直接回车退出）：");
while (true)
{
    Console.Write("\n> ");
    var line = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(line))
    {
        break;
    }

    try
    {
        await host.SendAsync(line);
        Console.WriteLine();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[错误] {ex.Message}");
    }
}

Console.WriteLine("\n再见喵~");
return 0;

static void TryOpenBrowser(string url)
{
    try
    {
        System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    }
    catch
    {
        // 打不开就让用户自己点链接
    }
}
