using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AgentFramework.Contracts;
using AgentFramework.Data;
using AgentFramework.Llm;

namespace AgentFramework.Host;

/// <summary>
/// 对话界面的本地服务：HTTP + SSE + 会话管理。
///
/// 为什么用 SSE 而不是 WebSocket：对话是**单向流**（服务端推 → 浏览器收），
/// SSE 就是为这个场景设计的 —— 自动重连、纯文本、浏览器原生支持、服务端实现只有几十行。
/// 用 WebSocket 反而要自己处理心跳与重连。
/// </summary>
public sealed partial class WebUiServer : IDisposable, IApprovalPrompt
{
    private readonly HttpListener _listener = new();
    private readonly HostOptions _baseOptions;
    private readonly List<SseClient> _clients = [];
    private readonly Dictionary<string, TaskCompletionSource<ApprovalAnswer>> _pendingApprovals = [];
    private readonly object _gate = new();
    private readonly SemaphoreSlim _switchGate = new(1, 1);

    /// <summary>可注册路由：功能模块自带端点，不必再改主分发（见 <see cref="IWebRoute"/>）。</summary>
    private readonly WebRouteTable _routes = new();

    /// <summary>
    /// 注册一段路由。路径被占用时返回 false —— 冲突当场发现，而不是等运行到那一步。
    /// </summary>
    public bool Map(IWebRoute route)
    {
        if (_routes.Match(route.Method, route.Path) is not null)
        {
            return false;
        }

        _routes.Add(route);
        return true;
    }

    /// <summary>已注册的自定义路由数（诊断）。</summary>
    public int RouteCount => _routes.Count;

    private AgentHost _host;
    private bool _disposed;

    public WebUiServer(AgentHost host, HostOptions options, int port)
    {
        _host = host;
        _baseOptions = options;
        Url = $"http://localhost:{port}/";
        _listener.Prefixes.Add(Url);

        Attach(host);

        // 内置的可注册路由。示范：以后诊断类端点都长这样，不再往主分发里塞。
        Map(new ToolsRoute());

        // 按域注册内置端点（P5）：主分发只留 HTTP 管道、SSE、以及尚未搬走的端点。
        // 判断标准：以后加一个审批策略端点，不该再打开主分发文件。
        RegisterSessionRoutes();
        RegisterChatRoutes();
        RegisterModelRoutes();
        RegisterRephraseRoutes();
        RegisterStreamRoute();
    }

    public string Url { get; }

    private void Attach(AgentHost host)
    {
        host.EventEmitted += OnEventEmitted;
        host.TextDelta += OnTextDelta;
        host.ReasoningDelta += OnReasoningDelta;
        host.ApprovalPrompt = this;
    }

