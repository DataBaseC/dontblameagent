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

        ILlmClient? client;
        lock (_targetsGate)
        {
            if (!_targets.TryGetValue(target, out client))
            {
                // 只配了一个端点时，规则仍可能把请求指向另一个 ——
                // 例如「只配了云端」，而闲聊模式的长上下文 + 无工具按规则该走本地。
                // 这时退回唯一可用的目标，而不是直接抛异常：**规则是偏好，不是前提。**
                client = _targets.Count == 1 ? _targets.Values.First() : null;
            }
        }

        if (client is null)
        {
            throw new InvalidOperationException(
                $"路由目标不存在：{target}（已注册：{string.Join(", ", _targets.Keys)}）");
        }

        if (!string.Equals(client.Name, target, StringComparison.OrdinalIgnoreCase))
        {
            target = client.Name;   // 历史里记实际用了谁
        }

        lock (_history)
        {
            _history.Add(new RouteRecord(target, useManual ? "manual" : "rule", DateTimeOffset.UtcNow));

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
}

public sealed record RouteRecord(string Target, string Source, DateTimeOffset At);

/// <summary>
/// 默认路由规则。
///
/// 对应「联网负责复杂任务、本地负责量大但简单的任务」这条方针：
///   - 长上下文 + 不需要工具  → 本地（token 量大，交给本地省成本、还不出机）
///   - 其余（要推理、要用工具）→ 云端（质量优先）
///
/// 规则本身是可替换的委托，将来想加入更多判据（工具类型、历史轮数、失败重试）不用改主干。
/// </summary>
public static class DefaultRouting
{
    public static Func<LlmRequest, string> Rule(int longContextChars = 6000) => request =>
    {
        var totalChars = request.Messages.Sum(m => m.Content?.Length ?? 0);

        if (totalChars > longContextChars && request.Tools.Count == 0)
        {
            return "local";
        }

        return "cloud";
    };
}
