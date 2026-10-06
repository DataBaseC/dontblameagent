using System.Text;
using AgentFramework.Contracts;

namespace AgentFramework.Host;

/// <summary>
/// <b>工具检索</b>（Tool Search，v3.26）：把「延迟工具」（<c>Eager=false</c> 的包，如 <c>mcp:&lt;server&gt;</c>）
/// 按需拉进当前会话。
///
/// <para>
/// 借鉴 Anthropic 的 <i>Tool Search Tool</i>（<c>defer_loading</c> + 检索后展开完整定义）与
/// Claude Code 的 <c>ToolSearchTool</c>：工具定义不该在开跑前全塞进上下文 ——
/// 那既贵（50 个工具约 10–20k token）又让选择准确率下滑（30–50 个是拐点）。
/// 延迟的工具<em>完全不进初始上下文</em>，模型需要时先搜、命中后再把完整定义拉进来。
/// </para>
///
/// <para>
/// 与 dba 既有机制的关系：「延迟」不是新通路，就是现成的 <c>Eager=false</c> 包 +
/// <c>DisabledToolsets</c>；本工具只给这条通路补上<b>工具级</b>入口 ——
/// <c>tool_catalog</c> 看包、<c>use_toolset</c> 整包开，本工具则按<b>单件</b>拉起
/// （一个包里 30 个工具只用到 2 个时，不必背上另外 28 个的 schema）。
/// </para>
/// </summary>
public sealed class ToolSearchTool(
    Func<IReadOnlyCollection<ITool>> registry,
    Func<IReadOnlyCollection<ITool>> visibleNow,
    Func<IReadOnlyList<ToolsetView>> views,
    Func<string, bool, bool> setActivated,
    Func<IReadOnlyCollection<string>> activated,
    Func<string, string?> toolsetOf) : IToolWithRisk, ITool, IToolWithSchema
{
    private const int DefaultLimit = 5;
    private const int MaxLimit = 20;

    public string Name => "tool_search";

    public ToolRisk Risk => ToolRisk.ReadOnly;

    public string Description =>
        "检索工具库并把命中的工具加载到本会话（按需发现）。" +
        "当前工具表里没有你要的能力时先用它 —— 例如接了 MCP server，" +
        "它的工具默认不进上下文（延迟包），搜一下就能拉起来。" +
        "action=list 看还有哪些工具没加载；action=reset 卸下本会话已加载的。";

    public string ParametersJsonSchema => """
        {"type":"object","properties":{
          "query":{"type":"string","description":"关键词，空格分隔（需全部命中）；也可直接给工具名"},
          "action":{"type":"string","enum":["search","list","reset"],"description":"默认 search"},
          "toolset":{"type":"string","description":"只在这个包里搜"},
          "limit":{"type":"integer","description":"最多返回几个（默认 5，上限 20）"}
        }}
        """;

    public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        var action = SkillToolArgs.Get(invocation, "action")?.Trim().ToLowerInvariant() ?? "search";

        return action switch
        {
            "list" => ValueTask.FromResult(List()),
            "reset" => ValueTask.FromResult(Reset()),
            "search" => ValueTask.FromResult(Search(invocation)),
            _ => ValueTask.FromResult(ToolResult.Fail($"未知 action：{action}（可用：search / list / reset）")),
        };
    }

    private ToolResult Search(ToolInvocation invocation)
    {
        var query = SkillToolArgs.Get(invocation, "query");
        if (string.IsNullOrWhiteSpace(query))
        {
            return ToolResult.Fail("query 不能为空（想看索引请用 action=list）");
        }

        var toolsetFilter = SkillToolArgs.Get(invocation, "toolset");
        var limit = ParseLimit(SkillToolArgs.Get(invocation, "limit"));
        var terms = query.Split([' ', ',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var visible = visibleNow().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        var hits = registry()
            .Where(t => string.IsNullOrWhiteSpace(toolsetFilter)
                || string.Equals(toolsetOf(t.Name), toolsetFilter, StringComparison.Ordinal))
            .Where(t => terms.All(term => Matches(t, term)))
            // 名字里直接含着整串查询的排前面（通常正是要点名调的那个）
            .OrderByDescending(t => t.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .Take(limit)
            .ToList();

        if (hits.Count == 0)
        {
            return ToolResult.Ok(
                $"没有匹配「{query}」的工具。" +
                "（试试更短的词或直接给工具名；也可以 action=list 看全部未加载的工具）");
        }

        var report = new StringBuilder();
        var loaded = new List<string>();

        foreach (var tool in hits)
        {
            var wasVisible = visible.Contains(tool.Name);
            if (!wasVisible && setActivated(tool.Name, true))
            {
                loaded.Add(tool.Name);
            }

            var set = toolsetOf(tool.Name);
            report.Append("### ").Append(tool.Name);
            if (!string.IsNullOrWhiteSpace(set))
            {
                report.Append("  · 包：").Append(set);
            }

            report.Append(wasVisible ? "  · 已在工具表" : "  · 已加载").Append('\n');
            report.Append(tool.Description).Append('\n');

            var schema = ToolSchemas.For(tool);
            if (!string.IsNullOrWhiteSpace(schema))
            {
                report.Append("参数：").Append(schema).Append('\n');
            }

            report.Append('\n');
        }

        if (loaded.Count > 0)
        {
            report.Append($"（{string.Join(", ", loaded)} 已加载 —— **下一轮**起可直接调用。）");
        }

        return ToolResult.Ok(report.ToString().TrimEnd());
    }

    private ToolResult List()
    {
        var visible = visibleNow().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var already = activated().ToHashSet(StringComparer.Ordinal);

        var report = new StringBuilder();
        var pending = 0;

        foreach (var v in views())
        {
            var hidden = v.Tools.Where(n => !visible.Contains(n)).ToList();
            if (hidden.Count == 0)
            {
                continue;
            }

            pending += hidden.Count;
            report.Append($"[{(v.Enabled ? "开" : "延迟")}] {v.Id}（{v.Name}）");
            if (!string.IsNullOrWhiteSpace(v.Description))
            {
                report.Append("  —— ").Append(v.Description);
            }

            report.Append('\n');

            foreach (var n in hidden)
            {
                report.Append("   - ").Append(n).Append(already.Contains(n) ? "（已加载）" : string.Empty).Append('\n');
            }
        }

        if (already.Count > 0)
        {
            report.Append($"\n本会话已加载：{string.Join(", ", already.OrderBy(n => n, StringComparer.Ordinal))}\n");
        }

        return ToolResult.Ok(pending == 0
            ? "当前没有延迟的工具 —— 全部都在工具表里。"
            : report.ToString().TrimEnd());
    }

    private ToolResult Reset()
    {
        var cleared = activated().Count(name => setActivated(name, false));

        return ToolResult.Ok(cleared == 0
            ? "本会话没有已加载的工具（无需重置）。"
            : $"已卸下本会话加载的 {cleared} 个工具（下一轮起不再进上下文）。");
    }

    private static int ParseLimit(string? raw)
        => int.TryParse(raw, out var n) ? Math.Clamp(n, 1, MaxLimit) : DefaultLimit;

    private static bool Matches(ITool tool, string term)
        => tool.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
           || tool.Description.Contains(term, StringComparison.OrdinalIgnoreCase);
}