    private void Detach(AgentHost host)
    {
        host.EventEmitted -= OnEventEmitted;
        host.TextDelta -= OnTextDelta;
        host.ReasoningDelta -= OnReasoningDelta;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _listener.Start();

        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ct.IsCancellationRequested || ex is ObjectDisposedException)
            {
                break;   // 正常关停
            }
            catch (Exception ex)
            {
                // 瞬时异常不该让界面服务静默消失 —— 但要喘口气再重试，免得成了忙等
                Console.Error.WriteLine($"[WebUi] 接收请求出错：{ex.Message}");
                try
                {
                    await Task.Delay(200, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            _ = Task.Run(() => HandleAsync(context, ct), CancellationToken.None);
        }
    }

    public void Stop()
    {
        try
        {
            if (_listener.IsListening)
            {
                _listener.Stop();
            }
        }
        catch
        {
            // 关闭时异常无所谓
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Detach(_host);
        Stop();
        _switchGate.Dispose();
    }

    // ── 推送 ───────────────────────────────────────────────

    // B2：事件帧同样带 sessionId（事件里本身有 SessionId 字段，前端分流用外层的即可）。
    private void OnEventEmitted(SessionEvent sessionEvent)
        => Broadcast(JsonSerializer.SerializeToElement(
            new { type = "event", sessionId = sessionEvent.SessionId, @event = sessionEvent },
            WebUiJson.Options));

    // B2：delta / reasoning 帧带 sessionId —— P1 之后回合所属会话与当前会话可以不同，
    // 前端据此分流（当前会话照常渲染；后台会话只更新“进行中”标记，不动气泡）。
    private void OnTextDelta(string sessionId, string text)
        => Broadcast(JsonSerializer.SerializeToElement(new { type = "delta", sessionId, text }, WebUiJson.Options));

    /// <summary>
    /// 思考增量单独走一个消息类型。
    ///
    /// 与正文分开是刻意的：界面上两者该放不同位置（思考折叠、正文直出），
    /// 混在同一个通道里前端就再也没法区分了。
    /// </summary>
    private void OnReasoningDelta(string sessionId, string text)
        => Broadcast(JsonSerializer.SerializeToElement(new { type = "reasoning", sessionId, text }, WebUiJson.Options));

    /// <summary>
    /// IApprovalPrompt 实现：把审批请求推给浏览器，等用户点「允许 / 拒绝」。
    ///
    /// 等待设了超时上限 —— 用户关掉页面不响应时，不能把回合永远挂住。
    /// 超时按「拒绝」处理：安全侧的默认必须保守。
    /// </summary>
    /// <summary>
    /// P6：带「本会话记住」的审批。
    /// 老的 <see cref="AskAsync"/> 退化成它的薄封装 ——
    /// 「把问题问出去」这件事只有一份实现，多一个选项不该多一条路。
    /// </summary>
    public async ValueTask<ApprovalAnswer> AskDetailedAsync(ToolPreExecuteEvent request, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var completion = new TaskCompletionSource<ApprovalAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_gate)
        {
            _pendingApprovals[id] = completion;
        }

        Broadcast(JsonSerializer.SerializeToElement(new
        {
            type = "approval",
            id,
            toolName = request.ToolName,
            arguments = request.Arguments,
            // 告诉卡片：可以给出「本会话允许此工具」这个选项（P6）
            canRemember = true,
        }, WebUiJson.Options));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));

