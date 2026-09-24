using System.Text;
using AgentFramework.Contracts;

namespace AgentFramework.Host;

/// <summary>
/// 看工具包：有哪些包、开着还是关着、每个包里有什么。
///
/// <para>
/// 为什么要给 agent 这个工具：主人要的是「真实工作时有的选择地开」，
/// 而<b>最清楚这一段活需要什么的人（其实是模型自己）</b>才有资格做这个选择 ——
/// 写小说时把编程包收起来、查资料时把本地工具收起来，都是它自己该判断的事。
/// </para>
///
/// <para>
/// 工具本身属于 <c>meta</c> 包且不可关闭：关掉它，就再也没有办法把别的包开回来了。
/// </para>
/// </summary>
public sealed class ToolsetsTool(Func<IReadOnlyList<ToolsetView>> views) : ITool, IToolWithSchema
{
    public string Name => "toolsets";

    public string Description =>
        "列出所有工具包（Toolset）及其开关状态与包含的工具。" +
        "工具包是「这一轮给模型看哪些工具」的最小单位：用不上的包关掉，能省上下文、也让选择更集中。" +
        "（core 与 工具包管理 两组不可关）";

    public string ParametersJsonSchema => """{"type":"object","properties":{}}""";

    public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        var all = views();
        var closed = all.Count(v => !v.Enabled);

        var report = new StringBuilder();
        report.Append($"共 {all.Count} 个工具包（{closed} 个已关闭）\n");

        foreach (var view in all)
        {
            report.Append(view.Enabled ? "[开] " : "[关] ")
                .Append(view.Id.PadRight(12))
                .Append(view.Name);

            if (!string.IsNullOrWhiteSpace(view.Description))
            {
                report.Append("  —— ").Append(view.Description);
            }

            report.Append($"\n     工具（{view.Tools.Count}）：{string.Join(", ", view.Tools)}");

            if (view.Protected)
            {
                report.Append("\n     保留包，不可关闭");
            }

            report.Append('\n');
        }

        report.Append("\n用 use_toolset 开关某个包 —— 关掉这段活用不上的包，能少占上下文。");

        return ValueTask.FromResult(ToolResult.Ok(report.ToString()));
    }
}

/// <summary>
/// 开关一个工具包。回合边界生效（可见面每轮现取），所以下一轮就变，不必重启。
/// </summary>
public sealed class UseToolsetTool(
    Func<IReadOnlyList<ToolsetView>> views,
    Func<string, bool, bool> toggle) : ITool, IToolWithSchema
{
    public string Name => "use_toolset";

    public string Description =>
        "打开或关闭一个工具包。关掉当前任务用不上的包可以省上下文、也让挑选更集中；" +
        "需要时随时再打开。core 与 工具包管理 两组保留包不可关闭。";

    public string ParametersJsonSchema => """
        {"type":"object","properties":{
          "toolset":{"type":"string","description":"包 id，如 web、plan、writing-kit"},
          "enabled":{"type":"boolean","description":"true 打开，false 关闭"}
        },"required":["toolset","enabled"]}
        """;

    public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        var args = invocation.Arguments;

        if (!args.TryGetValue("toolset", out var id) || string.IsNullOrWhiteSpace(id))
        {
            return ValueTask.FromResult(ToolResult.Fail("缺少参数 toolset"));
        }

        var raw = args.TryGetValue("enabled", out var value) ? value?.Trim().ToLowerInvariant() : null;
        var enabled = raw is "true" or "1" or "yes" or "on";

        var known = views();
        var view = known.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal));
        if (view is null)
        {
            return ValueTask.FromResult(ToolResult.Fail(
                $"没有名为「{id}」的工具包。现有的包：{string.Join(", ", known.Select(x => x.Id))}"));
        }

        if (!toggle(id, enabled))
        {
            return ValueTask.FromResult(ToolResult.Fail(
                $"「{view.Id}」（{view.Name}）是保留包，不能关闭 —— " +
                "关掉「读文件 / 写文件 / 列目录 / 问用户」会把能力关成残废，关掉工具包开关本身则再也开不回来。"));
        }

        return ValueTask.FromResult(ToolResult.Ok(enabled
            ? $"已打开工具包「{view.Id}」（{view.Tools.Count} 个工具回到可见面）"
            : $"已关闭工具包「{view.Id}」（{view.Tools.Count} 个工具移出可见面，需要时用 use_toolset 再打开）"));
    }
}
