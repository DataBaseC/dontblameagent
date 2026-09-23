using System.Text.Json;

namespace AgentFramework.Host;

/// <summary>
/// 技能定义（G2 起步 → 创意工坊就位）。
///
/// 刻意保持**纯声明式**：一个技能只能是「工具白名单 + 提示词后缀」，
/// 不含任何可执行内容。这不是能力上的妥协，而是创意工坊的安全地基 ——
/// 声明式包可以放心地导入、分享、再分发，导入动作永远不可能引入代码。
/// 将来若要「可执行技能」，必须先有沙箱，而不是先有工坊。
/// </summary>
public sealed class SkillDefinition
{
    /// <summary>技能目录名（加载器回填）—— 工坊条目的唯一身份。</summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    /// <summary>启用此技能时暴露给模型的工具白名单；空 = 不收窄（仅提示词型技能）。</summary>
    public List<string> Tools { get; set; } = [];

    /// <summary>技能激活时追加到冻结段尾部的说明（第一版先支持静态文本）。</summary>
    public string? PromptSuffix { get; set; }

    // ── 创意工坊元数据（全部可选；旧 skill.json 不写这些字段照常加载）──

    /// <summary>语义化版本（工坊条目将来靠它做更新提示）。</summary>
    public string? Version { get; set; }

    /// <summary>作者署名 —— 分享内容的最基本礼仪。</summary>
    public string? Author { get; set; }

    /// <summary>检索标签（工坊目录页的过滤维度）。</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>出处：builtin（工作区 skills/ 自写）| workshop（workshop/ 导入）。加载器回填。</summary>
    public string Source { get; set; } = "builtin";

    /// <summary>清单里声明的宿主最低版本（可选，导入时校验；当前仅记录）。</summary>
    public string? MinHostVersion { get; set; }
}

/// <summary>
/// 技能加载器。
///
/// 技能 =「模式白名单的动态版」：启用时不重装配、不重启 ——
/// VisibleTools 每轮现取，技能只改「这一轮暴露什么」的交集。
///
/// 目录约定（创意工坊地基）：
///   · <c>{workspace}/skills/</c>   —— 本机自写技能（Source = builtin）
///   · <c>{workspace}/workshop/</c> —— 导入的工坊技能（Source = workshop）
/// 两个目录同名时 **builtin 优先**：外来内容永远不覆盖本地创作；
/// 工坊条目想顶掉内置版，请先删内置或改名 —— 显式操作好过隐式覆盖。
/// </summary>
public static class SkillLoader
{
    public const string BuiltinDirName = "skills";
    public const string WorkshopDirName = "workshop";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>扫描工作区的两个技能来源（skills/ 自写 + workshop/ 导入），同名时内置优先。</summary>
    public static IReadOnlyList<SkillDefinition> ScanWorkspace(string? workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return [];
        }

        var builtin = ScanDirectory(Path.Combine(workspaceRoot, BuiltinDirName), "builtin");
        var workshop = ScanDirectory(Path.Combine(workspaceRoot, WorkshopDirName), "workshop");

        var merged = new List<SkillDefinition>(builtin);
        var seen = builtin.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var skill in workshop)
        {
            if (seen.Add(skill.Name))
            {
                merged.Add(skill);
            }
        }

        return merged;
    }

    /// <summary>扫描单个技能目录（每个子目录 = 一个技能，须含 skill.json）。</summary>
    public static IReadOnlyList<SkillDefinition> ScanDirectory(string? dir, string source)
    {
        var skills = new List<SkillDefinition>();
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            return skills;
        }

        foreach (var subdir in Directory.EnumerateDirectories(dir).OrderBy(d => d, StringComparer.Ordinal))
        {
            var manifestPath = Path.Combine(subdir, "skill.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            try
            {
                var skill = JsonSerializer.Deserialize<SkillDefinition>(File.ReadAllText(manifestPath), JsonOptions);
                if (skill is not null && !string.IsNullOrWhiteSpace(skill.Name))
                {
                    skill.Id = Path.GetFileName(subdir);
                    skill.Source = source;
                    skills.Add(skill);
                }
            }
            catch (JsonException)
            {
                // 坏技能清单跳过 —— 与插件扫描同一纪律（坏的不拖垮整体，但也不静默）
                skills.Add(new SkillDefinition
                {
                    Id = Path.GetFileName(subdir),
                    Name = Path.GetFileName(subdir),
                    Description = "（skill.json 解析失败）",
                    Tools = [],
                    Source = source,
                });
            }
        }

        return skills;
    }

    /// <summary>从任意目录读一份技能清单（导入前的预检解析；不落盘、不生效）。</summary>
    public static SkillDefinition? TryReadManifest(string manifestPath)
    {
        try
        {
            var skill = JsonSerializer.Deserialize<SkillDefinition>(File.ReadAllText(manifestPath), JsonOptions);
            return skill is not null && !string.IsNullOrWhiteSpace(skill.Name) ? skill : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }
}
