using System.Text.Json;
using AgentFramework.Agent;
using AgentFramework.Contracts;
using AgentFramework.Data;
using AgentFramework.Index;
using AgentFramework.Kernel;
using AgentFramework.Llm;
using AgentFramework.Sandbox;
using AgentFramework.Tools;

namespace AgentFramework.Host.Hosting;

/// <summary>
/// 宿主装配器：按序跑一串模块，把六层装成一个可运行的整体。
///
/// 与从前那个 266 行装配方法的差别，不是「拆小了好看」，而是：
///   · 模块可以**从外面加进来**（<c>customModules</c> 参数）；
///   · 每个模块注册的东西都挂在同一个撤收集上，关闭时自动逆序回收；
///   · 想加功能，加模块；想找东西，按模块名找。
/// </summary>
public static class HostBuilder
{
    /// <summary>宿主自带的模块（序号即装配顺序，见 <see cref="IHostModule.Order"/>）。</summary>
    public static IReadOnlyList<IHostModule> DefaultModules() =>
    [
        new ModelModule(),     // 100 模型接入
        new StorageModule(),   // 200 日志 / 索引 / 记忆
        new ToolModule(),      // 300 工具集（官方工具先占名字）
        new PluginModule(),    // 400 外部插件（重名即插件加载失败 —— 明确优于静默）
        new Mcp.McpModule(),   // 450 外部 MCP server（子进程 + stdio JSON-RPC，默认延迟）
        new LoopModule(),      // 500 主循环
    ];

    /// <summary>
    /// 装配。自定义模块会与默认模块一起按 <see cref="IHostModule.Order"/> 排序执行 ——
    /// 所以「插进哪一层」由它自己的序号决定，不需要改这里。
    /// </summary>
    public static async Task<AgentHost> BuildAsync(
        HostOptions options,
        IReadOnlyList<IHostModule>? customModules = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(options.WorkspaceRoot);
        Directory.CreateDirectory(options.SessionsDir);

        var kernel = new PluginHost(new PluginHostOptions
        {
            DataRoot = Path.Combine(options.SessionsDir, "plugin-data"),
        });

        var state = new HostState(options, kernel);

        var modules = customModules is null || customModules.Count == 0
            ? DefaultModules()
            : [.. DefaultModules().Concat(customModules)];

        try
        {
            foreach (var module in modules.OrderBy(m => m.Order))
            {
                await module.ConfigureAsync(state, ct).ConfigureAwait(false);
            }

            // ── 延迟包（Eager=false，如 mcp:<server>）：默认不进上下文 ──────────
            // 必须放在**所有模块装配完之后**：MCP 包是 McpModule(450) 才注册的，
            // 若放在 ToolModule 里就会漏掉它们。工具仍注册着、只是这一轮不发放给模型；
            // 模型可经 tool_catalog 发现这些包，再 use_toolset 把它们拉进来。
            // 这里复用「关闭包」这套现成机制 —— 于是「延迟」不另开第二条通路，
            // 也就不会与包开关分叉成两套语义。
            foreach (var descriptor in kernel.Toolsets.Values)
            {
                if (!descriptor.Eager && !descriptor.Protected)
                {
                    state.DisabledToolsets.Add(descriptor.Id);
                }
            }
        }
        catch
        {
            // v3.6 审查修复：装配中途失败要回收已建资源 —— 否则内核作用域、事件日志句柄、
            // SQLite 连接、插件 ALC 都会泄漏，反复失败启动会累积。
            try
            {
                kernel.DisposeKernelScopes();
            }
            catch
            {
                // 回收失败不掩盖原始异常
            }

            throw;
        }

        return AgentHost.CreateCore(state);
    }
}

// ═══════════════════════════════════════════════════════════════
//  400 · 外部插件
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// 从插件目录装载外部插件。启动器的 profile 就在这里生效。
/// 单个插件坏掉不拖垮整个应用 —— 这是稳定性的底线。
/// </summary>
public sealed class PluginModule : IHostModule
{
    public string Name => "plugins";

    public int Order => 400;

    public async ValueTask ConfigureAsync(HostState state, CancellationToken ct = default)
    {
        var options = state.Options;

        var loaded = new List<PluginHandle>();
        var skipped = new List<string>();
        var failed = new List<string>();

        if (!string.IsNullOrWhiteSpace(options.PluginsDir) && Directory.Exists(options.PluginsDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(options.PluginsDir).OrderBy(d => d, StringComparer.Ordinal))
            {
                var manifestPath = Path.Combine(dir, "plugin.json");
                if (!File.Exists(manifestPath))
                {
                    continue;
                }

                var pluginId = TryReadPluginId(manifestPath);
                var folderName = Path.GetFileName(dir);

                // ★ 启动器的 profile 在这里生效：没被勾选的一律不加载
                if (options.EnabledPlugins is not null
                    && (pluginId is null || !options.EnabledPlugins.Contains(pluginId, StringComparer.Ordinal)))
                {
                    skipped.Add(pluginId ?? folderName);
                    continue;
                }

                try
                {
                    loaded.Add(await state.Kernel.LoadAsync(dir, ct).ConfigureAwait(false));
                }
                catch (Exception ex)
                {
                    if (options.FailFastOnPluginError)
                    {
                        throw;
                    }

                    failed.Add($"{pluginId ?? folderName}：{ex.Message}");
                }
            }
        }

        // ── 自写插件仓库：agent 自己写的插件也在这里装上 ──
        // 不过 profile 过滤：那个开关是给"随包插件"用的；自写插件是主人/agent 主动装上的，
        // 再多过滤一道，只会让「明明写进去了却没生效」变成一桩谜案。
        var storeRoot = options.EffectiveWorkspacePluginsDir;
        if (Directory.Exists(storeRoot))
        {
            foreach (var dir in Directory.EnumerateDirectories(storeRoot).OrderBy(d => d, StringComparer.Ordinal))
            {
                var manifestPath = Path.Combine(dir, "plugin.json");
                if (!File.Exists(manifestPath))
                {
                    continue;   // 含写入过程中的 *.staging-* 残留
                }

                var pluginId = TryReadPluginId(manifestPath);
                try
                {
                    loaded.Add(await state.Kernel.LoadAsync(dir, ct).ConfigureAwait(false));
                }
                catch (Exception ex)
                {
                    failed.Add($"{pluginId ?? Path.GetFileName(dir)}：{ex.Message}");
                }
            }
        }

        state.LoadedPlugins = loaded;
        state.SkippedPlugins = skipped;
        state.FailedPlugins = failed;
    }

