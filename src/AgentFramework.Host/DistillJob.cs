using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentFramework.Contracts;

namespace AgentFramework.Host;

/// <summary>一个被提炼出来的调用模式（flow = 两步工作流；task = 反复执行的单步操作）。</summary>
public sealed record DistillPattern(
    string Kind,
    IReadOnlyList<string> Steps,
    int Occurrences,
    int Sessions)
{
    public const string KindFlow = "flow";
    public const string KindTask = "task";

    public string Display => string.Join(" → ", Steps);
}

/// <summary>一轮 Distill 的结果。</summary>
public sealed record DistillReport(
    IReadOnlyList<DistillPattern> Patterns,
    IReadOnlyList<string> SkillsWritten,
    IReadOnlyList<string> Notes)
{
    public bool Changed => SkillsWritten.Count > 0;

    public static DistillReport Empty { get; } = new([], [], []);
}

/// <summary>
/// <b>Distill</b>（PLAN-memory-evolution 支柱四）：技能固化 ——
/// 从历史会话里挖出<b>反复出现的调用模式</b>，固化成技能（skill）草稿。
///
/// <para>
/// MiMo 的 Distill（30 天，把工作模式固化成 skill / CLI 命令 / SOP 文档）在我们这边
/// 落成两层：<b>挖矿</b>（纯函数，确定性）与<b>固化</b>（写进 <c>skills/</c>，
/// 产物是纯声明式技能 —— 工具白名单 + 提示词后缀，SkillLoader 立即可装载）。
/// </para>
/// <para>
/// 为什么确定性挖矿、不请模型：模式识别要的是**可复现**（同一份历史必出同一份产物，
/// 测试可对账），而措辞润色是锦上添花。提炼出的技能是**草稿** —— README 里如实写明
/// 提炼依据（出现次数 / 会话数 / 示例），人工修订后才是正典。
/// </para>
/// <para>
/// 判据：<c>DistillMinOccurrences</c>（默认 3 次）且 <c>DistillMinSessions</c>（默认 2 个会话）——
/// 「反复出现」且「横跨会话」才算工作方式，一次巧合不算。
/// 产出上限 <c>DistillMaxSkills</c>（默认 3）：技能是拿来用的，不是收藏。
/// </para>
/// </summary>
public static class DistillJob
{
    /// <summary>纯挖矿：不写任何文件。同一份输入必出同一份结果（确定性）。</summary>
    public static DistillReport Mine(
        IEnumerable<(string SessionId, IReadOnlyList<SessionEvent> Events)> sessions,
        EvolutionOptions? options = null)
    {
        var opt = options ?? new EvolutionOptions();

        var singleHits = new Dictionary<string, (HashSet<string> Sessions, int Count, string Signature)>(StringComparer.Ordinal);
        var pairHits = new Dictionary<string, (HashSet<string> Sessions, int Count, string First, string Second)>(StringComparer.Ordinal);

        foreach (var (sessionId, events) in sessions)
        {
            var sequence = events
                .OfType<ToolCallRequestedEvent>()
                .Select(e => ApprovalRuleSet.Signature(e.ToolName, e.Arguments))
                .ToList();

            foreach (var signature in sequence)
            {
                if (!singleHits.TryGetValue(signature, out var hit))
                {
                    singleHits[signature] = hit = (new HashSet<string>(StringComparer.Ordinal), 0, signature);
                }

                hit.Sessions.Add(sessionId);
                singleHits[signature] = (hit.Sessions, hit.Count + 1, hit.Signature);
            }

            for (var i = 0; i + 1 < sequence.Count; i++)
            {
                var key = sequence[i] + " → " + sequence[i + 1];
                if (!pairHits.TryGetValue(key, out var hit))
                {
                    pairHits[key] = hit = (new HashSet<string>(StringComparer.Ordinal), 0, sequence[i], sequence[i + 1]);
                }

                hit.Sessions.Add(sessionId);
                pairHits[key] = (hit.Sessions, hit.Count + 1, hit.First, hit.Second);
            }
        }

        // 两步工作流（flow）优先于单步操作（task）：「先 A 再 B」比「常跑 A」更值钱
        var patterns = new List<DistillPattern>();

        patterns.AddRange(pairHits.Values
            .Where(h => h.Count >= opt.DistillMinOccurrences && h.Sessions.Count >= opt.DistillMinSessions)
            .OrderByDescending(h => h.Count)
            .ThenBy(h => h.First + " → " + h.Second, StringComparer.Ordinal)
            .Select(h => new DistillPattern(DistillPattern.KindFlow, [h.First, h.Second], h.Count, h.Sessions.Count)));

        patterns.AddRange(singleHits.Values
            .Where(h => h.Count >= opt.DistillMinOccurrences && h.Sessions.Count >= opt.DistillMinSessions)
            .OrderByDescending(h => h.Count)
            .ThenBy(h => h.Signature, StringComparer.Ordinal)
            .Select(h => new DistillPattern(DistillPattern.KindTask, [h.Signature], h.Count, h.Sessions.Count)));

        return new DistillReport(
            patterns,
            [],
            [$"挖到 {patterns.Count(p => p.Kind == DistillPattern.KindFlow)} 个工作流模式、"
             + $"{patterns.Count(p => p.Kind == DistillPattern.KindTask)} 个单步模式"]);
    }

    /// <summary>
    /// 固化：挖矿 + 把排名靠前的模式写成 <c>skills/&lt;id&gt;/{skill.json, README.md}</c>。
    /// 目录已存在则**跳过不覆盖**（用户改过的技能永远不被自动作业顶掉）。
    /// </summary>
    public static DistillReport Run(
        string? workspaceRoot,
        IEnumerable<(string SessionId, IReadOnlyList<SessionEvent> Events)> sessions,
        EvolutionOptions? options = null,
        Action? skillsChanged = null)
    {
        var opt = options ?? new EvolutionOptions();
        var mined = Mine(sessions, opt);
        var notes = new List<string>(mined.Notes);

        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            notes.Add("没有工作区，无法固化技能（挖矿结果见 Patterns）");
            return mined with { Notes = notes };
        }

