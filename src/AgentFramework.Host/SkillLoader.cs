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
///
/// 此外还有第三来源：宿主<b>代码内置</b>技能（<see cref="Builtins"/>），
/// 随宿主分发、不依赖工作区目录，优先级最低 —— 工作区里出现同名技能即覆盖它，
/// 于是「内置的知识」可以随时被用户改写，而不必改宿主代码。
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

    /// <summary>
    /// 宿主<b>代码内置</b>的技能：不落工作区目录、随宿主一起分发。
    ///
    /// <para>
    /// 首个内置技能是 <c>skill-creator</c>（创建技能的技能）：
    /// 它本身是「提示词 + 工具白名单」的纯声明包 —— 让 agent 手里随时有一套
    /// 「怎么把做法沉淀成可复用技能」的方法论，而不是每次现编。
    /// </para>
    /// </summary>
    public static IReadOnlyList<SkillDefinition> Builtins { get; } =
    [
        new SkillDefinition
        {
            Id = "skill-creator",
            Name = "skill-creator",
            Description = "创建技能的技能：把一套做法（连同它要用的工具）打包成可复用、可分享的 skill。",
            Tools =
            [
                "skill_scaffold", "skill_validate", "skill_extract",
                "skill_from_toolset", "tool_catalog",
                "read_file", "write_file", "list_dir",
            ],
            PromptSuffix = SkillCreatorPrompt,
            Tags = ["meta", "skill", "builtin"],
            Source = "builtin",
            Version = "1.0.0",
            Author = "AgentFramework",
        },
    ];

    private const string SkillCreatorPrompt = """
        # 技能工坊（skill-creator）

        你现在带着「创建技能」的能力。技能（skill）是**纯声明式**的复用单元：
        一份 skill.json = 一句话说明 + 工具白名单 + 一段提示词后缀，不含任何可执行代码。

        ## 什么时候该建一个技能
        - 同一套「先做什么、再做什么」的做法已经重复出现两次以上；
        - 某类任务总是只需要某一小撮工具（用白名单收窄，降低模型负担）；
        - 你希望把一套经验沉淀下来，之后一句话就能召回。

        ## 标准流程
        1) 想清楚三件事：**叫什么**（id：字母数字与 - _）、**解决什么**（description 一句话）、**要用哪些工具**（tools 白名单）。
        2) 若工具来自某个现成的工具包，优先用 `skill_from_toolset` 直接把整包工具转成技能草稿；
           否则用 `skill_scaffold` 手写白名单。
        3) 提示词后缀（promptSuffix）写「怎么做」：步骤、判据、常见坑。它是技能的灵魂，别写成空话。
        4) 用 `skill_validate` 自检：白名单里的工具必须都已注册，id 不得与已有技能冲突。
        5) 需要从材料里提炼时用 `skill_extract` 起草，再人工补全。

        ## 纪律
        - 一个技能只解决一类问题；贪多会让白名单与提示词双双失焦。
        - 白名单**宁窄勿宽**：技能的价值一半在于「该收的时候收得回来」。
        - 技能不执行代码；要执行逻辑那是插件（plugin_write）的事，别混为一谈。
        """;

    /// <summary>扫描工作区的两个技能来源（skills/ 自写 + workshop/ 导入），再兜底代码内置技能。</summary>
    public static IReadOnlyList<SkillDefinition> ScanWorkspace(string? workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return Builtins;
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

        // 代码内置技能兜底：同名者不覆盖工作区来源 —— 用户可显式改写/顶掉内置技能。
        foreach (var skill in Builtins)
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
