using System.Text;
using AgentFramework.Contracts;

namespace AgentFramework.Tools;

/// <summary>
/// 写下一条长期记忆。
///
/// 写进哪一级由**模式**决定（工作模式 → 项目；闲聊模式 → 全局），
/// 模型不需要、也不应该关心这件事 —— 参数保留极简（学 dsh 的"参数极简"）。
///
/// 提示词里刻意强调"只记长期有用的东西"：记忆是**结论集**，
/// 写进流水账只会让每一轮的注入都变贵。
///
/// <c>slot</c> 是"改主意"的正解：同一槽位再记一次会自动取代旧的那条，
/// 于是模型不必先删再写，也不会留下互相矛盾的两条。
/// </summary>
public sealed class RememberTool(
    IMemoryStore store,
    Func<string> scopeProvider,
    Func<string?> sessionIdProvider) : ITool, IToolWithSchema
{
    public string Name => "remember";

    public string Description =>
        "把一条**值得长期记住**的事实、偏好或结论记下来（跨会话保留）。"
        + "只记长期有用的（用户偏好、项目约定、已定的方案、踩过的坑），不要记流水账。"
        + "改主意时用 slot 覆盖旧的那条，而不是再记一条新的。"
        + "特别重要、希望常驻索引置顶的事实用 important=true。";

    public string ParametersJsonSchema =>
        """{"type":"object","properties":{"text":{"type":"string","description":"要记住的内容，一句到几句，独立可读（不要写「刚才那个」这类依赖上下文的指代）"},"slot":{"type":"string","description":"可选：同一件事的槽位名（如「构建命令」「包管理器」）。同一槽位再记一次会自动取代旧的那条 —— 改主意时用它，不要重复堆积。"},"important":{"type":"boolean","description":"可选：置顶标记（默认 false）。只用于长期不变的核心事实（用户偏好、项目硬约定），不要滥用。"}},"required":["text"]}""";

    public async ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        if (!invocation.Arguments.TryGetValue("text", out var text) || string.IsNullOrWhiteSpace(text))
        {
            return ToolResult.Fail("缺少参数 text");
        }

        invocation.Arguments.TryGetValue("slot", out var slot);
        var important = invocation.Arguments.TryGetValue("important", out var impRaw)
            && string.Equals(impRaw, "true", StringComparison.OrdinalIgnoreCase);
        var scope = scopeProvider();

        try
        {
            var entry = await store
                .AppendAsync(scope, text.Trim(), null, sessionIdProvider(), "agent", slot, ct)
                .ConfigureAwait(false);

            // 置顶走同一条 score 事件通道 —— 视图字段只有一份来源，折叠器不用特判。
            if (important)
            {
                await store.RecordHitAsync(scope, entry.Id, delta: 0, markImportant: true, ct).ConfigureAwait(false);
            }

            var slotNote = entry.Slot is null ? string.Empty : $"，槽位「{entry.Slot}」";
            var importantNote = important ? "，已置顶" : string.Empty;

            return ToolResult.Ok($"已记住（{scope}{slotNote}{importantNote}）：{entry.Text}\n记忆 id：{entry.Id}");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"写入记忆失败：{ex.Message}");
        }
    }
}

/// <summary>
/// 撤销一条记错的记忆。
///
/// 它的存在本身就是一条设计声明：**记忆是要能被收回的**。
/// 写错的记忆会一直误导后续每一轮，所以"能删掉"比"能写下"更关键 ——
/// 实现上是追加一条 retract 事件，原有记录一个字不改（见 <see cref="MemoryKinds"/>）。
/// </summary>
public sealed class ForgetTool(
    IMemoryStore store,
    Func<IReadOnlyList<string>> scopeProvider) : ITool, IToolWithSchema
{
    public string Name => "forget";

    public string Description =>
        "撤销一条**记错了**的记忆（跨会话生效）。id 从 recall_memory 的结果里取。"
        + "如果是「情况变了」，优先用 remember 的 slot 覆盖，而不是撤销。";

    public string ParametersJsonSchema =>
        """{"type":"object","properties":{"id":{"type":"string","description":"要撤销的记忆 id（见 recall_memory 的结果）"}},"required":["id"]}""";

    public async ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        if (!invocation.Arguments.TryGetValue("id", out var id) || string.IsNullOrWhiteSpace(id))
        {
            return ToolResult.Fail("缺少参数 id");
        }

        var scopes = scopeProvider();
        if (scopes.Count == 0)
        {
            return ToolResult.Fail("当前模式没有加载任何记忆层级");
        }

        var target = id.Trim();

        try
        {
            foreach (var scope in scopes)
            {
                var entry = await store
                    .RetractAsync(scope, target, "agent", ct)
                    .ConfigureAwait(false);

                if (entry is not null)
                {
                    return ToolResult.Ok($"已撤销记忆（{scope}）：{target}");
                }
            }
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"撤销记忆失败：{ex.Message}");
        }

        return ToolResult.Ok($"没有找到可撤销的记忆（id={target}）—— 可能已经撤销过，或 id 不对。");
    }
}