        try
        {
            await using var registration = timeout.Token
                .Register(() => completion.TrySetResult(new ApprovalAnswer(false, false)))
                .ConfigureAwait(false);

            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _pendingApprovals.Remove(id);
            }
        }
    }

    /// <summary>只问「允不允许」—— 老缝的语义，薄封装在 <see cref="AskDetailedAsync"/> 之上。</summary>
    public async ValueTask<bool> AskAsync(ToolPreExecuteEvent request, CancellationToken ct)
        => (await AskDetailedAsync(request, ct).ConfigureAwait(false)).Allowed;

    /// <summary>
    /// 每个 SSE 客户端一个写循环：队列 → 网络。
    /// 网络写从回合线程上挪开 —— 回合不该等任何一个客户端（P1-F3）。
    /// </summary>
    private void StartWriter(SseClient client)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var frame in client.Queue.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    await client.Response.OutputStream.WriteAsync(frame).ConfigureAwait(false);
                    await client.Response.OutputStream.FlushAsync().ConfigureAwait(false);
                }
            }
            catch
            {
                RemoveClient(client);
            }
        });
    }

    /// <summary>
    /// 广播只入队，不做网络写。
    /// 从前这里是同步写网络：一个不读数据的标签页就能把整个 Agent 拖住。
    /// 现在最坏也只是那个客户端自己丢几帧。
    /// </summary>
    private void Broadcast(JsonElement payload)
    {
        var frame = Encoding.UTF8.GetBytes($"data: {payload.GetRawText()}\n\n");

        SseClient[] snapshot;
        lock (_gate)
        {
            snapshot = [.. _clients];
        }

        foreach (var client in snapshot)
        {
            client.Queue.Writer.TryWrite(frame);   // 永不阻塞；队列满自动丢最老
        }
    }

    private void RemoveClient(SseClient client)
    {
        lock (_gate)
        {
            _clients.Remove(client);
        }

        client.Queue.Writer.TryComplete();   // 让写循环自然退出

        try
        {
            client.Response.Close();
        }
        catch
        {
            // 已经断了
        }
    }

    // ── 会话管理 ───────────────────────────────────────────

    /// <summary>
    /// 列出所有会话。直接扫 sessions 目录里的 .jsonl ——
    /// 因为**日志就是会话本身**，不需要额外的会话注册表。
    /// </summary>
    private List<SessionSummary> ListSessions()
    {
        var result = new List<SessionSummary>();
        var dir = _baseOptions.SessionsDir;

        if (!Directory.Exists(dir))
        {
            return result;
        }

        foreach (var file in Directory.EnumerateFiles(dir, "*.jsonl"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            try
            {
                var info = new FileInfo(file);

                // ★ 流式扫一遍就够（P2-2）：
                //   原先每次都 JsonlEventLog.Read(file).ToList() —— 把所有会话的**全文**
                //   物化进内存，只为取一个首条消息和两个计数。会话一多、越长，这笔浪费越大。
                //   JsonlEventLog.Read 本身就是惰性迭代器，这里顺势边读边数、读完即丢。
                var count = 0;
                var messages = 0;
                string? firstUser = null;

                foreach (var e in JsonlEventLog.Read(file))
                {
                    count++;

                    switch (e)
                    {
                        case UserMessageEvent user:
                            messages++;
                            firstUser ??= user.Text;
                            break;

                        case AssistantMessageEvent:
                            messages++;
                            break;
                    }
                }

                // F4：自定义标题优先（titles.json），否则回落首条用户消息
                var customTitle = _host.LoadTitles().TryGetValue(id, out var customT) ? customT : null;

                result.Add(new SessionSummary(
                    id,
                    count,
                    messages,
                    info.LastWriteTimeUtc,
                    customTitle ?? (firstUser is { Length: > 0 } text ? Truncate(text, 28) : "(空会话)"),
                    string.Equals(id, _host.SessionId, StringComparison.Ordinal),
                    _host.SessionMetas().TryGetValue(id, out var sessMeta) ? sessMeta.Mode : "work",
                    _host.SessionMetas().TryGetValue(id, out var sessMeta2) ? sessMeta2.ProjectDir : null));
            }
            catch
            {
                // 坏日志不该让会话列表整个挂掉
                result.Add(new SessionSummary(id, 0, 0, DateTimeOffset.MinValue, "(无法读取)", false, "work", null));
            }
        }

        return [.. result.OrderByDescending(s => s.UpdatedAt)];
    }

    /// <summary>
    /// 上下文水位报告 —— 界面上的水位条与「压缩过几次」都读它。
    /// 这里会重投影一遍：一次 HTTP 请求的代价，换一个看得懂的状态，划算。
    /// </summary>
    private object ContextStatus()
    {
        var events = _host.Events();
        // v3.5 审查 P2：水位必须与**真实回合**同一口径（按当前档位生效的配置）——
        // 直接读 ContextSettings 的话，档位把治理关掉时界面会显示一个并不存在的预算。
        var projection = SessionContextBuilder.Project(events, _host.EffectiveContextSettings());

        return new
        {
            budget = projection.Budget,
            tokens = projection.EstimatedTokens,
            waterLevel = Math.Round(projection.WaterLevel, 3),
            turns = projection.TotalTurns,
            keptTurns = projection.KeptTurns,
            maskedResults = projection.MaskedResults,
            compactions = events.OfType<ContextCompactedEvent>().Count(),
            taskCard = projection.TaskCardInjected,
            summaryEnabled = _host.ContextSettings.SummarizeOlderHistory,
            summarizerAvailable = _host.ContextSummarizerAvailable,
            index = new
            {
                kind = _host.Index.Kind,
                available = _host.Index.IsAvailable,
            },
        };
    }

    /// <summary>
    /// 当前模式档位 —— 界面上的模式按钮、以及「这一轮到底注入了什么」都读它。
    /// 有了它，「少调用」就是**看得见**的，而不是一句口号。
    /// </summary>
    private object ModeStatus()
    {
        var profile = _host.ModeProfile;

        return new
        {
            // HCI：模式属会话 —— id 直接取会话钉住的字符串（含插件自定义档位），不再二值化
            current = _host.ModeId ?? AgentModes.IdOf(profile.Mode),
            name = profile.Name,
            memoryScopes = profile.MemoryScopes,
            exposesTools = profile.ExposesTools,
            tools = _host.ExposedToolNames,
            taskCard = profile.InjectTaskCard,
            governance = profile.ContextGovernance,
        };
    }

    /// <summary>
    /// 会话用量账目（累计）。
    ///
    /// 它是从事件流**投影**出来的 —— 所以重启界面、换个浏览器再看，数字都一样。
    /// 这比「内存里挂一个计数器」强的地方在于：对不上的时候永远以日志为准。
    /// </summary>
    private object UsageStatus()
    {
        var usage = _host.Usage;

        return new
        {
            calls = usage.Calls,
            unknownCalls = usage.UnknownCalls,
            inputTokens = usage.InputTokens,
            outputTokens = usage.OutputTokens,
            cachedTokens = usage.CachedTokens,
            cacheWriteTokens = usage.CacheWriteTokens,
            reasoningTokens = usage.ReasoningTokens,
            totalTokens = usage.TotalTokens,
            cacheHitRate = usage.CacheHitRate is null
                ? (double?)null
                : Math.Round(usage.CacheHitRate.Value, 4),
        };
    }

    /// <summary>
    /// 命令沙箱档位。界面上看得见「当前这一档到底管住了什么」——
    /// 回落（配置写了 job、机器上用 process）也在这里如实说明。
    /// </summary>
    private object SandboxStatus()
    {
        var info = _host.SandboxInfo;

        return new
        {
            name = info.Name,
            description = info.Description,
            note = info.Note,
        };
    }

    // ── 模型管理 ─────────────────────────────────────────────

    /// <summary>
    /// 端点与模型的全貌。
    /// <b>刻意不回传密钥本身</b> —— 只告诉界面「有没有配」，
    /// 密钥这种东西不该为了画个界面就在前后端之间来回跑。
    /// </summary>
    private object ModelsStatus()
    {
        var store = _host.Models;
        var settings = _host.ModelSettings;

        return new
        {
            enabled = store is not null,
            configPath = store?.Path,
            protector = new
            {
                kind = store?.Protector.Kind ?? "none",
                available = store?.Protector.IsAvailable ?? false,
            },
            active = settings?.Active is null
                ? null
                : new { providerId = settings.Active.ProviderId, modelId = settings.Active.ModelId },
            effective = _host.ActiveModelName,
            providers = settings is null
                ? []
                : settings.Providers.Select(p => new
                {
                    id = p.Id,
                    name = p.Name,
                    baseUrl = p.BaseUrl,
                    hasKey = !string.IsNullOrWhiteSpace(p.ApiKey) || !string.IsNullOrWhiteSpace(p.ApiKeyProtected),
                    local = p.IsLocalLike,
                    reasoningStyle = string.IsNullOrWhiteSpace(p.ReasoningStyle) ? ReasoningStyles.None : p.ReasoningStyle,
                    reasoningEffort = p.ReasoningEffort ?? string.Empty,
                    models = p.Models.Select(m => new
                    {
                        id = m.Id,
                        label = m.Label,
                        // 没标注的按启发式给一个，界面据此决定要不要画思考块
                        reasoning = m.SupportsReasoning ?? ModelCapabilities.GuessSupportsReasoning(m.Id),
                    }).ToList(),
                }).ToList(),
        };
    }

    /// <summary>
    /// 测试连接 —— 就是拉一次 `GET {baseUrl}/models`。
    /// 界面不回传密钥（那等于让它到处乱跑），只传 providerId，由这里去取。
    /// </summary>
    private async Task<ModelProbeResult> ProbeModelsAsync(JsonElement body, CancellationToken ct)
    {
        var baseUrl = ReadString(body, "baseUrl") ?? string.Empty;
        var apiKey = ReadString(body, "apiKey");
        var providerId = ReadString(body, "providerId");

        var protector = _host.Models?.Protector ?? SecretProtectors.Default;

        if (string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(providerId))
        {
            var stored = _host.ModelSettings?.FindProvider(providerId);

            // ★ 只有地址与已存端点一致时才附带存储的密钥（P1-S4）。
            //   「providerId + 新地址」一律不带 key —— 否则只要 baseUrl 被改掉，
            //   任何一次 probe 都会把真实密钥送到那个新地址去。
            if (stored is not null
                && !string.IsNullOrWhiteSpace(baseUrl)
                && string.Equals(stored.BaseUrl?.Trim().TrimEnd('/'), baseUrl.Trim().TrimEnd('/'),
                                 StringComparison.OrdinalIgnoreCase))
            {
                apiKey = stored.ResolveApiKey(protector);
            }
        }

        var probe = new ProviderConfig
        {
            Id = providerId ?? "probe",
            BaseUrl = baseUrl,
            ApiKey = apiKey,
        };

        return await ModelDiscovery.ProbeAsync(probe, protector, ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 保存一个端点（新建或更新）。请求体形如：
    /// <c>{ "id": "...", "name": "...", "baseUrl": "...", "apiKey": "...", "models": ["a","b"] }</c>
    /// </summary>
    private (bool Ok, string? Error) SaveProvider(JsonElement body)
    {
        var store = _host.Models;
        var settings = _host.ModelSettings;

        if (store is null || settings is null)
        {
            return (false, "这次启动没有启用模型配置（缺少配置单路径）");
        }

        var id = (ReadString(body, "id") ?? string.Empty).Trim();
        var baseUrl = (ReadString(body, "baseUrl") ?? string.Empty).Trim();

        if (id.Length == 0)
        {
            return (false, "端点 id 不能为空");
        }

        if (baseUrl.Length == 0)
        {
            return (false, "端点地址（baseUrl）不能为空");
        }

        var provider = settings.FindProvider(id);

        // ★ 改地址必须同时重输密钥（P1-S4）：否则「改掉 baseUrl」这一步
        //   就能把已存的密钥悄悄带去一个新地址，而界面看上去只是换了个地址。
        var addressChanged = provider is not null
            && !string.Equals(provider.BaseUrl?.Trim().TrimEnd('/'), baseUrl.Trim().TrimEnd('/'),
                              StringComparison.OrdinalIgnoreCase);

        if (addressChanged && string.IsNullOrWhiteSpace(ReadString(body, "apiKey")))
        {
            return (false, "修改端点地址需要同时重新确认密钥（防止存储的密钥被发往新地址）");
        }

        if (provider is null)
        {
            provider = new ProviderConfig { Id = id, BaseUrl = baseUrl };
            settings.Providers.Add(provider);
        }

        provider.Name = ReadString(body, "name") ?? provider.Name;
        provider.BaseUrl = baseUrl;

        // 思考参数风格与默认强度：请求里带了就更新（界面编辑器里选）
        if (body.TryGetProperty("reasoningStyle", out var styleNode) && styleNode.ValueKind == JsonValueKind.String)
        {
            var style = (styleNode.GetString() ?? ReasoningStyles.None).Trim().ToLowerInvariant();
            provider.ReasoningStyle = style is ReasoningStyles.OpenAi or ReasoningStyles.Qwen ? style : ReasoningStyles.None;
        }

        if (body.TryGetProperty("reasoningEffort", out var effortNode) && effortNode.ValueKind == JsonValueKind.String)
        {
            var effort = (effortNode.GetString() ?? string.Empty).Trim().ToLowerInvariant();
            provider.ReasoningEffort = effort is "off" or "low" or "medium" or "high" ? effort : string.Empty;
        }

        // 只有界面**真的传了**密钥才动它：空字符串表示「不改」，
        // 否则每次改个模型名都会把密钥抹掉。
        var apiKey = ReadString(body, "apiKey");
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            store.ApplySecret(provider, apiKey);
        }

        if (body.TryGetProperty("models", out var modelsNode) && modelsNode.ValueKind == JsonValueKind.Array)
        {
            var incoming = modelsNode
                .EnumerateArray()
                .Where(m => m.ValueKind == JsonValueKind.String)
                .Select(m => (m.GetString() ?? string.Empty).Trim())
                .Where(m => m.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            // 保留已有模型的标注（reasoning / 上下文窗口），别因为重列一遍就丢
            provider.Models = [.. incoming.Select(modelId =>
                provider.Models.FirstOrDefault(existing => string.Equals(existing.Id, modelId, StringComparison.Ordinal))
                ?? new ModelEntry { Id = modelId })];
        }

        if (settings.Active is null || settings.FindProvider(settings.Active.ProviderId) is null)
        {
            settings.Active = new ActiveModelRef
            {
                ProviderId = provider.Id,
                ModelId = provider.Models.FirstOrDefault()?.Id ?? string.Empty,
            };
        }

        _host.ApplyModelSettings(settings);
        return (true, null);
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";

    /// <summary>
    /// 切换会话 = 重新装配一个宿主（旧宿主释放，插件重新加载）。
    ///
    /// 与回合互斥（P0-3）：旧宿主还在跑回合时**不许拆它** ——
    /// 拆了它，正在跑的那一轮会在写事件时撞上已经 Dispose 的日志与插件，
    /// 抛 ObjectDisposedException。等不到就明确报错，比崩掉好。
    /// </summary>
    private async Task<(bool Ok, string? Error)> SwitchSessionAsync(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)
            || string.Equals(sessionId, _host.SessionId, StringComparison.Ordinal))
        {
            return (true, null);
        }

        await _switchGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (string.Equals(sessionId, _host.SessionId, StringComparison.Ordinal))
            {
                return (true, null);
            }

            // ★ P1：切会话 = 换宿主的「当前会话」指针，**不再重建宿主**。
            //
            //   老做法（4.11）是「切换 = 整宿主重建」：重载全部插件、重开 SQLite、
            //   重扫 profile、重读记忆 —— 插件一多就是秒级卡顿。那时这么做的理由是
            //   「丢掉宿主 = 零共享可变状态」，可那条理由的前提是「宿主 == 会话」，
            //   而 4.19 已经把这个前提推翻了（宿主 = 装配，会话 = runtime）。
            //
            //   现在装配只有一份：不重建、不卸载、不 Detach/Attach，切换零延迟；
            //   而且**没有 dispose 就没有那个亚秒级竞态**（P0 的窗口从根上消失）。
            //   仍然等回合结束 —— 但理由变了：不是为了防拆，
            //   而是语义上「当前会话正在跑」时切走，会让 SSE 的输出属于上一个会话。
            if (!await _host.SwitchSessionAsync(sessionId).ConfigureAwait(false))
            {
                return (false, "当前会话有回合正在进行，请等它结束再切换");
            }
        }
        finally
        {
            _switchGate.Release();
        }

        // 通知所有页面刷新
        Broadcast(JsonSerializer.SerializeToElement(
            new { type = "session-switched", sessionId },
            WebUiJson.Options));

        return (true, null);
    }

    // ── 入口处的两道校验 ───────────────────────────────────

    /// <summary>
    /// 会话 id 白名单：只允许字母数字与 - _（最长 64）。
    ///
    /// 两个端点都拿 id 直接拼路径 —— 不校验的话，
    /// id = "..\\..\\某文件" 能让 switch 对任意文件跑「扫描修复」（截断尾部），
    /// 让 delete 删掉任意 *.jsonl。这是路径穿越，不是理论风险。
    /// </summary>
    private static bool IsValidSessionId(string? id)
        => !string.IsNullOrWhiteSpace(id)
           && id.Length <= 64
           && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>
    /// 跨站请求防护：浏览器发起的跨站 fetch 必带 Origin 头。
    /// 本地服务的 POST 一律要求 Origin 为空（curl / 桌面壳 / 验证程序）
    /// 或与自身同源 —— 否则任意网页都能 fetch localhost:8090 替你发消息、替你点「允许」。
    /// </summary>
    /// <summary>
    /// 找插件目录下的 panel.html（创意工坊时代的插件独立面板约定：
    /// 插件目录内放一个 panel.html，管理页即可点入）。
    /// 插件目录名不一定等于 manifest id（目录名由用户起的），所以扫 plugin.json 匹配 id。
    /// </summary>
    private string? FindPanelPath(string pluginId)
    {
        var pluginsDir = _baseOptions.PluginsDir;
        if (string.IsNullOrWhiteSpace(pluginsDir) || !Directory.Exists(pluginsDir))
        {
            return null;
        }

        foreach (var dir in Directory.EnumerateDirectories(pluginsDir))
        {
            var manifest = Path.Combine(dir, "plugin.json");
            if (!File.Exists(manifest))
            {
                continue;
            }

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest));
                if (doc.RootElement.TryGetProperty("id", out var idEl)
                    && string.Equals(idEl.GetString(), pluginId, StringComparison.Ordinal)
                    && File.Exists(Path.Combine(dir, "panel.html")))
                {
                    return Path.Combine(dir, "panel.html");
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // 坏清单不拦其他插件的面板查找
            }
        }

        return null;
    }

    private static bool IsAllowedOrigin(HttpListenerRequest request)
    {
        var origin = request.Headers["Origin"];
        if (string.IsNullOrEmpty(origin))
        {
            return true;
        }

        var port = request.LocalEndPoint?.Port ?? request.Url?.Port ?? 80;
        // 与 IsAllowedHost 的白名单保持一致：Host 收 [::1]，Origin 也要收 http://[::1]:{port}，
        // 否则用 [::1] 打开界面时页面能看、所有 POST 却全被 403。
        return origin.Equals($"http://localhost:{port}", StringComparison.OrdinalIgnoreCase)
            || origin.Equals($"http://127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase)
            || origin.Equals($"http://[::1]:{port}", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 防 DNS rebinding（安全审查 P0-2）：只认可指向本机的 Host 头。
    /// 恶意页把 evil.com 解析到 127.0.0.1 后，从浏览器看请求是"同源"的 ——
    /// CORS 与 Origin 校验都挡不住；但 Host 头仍会是 evil.com，一眼识破。
    /// </summary>
    private bool IsAllowedHost(HttpListenerRequest request)
    {
        var host = request.Headers["Host"];
        if (string.IsNullOrEmpty(host))
        {
            // HTTP/1.1 要求必有 Host；缺失只可能来自手工构造的包，拒绝。
            return false;
        }

        var port = request.LocalEndPoint?.Port ?? request.Url?.Port ?? 80;
        return host.Equals($"localhost:{port}", StringComparison.OrdinalIgnoreCase)
            || host.Equals($"127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase)
            || host.Equals($"[::1]:{port}", StringComparison.OrdinalIgnoreCase);
    }

    // ── 路由 ───────────────────────────────────────────────

    private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        var path = context.Request.Url?.AbsolutePath ?? "/";
        var method = context.Request.HttpMethod;

        try
        {
            // 防 DNS rebinding（安全审查 P0-2）：浏览器发同源请求时 Host 头会是被 rebind 的域名，
            // 而 CORS 管不了"同源"请求。只认可 localhost/127.0.0.1 的本机 Host ——
            // curl / 桌面壳 / 浏览器直开都带正确 Host，零影响；rebind 域名一律 403。
            // 与 POST 的 Origin 校验互补：Origin 挡跨站，Host 挡同源伪装。
            if (!IsAllowedHost(context.Request))
            {
                WriteJson(context, new { ok = false, error = "invalid host" }, 403);
                return;
            }

            // 跨站请求防护（P1-S1）：放在最前面，任何路由都不该绕开它。
            if (method == "POST" && !IsAllowedOrigin(context.Request))
            {
                WriteJson(context, new { ok = false, error = "cross-origin request rejected" }, 403);
                return;
            }

            // 注册路由优先。顺序刻意放在内置路由**之前**：
            // 注册进来的要么用新路径，要么就是想覆盖内置行为 —— 两种都该让它先说话。
            // 用请求的 ct 而不是 CancellationToken.None：关停/取消时自定义路由能跟着停，
            // 而不是在后台继续跑完一整段工作。
            if (_routes.Match(method, path) is { } custom)
            {
                await custom
                    .HandleAsync(new WebUiRequest(context, _host, method, path), ct)
                    .ConfigureAwait(false);
                return;
            }

            // 路由表与内置路由都没命中 → 404。
            //
            // P5 之后端点都自带路由（WebUiServer.Routes.*.cs），这里不再是一个
            // 越写越长的 switch ——「加一个端点」不必再打开这个文件。
            WriteJson(context, new { ok = false, error = "not found" }, 404);
            return;
        }
        catch (Exception ex)
        {
            // 异常详情只进日志（安全审查 P1-2）：ex.Message 可能含绝对路径/配置细节，
            // 叠加 rebinding 就是磁盘信息校举器。对外统一一句，排查靠控制台。
            try
            {
                Console.Error.WriteLine($"[webui] {method} {path} → 500: {ex}");
                WriteJson(context, new { ok = false, error = "操作失败（详情见宿主日志）" }, 500);
            }
            catch
            {
                // 连接已断
            }
        }
    }

    internal static async Task<JsonElement?> ReadJsonBodyAsync(HttpListenerContext context)
    {
        // 请求体上限（安全审查 P1-1）：无上限的 ReadToEnd 是内存 DoS 入口。
        // 1 MB 对本服务全部端点绰绰有余（发消息/审批/配置单都定几十 KB 级）。
        const long MaxBodyBytes = 1_000_000;

        if (context.Request.ContentLength64 is > MaxBodyBytes)
        {
            return null;
        }

        // Content-Length 可能缺失（chunked）——按字节硬截断读，同样不超标。
        using var limited = new MemoryStream();
        var buffer = new byte[8_192];
        int read;
        var total = 0L;
        while ((read = await context.Request.InputStream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxBodyBytes)
            {
                return null;   // 超限：当作非法 body，由各端点自己的 null 分支拒绝
            }

            await limited.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
        }

        limited.Position = 0;
        string body;
        using (var reader = new StreamReader(limited, Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryReadBool(JsonElement element, string property, out bool value)
    {
        value = false;

        if (!element.TryGetProperty(property, out var node))
        {
            return false;
        }

        if (node.ValueKind == JsonValueKind.True)
        {
            value = true;
            return true;
        }

        if (node.ValueKind == JsonValueKind.False)
        {
            return true;
        }

        return false;
    }

    private static bool TryReadInt(JsonElement element, string property, out int value)
    {
        value = 0;
        return element.TryGetProperty(property, out var node)
            && node.ValueKind == JsonValueKind.Number
            && node.TryGetInt32(out value);
    }

    internal static void WriteText(HttpListenerContext context, string contentType, string content, int statusCode = 200)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes);
        context.Response.Close();
    }

    internal static void WriteJson(HttpListenerContext context, object payload, int statusCode = 200)
        => WriteText(
            context,
            "application/json; charset=utf-8",
            JsonSerializer.Serialize(payload, WebUiJson.Options),
            statusCode);

    internal static void WriteRawJson(HttpListenerContext context, string json, int statusCode = 200)
        => WriteText(context, "application/json; charset=utf-8", json, statusCode);

    private sealed class SseClient(HttpListenerResponse response)
    {
        public HttpListenerResponse Response { get; } = response;

        /// <summary>
        /// 有界队列：满了丢最老的。
        /// 「一个不读数据的标签页不该拖住整个 Agent」—— 这是它存在的全部理由。
        /// </summary>
        public Channel<byte[]> Queue { get; } =
            Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
            });
    }
}

public sealed record SessionSummary(
    string Id,
    int EventCount,
    int MessageCount,
    DateTimeOffset UpdatedAt,
    string Preview,
    bool IsCurrent,
    string Mode,
    string? ProjectDir);

internal static class WebUiJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        // 中文等非 ASCII 直接输出（默认 encoder 会转成 \uXXXX）：
        // 本服务的 JSON 一律以 UTF-8 输出、由浏览器 JSON.parse 消费，宽松转义是安全的；
        // 同时让会话标题等中文内容在原始 JSON 里保持可读、可 grep。
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
