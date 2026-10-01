using System.Text.RegularExpressions;
using AgentFramework.Contracts;

namespace AgentFramework.Host;

/// <summary>
/// 一条<strong>输入级</strong>权限规则（MiMo Code 的 permission rules）。
///
/// <para>
/// 匹配对象是「工具名 + 关键参数」拼出的<b>签名</b>（见 <see cref="ApprovalRuleSet.Signature"/>），
/// 模式里 <c>*</c> 通配任意、<c>?</c> 通配单字符，大小写不敏感：
/// <code>
///   run_command git *     → allow   （git 只读操作不打断）
///   run_command rm *      → deny    （删东西必须走显式授权）
///   edit_file *.mdx       → allow
/// </code>
/// </para>
/// <para>
/// 多条命中时<strong>后写覆盖先写</strong>（last-match-wins）——
/// 于是「先放行 git *，再收回 git push *」这种渐进细化是自然的写法。
/// </para>
/// </summary>
public sealed record ApprovalRule(string Match, ApprovalDecision Decision);

/// <summary>规则命中结果：处置 + 命中的模式（写进拒绝理由，方便对账）。</summary>
public readonly record struct ApprovalRuleHit(ApprovalDecision Decision, string Pattern);

/// <summary>
/// 输入级规则的匹配语义。档位判「动作性质」，这里判「动作内容」——
/// 两者互补：<c>run_command</c> 在 Build 档按危险命令启发式判定，
/// 而 <c>run_command git status</c> 这种用户自己的口径，只有规则说得清。
/// </summary>
public static class ApprovalRuleSet
{
    /// <summary>签名里优先取的参数键（其余参数默认不进签名，避免内容噪音淹没模式）。</summary>
    private static readonly string[] PreferredArgKeys = ["command", "path", "from", "to", "dir", "directory"];

    /// <summary>
    /// 把一次工具调用拼成规则匹配用的签名：<c>"{toolName} {关键参数…}"</c>。
    /// 没有已知关键参数时退化为全部参数值（按参数名排序，保证确定性）。
    /// </summary>
    public static string Signature(ToolPreExecuteEvent e) => Signature(e.ToolName, e.Arguments);