/// <summary>
/// 检索长期记忆。
///
/// 与 <c>search_history</c> 分工明确：
///   - <c>search_history</c> 查「当时说了什么」（事件流，可回放）
///   - <c>recall_memory</c> 查「我认定了什么」（记忆库，跨会话）
///
/// 命中即升温（RecordHitAsync）：这是温度排序闭环的入口 ——
/// 读与记命中在同一个工具里原子发生，模型不需要、也不应该分别调「查」与「记命中」。
/// 升温失败绝不阻断召回 —— 热度是优化，不是功能。
/// </summary>
public sealed class RecallMemoryTool(
    ToolkitOptions options,
    IMemoryStore store,
    Func<IReadOnlyList<string>> scopeProvider) : ITool, IToolWithSchema
{
    public string Name => "recall_memory";

    public string Description =>
        "检索长期记忆（跨会话保留的事实与偏好）。查历史对话用 search_history；"
        + "查「以前定过什么」用这个。命中的记忆会自动提升热度，之后更容易进入常驻索引。";

    public string ParametersJsonSchema =>
        """{"type":"object","properties":{"query":{"type":"string","description":"检索关键词；留空则列出最近的记忆"}},"required":[]}""";

    public async ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        invocation.Arguments.TryGetValue("query", out var query);
        var scopes = scopeProvider();

        if (scopes.Count == 0)
        {
            return ToolResult.Fail("当前模式没有加载任何记忆层级");
        }

        var all = new List<MemoryEntry>();
        var trimmed = (query ?? string.Empty).Trim();

        try
        {
            foreach (var scope in scopes)
            {
                var found = trimmed.Length == 0
                    ? await store.LoadAsync(scope, options.MemorySearchMaxResults, ct).ConfigureAwait(false)
                    : await store.SearchAsync(trimmed, scope, options.MemorySearchMaxResults, ct).ConfigureAwait(false);

                all.AddRange(found);
            }
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"检索记忆失败：{ex.Message}");
        }

        if (all.Count == 0)
        {
            return ToolResult.Ok(trimmed.Length == 0
                ? "（记忆库还是空的）"
                : $"（记忆里没有找到与「{trimmed}」相关的内容）");
        }

        // 保持检索序（相关性优先）；只做跨 scope 去重与总量封顶。
        // 不再按 CreatedAt 重排 —— 那会把「最相关的」换成「最新的」。
        var ordered = all
            .DistinctBy(e => e.Id, StringComparer.Ordinal)
            .Take(options.MemorySearchMaxResults)
            .ToList();

        // 命中即升温（只增不减的事件）。并行量极小（≤若条），逐条 await 足够；
        // 失败静默 —— 升温是优化信号，不是用户可见功能。
        foreach (var entry in ordered)
        {
            try
            {
                await store.RecordHitAsync(entry.Scope, entry.Id, delta: 1, ct: ct).ConfigureAwait(false);
            }
            catch
            {
                // 升温失败不影响本轮召回
            }
        }

        var builder = new StringBuilder();
        builder.Append("记忆中找到 ").Append(ordered.Count).AppendLine(" 条：");

        foreach (var entry in ordered)
        {
            // 带上 id：这是 forget 的入口，也是「这条到底指哪一条」的唯一凭据
            builder.Append("- [").Append(entry.Scope).Append("] ").Append(entry.Text)
                   .Append("（id ").Append(entry.Id).Append('）').AppendLine();
        }

        return ToolResult.Ok(builder.ToString());
    }
}