    private static string? TryReadPluginId(string manifestPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            return doc.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }
}

// ═══════════════════════════════════════════════════════════════
//  100 · 模型接入
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// 模型接入：配置单里的端点、运行时模型配置、路由器、以及两个「旁路消费者」
/// （输入转述器、本地摘要器）。旁路消费者刻意绕开路由器直接持有端点 ——
/// 路由规则是偏好，不是前提。
/// </summary>
public sealed class ModelModule : IHostModule
{
    public string Name => "model";

    public int Order => 100;

    public ValueTask ConfigureAsync(HostState state, CancellationToken ct = default)
    {
        var options = state.Options;

        // 模型配置（若启用了）：显式选定的端点会盖过 cloud / local
        var modelStore = string.IsNullOrWhiteSpace(options.ConfigPath)
            ? null
            : new ModelSettingsStore(options.ConfigPath!);
        var modelSettings = modelStore?.Load();

        var (llm, offline, localClient, cloudClient) = BuildLlm(options, modelSettings, modelStore?.Protector);

        // 转述设置来自「用户偏好」文件 —— 它不进 JSONL，那是「会话发生了什么」的地盘
        options.Rephrase = RephraseSettingsStore.Load(options.SessionsDir, options.Rephrase);

        // 上下文治理 / 写盘设置同样是「用户偏好」：从 context.json 读回，覆盖默认值（任务 1）。
        var contextSettings = ContextSettingsStore.Load(options.SessionsDir, options.Context, options.Checkpoint);
        options.Context = contextSettings.Context;
        options.Checkpoint = contextSettings.Checkpoint;

        // 转述器直接持有端点客户端，**绕过路由器**：
        // 路由规则是「长上下文 + 无工具 → 本地」，而转述请求恰好长这样，
        // 但它必须去用户指定的那个端点。
        // 转述目标解析：支持两种写法 ——
        //   · 端点 id（"cloud" / "local" / 任意端点 id）→ 用那个端点的直连客户端（老配置不变）；
        //   · "端点:模型"（如 "deepseek:deepseek-chat"）→ 临时造一个指到具体模型的客户端。
        // 多端点体系下只认 local/cloud 会让转述在大多数配置里静默失效 —— 这是要修的根因。
        var rephraseTargets = new Dictionary<string, ILlmClient>(StringComparer.OrdinalIgnoreCase);
        if (cloudClient is not null)
        {
            rephraseTargets["cloud"] = cloudClient;
        }

        if (localClient is not null)
        {
            rephraseTargets["local"] = localClient;
        }

        // v3.6 审查修复：按需造的「端点:模型」客户端要缓存复用 ——
        // 原实现每次转述都 new 一个（各带自己的 HttpClient）且从不释放，高频转述会耗尽本地连接。
        var adHocTargets = new Dictionary<string, ILlmClient>(StringComparer.OrdinalIgnoreCase);

        // ★ 解析不到就回退 active 端点（P0）：配了 providers（如 amd-radeon）时
        //   local/cloud 直连客户端为空，硬编码 "local" 默认值会静默失效。
        //   回退到当前生效端点，转述/摘要才在多端点配置下可用。
        ILlmClient? FallbackActive()
        {
            if (modelSettings is null)
            {
                return null;
            }

            var active = modelSettings.ActiveProvider;
            if (active is null || string.IsNullOrWhiteSpace(active.BaseUrl))
            {
                return null;
            }

            if (adHocTargets.TryGetValue("active-fallback", out var hit))
            {
                return hit;
            }

            var activeModelId = modelSettings.ActiveModel?.Id;
            if (string.IsNullOrWhiteSpace(activeModelId))
            {
                activeModelId = active.Models.FirstOrDefault()?.Id;
            }

            var created = new OpenAiCompatibleClient(active.Id, new OpenAiCompatibleOptions
            {
                BaseUrl = active.BaseUrl,
                ApiKey = active.ResolveApiKey(modelStore?.Protector ?? SecretProtectors.Default) ?? string.Empty,
                DefaultModel = string.IsNullOrWhiteSpace(activeModelId) ? "auto" : activeModelId!,
                ReasoningStyle = string.IsNullOrWhiteSpace(active.ReasoningStyle) ? ReasoningStyles.None : active.ReasoningStyle,
                ReasoningEffort = active.ReasoningEffort ?? string.Empty,
            });
            adHocTargets["active-fallback"] = created;
            return created;
        }

        ILlmClient? ResolveRephraseTarget(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return FallbackActive();
            }

            if (rephraseTargets.TryGetValue(name.Trim(), out var direct))
            {
                return direct;
            }

            if (modelSettings is null)
            {
                return FallbackActive();
            }

            // "端点:模型" 形态（如 "deepseek:deepseek-chat"）→ 指到具体模型的客户端
            var parts = name.Split(':', 2);
            if (parts.Length == 2)
            {
                var provider = modelSettings.FindProvider(parts[0].Trim());
                var modelId = parts[1].Trim();
                if (provider is not null && !string.IsNullOrWhiteSpace(provider.BaseUrl) && modelId.Length > 0)
                {
                    var cacheKey = provider.Id + ":" + modelId;
                    if (adHocTargets.TryGetValue(cacheKey, out var cached))
                    {
                        return cached;
                    }

                    var created = new OpenAiCompatibleClient(provider.Id, new OpenAiCompatibleOptions
                    {
                        BaseUrl = provider.BaseUrl,
                        ApiKey = provider.ResolveApiKey(modelStore?.Protector ?? SecretProtectors.Default) ?? string.Empty,
                        DefaultModel = modelId,
                        ReasoningStyle = string.IsNullOrWhiteSpace(provider.ReasoningStyle) ? ReasoningStyles.None : provider.ReasoningStyle,
                        ReasoningEffort = provider.ReasoningEffort ?? string.Empty,
                        // 任务 6：把模型级能力带给客户端，发送侧据此门禁（false 不发思考参数）。
                        SupportsReasoning = provider.Models
                            .FirstOrDefault(mm => string.Equals(mm.Id, modelId, StringComparison.OrdinalIgnoreCase))?.SupportsReasoning,
                    });
                    adHocTargets[cacheKey] = created;
                    return created;
                }

                return FallbackActive();
            }

            // 只写端点 id（"deepseek"）→ 用该端点的默认/首个模型。
            // 界面下拉会提供这种项；从前只认 local/cloud/「端点:模型」，这里会静默失效。
            var whole = modelSettings.FindProvider(name.Trim());
            if (whole is not null && !string.IsNullOrWhiteSpace(whole.BaseUrl))
            {
                var fallbackModel = whole.Models.FirstOrDefault()?.Id;
                return new OpenAiCompatibleClient(whole.Id, new OpenAiCompatibleOptions
                {
                    BaseUrl = whole.BaseUrl,
                    ApiKey = whole.ResolveApiKey(modelStore?.Protector ?? SecretProtectors.Default) ?? string.Empty,
                    DefaultModel = string.IsNullOrWhiteSpace(fallbackModel) ? "auto" : fallbackModel!,
                    ReasoningStyle = string.IsNullOrWhiteSpace(whole.ReasoningStyle) ? ReasoningStyles.None : whole.ReasoningStyle,
                    ReasoningEffort = whole.ReasoningEffort ?? string.Empty,
                });
            }

            // ★ 最终兜底：名字完全认不出（典型是默认 "local" 而端点叫 amd-radeon）——
            //   转述不该因此彻底不可用，回退到 active 端点。
            return FallbackActive();
        }

