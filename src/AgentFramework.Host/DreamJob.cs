using System.Text.RegularExpressions;
using AgentFramework.Contracts;

namespace AgentFramework.Host;

/// <summary>一轮 Dream 的结果（人可读、可展示在面板上）。</summary>
public sealed record DreamReport(
    int MergedGroups,
    int MergedEntries,
    int Swept,
    int Promoted,
    IReadOnlyList<string> StalePaths,
    IReadOnlyList<string> Notes)
{
    public bool Changed => MergedGroups > 0 || Swept > 0 || Promoted > 0;

    public static DreamReport Empty { get; } = new(0, 0, 0, 0, [], []);
}

/// <summary>
/// <b>Dream</b>（PLAN-memory-evolution 支柱四）：记忆的周期性整理 ——
/// MiMo 的 7 天 Dream（合并 / 去重 / 路径有效性 / 压缩记忆）在我们这边的落地。
///
/// <para>
/// 「零件全在，只缺触发者」：merge / sweep 的存储原语早已就位（事件化、可回滚），
/// Dream 只是那个<b>定期触发并编排</b>的角色。全部动作都经既有记忆事件落盘 ——
/// 可审计、可 restore，绝不做「改写文件」这种账外动作。
/// </para>
/// <para>
/// 四步（每步独立可关）：
///   1. <b>合并去重</b> —— 同一作用域里规范化后同文的条目折叠成一条（同槽位才合并，
///      不破坏「改主意」的槽位语义）；热度取最大值继承；
///   2. <b>跨会话提升</b> —— 同一条事实在多个项目作用域反复出现 → 它是常识，
///      提升进 global 并撤销各项目的副本（MiMo 的「session 观察 → project 记忆」
///      在我们的作用域模型里的对应物）；
///   3. <b>频率降档清扫</b> —— 与「巩固」按钮完全同一判据（调用频率 × 半衰期衰减）；
///   4. <b>路径有效性核验</b> —— 记忆里引用的文件路径逐个查存在性，只报告不改动
///      （失效引用该由人/agent 决定改写还是撤销，梦里不擅自改记忆内容）。
/// </para>
/// </summary>
public static class DreamJob
{
    /// <summary>执行一轮 Dream。幂等：连续跑两轮，第二轮基本无事可做。</summary>
    public static async Task<DreamReport> RunAsync(
        IMemoryStore store,
        IReadOnlyList<string> scopes,
        DateTimeOffset now,
        EvolutionOptions? options = null,
        string? workspaceRoot = null,
        CancellationToken ct = default)
    {
        var opt = options ?? new EvolutionOptions();
        var notes = new List<string>();
        var mergedGroups = 0;
        var mergedEntries = 0;
        var swept = 0;
        var promoted = 0;
        var stale = new List<string>();

        // 载入全部作用域的主视图（归档层不动 —— 那是「降噪区」，不是整理对象）
        var byScope = new Dictionary<string, List<MemoryEntry>>(StringComparer.Ordinal);
        foreach (var scope in scopes.Distinct(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            byScope[scope] = [.. await store.LoadAsync(scope, 2_000, ct).ConfigureAwait(false)];
        }

        // ── 1) 合并去重（同作用域、同槽位、规范化同文）────────────
        if (opt.MaxMergeGroups > 0)
        {
            foreach (var (scope, entries) in byScope)
            {
                var groups = entries
                    .Where(e => !e.IsRetraction && e.Text.Length > 0)
                    .GroupBy(e => (Slot: e.Slot ?? string.Empty, Key: EvolutionText.Normalize(e.Text)))
                    .Where(g => g.Count() >= 2 && g.Key.Key.Length > 0)
                    .OrderBy(g => g.Min(e => e.CreatedAt))
                    .Take(opt.MaxMergeGroups)
                    .ToList();

                foreach (var group in groups)
                {
                    ct.ThrowIfCancellationRequested();

                    var members = group.OrderBy(e => e.CreatedAt).ToList();
                    // 合并后的正文取信息量最大的那条（最长）—— 组内规范化同文，取谁都等价
                    var text = members.OrderByDescending(e => e.Text.Length).First().Text;

                    try
                    {
                        await store.MergeAsync(
                            scope,
                            members.Select(e => e.Id).ToList(),
                            text,
                            sourceSession: null,
                            source: "dream",
                            ct: ct).ConfigureAwait(false);
                        mergedGroups++;
                        mergedEntries += members.Count;
                    }
                    catch (ArgumentException)
                    {
                        // 预检失败（条目刚被别的路径动过）—— 跳过这组，下一轮再来
                        notes.Add($"合并跳过（条目已变动）：{scope} × {members.Count}");
                    }
                }
            }
        }

        // ── 2) 跨会话提升：多项目重复的事实 → 全局常识 ─────────────
        promoted = await PromoteCrossProjectAsync(store, byScope, notes, ct).ConfigureAwait(false);

        // ── 3) 频率降档清扫（与「巩固」同判据）────────────────────
        if (opt.SweepInDream)
        {
            foreach (var scope in scopes.Distinct(StringComparer.Ordinal))
            {
                swept += await store.SweepAsync(scope, now, SweepPolicy.Default, "dream", ct).ConfigureAwait(false);
            }
        }

        // ── 4) 路径有效性核验（只报告，不改动）────────────────────
        if (!string.IsNullOrWhiteSpace(workspaceRoot))
        {
            foreach (var (scope, entries) in byScope)
            {
                foreach (var entry in entries)
                {
                    foreach (var token in EvolutionText.ExtractPathTokens(entry.Text))
                    {
                        if (!EvolutionText.ExistsUnder(workspaceRoot!, token))
                        {
                            stale.Add($"{scope}#{entry.Id}: {token}");
                        }
                    }
                }
            }
        }

        return new DreamReport(mergedGroups, mergedEntries, swept, promoted, stale, notes);
    }

    /// <summary>
    /// 同一条事实出现在 ≥2 个项目作用域 → 提升进 global（常识不为某个项目私有），
    /// 并撤销各项目的副本（追加 retract，原文仍可回溯）。
    /// global 里已有同文条目时只撤项目副本、不重复提升。
    /// </summary>
    private static async Task<int> PromoteCrossProjectAsync(
        IMemoryStore store,
        Dictionary<string, List<MemoryEntry>> byScope,
        List<string> notes,
        CancellationToken ct)
    {
        var projectScopes = byScope.Keys
            .Where(s => s == MemoryScope.Project || s.StartsWith(MemoryScope.ProjectPrefix, StringComparison.Ordinal))
            .ToList();

        if (projectScopes.Count < 2)
        {
            return 0;
        }

        var globalKey = new HashSet<string>(
            (byScope.TryGetValue(MemoryScope.Global, out var globalEntries) ? globalEntries : [])
                .Where(e => !e.IsRetraction)
                .Select(e => EvolutionText.Normalize(e.Text)),
            StringComparer.Ordinal);

        var cross = new Dictionary<string, List<MemoryEntry>>(StringComparer.Ordinal);

        foreach (var scope in projectScopes)
        {
            foreach (var entry in byScope[scope].Where(e => !e.IsRetraction && e.Text.Length > 0))
            {
                var key = EvolutionText.Normalize(entry.Text);
                if (key.Length == 0)
                {
                    continue;
                }

                if (!cross.TryGetValue(key, out var list))
                {
                    cross[key] = list = [];
                }

                list.Add(entry);
            }
        }

        var promoted = 0;

        foreach (var (key, entries) in cross
                     .Where(kv => kv.Value.Select(e => e.Scope).Distinct(StringComparer.Ordinal).Count() >= 2)
                     .OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();

            // 同一作用域内的重复先并成一条（保留各自作用域一条代表）
            var representatives = new List<MemoryEntry>();
            foreach (var scopeGroup in entries.GroupBy(e => e.Scope, StringComparer.Ordinal))
            {
                representatives.Add(scopeGroup.OrderBy(e => e.CreatedAt).First());
            }

            if (!globalKey.Contains(key))
            {
                await store.AppendAsync(
                    MemoryScope.Global,
                    representatives.OrderByDescending(e => e.Text.Length).First().Text,
                    tags: ["dream", "promoted"],
                    sourceSession: null,
                    source: "dream",
                    slot: null,
                    ct: ct).ConfigureAwait(false);
                globalKey.Add(key);
                promoted++;
            }

            foreach (var entry in representatives)
            {
                await store.RetractAsync(entry.Scope, entry.Id, "dream", ct).ConfigureAwait(false);
            }

            notes.Add($"跨项目常识提升：{representatives.Count} 个项目 → global");
        }

        return promoted;
    }
}

/// <summary>进化作业共用的文本工具（归一化与路径提取）。</summary>
internal static class EvolutionText
{
    /// <summary>规范化用于「是否同一条事实」的比较：去首尾空白、折叠内部空白、大小写不敏感。</summary>
    public static string Normalize(string text)
        => Regex.Replace(text.Trim(), @"\s+", " ").ToLowerInvariant();