    /// <summary>签名拼装的核心（供审批规则与 DistillJob 挖矿共用 —— 单一实现）。</summary>
    public static string Signature(string toolName, IReadOnlyDictionary<string, string?>? arguments)
    {
        var parts = new List<string>();

        if (arguments is not null)
        {
            foreach (var key in PreferredArgKeys)
            {
                if (arguments.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                {
                    parts.Add(value!.Trim());
                }
            }

            if (parts.Count == 0)
            {
                foreach (var kv in arguments.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (!string.IsNullOrWhiteSpace(kv.Value))
                    {
                        parts.Add(kv.Value!.Trim());
                    }
                }
            }
        }

        return parts.Count == 0 ? toolName : toolName + " " + string.Join(" ", parts);
    }

    /// <summary>模式匹配（<c>*</c> 任意、<c>?</c> 单字符、大小写不敏感）。</summary>
    public static bool IsMatch(string pattern, string signature)
    {
        var p = pattern?.Trim() ?? string.Empty;
        if (p.Length == 0)
        {
            return false;
        }

        var regex = "^" + string.Concat(p.Select(ch => ch switch
        {
            '*' => ".*",
            '?' => ".",
            _ => Regex.Escape(ch.ToString()),
        })) + "$";

        return Regex.IsMatch(signature, regex, RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    }

    /// <summary>按序匹配，<strong>最后一条命中的说了算</strong>（last-match-wins）。没有命中返回 null。</summary>
    public static ApprovalRuleHit? TryDecide(IReadOnlyList<ApprovalRule> rules, ToolPreExecuteEvent e)
    {
        if (rules is null || rules.Count == 0)
        {
            return null;
        }

        var signature = Signature(e);
        ApprovalRuleHit? hit = null;

        foreach (var rule in rules)
        {
            if (rule is not null && IsMatch(rule.Match, signature))
            {
                hit = new ApprovalRuleHit(rule.Decision, rule.Match);
            }
        }

        return hit;
    }

    /// <summary>解析配置单里的 action 字符串（allow / ask / deny；认不出 = null = 该条忽略）。</summary>
    public static ApprovalDecision? ParseAction(string? action) => action?.Trim().ToLowerInvariant() switch
    {
        "allow" => ApprovalDecision.Allow,
        "ask" => ApprovalDecision.Ask,
        "deny" => ApprovalDecision.Deny,
        _ => null,
    };
}

/// <summary>
/// <c>external_directory</c> 特殊防护（MiMo Code 的越界访问保护）：
/// <b>写到工作区之外</b>不许被档位或规则静默放行 —— 至少升级为 Ask
/// （无界面场景 Ask 即拒绝）。要放开，得由用户显式授予
/// （<c>HostOptions.AllowExternalDirectory</c> / 配置单 <c>allowExternalDirectory</c>）。
///
/// <para>
/// 为什么只管「写」：越界读是常见的合法需求（读系统文件、共享目录），
/// 而<b>伤害向量是越界写</b>。读取类仍按档位判定，不搞一刀切。
/// </para>
/// </summary>
public static class ExternalDirectoryGuard
{
    /// <summary>带路径参数的键。</summary>
    private static readonly string[] PathArgKeys = ["path", "from", "to", "dir", "directory"];

    /// <summary>有副作用的工具才有「越界写」一说。</summary>
    private static readonly HashSet<string> StateChangingTools = new(StringComparer.Ordinal)
    {
        "write_file", "edit_file", "make_dir", "copy_path", "move_path", "delete_path",
    };

    /// <summary>这次调用是否要把路径写到工作区之外（含 <c>..</c> 逃逸；判不出来按越界算）。</summary>
    public static bool EscapesWorkspace(ToolPreExecuteEvent e, string? workspaceRoot)
    {
        if (!StateChangingTools.Contains(e.ToolName))
        {
            return false;
        }

        foreach (var key in PathArgKeys)
        {
            if (e.Arguments.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw))
            {
                if (!InWorkspace(raw!, workspaceRoot))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool InWorkspace(string path, string? workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            // 没有工作区概念就判不了 —— 不瞎拦
            return true;
        }

        try
        {
            var root = Path.GetFullPath(workspaceRoot);
            var full = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(root, path));
            var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            return full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// 审批判定的<strong>完整链条</strong>（MiMo 权限面在宿主侧的合流点）。
/// 优先级从高到低：
/// <list type="number">
///   <item>
///   <b>计划模式闸</b>（<see cref="PlanModePolicy"/>）—— 模式的承诺，最高优先，
///   规则和档位都推不翻；
///   </item>
///   <item>
///   <b>输入级规则</b>（<see cref="ApprovalRuleSet"/>）—— 用户显式写下的模式口径，
///   last-match-wins，命中即定（可比档位更松、也可更紧）；
///   </item>
///   <item>
///   <b>档位策略</b>（<c>inner</c>，通常 <see cref="ApprovalTiers.Decide"/>）——
///   按动作性质判定；
///   </item>
///   <item>
///   <b>external_directory 硬闸</b>（<see cref="ExternalDirectoryGuard"/>）——
///   越界写不许静默放行，除非用户显式授予。
///   </item>
/// </list>
/// </summary>
public static class ApprovalPolicyChain
{
    public static ApprovalDecision Decide(
        string? modeId,
        ToolPreExecuteEvent request,
        Func<ToolPreExecuteEvent, ApprovalDecision> inner,
        string? workspaceRoot,
        IReadOnlyList<ApprovalRule>? rules = null,
        bool allowExternalDirectory = false)
        => PlanModePolicy.Decide(modeId, request, r =>
        {
            // 输入级规则优先于档位：用户写下的内容口径说了算（last-match-wins）
            var hit = ApprovalRuleSet.TryDecide(rules ?? [], r);
            var decision = hit is { } matched ? matched.Decision : inner(r);

            if (decision == ApprovalDecision.Deny
                && hit is { } denyHit
                && string.IsNullOrWhiteSpace(r.RejectReason))
            {
                r.RejectReason = $"权限规则拒绝：匹配「{denyHit.Pattern}」→ deny（如需放行请调整 agent.json 的 approvalRules）";
            }

            // external_directory 硬闸：越界写最多问，绝不静默放行
            if (decision == ApprovalDecision.Allow
                && !allowExternalDirectory
                && ExternalDirectoryGuard.EscapesWorkspace(r, workspaceRoot))
            {
                decision = ApprovalDecision.Ask;
            }

            return decision;
        }, workspaceRoot);
}