        var rephraser = options.RephraserOverride
            ?? (rephraseTargets.Count == 0 && (modelSettings?.Providers.Count ?? 0) == 0
                ? null
                : new LlmUserInputRephraser(ResolveRephraseTarget));

        // L5 上下文摘要器：与转述共用同一套解析（local/cloud/端点 id/端点:模型），
        // 认不出时回退 active 端点 —— 多端点配置（如 amd-radeon）下摘要才不会静默失效。
        // 只挂 localClient 的老写法在「配了 providers 就没有 localClient」时恒为 null，
        // 于是 SummarizeOlderHistory 再怎么开也永远生不出摘要（压缩只剩遮蔽、不折叠）。
        var summarizerClient = ResolveRephraseTarget("local")
            ?? ResolveRephraseTarget("cloud")
            ?? FallbackActive();
        var contextSummarizer = options.ContextSummarizerOverride
            ?? (summarizerClient is null
                ? null
                : new LlmContextSummarizer(summarizerClient, new LlmContextSummarizerOptions()));

        state.ModelStore = modelStore;
        state.ModelSettings = modelSettings;
        state.Llm = llm;
        state.Offline = offline;
        state.LocalClient = localClient;
        state.CloudClient = cloudClient;
        state.Rephraser = rephraser;
        state.ContextSummarizer = contextSummarizer;

        // 包一层可替换的壳：之后换模型只换它的内层，主循环不用重建
        state.Switchable = new SwitchableLlmClient(llm);