    private static readonly Regex PathToken = new(
        @"(?:[A-Za-z0-9_.\-\\/]+)[\\/][A-Za-z0-9_.\-]+(?:\.[A-Za-z0-9]{1,8})?|[A-Za-z0-9_.\-]+\.[A-Za-z0-9]{1,8}",
        RegexOptions.Compiled);

    /// <summary>
    /// 从文本里提取**路径样式**的词（含分隔符的优先，其次带扩展名的文件名）。
    /// 保守提取：宁可漏报，不可把普通词当路径满屏误报。
    /// </summary>
    public static IReadOnlyList<string> ExtractPathTokens(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (Match match in PathToken.Matches(text))
        {
            var token = match.Value.Trim('.', '-', '_');
            if (token.Length < 3 || !seen.Add(token))
            {
                continue;
            }

            // 太像普通词的（纯数字、纯扩展名）不要
            if (token.StartsWith('.') || char.IsDigit(token[0]))
            {
                continue;
            }

            result.Add(token);
        }

        return result;
    }

    /// <summary>这个路径在工作区下（或作为绝对路径）真的存在吗？</summary>
    public static bool ExistsUnder(string workspaceRoot, string token)
    {
        try
        {
            var candidates = Path.IsPathRooted(token)
                ? new[] { token }
                : new[] { Path.Combine(workspaceRoot, token), token };

            return candidates.Any(p => File.Exists(p) || Directory.Exists(p));
        }
        catch
        {
            return true; // 判不出来就不报 —— 误报比漏报烦人
        }
    }
}
