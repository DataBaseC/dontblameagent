using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentFramework.Contracts;
using AgentFramework.Data;
using AgentFramework.Host;
using AgentFramework.Tools;

// ═══════════════════════════════════════════════════════════
//  分级记忆 + 工作模式 垂直切片验证
//  契约 / 存储 / 工具 / 模式驱动装配 / 端到端「少看到了什么」/ HTTP
//
//  验的是四句承诺：
//    1. 记忆分三级，按模式加载，**不用的那级根本不读**
//    2. 记忆是真相源（索引可丢、记忆不可丢），append-only
//    3. 闲聊模式真的省：不挂工具、不注任务卡、不治理、只读全局记忆
//    4. 切模式不需要重启
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

var root = Path.Combine(Path.GetTempPath(), "af-memory-verify", Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(root);
Console.WriteLine("═══ 分级记忆 + 工作模式验证 ═══");
Console.WriteLine($"工作目录：{root}");

// ── 1. 模式档 ──────────────────────────────────────────────
Section("1. 模式档（决定这一轮暴露什么）");

var work = AgentModes.Work;
var chat = AgentModes.Chat;

Check("工作模式加载项目记忆", work.MemoryScopes.Count == 1 && work.MemoryScopes[0] == MemoryScope.Project);
Check("★ 闲聊模式只加载全局记忆", chat.MemoryScopes.Count == 1 && chat.MemoryScopes[0] == MemoryScope.Global);
Check("工作模式暴露全部工具", work.AllowedTools is null && work.ExposesTools);
Check("★ 闲聊模式不挂任何工具", chat.AllowedTools is not null && chat.AllowedTools.Count == 0 && !chat.ExposesTools);
Check("工作模式注入任务卡", work.InjectTaskCard);
Check("★ 闲聊模式不注入任务卡", !chat.InjectTaskCard);
Check("工作模式启用上下文治理", work.ContextGovernance);
Check("★ 闲聊模式不做治理（闲聊没有长任务）", !chat.ContextGovernance);
Check("闲聊模式带模式说明", chat.SystemPromptSuffix?.Contains("闲聊模式") == true);
Check("工作模式带长任务行为说明（本批新增：骨架化提醒 + 子 Agent 派工纪律）",
    work.SystemPromptSuffix is not null
        && work.SystemPromptSuffix.Contains("search_history")
        && work.SystemPromptSuffix.Contains("spawn_subagent"));
Check("For() 能按枚举取档", AgentModes.For(AgentMode.Chat).Mode == AgentMode.Chat && AgentModes.For(AgentMode.Work).Mode == AgentMode.Work);

// ── 2. 记忆存储 ────────────────────────────────────────────
Section("2. JSONL 记忆存储（append-only 真相源）");

var memoryDir = Path.Combine(root, "memory");
var store = new JsonlMemoryStore(new MemoryStoreOptions
{
    GlobalPath = Path.Combine(memoryDir, "global.jsonl"),
    ProjectPath = Path.Combine(root, "ws", ".agent-memory", "project.jsonl"),
});

Check("存储实现可识别", store.Kind == "jsonl");

await store.AppendAsync(MemoryScope.Global, "主人喜欢短句，讨厌客套话，称呼用「主人」", ["偏好"], "s-1", "user");
await store.AppendAsync(MemoryScope.Global, "主人有一块 RX 9070，本地跑 LM Studio", ["硬件"], "s-1", "agent");
await store.AppendAsync(MemoryScope.Project, "本项目的验证命令是 npm test", ["约定"], "s-2", "user");

Check("追加后按层级计数正确", await store.CountAsync(MemoryScope.Global) == 2 && await store.CountAsync(MemoryScope.Project) == 1);

var loaded = await store.LoadAsync(MemoryScope.Global, 10);
Check("读回内容正确", loaded.Count == 2 && loaded[0].Text.Contains("短句"));
Check("★ 层级互相隔离", !(await store.LoadAsync(MemoryScope.Project, 10)).Any(e => e.Text.Contains("短句")));
Check("保留来源会话", loaded[0].SourceSession == "s-1");
Check("保留来源方（用户 vs agent）", loaded[0].Source == "user" && loaded[1].Source == "agent");
Check("保留标签", loaded[0].Tags.Contains("偏好"));

var limited = await store.LoadAsync(MemoryScope.Global, 1);
Check("加载受条数上限约束", limited.Count == 1);
Check("加载取最近的", limited[0].Text.Contains("RX 9070"));

var found = await store.SearchAsync("短句", MemoryScope.Global, 10);
Check("关键词检索命中", found.Count == 1);
Check("大小写不敏感", (await store.SearchAsync("rx 9070", MemoryScope.Global, 10)).Count == 1);
Check("可按标签检索", (await store.SearchAsync("硬件", MemoryScope.Global, 10)).Count == 1);
Check("★ 跨层级检索（scope 传 null）", (await store.SearchAsync("本项目", null, 10)).Count == 1);
Check("无命中返回空", (await store.SearchAsync("这个词不存在zzz", null, 10)).Count == 0);

// 坏行不该炸：与事件日志同一条纪律
await File.AppendAllTextAsync(Path.Combine(memoryDir, "global.jsonl"), "{ 这不是 json\n");
Check("★ 坏行被跳过，已有记忆仍可读", await store.CountAsync(MemoryScope.Global) == 2);

var longText = new string('长', 3000);
var truncated = await store.AppendAsync(MemoryScope.Global, longText);
Check("超长记忆被截断", truncated.Text.Length <= 2000, $"{truncated.Text.Length}");

Check("空存储实现不抛异常", await NullMemoryStore.Instance.CountAsync(MemoryScope.Global) == 0);

// ── 2.5 记忆事件化：改主意（槽位覆盖）与撤销 ────────────────
Section("2.5 记忆事件化：改主意与撤销（append-only 折叠出当前视图）");

var evoStore = new JsonlMemoryStore(new MemoryStoreOptions
{
    GlobalPath = Path.Combine(root, "evo", "g.jsonl"),
    ProjectPath = Path.Combine(root, "evo", "p.jsonl"),
});
var evoProjectPath = Path.Combine(root, "evo", "p.jsonl");

var npmEntry = await evoStore.AppendAsync(MemoryScope.Project, "构建命令是 npm run build", null, null, "user", "构建命令");
var pnpmEntry = await evoStore.AppendAsync(MemoryScope.Project, "构建命令改成 pnpm build", null, null, "user", "构建命令");

Check("★ 同槽位的新事实自动取代旧事实（改主意不再并存矛盾）", await evoStore.CountAsync(MemoryScope.Project) == 1);
var slotView = await evoStore.LoadAsync(MemoryScope.Project, 10);
Check("★ 当前视图里只剩最新那条", slotView.Count == 1 && slotView[0].Id == pnpmEntry.Id && slotView[0].Text.Contains("pnpm"));
Check("★ 旧事实仍在文件里（append-only，可审计、可回放）",
    File.ReadAllLines(evoProjectPath).Any(l => l.Contains(npmEntry.Id, StringComparison.Ordinal)));

await evoStore.AppendAsync(MemoryScope.Project, "包管理器用 pnpm", null, null, "user", "包管理器");
Check("★ 不同槽位互不干扰", await evoStore.CountAsync(MemoryScope.Project) == 2);

var noteA = await evoStore.AppendAsync(MemoryScope.Project, "随手记一条 A", null, null, "user");
var noteB = await evoStore.AppendAsync(MemoryScope.Project, "随手记一条 B", null, null, "user");
Check("无槽位的记忆各自独立（不会被误覆盖）", await evoStore.CountAsync(MemoryScope.Project) == 4);

var retractEvent = await evoStore.RetractAsync(MemoryScope.Project, noteB.Id);
Check("★ 撤销成功（返回一条 retract 事件）", retractEvent is not null && retractEvent.IsRetraction);
Check("★ 撤销后立刻从当前视图消失", await evoStore.CountAsync(MemoryScope.Project) == 3);
Check("★ 撤销是追加而非删除：原记录仍在文件里",
    File.ReadAllLines(evoProjectPath).Any(l => l.Contains(noteB.Id, StringComparison.Ordinal)));
Check("撤销不存在的 id 返回 null（不算错误）", await evoStore.RetractAsync(MemoryScope.Project, "不存在的id") is null);
Check("重复撤销同一条不再产生新事件", (await evoStore.RetractAsync(MemoryScope.Project, noteB.Id)) is null);

// 工具面：remember 的 slot、forget 的撤销
var evoRemember = new RememberTool(evoStore, () => MemoryScope.Project, () => "s-x");
var firstWrite = await evoRemember.InvokeAsync(new ToolInvocation("remember", new Dictionary<string, string?>
{
    ["text"] = "测试命令是 npm test",
    ["slot"] = "测试命令",
}));
Check("remember 回执里说明了槽位", firstWrite.Success && firstWrite.Output!.Contains("测试命令"));

await evoRemember.InvokeAsync(new ToolInvocation("remember", new Dictionary<string, string?>
{
    ["text"] = "测试命令改成了 pnpm test",
    ["slot"] = "测试命令",
}));
var afterSlot = await evoStore.SearchAsync("测试命令", MemoryScope.Project, 10);
Check("★ remember 的 slot 生效：旧说法已被取代、且最新的排最前",
    afterSlot.Count > 0
    && afterSlot[0].Text.Contains("pnpm test")
    && !afterSlot.Any(e => e.Text.Contains("命令是 npm", StringComparison.Ordinal)));

var evoForget = new ForgetTool(evoStore, () => [MemoryScope.Project]);
var forgetResult = await evoForget.InvokeAsync(new ToolInvocation("forget", new Dictionary<string, string?>
{
    ["id"] = noteA.Id,
}));
Check("★ forget 工具能撤销", forgetResult.Success && forgetResult.Output!.Contains("已撤销"));

var forgetAgain = await evoForget.InvokeAsync(new ToolInvocation("forget", new Dictionary<string, string?>
{
    ["id"] = noteA.Id,
}));
Check("重复撤销给出提示而非报错", forgetAgain.Success && forgetAgain.Output!.Contains("没有找到"));

Check("forget 缺参数时报错",
    !(await evoForget.InvokeAsync(new ToolInvocation("forget", new Dictionary<string, string?>()))).Success);

// ── 3. 记忆工具 ────────────────────────────────────────────
Section("3. remember / recall_memory");

var toolStore = new JsonlMemoryStore(new MemoryStoreOptions
{
    GlobalPath = Path.Combine(root, "t", "global.jsonl"),
    ProjectPath = Path.Combine(root, "t", "project.jsonl"),
});

var rememberWork = new RememberTool(toolStore, () => MemoryScope.Project, () => "s-9");
var rememberChat = new RememberTool(toolStore, () => MemoryScope.Global, () => "s-9");

var wrote = await rememberWork.InvokeAsync(new ToolInvocation("remember", new Dictionary<string, string?>
{
    ["text"] = "这个项目用 net10.0",
}));
Check("remember 写入成功", wrote.Success);
Check("★ 工作模式写进项目层级", await toolStore.CountAsync(MemoryScope.Project) == 1);
Check("回执里说明写到了哪一级", wrote.Output!.Contains(MemoryScope.Project));

await rememberChat.InvokeAsync(new ToolInvocation("remember", new Dictionary<string, string?>
{
    ["text"] = "主人怕吵",
}));
Check("★ 闲聊模式写进全局层级", await toolStore.CountAsync(MemoryScope.Global) == 1);

var emptyWrite = await rememberWork.InvokeAsync(new ToolInvocation("remember", new Dictionary<string, string?>()));
Check("remember 缺参数时报错", !emptyWrite.Success);

var recall = new RecallMemoryTool(new ToolkitOptions { WorkspaceRoot = root }, toolStore, () => [MemoryScope.Project]);
var recalled = await recall.InvokeAsync(new ToolInvocation("recall_memory", new Dictionary<string, string?>
{
    ["query"] = "net10",
}));
Check("recall 检索命中", recalled.Success && recalled.Output!.Contains("net10.0"));
Check("结果标出所属层级", recalled.Output!.Contains(MemoryScope.Project));
Check("★ 结果带 id（forget 的入口，也是「这条指哪一条」的唯一凭据）", recalled.Output!.Contains("（id "));

var listed = await recall.InvokeAsync(new ToolInvocation("recall_memory", new Dictionary<string, string?>()));
Check("留空则列出最近记忆", listed.Success && listed.Output!.Contains("net10.0"));

var missed = await recall.InvokeAsync(new ToolInvocation("recall_memory", new Dictionary<string, string?>
{
    ["query"] = "zzz不存在",
}));
Check("无命中给出提示", missed.Success && missed.Output!.Contains("没有找到"));

var noScope = new RecallMemoryTool(new ToolkitOptions { WorkspaceRoot = root }, toolStore, () => []);
Check("没有加载任何层级时明确失败", !(await noScope.InvokeAsync(new ToolInvocation("recall_memory", new Dictionary<string, string?>()))).Success);

var emptyStore = new JsonlMemoryStore(new MemoryStoreOptions
{
    GlobalPath = Path.Combine(root, "empty", "g.jsonl"),
    ProjectPath = Path.Combine(root, "empty", "p.jsonl"),
});
var emptyRecall = new RecallMemoryTool(new ToolkitOptions { WorkspaceRoot = root }, emptyStore, () => [MemoryScope.Global]);
Check("记忆库为空时给出提示",
    (await emptyRecall.InvokeAsync(new ToolInvocation("recall_memory", new Dictionary<string, string?>()))).Output!.Contains("还是空的"));

// ── 4. 宿主：模式驱动装配 ──────────────────────────────────
Section("4. 宿主：模式决定暴露面");

var hostRoot = Path.Combine(root, "host");
var hostWorkspace = Path.Combine(hostRoot, "ws");
Directory.CreateDirectory(hostWorkspace);

var client = new RecordingClient();
var hostOptions = new HostOptions
{
    WorkspaceRoot = hostWorkspace,
    SessionsDir = Path.Combine(hostRoot, "sessions"),
    SessionId = "m",
    LlmOverride = client,
    Context = new ContextOptions { TokenBudget = 50, CompressionTriggerRatio = 0.8 },
};

var host = await AgentHost.CreateAsync(hostOptions);

Check("装配时带上了记忆工具", host.ToolNames.Contains("remember") && host.ToolNames.Contains("recall_memory"));
Check("工作模式暴露全部工具", host.ExposedToolNames.Count == host.ToolNames.Count && host.ExposedToolNames.Count > 0,
    $"{host.ExposedToolNames.Count} 个");

// 预置两级的记忆，验证"只读该读的那一级"
await host.Memory.AppendAsync(MemoryScope.Global, "全局：主人喜欢短句", null, null, "user");
await host.Memory.AppendAsync(MemoryScope.Project, "项目：验证命令是 npm test", null, null, "user");

await host.SendAsync("你好");
var workRequest = client.LastRequest!;
var workText = string.Join('\n', workRequest.Messages.Select(m => m.Content ?? ""));

Check("★ 工作模式：注入了项目记忆", workText.Contains("验证命令是 npm test"));
Check("★ 工作模式：不注入全局记忆（非工作模式才启用）", !workText.Contains("主人喜欢短句"));
Check("首轮还没有任务可卡（任务卡从第二轮起才有意义）", !workText.Contains("【任务卡】"));

client.Reset();
await host.SendAsync("再问一句");
Check("★ 工作模式：第二轮起注入任务卡",
    string.Join('\n', client.LastRequest!.Messages.Select(m => m.Content ?? "")).Contains("【任务卡】"));
Check("工作模式：工具 schema 照常发给模型", workRequest.Tools.Count > 0, $"{workRequest.Tools.Count} 个");
Check("工作模式：没有模式说明", !workText.Contains("当前是闲聊模式"));

// ── 切换模式：不重启、不重装 ───────────────────────────────
var beforeMode = host.Mode;
host.SetMode(AgentMode.Chat);
Check("★ 切换模式后宿主实例不变", ReferenceEquals(host, host) && host.Mode == AgentMode.Chat && beforeMode == AgentMode.Work);
Check("★ 闲聊模式暴露 0 个工具", host.ExposedToolNames.Count == 0);
Check("闲聊模式下工具仍然注册着（只是不暴露）", host.ToolNames.Count > 0);

client.Reset();
await host.SendAsync("随便聊聊");

var chatRequest = client.LastRequest!;
var chatText = string.Join('\n', chatRequest.Messages.Select(m => m.Content ?? ""));

Check("★ 闲聊模式：一个工具 schema 都不发（最实在的一笔省）", chatRequest.Tools.Count == 0, $"{chatRequest.Tools.Count} 个");
Check("★ 闲聊模式：只读全局记忆", chatText.Contains("主人喜欢短句") && !chatText.Contains("验证命令是 npm test"));
Check("★ 闲聊模式：不注入任务卡", !chatText.Contains("【任务卡】"));
Check("闲聊模式：带上了模式说明", chatText.Contains("当前是闲聊模式"));

Check("★ 闲聊模式：模型没看到工具，也没产生压缩事件",
    !host.Events().OfType<ContextCompactedEvent>().Any());

// 切回工作模式
host.SetMode(AgentMode.Work);
Check("切回工作模式后暴露面恢复", host.ExposedToolNames.Count == host.ToolNames.Count);

// ── 5. 记忆持久化 ──────────────────────────────────────────
Section("5. 记忆持久化（真相源，重启不丢）");

Check("项目记忆文件在工作区内（跟着项目走）",
    File.Exists(Path.Combine(hostWorkspace, ".agent-memory", "project.jsonl")));
Check("全局记忆文件在会话目录下",
    File.Exists(Path.Combine(hostRoot, "sessions", "memory", "global.jsonl")));

// Windows 上日志是排他写：先关掉第一个实例再「重启」，才符合重启的语义。
await host.DisposeAsync();

await using (var reopened = await AgentHost.CreateAsync(hostOptions))
{
    Check("★ 重新装配后项目记忆仍在", await reopened.Memory.CountAsync(MemoryScope.Project) == 1);
    Check("★ 重新装配后全局记忆仍在", await reopened.Memory.CountAsync(MemoryScope.Global) == 1);
    Check("默认模式是工作模式", reopened.Mode == AgentMode.Work);
}

// ── 6. HTTP：模式可切换、可观测 ────────────────────────────
Section("6. HTTP：模式切换与可观测性");

var webRoot = Path.Combine(root, "web");
var webOptions = new HostOptions
{
    WorkspaceRoot = Path.Combine(webRoot, "ws"),
    SessionsDir = Path.Combine(webRoot, "sessions"),
    SessionId = "web",
    LlmOverride = new RecordingClient(),
};

await using var webHost = await AgentHost.CreateAsync(webOptions);
var port = PickFreePort();
using var server = new WebUiServer(webHost, webOptions, port);
using var serverCts = new CancellationTokenSource();
_ = server.RunAsync(serverCts.Token);
await Task.Delay(500);

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

var page = await http.GetStringAsync(server.Url);
Check("★ 页面带新建会话选模式（创建时选定，无顶栏切换器）", page.Contains("mode-pick", StringComparison.Ordinal) && page.Contains("/api/modes", StringComparison.Ordinal) && !page.Contains("id=\"pill-mode\"", StringComparison.Ordinal));
Check("页面带模式差异说明（弹层副标题 + 模式卡描述由 /api/modes 提供）", page.Contains("模式决定暴露哪些工具与记忆层级", StringComparison.Ordinal));

using (var doc = JsonDocument.Parse(await http.GetStringAsync(server.Url + "api/status")))
{
    var mode = doc.RootElement.GetProperty("mode");
    Check("status 暴露当前模式", mode.GetProperty("current").GetString() == "work");
    Check("status 暴露暴露的工具集", mode.GetProperty("tools").GetArrayLength() > 0);
    Check("status 暴露记忆层级", mode.GetProperty("memoryScopes").GetArrayLength() == 1);
    Check("status 暴露治理开关", mode.GetProperty("governance").GetBoolean());
}

var switched = await http.PostAsync(
    server.Url + "api/mode",
    new StringContent("""{"mode":"chat"}""", System.Text.Encoding.UTF8, "application/json"));

Check("切模式接口返回 ok", switched.IsSuccessStatusCode);

using (var doc = JsonDocument.Parse(await switched.Content.ReadAsStringAsync()))
{
    var mode = doc.RootElement.GetProperty("mode");
    Check("★ 切换后立刻变成闲聊模式", mode.GetProperty("current").GetString() == "chat");
    Check("★ 返回的暴露工具数为 0", mode.GetProperty("tools").GetArrayLength() == 0);
    Check("返回记忆层级为 global", mode.GetProperty("memoryScopes")[0].GetString() == MemoryScope.Global);
    Check("返回治理已关", !mode.GetProperty("governance").GetBoolean());
}

using (var doc = JsonDocument.Parse(await http.GetStringAsync(server.Url + "api/status")))
{
    Check("★ 状态接口反映新模式（无需重启）",
        doc.RootElement.GetProperty("mode").GetProperty("current").GetString() == "chat");
}

var bad = await http.PostAsync(
    server.Url + "api/mode",
    new StringContent("""{"nope":1}""", System.Text.Encoding.UTF8, "application/json"));
Check("缺 mode 参数返回 400", (int)bad.StatusCode == 400, $"{(int)bad.StatusCode}");

// ── 7. 上下文装配：冻结段 / 动态段（缓存安全）────────────────
Section("7. 上下文装配：冻结段在前、动态段在后（前缀缓存安全）");

var asmRoot = Path.Combine(root, "asm");
var asmClient = new RecordingClient();
var asmOptions = new HostOptions
{
    WorkspaceRoot = Path.Combine(asmRoot, "ws"),
    SessionsDir = Path.Combine(asmRoot, "sessions"),
    SessionId = "asm",
    LlmOverride = asmClient,
};
Directory.CreateDirectory(asmOptions.WorkspaceRoot);

await using var asmHost = await AgentHost.CreateAsync(asmOptions);
await asmHost.Memory.AppendAsync(MemoryScope.Project, "本项目用 net10.0 与 xunit", ["约定"], null, "user");
await asmHost.Memory.AppendAsync(MemoryScope.Project, "打包必须排除 bin 与 obj", ["约定"], null, "user");
await asmHost.Memory.AppendAsync(MemoryScope.Global, "全局：主人喜欢短句", null, null, "user");

await asmHost.SendAsync("打包脚本要怎么写？");

AssembledContext assembly = asmHost.LastAssembly
    ?? throw new InvalidOperationException("SendAsync 之后应当记录装配结果");

var frozenCount = assembly.Frozen.Count;
Check("★ 装配结果被记录（可诊断「模型到底看到什么」）", frozenCount > 0, $"{frozenCount} 条冻结");

var frozenText = string.Join('\n', assembly.Frozen.Select(m => m.Content ?? ""));
var dynamicText = string.Join('\n', assembly.Dynamic.Select(m => m.Content ?? ""));

Check("★ 索引卡在冻结段（进缓存前缀）", frozenText.Contains("记忆·常驻索引"));
Check("★ 索引卡只含本项目记忆，不含全局记忆", frozenText.Contains("打包必须排除 bin") && !frozenText.Contains("主人喜欢短句"));
Check("★ 检索块在动态段（放尾部，不进前缀）", dynamicText.Contains("相关记忆·本轮检索"));
Check("★ 检索命中了与当前输入相关的记忆（先查再取，而非全量搬运）", dynamicText.Contains("打包必须排除 bin"));

// 前缀稳定性：状态没变 → 冻结段逐字节一致，缓存才可能命中
asmClient.Reset();
await asmHost.SendAsync("net10.0 的事再确认一下");
var second = asmHost.LastAssembly!;
var secondFrozenText = string.Join('\n', second.Frozen.Select(m => m.Content ?? ""));

Check("★ 状态不变时冻结段逐字节一致（前缀稳定）", secondFrozenText == frozenText);
Check("★ 动态段随输入变化（这才是允许每轮变的地方）",
    string.Join('\n', second.Dynamic.Select(m => m.Content ?? "")).Contains("net10.0"));

// 记忆真变了 → 冻结段跟着变。这一次前缀失效是**应该**的：
// 新记忆必须能被看见，而写入是低频、显式的动作。
await asmHost.Memory.AppendAsync(MemoryScope.Project, "部署走绿色目录版", null, null, "user");
asmClient.Reset();
await asmHost.SendAsync("再问一句");
var thirdFrozenText = string.Join('\n', asmHost.LastAssembly!.Frozen.Select(m => m.Content ?? ""));
Check("★ 记忆真变化时才更新冻结段（否则前缀一直稳定）", thirdFrozenText.Contains("部署走绿色目录版"));

// 闲聊模式：不检索（RecallLimit = 0），连文件扫描都省掉
asmHost.SetMode(AgentMode.Chat);
asmClient.Reset();
await asmHost.SendAsync("随便聊聊");

var chatDynamicText = string.Join('\n', asmHost.LastAssembly!.Dynamic.Select(m => m.Content ?? ""));
Check("★ 闲聊模式：动态段里没有检索块（一次文件扫描都不做）", !chatDynamicText.Contains("相关记忆·本轮检索"));

// 来源标注：框架不替模型判断真假，只把来源摆出来（防污染里最便宜的一招）
Check("用户说的记忆不标注（默认可信、也省 token）", !frozenText.Contains("我自己记的"));

asmHost.SetMode(AgentMode.Work);
await asmHost.Memory.AppendAsync(MemoryScope.Project, "我自己推断的：这个项目偏爱不可变数据", null, null, "agent");
asmClient.Reset();
await asmHost.SendAsync("确认一下项目风格");
var markedFrozen = string.Join('\n', asmHost.LastAssembly!.Frozen.Select(m => m.Content ?? ""));
Check("★ agent 自记的记忆被标出来源", markedFrozen.Contains("我自己记的"));

// ── 8. 工作小本本（人机共写的计划本）───────────────────────
Section("8. 工作小本本：计划外化，且人可共写");

// 纯逻辑：只动指定小节，其余一字不改
var withGoal = WorkNotes.UpdateSection(WorkNotes.Template, "目标", "- 把长任务治理做完");
Check("★ 更新小节后其他小节原样保留",
    withGoal.Contains("## 计划") && withGoal.Contains("## 阻塞") && withGoal.Contains("- 把长任务治理做完"));

var appendedOnce = WorkNotes.UpdateSection(withGoal, "随手记", "- 想到：折叠要有门槛", append: true);
var appendedTwice = WorkNotes.UpdateSection(appendedOnce, "随手记", "- 又想到：占位符要极简", append: true);
Check("★ 追加模式保留既有条目", appendedTwice.Contains("折叠要有门槛") && appendedTwice.Contains("占位符要极简"));

var replacedGoal = WorkNotes.UpdateSection(appendedTwice, "目标", "- 改成：先修缓存纪律");
Check("★ 替换模式只换该小节", replacedGoal.Contains("先修缓存纪律") && !replacedGoal.Contains("把长任务治理做完"));

var summaryText = WorkNotes.BuildSummary(replacedGoal, 600);
Check("★ 摘要含各小节要点", summaryText is not null && summaryText.Contains("目标") && summaryText.Contains("先修缓存纪律"));
Check("空本子不产出摘要（默认零成本）", WorkNotes.BuildSummary(WorkNotes.Template, 600) is null);

// 工具 + 人机共写
var notesFile = Path.Combine(root, "notes", WorkNotes.DefaultFileName);
var notesTool = new UpdateNotesTool(() => notesFile);

var created = await notesTool.InvokeAsync(new ToolInvocation("update_notes", new Dictionary<string, string?>
{
    ["section"] = "计划",
    ["content"] = "- 先做记忆平面\n- 再做小本本",
}));
Check("★ 工具能建本子并写入小节", created.Success && File.Exists(notesFile));

// 模拟人中途手改
await File.WriteAllTextAsync(notesFile, await File.ReadAllTextAsync(notesFile) + "\n人手动加的一行：别删我\n");

await notesTool.InvokeAsync(new ToolInvocation("update_notes", new Dictionary<string, string?>
{
    ["section"] = "进度",
    ["content"] = "- 记忆平面已完成",
}));
var afterAgent = await File.ReadAllTextAsync(notesFile);
Check("★ 人手的编辑被保留（agent 只动自己的小节）", afterAgent.Contains("别删我"));
Check("agent 的小节也写进去了", afterAgent.Contains("记忆平面已完成"));

Check("工具缺参数时报错",
    !(await notesTool.InvokeAsync(new ToolInvocation("update_notes", new Dictionary<string, string?>()))).Success);

// 装配：摘要进动态段、不进前缀
var notesRoot = Path.Combine(root, "nh");
var notesClient = new RecordingClient();
var notesOptions = new HostOptions
{
    WorkspaceRoot = Path.Combine(notesRoot, "ws"),
    SessionsDir = Path.Combine(notesRoot, "sessions"),
    SessionId = "nh",
    LlmOverride = notesClient,
};
Directory.CreateDirectory(notesOptions.WorkspaceRoot);

await using var notesHost = await AgentHost.CreateAsync(notesOptions);
Check("装配时带上了小本本工具", notesHost.ToolNames.Contains("update_notes"));

await notesHost.SendAsync("第一句");
var noNotes = string.Join('\n', notesHost.LastAssembly!.Dynamic.Select(m => m.Content ?? ""));
Check("小本本不存在时不注入（默认零成本）", !noNotes.Contains("【工作小本本】"));

var hostNotesTool = new UpdateNotesTool(
    () => Path.Combine(notesOptions.WorkspaceRoot, WorkNotes.DefaultFileName));
await hostNotesTool.InvokeAsync(new ToolInvocation("update_notes", new Dictionary<string, string?>
{
    ["section"] = "目标",
    ["content"] = "- 验证小本本复述",
}));

notesClient.Reset();
await notesHost.SendAsync("第二句");
var notesAssembly = notesHost.LastAssembly!;
var notesDynamic = string.Join('\n', notesAssembly.Dynamic.Select(m => m.Content ?? ""));
var notesFrozen = string.Join('\n', notesAssembly.Frozen.Select(m => m.Content ?? ""));

Check("★ 小本本摘要被复述进上下文", notesDynamic.Contains("【工作小本本】"));
Check("★ 小本本摘要走动态段、不进缓存前缀", !notesFrozen.Contains("【工作小本本】"));

notesHost.SetMode(AgentMode.Chat);
await notesHost.SendAsync("闲聊一句");
Check("★ 闲聊模式不注入小本本（没有计划可复述）",
    !string.Join('\n', notesHost.LastAssembly!.Dynamic.Select(m => m.Content ?? "")).Contains("【工作小本本】"));

// ── 9. 记忆温度：命中升温 + 热度排序（本批新增）──────────────
Section("9. 记忆温度：命中升温 + 热度排序");

var tempStore = new JsonlMemoryStore(new MemoryStoreOptions
{
    GlobalPath = Path.Combine(root, "mem-t", "global.jsonl"),
    ProjectPath = Path.Combine(root, "mem-t", "project.jsonl"),
});

// 三条记忆：a 老而常用（被多次召回）、b 新而冷、c 被用户置顶。
var oldButHot = await tempStore.AppendAsync(MemoryScope.Project, "部署命令是 ./deploy.sh --prod（老而常用）", source: "user");
var newButCold = await tempStore.AppendAsync(MemoryScope.Project, "上周随手记的一条流水账（新而冷）", source: "user");
var important = await tempStore.AppendAsync(MemoryScope.Project, "用户明确要求：数据库只读账号不得访问生产（置顶）", source: "user");

await tempStore.RecordHitAsync(MemoryScope.Project, oldButHot.Id, delta: 5);
await tempStore.RecordHitAsync(MemoryScope.Project, oldButHot.Id, delta: 5);
await tempStore.RecordHitAsync(MemoryScope.Project, important.Id, delta: 0, markImportant: true);

// 折叠视图：score 事件不出现，热度叠加到条目本身。
var foldedView = await tempStore.LoadAsync(MemoryScope.Project, 10);
var hotEntry = foldedView.First(e => e.Id == oldButHot.Id);
Check("★ score 事件不进视图、热度叠加到条目", hotEntry.Score == 11, $"Score={hotEntry.Score}");
Check("★ 置顶标记进了视图",
    foldedView.First(e => e.Id == important.Id).IsImportant);
Check("热度事件化：文件仍是 append-only（可回放）", File.ReadAllLines(Path.Combine(root, "mem-t", "project.jsonl")).Length == 6,
    $"实际 {File.ReadAllLines(Path.Combine(root, "mem-t", "project.jsonl")).Length} 行（3 assert + 3 score）");

// 热度排序：置顶 > 热度 > 时间 —— 老而常用应该排在最前，新而冷垫底（同分时）。
var ordered = MemoryPrioritizer.Order(foldedView, MemoryPriorityStrategy.Temperature);
Check("★ 温度排序把老而常用排到新而冷前面",
    IndexOfId(ordered, oldButHot.Id) < IndexOfId(ordered, newButCold.Id));
Check("置顶条目排最前", ordered[0].Id == important.Id);
Check("Insertion 策略保持 v3.4 行为（原序返回）",
    MemoryPrioritizer.Order(foldedView, MemoryPriorityStrategy.Insertion).Select(e => e.Id)
        .SequenceEqual(foldedView.Select(e => e.Id)));

// 撤销后升温无效：目标不存在 → null，且不写垃圾事件。
var retracted = await tempStore.RetractAsync(MemoryScope.Project, oldButHot.Id, "user");
Check("撤销成功", retracted is not null);
var hitAfterRetract = await tempStore.RecordHitAsync(MemoryScope.Project, oldButHot.Id, delta: 1);
Check("★ 撤销后的条目升温返回 null（不写垃圾事件）", hitAfterRetract is null);
Check("撤销后热度事件未增加",
    File.ReadAllLines(Path.Combine(root, "mem-t", "project.jsonl")).Last().Contains("\"retract\""));

// 跨进程重开：热度从文件重算（真相源在文件，不在内存）。
var reopenedStore = new JsonlMemoryStore(new MemoryStoreOptions
{
    GlobalPath = Path.Combine(root, "mem-t", "global.jsonl"),
    ProjectPath = Path.Combine(root, "mem-t", "project.jsonl"),
});
var reopenedView = await reopenedStore.LoadAsync(MemoryScope.Project, 10);
var reopenedHot = reopenedView.FirstOrDefault(e => e.Id == newButCold.Id);
Check("重开存储后视图正常（新条目仍在）", reopenedHot is not null);

// remember 的 important 参数走同一条 score 通道。
var pinned = await tempStore.AppendAsync(MemoryScope.Project, "构建必须用 Release 模式（写入即置顶）");
await new RememberTool(tempStore, () => MemoryScope.Project, () => "t")
    .InvokeAsync(new ToolInvocation("remember", new Dictionary<string, string?>
    {
        ["text"] = "接口版本号从 v2 起固定写进 URL（写入即置顶）",
        ["important"] = "true",
    }));
var afterImportant = await tempStore.LoadAsync(MemoryScope.Project, 10);
Check("★ remember(important=true) 置顶生效",
    afterImportant.First(e => e.Text.Contains("v2 起固定写进 URL")).IsImportant);

// recall_memory 命中即升温。
var beforeHits = (await tempStore.LoadAsync(MemoryScope.Project, 20))
    .First(e => e.Text.Contains("Release 模式")).Score;
var recallResult = await new RecallMemoryTool(
    new ToolkitOptions { WorkspaceRoot = root },
    tempStore,
    () => [MemoryScope.Project])
    .InvokeAsync(new ToolInvocation("recall_memory", new Dictionary<string, string?>
    {
        ["query"] = "Release 模式",
    }));
Check("recall 命中返回内容", recallResult.Success && recallResult.Output!.Contains("Release 模式"));
var afterHits = (await tempStore.LoadAsync(MemoryScope.Project, 20))
    .First(e => e.Text.Contains("Release 模式")).Score;
Check("★ recall 命中后热度上升", afterHits > beforeHits, $"{beforeHits} → {afterHits}");

// ── 10. 记忆分层：归档 / 升级 / 合并 / 清扫（本批新增）──────
Section("10. 记忆分层：归档 / 升级 / 合并 / 清扫");

var tierStore = new JsonlMemoryStore(new MemoryStoreOptions
{
    GlobalPath = Path.Combine(root, "mem-tier", "global.jsonl"),
    ProjectPath = Path.Combine(root, "mem-tier", "project.jsonl"),
});

var hot = await tierStore.AppendAsync(MemoryScope.Project, "项目构建命令（常被引用）", source: "user");
var cold1 = await tierStore.AppendAsync(MemoryScope.Project, "旧的尝试方案 A（已废弃）", source: "user");
var cold2 = await tierStore.AppendAsync(MemoryScope.Project, "旧的尝试方案 B（已废弃）", source: "user");
var pinnedMem = await tierStore.AppendAsync(MemoryScope.Project, "用户偏好：回复用中文（置顶）", source: "user");
await tierStore.RecordHitAsync(MemoryScope.Project, hot.Id, delta: 4);
await tierStore.RecordHitAsync(MemoryScope.Project, pinnedMem.Id, delta: 0, markImportant: true);

// 归档：离开主视图，进归档层，事件可追溯
var archived = await tierStore.ArchiveAsync(MemoryScope.Project, cold1.Id, "user");
Check("★ 归档返回被归档条目", archived is not null && archived.Id == cold1.Id);
var tierView = await tierStore.LoadAsync(MemoryScope.Project, 20);
Check("★ 归档后离开主视图", tierView.All(e => e.Id != cold1.Id));
var archivedView = await tierStore.LoadArchivedAsync(MemoryScope.Project, 20);
Check("★ 归档层可见（原文保留）", archivedView.Any(e => e.Id == cold1.Id && e.Text.Contains("方案 A")));
Check("事件仍是 append-only（archive 事件在文件里）",
    File.ReadAllLines(Path.Combine(root, "mem-tier", "project.jsonl")).Any(l => l.Contains("\"archive\"")));

// 重复归档同一 id → null（不写重复事件）
var linesBefore = File.ReadAllLines(Path.Combine(root, "mem-tier", "project.jsonl")).Length;
Check("重复归档返回 null", await tierStore.ArchiveAsync(MemoryScope.Project, cold1.Id) is null);
Check("重复归档不写事件",
    File.ReadAllLines(Path.Combine(root, "mem-tier", "project.jsonl")).Length == linesBefore);

// 恢复：回主视图
var restored = await tierStore.RestoreAsync(MemoryScope.Project, cold1.Id, "user");
Check("★ 恢复回到主视图", restored is not null && (await tierStore.LoadAsync(MemoryScope.Project, 20)).Any(e => e.Id == cold1.Id));
Check("恢复后归档层为空（该条）", !(await tierStore.LoadArchivedAsync(MemoryScope.Project, 20)).Any(e => e.Id == cold1.Id));

// 清扫：只动「老 && 冷 && 未置顶」—— hot 有 5 分、pinned 置顶，都不该被扫走
// 合并：两条旧方案 → 一条"结论"记忆（来源 retract + 新 assert + src: 凭据）
// 注意合并必须在来源还活跃时做 —— 合并整理的是主视图（归档里的先恢复再合并）。
var merged = await tierStore.MergeAsync(
    MemoryScope.Project,
    [cold1.Id, cold2.Id],
    "方案 A/B 均已验证无效：构建必须走 scripts/build.sh（结论由合并得出）",
    source: "user");
Check("★ 合并返回新记忆", merged.Text.Contains("build.sh") && merged.Tags.Any(t => t == "src:" + cold1.Id) && merged.Tags.Any(t => t == "src:" + cold2.Id));
Check("★ 来源热度继承（max：两条来源都是 1）", merged.Score == 1, $"merged={merged.Score}");
var afterMerge = await tierStore.LoadAsync(MemoryScope.Project, 20);
Check("★ 来源条目已 retract 退出主视图", afterMerge.All(e => e.Id != cold1.Id && e.Id != cold2.Id));
Check("★ 合并结果在主视图且可检索",
    (await tierStore.SearchAsync("build.sh", MemoryScope.Project, 5)).Any(e => e.Id == merged.Id));
Check("合并同样是事件（retract×2）",
    File.ReadAllLines(Path.Combine(root, "mem-tier", "project.jsonl"))
        .Count(l => l.Contains("\"retract\"")) >= 2);

// v3.5 审查 P2：合并要**继承置顶** —— 否则合并一条被用户显式钉住的约定后，
// IsImportant 就丢了，下次清扫会把它当冷条目扫走（钉住的意义荡然无存）。
var pinnedSrc = await tierStore.AppendAsync(MemoryScope.Project, "置顶约定：发布必须走 release 分支", source: "user");
await tierStore.RecordHitAsync(MemoryScope.Project, pinnedSrc.Id, delta: 0, markImportant: true);
var mergeCompanion = await tierStore.AppendAsync(MemoryScope.Project, "另一条待合并的发布说明", source: "user");
var mergedPinned = await tierStore.MergeAsync(
    MemoryScope.Project,
    [pinnedSrc.Id, mergeCompanion.Id],
    "发布流程结论（由两条合并得出）",
    source: "user");
Check("★ 合并继承置顶标记（IsImportant）", mergedPinned.IsImportant, $"important={mergedPinned.IsImportant}");

// 单条合并 → 参数错误（合并语义上至少两条）
try
{
    await tierStore.MergeAsync(MemoryScope.Project, [hot.Id], "x");
    Check("合并需要 ≥2 条（单条抛参数错误）", false);
}
catch (ArgumentException)
{
    Check("合并需要 ≥2 条（单条抛参数错误）", true);
}

// 清扫：只动「冷 && 未置顶」—— hot 5 分、pinned 置顶都不该被扫走；
// 合并出的新结论（热度 1）按策略同样会被扫 —— 分层是状态机，不是一次性动作。
var now = DateTimeOffset.UtcNow;
var sweepCount = await tierStore.SweepAsync(MemoryScope.Project, now, minAgeDays: 0, maxScore: 1, "system");
Check("★ 清扫只归档冷记忆（低分+未置顶）", sweepCount == 1, $"{sweepCount} 条（预期：合并结论）");
var afterSweep = await tierStore.LoadAsync(MemoryScope.Project, 20);
Check("★ 热记忆与置顶记忆不被清扫",
    afterSweep.Any(e => e.Id == hot.Id) && afterSweep.Any(e => e.Id == pinnedMem.Id));
Check("★ 清扫后的记忆都在归档层",
    (await tierStore.LoadArchivedAsync(MemoryScope.Project, 20)).Any(e => e.Text.Contains("build.sh")));

// 归档条目可被撤销（面板"删除"对两层可用）
var retractedArchived = await tierStore.RetractAsync(MemoryScope.Project, merged.Id, "user");
Check("★ 归档条目也能被撤销", retractedArchived is not null);

// ── 11. 多项目记忆隔离（本批新增）──────────────────────────
Section("11. 多项目记忆隔离");

// 同一存储实例，两个项目作用域（作用域 id 内嵌项目目录）
var multiStore = new JsonlMemoryStore(
    new MemoryStoreOptions
    {
        GlobalPath = Path.Combine(root, "mem-multi", "global.jsonl"),
        ProjectPath = Path.Combine(root, "mem-multi", "default-project.jsonl"),
    },
    dir => Path.Combine(dir, ".agent-memory", "project.jsonl"));

var dirA = Path.Combine(root, "projects", "alpha");
var dirB = Path.Combine(root, "projects", "beta");
var scopeA = MemoryScope.ProjectFor(dirA);
var scopeB = MemoryScope.ProjectFor(dirB);

Check("★ 作用域 id 内嵌项目目录", scopeA.Contains("alpha") && scopeA.StartsWith("project:"));
await multiStore.AppendAsync(scopeA, "alpha 项目的构建约定", source: "user");
await multiStore.AppendAsync(scopeB, "beta 项目的发布流程", source: "user");
await multiStore.AppendAsync(MemoryScope.Global, "跨项目的用户偏好", source: "user");

Check("★ 各项目记忆互不可见",
    (await multiStore.LoadAsync(scopeA, 20)).All(e => e.Text.Contains("alpha"))
    && (await multiStore.LoadAsync(scopeB, 20)).All(e => e.Text.Contains("beta")));
Check("★ 项目记忆落在各自目录",
    File.Exists(Path.Combine(dirA, ".agent-memory", "project.jsonl"))
    && File.Exists(Path.Combine(dirB, ".agent-memory", "project.jsonl")));
Check("★ 跨目录检索不串",
    (await multiStore.SearchAsync("构建约定", scopeA, 5)).All(e => e.Text.Contains("alpha")));
Check("全局记忆跨项目共享",
    (await multiStore.SearchAsync("用户偏好", null, 5)).Count == 1);

// 归档/清扫按项目作用域各管各的
var aEntry = (await multiStore.LoadAsync(scopeA, 5)).First(e => e.Text.Contains("alpha"));
await multiStore.RecordHitAsync(scopeA, aEntry.Id, delta: 3);
var sweptA = await multiStore.SweepAsync(scopeA, DateTimeOffset.UtcNow, 0, 1, "system");
var sweptB = await multiStore.SweepAsync(scopeB, DateTimeOffset.UtcNow, 0, 1, "system");
Check("★ 清扫按项目作用域隔离（alpha 的热记忆不被扫，beta 的被扫）", sweptA == 0 && sweptB == 1, $"A={sweptA} B={sweptB}");

// ── 12. 温度排序：先全局排序、后截断（v3.5 审查 P1-4）────────
Section("12. 温度排序：老而常用不被时间窗挤掉（先排序后截断）");

var tempRoot = Path.Combine(root, "temp-order");
var tempOptions = new HostOptions
{
    WorkspaceRoot = Path.Combine(tempRoot, "ws"),
    SessionsDir = Path.Combine(tempRoot, "sessions"),
    SessionId = "temp",
    LlmOverride = new RecordingClient(),
};
Directory.CreateDirectory(tempOptions.WorkspaceRoot);
await using var tempHost = await AgentHost.CreateAsync(tempOptions);
tempHost.SetMode(AgentMode.Work);   // Work 档 = Temperature + MemoryIndexLimit 8

// 先写下那条「老约定」（时间上最早），再灌 10 条更新的流水账，把它挤出「最近 8 条」。
var oldEntry = await tempHost.Memory.AppendAsync(MemoryScope.Project, "老约定：打包必须排除 bin 与 obj");
for (var i = 1; i <= 10; i++)
{
    await tempHost.Memory.AppendAsync(MemoryScope.Project, $"流水账 {i}：第 {i} 次临时改动");
}

// 让它变热（模拟被反复 recall 命中）。
for (var i = 0; i < 5; i++)
{
    await tempHost.Memory.RecordHitAsync(MemoryScope.Project, oldEntry.Id);
}

await tempHost.SendAsync("打包脚本怎么写？");
var tempFrozen = string.Join('\n', (tempHost.LastAssembly?.Frozen ?? []).Select(m => m.Content ?? ""));

Check("★ 被 10 条新流水账挤出时间窗的老约定，凭热度仍进索引卡（P1-4）",
    tempFrozen.Contains("老约定：打包必须排除 bin 与 obj"));

// 反证：它确实排在时间序最末，所以「先按时间取最近 8 条」的旧实现必然够不着它。
var tempView = await tempHost.Memory.LoadAsync(MemoryScope.Project, int.MaxValue);
Check("老约定排在时间序最末（旧实现「最近 8 条」够不着）",
    tempView.Count == 11 && tempView[0].Text.Contains("老约定"), $"{tempView.Count} 条");

// ── 收尾 ───────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine($"═══ 结果：{passes} 通过 / {failures} 失败 ═══");
return failures == 0 ? 0 : 1;

static int IndexOfId(IReadOnlyList<MemoryEntry> list, string id)
{
    for (var i = 0; i < list.Count; i++)
    {
        if (string.Equals(list[i].Id, id, StringComparison.Ordinal)) { return i; }
    }
    return -1;
}

static int PickFreePort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

/// <summary>只回文本、并记录最后一次请求的假模型 —— 用来检查"模型到底看到了什么"。</summary>
internal sealed class RecordingClient(string reply = "好的，我在听。") : ILlmClient
{
    public string Name => "main";

    public int Calls { get; private set; }

    public LlmRequest? LastRequest { get; private set; }

    public void Reset()
    {
        Calls = 0;
        LastRequest = null;
    }

    public async IAsyncEnumerable<LlmStreamChunk> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        Calls++;
        LastRequest = request;
        await Task.Yield();

        yield return new LlmStreamChunk.TextDelta(reply);
        yield return new LlmStreamChunk.Completed("stop");
    }
}