        return ValueTask.CompletedTask;
    }

    private static (ILlmClient Llm, bool Offline, ILlmClient? LocalClient, ILlmClient? CloudClient) BuildLlm(
        HostOptions options,
        ModelSettings? settings,
        ISecretProtector? protector)
    {
        // 0) 显式选定的端点优先 —— 主人明确挑了用哪个模型，那就别自作主张去分流。
        if (TryBuildActive(settings, protector, out var active))
        {
            return (active, false, null, null);
        }

        // 端点客户端先各自建出来：主链路要用（装进路由器），转述也要用（直接持有，绕过路由器）。
        ILlmClient? cloudClient = null;
        ILlmClient? localClient = null;

        if (options.Cloud?.IsConfigured == true)
        {
            cloudClient = new OpenAiCompatibleClient("cloud", new OpenAiCompatibleOptions
            {
                BaseUrl = options.Cloud.BaseUrl,
                ApiKey = options.Cloud.ApiKey,
                DefaultModel = options.Cloud.Model,
            });
        }

        if (options.Local?.IsConfigured == true)
        {
            localClient = new OpenAiCompatibleClient("local", new OpenAiCompatibleOptions
            {
                BaseUrl = options.Local.BaseUrl,
                ApiKey = options.Local.ApiKey,
                DefaultModel = options.Local.Model,
            });
        }

        // 注入的 LLM 只接管主链路；转述仍按配置走真实端点（有配置时）。
        if (options.LlmOverride is not null)
        {
            return (options.LlmOverride, false, localClient, cloudClient);
        }

        var router = new RouterLlmClient(DefaultRouting.Rule(options.LongContextChars));
        var configured = 0;

        if (cloudClient is not null)
        {
            router.AddTarget(cloudClient);
            configured++;
        }

        if (localClient is not null)
        {
            router.AddTarget(localClient);
            configured++;
        }

        if (configured > 0)
        {
            return (router, false, localClient, cloudClient);
        }

        if (!options.AllowOfflineDemo)
        {
            throw new InvalidOperationException("未配置任何模型端点（Cloud / Local），且不允许离线演示模式");
        }

        return (new OfflineDemoLlmClient(), true, null, null);
    }

    /// <summary>
    /// 按「当前选定的端点 + 模型」造一个直连客户端。
    /// 配置不全（没选、没地址）时返回 false，交给调用方回落旧逻辑。
    /// </summary>
    internal static bool TryBuildActive(
        ModelSettings? settings,
        ISecretProtector? protector,
        out ILlmClient client)
    {
        client = null!;

        if (settings?.Current is not { } current)
        {
            return false;
        }

        var (provider, model) = current;

        if (string.IsNullOrWhiteSpace(provider.BaseUrl) || string.IsNullOrWhiteSpace(model.Id))
        {
            return false;
        }

        client = new OpenAiCompatibleClient(provider.Id, new OpenAiCompatibleOptions
        {
            BaseUrl = provider.BaseUrl,
            ApiKey = provider.ResolveApiKey(protector ?? SecretProtectors.Default) ?? string.Empty,
            DefaultModel = model.Id,
            ReasoningStyle = string.IsNullOrWhiteSpace(provider.ReasoningStyle) ? ReasoningStyles.None : provider.ReasoningStyle,
            ReasoningEffort = provider.ReasoningEffort ?? string.Empty,
            // 任务 6：把当前模型的能力带给客户端，发送侧据此门禁。
            SupportsReasoning = model.SupportsReasoning,
        });

        return true;
    }
}

// ═══════════════════════════════════════════════════════════════
//  200 · 数据平面（日志 / 索引 / 记忆）
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// 数据平面三件套，按「能不能丢」排开：
///   日志 = 唯一真相源（不能丢）｜记忆 = 真相源（不能丢）｜索引 = 派生（随时可重建）。
/// </summary>
public sealed class StorageModule : IHostModule
{
    public string Name => "storage";

    public int Order => 200;

    public async ValueTask ConfigureAsync(HostState state, CancellationToken ct = default)
    {
        var options = state.Options;
        var logPath = Path.Combine(options.SessionsDir, options.SessionId + ".jsonl");

        // ── 派生索引（SQLite）：它是真相源之外的第二份数据，所以**必须可以丢** ——
        //    打不开就降级成「没有索引」，主流程照常跑（只是少了检索与增量加速）。
        var index = options.IndexOverride
            ?? (options.IndexEnabled
                ? await SqliteSessionIndex
                    .OpenAsync(Path.Combine(options.SessionsDir, "index.db"), null, ct)
                    .ConfigureAwait(false)
                : NullSessionIndex.Instance);

        // 索引落后于日志就重建（用户删过 db、上次写索引失败、或换了实现）
        // 判落后用 **seq 水位** 而不是 Count：空文本事件不进索引，Count 永远对不齐。
        if (index.IsAvailable)
        {
            try
            {
                var logged = JsonlEventLog.Read(logPath).ToList();
                if (logged.Count > 0)
                {
                    var lastSeq = logged[^1].Seq;
                    var watermark = await index
                        .GetIndexedWatermarkAsync(options.SessionId, ct)
                        .ConfigureAwait(false);

                    if (watermark < lastSeq)
                    {
                        await index.RebuildAsync(options.SessionId, logged, ct).ConfigureAwait(false);
                    }
                }
            }
            catch
            {
                // 重建失败就先用着空索引，下次启动再试 —— 反正它不是真相源
            }
        }

        // ── 分级记忆：与索引正好相反，**记忆丢了就真丢了**。
        var memoryDir = options.MemoryDir ?? Path.Combine(options.SessionsDir, "memory");
        var memoryStore = options.MemoryStoreOverride ?? new JsonlMemoryStore(new MemoryStoreOptions
        {
            GlobalPath = Path.Combine(memoryDir, "global.jsonl"),
            ProjectPath = Path.Combine(options.WorkspaceRoot, ".agent-memory", "project.jsonl"),
            // 多项目规范化锚点：抽象 "project" ≡ project:{WorkspaceRoot}（与 resolver 同构）
            WorkspaceRoot = options.WorkspaceRoot,
        },
        // 项目作用域 → 该目录下的 .agent-memory/project.jsonl（多项目记忆隔离的落点）
        dir => Path.Combine(dir, ".agent-memory", "project.jsonl"));

        state.LogPath = logPath;
        state.Index = index;
        state.Memory = memoryStore;
        state.MemoryDir = memoryDir;
        state.NotesPath = Path.Combine(options.WorkspaceRoot, WorkNotes.DefaultFileName);

        // 事件日志（真相源）。放在最后开：前面几步失败不该留下一个半开的日志文件。
        state.Log = JsonlEventLog.Open(logPath);

        // 把数据平面挂进内核 —— 别的模块（以及将来的插件）可以直接 Get 到它们
        var scope = state.Kernel.CreateKernelScope("storage");
        scope.Provide<ISessionIndex>(index);
        scope.Provide<IMemoryStore>(memoryStore);
    }
}

