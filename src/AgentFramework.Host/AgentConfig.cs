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

    /// <summary>
    /// 命令沙箱档位：<c>auto</c>（默认，Windows 用 job、其他平台用 process）/ <c>off</c> /
    /// <c>process</c> / <c>job</c> —— 或插件注册的后端名。
    /// </summary>
    public string? Sandbox { get; set; }

    /// <summary>
    /// shell 偏好（会话级一次决定，优先 POSIX）：<c>bash</c>/<c>sh</c>/<c>cmd</c>/<c>powershell</c>/<c>pwsh</c> /
    /// 显式路径 / <c>auto</c>。省略 = 自动解析。
    /// </summary>
    public string? Shell { get; set; }

    /// <summary>
    /// 启动时就收起的工具包（空 = 全部开启）。
    /// 保留包（core / meta）写了也不生效 —— 那是「把 agent 关成残废」，不是省负担。
    /// </summary>
    public List<string>? DisabledToolsets { get; set; }

    /// <summary>只加载这些插件 id；省略 = 全部加载。</summary>
    public List<string>? EnabledPlugins { get; set; }

    /// <summary>
    /// 外部 MCP server 列表（子进程 + stdio JSON-RPC）。
    /// 每个 server 成一个 <c>mcp:&lt;id&gt;</c> 工具包，<b>默认延迟</b>（注册但不进 schema），用到再开。
    /// </summary>
    public List<Mcp.McpServerConfig>? McpServers { get; set; }

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

    /// <summary>
    /// Goal 停止条件（自然语言，如「所有测试通过且代码已提交」）。不写 = 不启用目标核验。
    /// 模型每次想收尾时由旁路验证者裁决「真的达成了吗」，未达成就带着差距继续 —— 防提前收工。
    /// </summary>
    public string? Goal { get; set; }

    public double? Temperature { get; set; }

    /// <summary>
    /// 一个回合的最大步数（默认 60 —— 编程任务动辄几十步工具调用）。
    /// 到上限不硬杀：末步会注入「交接摘要」提示优雅收尾。
    /// </summary>
    public int? MaxSteps { get; set; }

    /// <summary>上下文治理参数（不写就用默认值）。</summary>
    public ContextConfig? Context { get; set; }

    /// <summary>
    /// 输入级权限规则（MiMo 的 permission rules）：
    /// <c>[{"match":"run_command git *","action":"allow"}]</c>。
    /// 按数组顺序匹配，<strong>后写覆盖先写</strong>（last-match-wins）；
    /// action = <c>allow</c> / <c>ask</c> / <c>deny</c>。不写 = 纯按审批档位判定。
    /// </summary>
    public List<ApprovalRuleConfig>? ApprovalRules { get; set; }

    /// <summary>
    /// 授予越界目录访问（<c>external_directory</c>）。默认 false：
    /// 写到工作区之外一律不静默放行（询问；无界面即拒绝），规则与档位都推不翻。
    /// </summary>
    public bool? AllowExternalDirectory { get; set; }

    /// <summary>
    /// 进化配置（PLAN-memory-evolution 支柱四）：Dream（记忆周期整理）/ Distill（技能固化）。
    /// 按累计回合数触发；Dream 默认开（30 回合一轮），Distill 默认关（要写工作区）。
    /// </summary>
    public EvolutionConfig? Evolution { get; set; }

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

    /// <summary>
    /// 生成一份<strong>严格 JSON</strong>配置单（无 //、无尾逗号）。
    /// 内置一个免费视觉友好端点（AMD Radeon 开发者 API）作开箱默认；
    /// 密钥<b>不预置</b> —— 留占位符，请填入自己的 key（或用 AGENT_CLOUD_KEY 环境变量）。
    /// </summary>
    public static string Sample() => """
        {
          "systemPrompt": "你是「dba助手」，可靠的桌面 AI 助理。\n人设：冷静、口语化、先结论后细节。\n硬约束：\n- 不编造；不确定就说不确定\n- 引用网页用 markdown 链接\n- 涉及写文件/执行命令时先说明要做什么\n- 回答尽量短，除非我要求展开",
          "cloud": {
            "baseUrl": "https://developer.amd.com.cn/radeon/api/v1",
            "apiKey": "在这里填你的 key",
            "model": "MiniCPM5-2B"
          },
          "local": {
            "baseUrl": "http://localhost:1234/v1",
            "model": "你加载的模型名"
          },
          "providers": [
            {
              "id": "amd-radeon",
              "name": "AMD Radeon 免费端点",
              "baseUrl": "https://developer.amd.com.cn/radeon/api/v1",
              "apiKey": "在这里填你的 key",
              "reasoningStyle": "none",
              "reasoningEffort": "",
              "models": [
                { "id": "MiniCPM5-2B", "displayName": "MiniCPM5-2B", "supportsVision": true, "supportsReasoning": false }
              ]
            }
          ],
          "active": { "providerId": "amd-radeon", "modelId": "MiniCPM5-2B" },
          "workspace": "agent-workspace",
          "sessions": "agent-sessions",
          "plugins": "plugins",
          "webPort": 8090,
          "sessionId": "default",
          "mode": "work",
          "temperature": 0.7,
          "maxSteps": 12,
          "includeUsage": true,
          "context": {
            "tokenBudget": 24000,
            "compressionTriggerRatio": 0.8,
            "recentTurnsKeptVerbatim": 2,
            "injectTaskCard": true
          },
          "sandbox": "auto",
          "shell": "auto"
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

    /// <summary>
    /// 提前摘要水位（默认 0.45）：到这个水位就先把「工作记忆」增量提取落盘，不收紧窗口。
    /// 0 = 关闭（回到「到压缩水位才一次性处理」的旧行为）。
    /// </summary>
    public double? EarlySummarizeRatio { get; set; }

    public int? RecentTurnsKeptVerbatim { get; set; }

    public bool? InjectTaskCard { get; set; }
}

/// <summary>一条输入级权限规则（配置单形态）：模式匹配 + 三值处置。</summary>
public sealed class ApprovalRuleConfig
{
    /// <summary>匹配「工具名 + 关键参数」签名的模式（<c>*</c>/<c>?</c> 通配，大小写不敏感）。</summary>
    public string? Match { get; set; }

    /// <summary>处置：<c>allow</c> / <c>ask</c> / <c>deny</c>。</summary>
    public string? Action { get; set; }
}

/// <summary>进化配置（不写就用默认值）。</summary>
public sealed class EvolutionConfig
{
    /// <summary>Dream 周期（累计回合数）；0 = 关闭。</summary>
    public int? DreamEveryTurns { get; set; }

    /// <summary>Distill 周期（累计回合数）；0 / 不写 = 关闭。</summary>
    public int? DistillEveryTurns { get; set; }

    /// <summary>模式至少出现多少次才算「反复出现」（默认 3）。</summary>
    public int? DistillMinOccurrences { get; set; }

    /// <summary>模式至少横跨多少个会话（默认 2）。</summary>
    public int? DistillMinSessions { get; set; }

    /// <summary>一轮最多固化几个技能（默认 3）。</summary>
    public int? DistillMaxSkills { get; set; }
}