        var written = new List<string>();

        foreach (var pattern in mined.Patterns.Take(Math.Max(0, opt.DistillMaxSkills)))
        {
            var id = SkillIdOf(pattern);
            var dir = Path.Combine(workspaceRoot!, SkillLoader.BuiltinDirName, id);

            if (Directory.Exists(dir))
            {
                notes.Add($"技能已存在，跳过（不覆盖）：{id}");
                continue;
            }

            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(
                    Path.Combine(dir, "skill.json"),
                    BuildManifest(pattern, id).ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                    new UTF8Encoding(false));
                File.WriteAllText(
                    Path.Combine(dir, "README.md"),
                    BuildReadme(pattern, id),
                    new UTF8Encoding(false));
                written.Add(id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                notes.Add($"写入技能失败：{id}（{ex.Message}）");
            }
        }

        if (written.Count > 0)
        {
            skillsChanged?.Invoke();
        }

        return mined with { SkillsWritten = written, Notes = notes };
    }

    /// <summary>
    /// 技能 id：<c>{kind}-{语义 slug}-{指纹}</c>。
    /// slug 让人一眼认出是什么，指纹（签名哈希前 6 位）保证不同模式不撞名；
    /// 全部落在 skill id 白名单（字母数字 - _）内。
    /// </summary>
    public static string SkillIdOf(DistillPattern pattern)
    {
        var slug = new StringBuilder();
        foreach (var ch in pattern.Display.ToLowerInvariant())
        {
            if (slug.Length >= 28)
            {
                break;
            }

            slug.Append(char.IsAsciiLetterOrDigit(ch) ? ch : '-');
        }

        var trimmed = slug.ToString().Trim('-');
        if (trimmed.Length == 0)
        {
            trimmed = "pattern";
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pattern.Display)))[..6].ToLowerInvariant();
        return $"{pattern.Kind}-{trimmed}-{hash}";
    }

    private static JsonObject BuildManifest(DistillPattern pattern, string id)
    {
        var tools = new JsonArray();
        foreach (var tool in pattern.Steps
                     .Select(ToolNameOf)
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(t => t, StringComparer.Ordinal))
        {
            tools.Add(tool);
        }

        return new JsonObject
        {
            ["name"] = id,
            ["description"] = DescriptionOf(pattern),
            ["promptSuffix"] = BuildPrompt(pattern),
            ["tools"] = tools,
            ["tags"] = new JsonArray("distilled", pattern.Kind),
            ["version"] = "1.0.0",
            ["author"] = "distill-job",
        };
    }

    private static string DescriptionOf(DistillPattern pattern)
        => pattern.Kind == DistillPattern.KindFlow
            ? $"反复出现的工作流（{pattern.Occurrences} 次 / {pattern.Sessions} 个会话）：{pattern.Display}"
            : $"反复执行的操作（{pattern.Occurrences} 次 / {pattern.Sessions} 个会话）：{pattern.Display}";

    private static string BuildPrompt(DistillPattern pattern)
    {
        var sb = new StringBuilder();
        sb.AppendLine(pattern.Kind == DistillPattern.KindFlow
            ? "# 固化工作流（DistillJob 从历史会话提炼）"
            : "# 固化操作（DistillJob 从历史会话提炼）");
        sb.AppendLine();
        sb.AppendLine($"当任务需要「{pattern.Display}」这类操作时，按下述顺序执行：");
        sb.AppendLine();

        for (var i = 0; i < pattern.Steps.Count; i++)
        {
            sb.AppendLine($"{i + 1}. `{pattern.Steps[i]}`");
        }

        sb.AppendLine();
        sb.AppendLine($"提炼依据：历史会话中出现 {pattern.Occurrences} 次，横跨 {pattern.Sessions} 个会话。");
        sb.AppendLine("这是从真实用法提炼的**草稿** —— 执行前先核对当前环境；修订后可留作正典。");
        return sb.ToString();
    }

    private static string BuildReadme(DistillPattern pattern, string id)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {id}").AppendLine();
        sb.AppendLine(DescriptionOf(pattern)).AppendLine();
        sb.AppendLine("## 何时用").AppendLine();
        sb.AppendLine($"当任务需要「{pattern.Display}」这类操作时。").AppendLine();
        sb.AppendLine("## 步骤").AppendLine();

        for (var i = 0; i < pattern.Steps.Count; i++)
        {
            sb.AppendLine($"{i + 1}. `{pattern.Steps[i]}`");
        }

        sb.AppendLine();
        sb.AppendLine("## 提炼依据").AppendLine();
        sb.AppendLine($"- 出现次数：{pattern.Occurrences}");
        sb.AppendLine($"- 横跨会话：{pattern.Sessions}");
        sb.AppendLine($"- 模式类型：{(pattern.Kind == DistillPattern.KindFlow ? "两步工作流" : "单步操作")}");
        sb.AppendLine();
        sb.AppendLine("> 由 DistillJob 自动提炼（纯声明式技能：工具白名单 + 提示词后缀，不含可执行内容）。");
        sb.AppendLine("> 这是**草稿** —— 执行前先核对当前环境；修订后可用 skill_validate 复检留存。");
        return sb.ToString();
    }

    private static string ToolNameOf(string step)
    {
        var space = step.IndexOf(' ');
        return space < 0 ? step : step[..space];
    }
}