// ═══════════════════════════════════════════════════════════════
//  300 · 工具集
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// 官方工具集 + 注册。
///
/// 注册纪律（学 dsh 的稳定注册原则）：
///   <b>注册 ≠ 可见</b>。工具一律注册着，这一轮到底给模型看哪些，由「工作模式」决定 ——
///   于是切模式不需要重装工具，闲聊模式也真的一个 schema 都不发。
/// </summary>
public sealed class ToolModule : IHostModule
{
    public string Name => "tools";

    public int Order => 300;

    /// <summary>
    /// 按配置拼搜索降级链。默认 bing → baidu → searxng：
    /// 公共 SearXNG 实例（searx.be）在不少网络下不可达，不能再当唯一后端。
    /// 名字写错的后端跳过并提示 —— 配置错了不该让整个宿主起不来。
    /// </summary>
    internal static ISearchProvider BuildSearchProvider(ToolkitOptions toolkit)
    {
        var names = (toolkit.SearchBackends ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (names.Length == 0)
        {
            names = ["bing", "baidu", "searxng"];
        }

        var providers = new List<ISearchProvider>();
        foreach (var raw in names)
        {
            var name = raw.ToLowerInvariant();
            ISearchProvider? provider = name switch
            {
                "bing" => new BingSearchProvider(),
                "baidu" => new BaiduSearchProvider(),
                "searxng" => new SearxngSearchProvider(
                    string.IsNullOrWhiteSpace(toolkit.SearxngBaseUrl) ? "https://searx.be" : toolkit.SearxngBaseUrl!),
                _ => null,
            };

            if (provider is not null)
            {
                providers.Add(provider);
            }
            else
            {
                Console.WriteLine($"[警告] 搜索配置里有不认识的后端「{raw}」，已跳过（可选：bing / baidu / searxng）");
            }
        }

        return providers.Count == 1 ? providers[0] : new FallbackSearchProvider([.. providers]);
    }
    public ValueTask ConfigureAsync(HostState state, CancellationToken ct = default)
    {
        var options = state.Options;

        // 边界参数只定义一次 —— 学 dsh：「预算和边界是运维的事」
        var toolkit = new ToolkitOptions
        {
            WorkspaceRoot = options.WorkspaceRoot,
            Context = options.Context,
            SearchBackends = options.SearchBackends ?? "bing,baidu,searxng",
            SearxngBaseUrl = options.SearxngBaseUrl,
            // 命令沙箱档位（auto / off / process / job / 插件注册的后端名）
            SandboxName = options.Sandbox,
            Shell = options.Shell,
        };
        state.Toolkit = toolkit;

        // 命令沙箱：内置三档（off / process / job），插件可注册新后端 ——
        // 于是「换一种沙箱」是加一个插件，而不是改宿主里任何 switch。
        var sandbox = new SandboxRegistry();
        state.Sandbox = sandbox;

        // 摘要器接到「本地」端点上：网页正文先在本机压缩，只有摘要会进入云端上下文。
        // 必须用本地模型 —— 若挂到云端，原文照样出网，这个设计就白做了。
        var summarizer = state.LocalClient is null ? null : new LlmResultSummarizer(state.LocalClient);

        // G2：技能扫描（只读 skill.json，不加载程序集 —— 与插件扫描同安全级）。
        // 目录约定：工作区根/skills/{name}/skill.json —— 与项目记忆同属工作区，拷走即带走。
        state.Skills = SkillLoader.ScanWorkspace(options.WorkspaceRoot);

        // 自写插件仓库（agent 自己写的插件落这里）+ 快照区。
        // 放在工具模块而不是插件模块：工具模块先跑（300 < 400），
        // 于是插件模块装载时仓库已经就绪，能把仓库里的插件一并装上；
        // 四个自管理工具也在这里注册，用的是同一个 store 实例。
        var pluginStore = new PluginStore(
            options.EffectiveWorkspacePluginsDir,
            Path.Combine(options.SessionsDir, "plugin-backups"));
        Directory.CreateDirectory(pluginStore.Root);
        state.PluginStore = pluginStore;

        // 多项目：工具沙箱按回合所属会话的项目目录解析（AsyncLocal 回合作用域）
        toolkit.WorkspaceRootResolver = () => state.TurnWorkspaceDir;

        var tools = state.OfficialTools;
        tools.Add(new ReadFileTool(toolkit));
        tools.Add(new WriteFileTool(toolkit));
        tools.Add(new ListDirTool(toolkit));
        tools.Add(new ReadImageTool(new WorkspaceService(toolkit)));

        // 文件链补齐（2026-09 上收 core）：改/行读/建/移/删/搜内容/找文件。
        // 从前这些在 devkit 插件包里 —— 模型要先 use_toolset 才能改文件，
        // 多一轮且容易猜错包。实现类保持 internal，经 CoreFileOps 一次收齐。
        foreach (var t in AgentFramework.Tools.FileOps.CoreFileOps.CreateAll(new WorkspaceService(toolkit)))
            tools.Add(t);
        tools.Add(new RunCommandTool(toolkit, sandbox));
        tools.Add(new WebSearchTool(toolkit, BuildSearchProvider(toolkit)));
        tools.Add(new WebFetchTool(toolkit, http: null, summarizer: summarizer));

        // ── 自我升级入口 ───────────────────────────────────────
        // agent 用这四个工具给自己长能力：写插件 → 热重装（含自测与回滚）→ 下一轮就能用；
        // 不想要了就卸掉。它们只碰「自写插件仓库」，与随包分发的官方插件互不干扰。
        tools.Add(new PluginWriteTool(pluginStore));
        tools.Add(new PluginReloadTool(pluginStore, state.Kernel));
        tools.Add(new PluginUninstallTool(pluginStore, state.Kernel));
        tools.Add(new PluginListTool(pluginStore, state.Kernel));

        // 历史检索：把被上下文折叠掉的内容捞回来。
        // 它与 L3 遮蔽是一对 —— 没有它，折叠就是「丢失」；有了它，折叠才是「卸载」。
        if (state.Index.IsAvailable)
        {
            // F5：会话感知 —— P1 之后宿主下有多个会话，闭包必须读"当前会话"，
        // 否则子 agent / 切换后的会话里 search_history 查到的永远是装配时那个会话的日志。
        tools.Add(new SearchHistoryTool(toolkit, state.Index, () => state.CurrentSessionIdProvider?.Invoke() ?? options.SessionId));
        }

        // 记忆工具：写进哪一级由**模式**决定，模型不需要关心这件事
        // 多项目：工具写/读的记忆层级必须经过回合作用域映射 ——
        // "project" → 回合所属会话的项目目录（写读同源，否则读写错位）。
        tools.Add(new RememberTool(
            state.Memory,
            () => state.TurnMemoryScopes(state.CurrentProfile.MemoryScopes).FirstOrDefault() ?? MemoryScope.Project,
            () => state.CurrentSessionIdProvider?.Invoke() ?? options.SessionId));

        tools.Add(new RecallMemoryTool(
            toolkit,
            state.Memory,
            () => state.TurnMemoryScopes(state.CurrentProfile.MemoryScopes)));

        // 撤销：与 remember 是一对 —— 能记就要能收回，否则写错的记忆会一直误导
        tools.Add(new ForgetTool(
            state.Memory,
            () => state.TurnMemoryScopes(state.CurrentProfile.MemoryScopes)));

        // 工作小本本：路径固定在工区根部，人随时能打开改。
        // 单独的更新工具而不是让它用 write_file —— 整篇覆盖太容易踩到人写的内容。
        tools.Add(new UpdateNotesTool(() => state.NotesPath));

        // 问用户：给「猜」留一条正当出口（把猜当成答，是模型最常见也最贵的错）。
        tools.Add(new AskUserTool(() => state.InteractionProvider?.Invoke() ?? NullUserInteraction.Instance));

        // 工具包管理（meta 包，不可关）：让 agent 自己把这段活用不上的包收起来。
        // 「真实工作时有的选择地开」最该由模型自己判断 —— 它最清楚眼前这段活需要什么。
        tools.Add(new ToolsetsTool(() => state.ToolsetViewProvider?.Invoke() ?? []));
        tools.Add(new UseToolsetTool(
            () => state.ToolsetViewProvider?.Invoke() ?? [],
            (id, enabled) => state.ToolsetToggle?.Invoke(id, enabled) ?? false));

        // G1 子 Agent：主模型自己决定派工。runner 由宿主回填（需要 AgentHost 的 OpenSession/SendAsync，
        // 装配期还没有宿主 —— 与 InteractionProvider 同一手法：留委托，运行期解引用）。
        // v3.4 原接线多传了一个 LastSeqOf（签名里没有这个位置）—— 子会话 id 由 runner 自造，分叉点参数已无用，删。
        tools.Add(new SpawnSubAgentTool(
            () => state.CurrentSessionIdProvider?.Invoke() ?? options.SessionId,
            async (parentSessionId, task, ct) =>
            {
                return await state.SubAgentRunner!(parentSessionId, task, ct).ConfigureAwait(false);
            }));

        // G3 计划：事件投影式计划，落盘走当前会话的 Sink。
        // 委托形参直接收 SessionEvent（工厂已在工具内调用），不再需要 PlanMutation 包装类型。
        tools.Add(new UpdatePlanTool(
            () => state.CurrentSessionIdProvider?.Invoke() ?? options.SessionId,
            mutation =>
            {
                var sessionId = state.CurrentSessionIdProvider?.Invoke() ?? options.SessionId;
                return state.EmitToSession?.Invoke(sessionId, mutation, default) ?? ValueTask.CompletedTask;
            }));

        // ── 技能工坊（任务 2）：生成 / 校验 / 提炼技能包 ────────────────
        // 从前「技能」偏声明包，得人手写 skill.json；这三个工具让 agent 自助产出与自检。
        // 校验复用同一套规则（SkillValidator），与界面导入路由同源。
        // 写盘后必须 SkillsReloader：否则界面列表与下一轮提示仍用启动时那份旧清单。
        tools.Add(new SkillScaffoldTool(toolkit, () => state.SkillsReloader?.Invoke()));
        tools.Add(new SkillValidateTool(
            toolkit,
            () => state.Kernel.ToolNames,
            () => state.Skills));
        tools.Add(new SkillExtractTool(() => state.Kernel.ToolNames));
        tools.Add(new SkillFromToolsetTool(
            toolkit,
            () => state.ToolsetViewProvider?.Invoke() ?? [],
            () => state.SkillsReloader?.Invoke()));

        // 工具目录查询：按包/关键词看已注册工具与参数摘要（meta 包）。
        tools.Add(new ToolCatalogTool(
            () => state.ToolsetViewProvider?.Invoke() ?? [],
            state.OfficialTools));

        // 结构化输出工具样例（lab 包）：扩展「工具类型」注册面。
        tools.Add(new CsvToJsonTool());

        // 官方工具注册进内核注册表 —— 于是「官方工具」与「插件工具」不再有双轨：
        // 主循环、InvokeToolAsync、诊断面看到的都是同一份名单，
        // 而且运行期挂上来的工具下一轮就可见（技能 / 子 agent / 模型自写插件都靠这条）。
        var scope = state.Kernel.CreateKernelScope("official-tools");

        // ── 官方工具的包归属 ─────────────────────────────────────
        // 集中在这里而不是让每个工具自报：包是**装配决策**（跟可见性同层），
        // 一处写全、一眼能审；工具本身只负责回答"我是什么"。
        var toolsetOf = OfficialToolsetMap();

        foreach (var tool in tools)
        {
            scope.RegisterTool(tool, toolsetOf.GetValueOrDefault(tool.Name));
        }

        // 内置包的说明 —— 自动生成的朴素描述只有 id，界面上那一排开关得有话说
        foreach (var descriptor in BuiltinToolsetDescriptors())
        {
            state.Kernel.DescribeToolset(descriptor);
        }

        // ── 启动配置里就关掉的包 ─────────────────────────────────
        // 保留包（core / meta）写了也不生效：关掉它们不是「省负担」，是「把 agent 关成残废」。
        if (options.DisabledToolsets is { Count: > 0 })
        {
            foreach (var id in options.DisabledToolsets)
            {
                if (!BuiltinToolsets.Protected.Contains(id))
                {
                    state.DisabledToolsets.Add(id);
                }
            }
        }

        // ── 给插件用的工作区 seam ─────────────────────────────────
        // 插件跑在独立 ALC 里，只有契约程序集共享 —— 官方工具手里那个 toolkit 实例
        // 它够不着。所以把「工作区在哪 / 能写到哪 / 输出多大」以契约接口提供出去，
        // 基石插件（devkit 之类）才能既干文件活儿、又走同一份边界检查。
        // 与工具注册挂在同一个作用域上：宿主关停时一起撤销。
        scope.Provide<IWorkspaceService>(new WorkspaceService(toolkit));

        // 沙箱后端注册表也交给内核：插件想加一档沙箱（容器 / 远程 / 带审计的包装）
        // 就 ctx.Effect(() => registry.Register(backend))，卸载时自动摘掉。
        scope.Provide<ISandboxRegistry>(sandbox);

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 官方工具 → 工具包。
    /// 表里没有的工具会落进与注册来源同名的包（<c>official-tools</c>）。
    ///
    /// 2026-09 重划：<b>日常干活链全收 core</b>（完整文件链 + 记忆 + 计划 + 历史），
    /// 模型不必先 use_toolset 才能改文件；可关包只留安全收窄（exec）与领域扩展。
    /// </summary>
    private static Dictionary<string, string> OfficialToolsetMap() => new(StringComparer.Ordinal)
    {
        // core（不可关）：完整文件链 + 问用户 + 记忆 + 计划/笔记 + 历史 + 派活
        ["read_file"] = BuiltinToolsets.Core,
        ["write_file"] = BuiltinToolsets.Core,
        ["list_dir"] = BuiltinToolsets.Core,
        ["read_image"] = BuiltinToolsets.Core,
        ["edit_file"] = BuiltinToolsets.Core,
        ["read_lines"] = BuiltinToolsets.Core,
        ["make_dir"] = BuiltinToolsets.Core,
        ["move_path"] = BuiltinToolsets.Core,
        ["delete_path"] = BuiltinToolsets.Core,
        ["grep_files"] = BuiltinToolsets.Core,
        ["find_files"] = BuiltinToolsets.Core,
        ["ask_user"] = BuiltinToolsets.Core,
        ["remember"] = BuiltinToolsets.Core,
        ["forget"] = BuiltinToolsets.Core,
        ["recall_memory"] = BuiltinToolsets.Core,
        ["search_history"] = BuiltinToolsets.Core,
        ["update_plan"] = BuiltinToolsets.Core,
        ["update_notes"] = BuiltinToolsets.Core,
        ["spawn_subagent"] = BuiltinToolsets.Core,

        // exec（可关）：跑命令单独成包 ——「这次不许它跑命令」得有出口
        ["run_command"] = BuiltinToolsets.Exec,

        ["web_search"] = BuiltinToolsets.Web,
        ["web_fetch"] = BuiltinToolsets.Web,

        // 实验/转换：纯函数，进 core（单工具包无开关价值）
        ["csv_to_json"] = BuiltinToolsets.Core,

        // 自我升级：插件四件套 + 技能工坊（都是「给自己长能力」）
        ["plugin_write"] = BuiltinToolsets.Self,
        ["plugin_reload"] = BuiltinToolsets.Self,
        ["plugin_uninstall"] = BuiltinToolsets.Self,
        ["plugin_list"] = BuiltinToolsets.Self,
        ["skill_scaffold"] = BuiltinToolsets.Self,
        ["skill_validate"] = BuiltinToolsets.Self,
        ["skill_extract"] = BuiltinToolsets.Self,
        ["skill_from_toolset"] = BuiltinToolsets.Self,

        // meta（不可关）：看/开关工具包本身 —— 关了就再也开不回来
        ["toolsets"] = BuiltinToolsets.Meta,
        ["use_toolset"] = BuiltinToolsets.Meta,
        ["tool_catalog"] = BuiltinToolsets.Meta,
    };

    /// <summary>内置包的显示名与说明（界面开关与诊断面都读它）。</summary>
    private static IEnumerable<ToolsetDescriptor> BuiltinToolsetDescriptors()
    {
        yield return new()
        {
            Id = BuiltinToolsets.Core,
            Name = "核心",
            Description = "完整文件链 + 问用户 + 记忆 + 计划/笔记 + 历史 + 派子 Agent + 结构化转换（不可关闭）",
            Protected = true,
            Source = "core",
        };
        yield return new() { Id = BuiltinToolsets.Meta, Name = "工具包管理", Description = "查看与开关工具包（不可关闭）", Protected = true, Source = "core" };
        yield return new() { Id = BuiltinToolsets.Exec, Name = "执行命令", Description = "在工作区里跑 shell 命令（受命令沙箱保护）", Source = "core" };
        yield return new() { Id = BuiltinToolsets.Web, Name = "联网", Description = "网页搜索与抓取", Source = "core" };
        yield return new() { Id = BuiltinToolsets.Self, Name = "自我升级", Description = "写插件、热重装、卸载、列插件 + 技能工坊", Source = "core" };
    }
}

// ═══════════════════════════════════════════════════════════════
//  500 · 主循环
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// 主循环装配：事件出口 + AgentRunner。
/// 用中继打破「runner / sink 要先于 host 创建」的循环引用。
/// </summary>
public sealed class LoopModule : IHostModule
{
    public string Name => "loop";

    public int Order => 500;

    public ValueTask ConfigureAsync(HostState state, CancellationToken ct = default)
    {
        var options = state.Options;

        var sink = new HostEventSink(
            state.Log!,
            state.Kernel,
            // 任务 5：包一层**动态读取** —— 运行期切档（替换 options.ApprovalPolicy）立即生效，
            // 不再是「装配时按值捕获、切了也不动」。这正是审查发现的那个坑。
            e => options.ApprovalPolicy(e),
            () => state.InteractionProvider?.Invoke() ?? NullUserInteraction.Instance,
            e =>
            {
                // ★ 先收进主会话的内存事件表（P2），再转给宿主的事件中继。
                //   MainSession 此刻还是 null，但这条回调只在运行期触发 —— 那时早赋好了。
                state.MainSession?.Track(e);
                state.EventRelay?.Invoke(e);
            },
            state.Index,
            options.SessionId,
            // P6：会话级审批放行集 —— 同样运行期才解引用
            toolName => state.MainSession?.IsToolAllowed(toolName) ?? false,
            // 任务 5：Plan 档的「本回合放行集」按当前档位判定
            () => options.ApprovalTier);

        var runner = new AgentRunner(
            state.Switchable!,
            // 模式决定「这一轮给模型看什么」—— 装配不变，只改暴露面。
            // 闲聊模式返回空集：连工具 schema 都不发，这是最实在的一笔省。
            state.VisibleTools,
            sink,
            new AgentOptions
            {
                SessionId = options.SessionId,
                Model = "auto", // 由具体端点的 DefaultModel 决定
                SystemPrompt = options.SystemPrompt,
                Temperature = options.Temperature,
                MaxSteps = options.MaxSteps,
                IncludeUsage = options.IncludeUsage,
                OnTextDelta = text => state.TextRelay?.Invoke(options.SessionId, text),
                OnReasoningDelta = text => state.ReasoningRelay?.Invoke(options.SessionId, text),
            });

        state.Sink = sink;
        state.Runner = runner;
        state.MainSession = new SessionRuntime(options.SessionId, state.Log!, sink, runner);

        return ValueTask.CompletedTask;
    }
}
