using System.Text;
using AgentFramework.Contracts;

namespace AgentFramework.Host;

/// <summary>
/// 按需启用 / 停用技能（对齐 dsh 的 skill 按需加载）。
///
/// <para>
/// 技能把可见工具收窄成「模式许可 ∩ 已启用技能的工具并集」。从前这个开关**只有宿主/界面**能拨，
/// 模型自己看不见也调不动 —— 明明它最清楚眼前这段活要什么。这个工具补上那个出口。
/// </para>
///
/// <para>
/// 与 <c>toolsets</c> / <c>use_toolset</c>（工具<b>包</b>）互补：包按用途分组，技能按<b>任务</b>组织；
/// 二者读的是同一份可见面。
/// </para>
/// </summary>
public sealed class UseSkillTool(
    Func<IReadOnlyList<SkillDefinition>> skills,
    HashSet<string> enabledSkills) : IToolWithRisk, ITool, IToolWithSchema
{
    public string Name => "use_skill";

    public ToolRisk Risk => ToolRisk.Write;

    public string Description =>
        "按需启用/停用技能：技能会把当前可见工具收窄成「模式许可 ∩ 已启用技能」的并集。" +
        "不传 name 时列出全部技能与启用状态；传 name 启用（默认）或停用。下一轮生效。";

    public string ParametersJsonSchema =>
        """
        {"type":"object","properties":{
        "name":{"type":"string","description":"技能名；省略则只列出技能与状态"},
        "enabled":{"type":"boolean","description":"true=启用（默认），false=停用"}},
        "required":[]}
        """;

    public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        var known = skills();

        invocation.Arguments.TryGetValue("name", out var rawName);
        var name = rawName?.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            return ValueTask.FromResult(ToolResult.Ok(Render(known)));
        }

        var match = known.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));
        if (match is null)
        {
            return ValueTask.FromResult(ToolResult.Fail(
                $"没有名为「{name}」的技能。\n{Render(known)}"));
        }

        var enabled = true;
        if (invocation.Arguments.TryGetValue("enabled", out var rawEnabled)
            && bool.TryParse(rawEnabled, out var parsed))
        {
            enabled = parsed;
        }

        if (enabled)
        {
            enabledSkills.Add(match.Name);
        }
        else
        {
            enabledSkills.Remove(match.Name);
        }

        return ValueTask.FromResult(ToolResult.Ok(
            $"{(enabled ? "已启用" : "已停用")}技能 {match.Name}（下一轮生效）。"));
    }

    private string Render(IReadOnlyList<SkillDefinition> known)
    {
        if (known.Count == 0)
        {
            return "工作区里没有扫描到技能（技能放在 <工作区>/skills/<名字>/）。";
        }

        var sb = new StringBuilder("技能列表（● 已启用 / ○ 未启用）：\n");
        foreach (var skill in known)
        {
            sb.Append(enabledSkills.Contains(skill.Name) ? "● " : "○ ")
              .Append(skill.Name)
              .Append("（工具 ").Append(skill.Tools.Count).Append(" 个）\n");
        }

        return sb.ToString().TrimEnd();
    }
}
