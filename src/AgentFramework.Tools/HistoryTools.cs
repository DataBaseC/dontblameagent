using System.Text;
using AgentFramework.Contracts;

namespace AgentFramework.Tools;

/// <summary>
/// 检索会话历史 —— 长任务里那条"捞回折叠内容"的口子。
///
/// 它和 L3 遮蔽是一对：
///   遮蔽把旧工具结果从上下文里拿走（腾出注意力和预算），
///   这个工具让模型在真需要时把它取回来。
/// 两者合起来，上下文裁剪才从「丢失」变成「卸载」。
///
/// 参数之所以保持极简（只有 query），还是那条方针：
/// 条数、范围这些**预算与边界**属于配置，不属于模型的工具契约。
/// </summary>
public sealed class SearchHistoryTool(
    ToolkitOptions options,
    ISessionIndex index,
    Func<string?> currentSessionId) : ITool, IToolWithSchema
{
    public string Name => "search_history";

    public string Description =>
        "在自己的会话历史里做全文检索 —— 包括那些因为上下文压缩而**已经不在你眼前**的旧消息与工具结果。"
        + "当你需要回想「之前看到过的内容」、或者用户引用「你刚才查过的东西」时用它。"
        + "多个关键词用空格分开，命中越全的结果排得越前。";

    public string ParametersJsonSchema =>
        """{"type":"object","properties":{"query":{"type":"string","description":"检索关键词；多个词用空格分开（命中越全，结果越靠前）"}},"required":["query"]}""";

    public async ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        if (!invocation.Arguments.TryGetValue("query", out var query) || string.IsNullOrWhiteSpace(query))
        {
            return ToolResult.Fail("缺少参数 query");
        }

        if (!index.IsAvailable)
        {
            return ToolResult.Fail("历史索引不可用（未启用，或索引后端加载失败）");
        }

        var sessionId = currentSessionId();

        IReadOnlyList<HistoryHit> hits;
        try
        {
            hits = await index
                .SearchAsync(query, sessionId, options.HistorySearchMaxResults, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"历史检索失败：{ex.Message}");
        }

        if (hits.Count == 0)
        {
            return ToolResult.Ok($"（历史里没有找到与「{query}」相关的内容；也可以试试换个关键词）");
        }

        var builder = new StringBuilder();
        builder.Append("在会话历史中找到 ").Append(hits.Count).AppendLine(" 条（最相关的在前）：");

        foreach (var hit in hits)
        {
            builder.Append("- [").Append(hit.Type).Append(" #").Append(hit.Seq).Append("] ")
                   .AppendLine(hit.Snippet);
        }

        builder.Append("\n（这些内容来自会话日志，不一定仍在你的当前上下文里）");

        return ToolResult.Ok(options.ShrinkResult("search_history", builder.ToString()));
    }
}
