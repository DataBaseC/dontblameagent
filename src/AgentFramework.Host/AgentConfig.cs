using System.Text.Json;

namespace AgentFramework.Host;

/// <summary>
/// 配置单（<c>agent.json</c>）—— 把端点、目录、开关都收进一个文件，
/// 不用每次都敲环境变量。
///
/// 刻意做成「一个平坦的 JSON，字段全部可选」：
///   · 没写的字段一律走默认值，不必抄一整份模板
///   · 允许 <c>//</c> 注释与尾逗号 —— 配置单是给人读的，不是给机器读的
///   · 里面只会读启动参数，不存运行时状态
///
/// 优先级（越显式越优先）：<b>命令行 &gt; 环境变量 &gt; 配置单</b>。
/// 于是「平时靠配置单、临时用环境变量覆盖」这种用法是自然的。
/// </summary>
public sealed class AgentConfig
{
    /// <summary>工作区目录（文件与命令工具的边界）。</summary>
    public string? Workspace { get; set; }

    /// <summary>会话日志目录（每个会话一个 .jsonl）。</summary>
    public string? Sessions { get; set; }

    /// <summary>插件目录（其下每个子目录是一个插件）。</summary>
    public string? Plugins { get; set; }

    /// <summary>只加载这些插件 id；省略 = 全部加载。</summary>
    public List<string>? EnabledPlugins { get; set; }

    /// <summary>会话 id。</summary>
    public string? SessionId { get; set; }

    /// <summary>Web 界面端口（默认 8090）。</summary>
    public int? WebPort { get; set; }

    /// <summary>启动时的工作模式：<c>work</c> 或 <c>chat</c>。</summary>
    public string? Mode { get; set; }

    /// <summary>是否要求端点回报用量（成本与缓存命中率靠它）。个别老端点不认时设为 false。</summary>
    public bool? IncludeUsage { get; set; }

    /// <summary>系统提示词（不写就用内置的）。</summary>
    public string? SystemPrompt { get; set; }

    public double? Temperature { get; set; }

    public int? MaxSteps { get; set; }

    /// <summary>上下文治理参数（不写就用默认值）。</summary>
    public ContextConfig? Context { get; set; }

    /// <summary>云端端点（负责复杂任务、要用工具的活）。</summary>
    public EndpointConfig? Cloud { get; set; }

    /// <summary>本地端点（LM Studio / Ollama 等，负责量大但简单的活）。</summary>
    public EndpointConfig? Local { get; set; }

    /// <summary>
    /// 搜索后端降级链（逗号分隔，按序尝试，第一个出结果的胜出）。
    /// 可选：bing、baidu、searxng；默认三者全开。
    /// </summary>
    public string? SearchBackends { get; set; }

    /// <summary>自建 SearXNG 实例地址（搜索链里含 searxng 时用）。</summary>
    public string? SearxngBaseUrl { get; set; }

    // ── 模型管理 ─────────────────────────────────────────────

    /// <summary>
    /// 多个模型端点。不填时会**自动**从上面的 cloud / local 迁移出两个 provider ——
    /// 老配置单照常能用，不需要手工改写。
    /// </summary>
    public List<ProviderConfig>? Providers { get; set; }

    /// <summary>当前选中的端点与模型；不填则用第一个可用端点。</summary>
    public ActiveModelRef? Active { get; set; }

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// 读配置单。文件不存在返回 null（= 没配，走默认）。
    /// 文件坏掉**不阻断启动** —— 提示一声，然后按默认跑。
    /// </summary>
    public static AgentConfig? TryLoad(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path), ReadOptions);
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"[警告] 配置单解析失败，已按默认配置继续：{ex.Message}");
            return null;
        }
    }

    /// <summary>把默认值写成一份带注释的配置单样例（供人照抄）。</summary>
    public static string Sample() => """
        {
          // ── 端点：填「地址 + 模型名」就算配好 ──────────────────
          // 云端（负责复杂任务、要用工具的活）
          "cloud": {
            "baseUrl": "https://api.deepseek.com/v1",
            "apiKey": "在这里填你的 key",
            "model": "deepseek-reasoner"      // 想看思考过程就用 reasoner；deepseek-chat 则没有
          },

          // 本地（可选。LM Studio 默认 1234；Ollama 是 http://localhost:11434/v1）
          "local": {
            "baseUrl": "http://localhost:1234/v1",
            "model": "你加载的模型名"
          },

          // ── 目录与界面 ────────────────────────────────────────
          "workspace": "agent-workspace",
          "sessions": "agent-sessions",
          "webPort": 8090,
          "mode": "work",                     // work = 干活；chat = 闲聊（不挂工具、少花钱）

          // ── 其它开关（都可以省略）──────────────────────────────
          // "includeUsage": true,             // 端点不认 stream_options 时设为 false
          // "maxSteps": 12,
          // "temperature": 0.7,
          // "context": { "tokenBudget": 24000, "compressionTriggerRatio": 0.8 }
        }
        """;
}

/// <summary>一个模型端点。</summary>
public sealed class EndpointConfig
{
    public string? BaseUrl { get; set; }

    /// <summary>本地端点不需要填。</summary>
    public string? ApiKey { get; set; }

    public string? Model { get; set; }
}

/// <summary>上下文治理参数（不写就用默认值）。</summary>
public sealed class ContextConfig
{
    public int? TokenBudget { get; set; }

    public double? CompressionTriggerRatio { get; set; }

    public int? RecentTurnsKeptVerbatim { get; set; }

    public bool? InjectTaskCard { get; set; }
}
