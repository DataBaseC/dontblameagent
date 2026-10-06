using System.Runtime.CompilerServices;
using AgentFramework.Contracts;

namespace AgentFramework.Llm;

/// <summary>
/// 模型路由 —— 它本身就是个 <see cref="ILlmClient"/>，
/// 所以对上层完全透明：主循环根本不知道背后是云端还是本地。
/// 换策略不动主干，这正是「路由即 Provider」的价值。
/// </summary>
public sealed class RouterLlmClient : ILlmClient
{
    private readonly Dictionary<string, ILlmClient> _targets = new(StringComparer.OrdinalIgnoreCase);

    // P5 遗漏项：运行期 AddTarget（换模型）与流式读并发 —— 换成加锁快照，
    // 与本类 History 字段同一套手法（并发面小，不值得引 ConcurrentDictionary）。
    private readonly object _targetsGate = new();
    private readonly List<RouteRecord> _history = [];

    /// <summary>
    /// 路由史上限（v3.5 审查 P2）：它只是诊断信息 ——
    /// 无界增长的话，长会话每轮都会往里加一条，纯属内存泄漏。
    /// </summary>
    private const int MaxHistory = 200;
    private readonly Func<LlmRequest, string> _rule;

    public RouterLlmClient(Func<LlmRequest, string> rule) => _rule = rule;

    public string Name => "router";

    /// <summary>手动覆盖：设置后无视规则，直达指定目标。null 表示交还给规则。</summary>
    public string? ManualOverride { get; set; }

    /// <summary>
    /// 显式回落目标（安全审查 P2）。规则把请求指向一个未注册的端点时优先落到它。
    /// </summary>
    public string? FallbackTarget { get; set; }

    /// <summary>
    /// 默认目标（v3.23）：显式回落目标之外的第二顺位 —— 通常是「当前选中的端点」。
    /// 有它，「规则指了个没注册的端点」就不会退化成「按字典枚举顺序随便挑一个」。
    /// </summary>
    public string? DefaultTarget { get; set; }

    /// <summary>路由历史（诊断用 —— 若用户频繁手动切，说明规则没抓住规律）。</summary>
    public IReadOnlyList<RouteRecord> History
    {
        get
        {
            lock (_history)
            {
                return [.. _history];
            }
        }
    }

    public void AddTarget(ILlmClient client)
    {
        lock (_targetsGate)
        {
            _targets[client.Name] = client;
        }
    }

    public async IAsyncEnumerable<LlmStreamChunk> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var useManual = !string.IsNullOrEmpty(ManualOverride);
        var target = useManual ? ManualOverride! : _rule(request);
        var fellBack = false;

        ILlmClient? client;
        lock (_targetsGate)
        {
            if (!_targets.TryGetValue(target, out client))
            {
                // 规则想把请求指向一个未注册的端点 —— 例如「只配了云端」，
                // 而闲聊模式的长上下文 + 无工具按规则该走本地。
                //
                // v3.23：**一次都不抛**。从前这里在多目标且没设 FallbackTarget 时直接抛
                // InvalidOperationException，把整轮对话打挂 —— 配置少写一个端点就变成
                // 「agent 完全不能用」，而路由规则的定位本就是**偏好**，不是前提。
                client = PickFallback();
                fellBack = client is not null;
            }
        }

        if (client is null)
        {
            throw new InvalidOperationException(
                $"路由目标不存在：{target}（已注册：{(NamesLocked().Count == 0 ? "（一个都没有）" : string.Join(", ", NamesLocked()))}）"
                + "，且没有可用目标可回落 —— 请检查端点配置。");
        }

        var source = useManual ? "manual" : fellBack ? "fallback" : "rule";

        if (!string.Equals(client.Name, target, StringComparison.OrdinalIgnoreCase))
        {
            target = client.Name;   // 历史里记实际用了谁
        }

        lock (_history)
        {
            _history.Add(new RouteRecord(target, source, DateTimeOffset.UtcNow));

            // 只保留最近 MaxHistory 条 —— 诊断信息不需要全史。
            if (_history.Count > MaxHistory)
            {
                _history.RemoveRange(0, _history.Count - MaxHistory);
            }
        }

        await foreach (var chunk in client.StreamAsync(request, ct).WithCancellation(ct).ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    /// <summary>
    /// 规则指了个不存在的端点时的回落顺序（须在 <c>_targetsGate</c> 内调用）：
    /// 显式回落目标 → 默认目标 → 唯一目标 → 按名字稳定排序的第一个。
    /// 全都不可用才返回 null（那时是配置真的错了，抛得有道理）。
    /// </summary>
    private ILlmClient? PickFallback()
    {
        if (!string.IsNullOrEmpty(FallbackTarget) && _targets.TryGetValue(FallbackTarget, out var explicitFallback))
        {
            return explicitFallback;
        }

        if (!string.IsNullOrEmpty(DefaultTarget) && _targets.TryGetValue(DefaultTarget, out var preferred))
        {
            return preferred;
        }

        if (_targets.Count == 0)
        {
            return null;
        }

        if (_targets.Count == 1)
        {
            return _targets.Values.First();   // 唯一目标：顺序无歧义
        }

        // 多目标且没指定：按名字排序取第一个 —— **顺序必须确定**（字典枚举顺序不可靠），
        // 且排序让「同一份配置每次回落同一个目标」，行为可复现。
        return _targets.OrderBy(pair => pair.Key, StringComparer.Ordinal).First().Value;
    }

    private IReadOnlyList<string> NamesLocked() => [.. _targets.Keys.OrderBy(k => k, StringComparer.Ordinal)];
}

public sealed record RouteRecord(string Target, string Source, DateTimeOffset At);

/// <summary>
/// 默认路由规则。
///
/// 对应「联网负责复杂任务、本地负责量大但简单的任务」这条方针：
///   - 长上下文 + 不需要工具  → 本地（token 量大，交给本地省成本、还不出机）
///   - 其余（要推理、要用工具）→ 云端（质量优先）
///
/// <para>
/// v3.23 起判据从「消息字符数」换成 <b>token 估算</b>：字符数在中文 / 代码场景下偏差显著
/// （一个汉字 ≈ 1 token，而 4 个 ASCII 字符才 ≈ 1 token），阈值语义随之改为 token。
/// 宿主会注入 <c>Data.TokenEstimator</c>（那个更准）；本类自带的近似只在没有注入时兜底，
/// 好让 Llm 层不必反向依赖 Data。
/// </para>
/// 规则本身是可替换的委托，将来想加入更多判据（工具类型、历史轮数、失败重试）不用改主干。
/// </summary>
public static class DefaultRouting
{
    public static Func<LlmRequest, string> Rule(int longContextTokens = 2000, Func<LlmRequest, int>? measure = null)
    {
        var count = measure ?? Approximate;

        return request => count(request) > longContextTokens && request.Tools.Count == 0
            ? "local"
            : "cloud";
    }

    /// <summary>兜底近似：CJK 每字约 1 token，其余每 4 字符约 1 token。</summary>
    internal static int Approximate(LlmRequest request) => request.Messages.Sum(m => Approximate(m.Content));

    internal static int Approximate(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var cjk = 0;
        foreach (var ch in text)
        {
            if (ch >= '\u2E80')
            {
                cjk++;
            }
        }

        return cjk + ((text.Length - cjk + 3) / 4);
    }
}
