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
//
//  命令沙箱强度（--sandbox）：
//    · Windows  → job 档：Job Object 资源限制（内存 / CPU / 进程数上限）
//    · Linux/macOS → process 档：仅进程级护栏（工作目录钉死 / TMP 重定向 /
//      超时连子孙终结 / 输出封顶 / 环境变量白名单）—— **不是隔离**：
//      命令以本用户权限运行，能读写本用户可访问的任何文件。
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
    // 强度因平台而异：Windows 的 job 档有内核配额；Linux/macOS 的 process 档只是进程级护栏（非隔离）。
    Sandbox = Pick(GetArg("--sandbox"), "AGENT_SANDBOX", config?.Sandbox),
    // shell：会话级一次决定，优先 POSIX。auto / bash / sh / cmd / powershell / pwsh / 路径
    Shell = Pick(GetArg("--shell"), "AGENT_SHELL", config?.Shell),
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
    MaxSteps = config?.MaxSteps ?? 60,
    // 端点不认 stream_options（会直接 400）时，用 --no-usage 或配置单关掉用量回报
    IncludeUsage = HasFlag("--no-usage") ? false : config?.IncludeUsage ?? true,
    SearchBackends = Pick(GetArg("--search"), "AGENT_SEARCH_BACKENDS", config?.SearchBackends),
    SearxngBaseUrl = Pick(null, "AGENT_SEARXNG", config?.SearxngBaseUrl),
    // 分级审批：只读操作放行；写文件 / 执行命令需确认（Web 模式弹卡片，无界面则拒绝）
    // --allow-command 是一次性 CLI 的全放行通道：连越界目录访问一并显式授予
    ApprovalPolicy = HasFlag("--allow-command")
        ? static _ => ApprovalDecision.Allow
        : DefaultApprovalPolicy.Decide,
    AllowExternalDirectory = HasFlag("--allow-command"),
};

// 配置单里写了才覆盖 —— 这几项 HostOptions 自带默认值，不能拿 null 盖掉
if (!string.IsNullOrWhiteSpace(config?.SystemPrompt))
{
    options.SystemPrompt = config.SystemPrompt;
}

// Goal 停止条件：配置单 goal 字段，或 --goal "..." 命令行（命令行优先）
var goalOverride = GetArg("--goal");
if (!string.IsNullOrWhiteSpace(goalOverride))
{
    options.Goal = goalOverride;
}
else if (!string.IsNullOrWhiteSpace(config?.Goal))
{
    options.Goal = config!.Goal;
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

    if (contextConfig.EarlySummarizeRatio is { } earlyRatio)
    {
        options.Context.EarlySummarizeRatio = earlyRatio;
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

// 输入级权限规则（agent.json 的 approvalRules）：模式匹配 + 三值处置，后写覆盖先写
if (config?.ApprovalRules is { Count: > 0 } ruleConfigs)
{
    foreach (var ruleConfig in ruleConfigs)
    {
        if (ruleConfig?.Match is { } match
            && match.Trim().Length > 0
            && ApprovalRuleSet.ParseAction(ruleConfig.Action) is { } decision)
        {
            options.ApprovalRules.Add(new ApprovalRule(match, decision));
        }
        else
        {
            Console.WriteLine($"[警告] approvalRules 有一条认不出（match/action），已忽略：{ruleConfig?.Match} / {ruleConfig?.Action}");
        }
    }
}

// 越界目录访问授权（external_directory）：配置单显式写了才生效（默认不授予）
if (config?.AllowExternalDirectory is { } allowExternal)
{
    options.AllowExternalDirectory = allowExternal;
}

// 进化节奏（agent.json 的 evolution）：Dream / Distill 按累计回合数触发
if (config?.Evolution is { } evolutionConfig)
{
    if (evolutionConfig.DreamEveryTurns is { } dreamTurns)
    {
        options.Evolution.DreamEveryTurns = dreamTurns;
    }

    if (evolutionConfig.DistillEveryTurns is { } distillTurns)
    {
        options.Evolution.DistillEveryTurns = distillTurns;
    }

    if (evolutionConfig.DistillMinOccurrences is { } minOccurrences)
    {
        options.Evolution.DistillMinOccurrences = minOccurrences;
    }

    if (evolutionConfig.DistillMinSessions is { } minSessions)
    {
        options.Evolution.DistillMinSessions = minSessions;
    }

    if (evolutionConfig.DistillMaxSkills is { } maxSkills)
    {
        options.Evolution.DistillMaxSkills = maxSkills;
    }
}

await using var host = await AgentHost.CreateAsync(options);

// 会话模式：配置单直接写模式 id（work / chat / design / code / write …）。
// 编程模式（code）由此一行启用 —— 字符串 id 钉在会话上，工具面 / 提示词 / 治理随之生效。
if (!string.IsNullOrWhiteSpace(config?.Mode))
{
    var wanted = config!.Mode.Trim();
    if (host.SetCurrentMode(wanted))
    {
        Console.WriteLine($"模式    ：{host.ModeProfile.Name}（{wanted}）");
    }
    else if (!wanted.Equals("chat", StringComparison.OrdinalIgnoreCase)
             && !wanted.Equals("work", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"模式    ：未识别的「{wanted}」，已按默认模式继续（可用：work / chat / design / code / write）");
    }
}

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
    try
    {
        var result = await host.SendAsync(oneShot);
        Console.WriteLine($"[完成] {result.FinalText}");
        Console.WriteLine($"[统计] 步数={result.Steps} 停止原因={result.StopReason}");
        return result.Completed ? 0 : 1;
    }
    catch (Exception ex)
    {
        // 回合级异常也要体面收尾：裸崩会让脚本化验收与自动化跑批全部失联
        Console.WriteLine($"[异常] 回合未完成：{ex.GetType().Name}: {ex.Message}");
        return 1;
    }
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
