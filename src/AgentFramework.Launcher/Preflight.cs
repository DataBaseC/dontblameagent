namespace AgentFramework.Launcher;

/// <summary>预检发现的一个问题。</summary>
public sealed record PreflightIssue(
    PreflightKind Kind,
    string PluginId,
    string Message,
    /// <summary>一键修复的动作提示（null = 无自动修法，只能人改）。</summary>
    string? FixHint = null);

/// <summary>问题类别。</summary>
public enum PreflightKind
{
    /// <summary>声明的 injects 指向的插件未启用。</summary>
    MissingDependency,

    /// <summary>前置排在依赖者之后（LoadOrder 违规）。</summary>
    DependencyAfterDependent,

    /// <summary>清单或目录层级的错误（来自扫描 Errors，启动必炸的那类）。</summary>
    CatalogError,

    /// <summary>两个插件 id 重复（目录冲突）。</summary>
    DuplicateId,
}

/// <summary>
/// 启动前静态预检。
///
/// 只读清单与 profile，<b>不加载程序集</b> —— 与扫描同一安全级别。
/// 它存在的理由：内核的 <c>CheckDependencies</c> 只在装配期爆炸，且一炸就跳过该插件；
/// 启动器提前把问题摊开给人看，还能给出"一键修法"，比"启动后少装了一个"友好得多。
/// </summary>
public static class Preflight
{
    /// <summary>
    /// 跑一遍预检。catalogOrder 为扫描顺序（目录序），enabled 为按加载顺序排好的启用列表。
    /// </summary>
    public static IReadOnlyList<PreflightIssue> Run(
        IReadOnlyList<PluginDescriptor> catalog,
        IReadOnlyList<string> enabledInLoadOrder,
        IReadOnlyList<string> catalogErrors)
    {
        var issues = new List<PreflightIssue>();
        // 重复 id 是预检的合法输入（场景 E 专门构造它）：ToDictionary 会直接炸，
        // 必须容错字典化 —— 同 id 取第一个，重复对在下面第 2 步统一报告。
        var byId = new Dictionary<string, PluginDescriptor>(StringComparer.Ordinal);
        foreach (var plugin in catalog)
        {
            if (!byId.ContainsKey(plugin.Id))
            {
                byId[plugin.Id] = plugin;
            }
        }

        // 1) 目录层错误：这些会让 Host 装配期直接失败（FailFast）或跳过，必须最先报
        foreach (var error in catalogErrors)
        {
            issues.Add(new PreflightIssue(PreflightKind.CatalogError, PluginId: string.Empty, error));
        }

        // 2) 重复 id：字典化时后写会覆盖，直接暴露
        var dupes = catalog
            .GroupBy(p => p.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1);
        foreach (var dupe in dupes)
        {
            issues.Add(new PreflightIssue(
                PreflightKind.DuplicateId,
                dupe.Key,
                $"插件 id 重复：{dupe.Key}（{string.Join("、", dupe.Select(p => Path.GetFileName(p.Directory)))}）"));
        }

        var enabledSet = new HashSet<string>(enabledInLoadOrder, StringComparer.Ordinal);
        var position = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < enabledInLoadOrder.Count; i++)
        {
            if (!position.ContainsKey(enabledInLoadOrder[i]))
            {
                position[enabledInLoadOrder[i]] = i;
            }
        }

        // 3) 缺前置 + 前置顺序
        foreach (var id in enabledInLoadOrder)
        {
            if (!byId.TryGetValue(id, out var plugin))
            {
                continue;   // 已启用但目录里没有：属于 Errors 范畴，别在这里重复报
            }

            foreach (var inject in plugin.Injects)
            {
                var depId = ResolveInject(inject, catalog);
                if (depId is null)
                {
                    continue;   // injects 指向服务类型而非插件 id（第一版简化语义），不当作错误
                }

                if (!enabledSet.Contains(depId))
                {
                    issues.Add(new PreflightIssue(
                        PreflightKind.MissingDependency,
                        id,
                        $"{plugin.Name} 的前置「{NameOf(depId, byId)}」未启用",
                        FixHint: $"启用 {NameOf(depId, byId)} 并排到 {plugin.Name} 之前"));
                }
                else if (position.TryGetValue(depId, out var depPos)
                         && position.TryGetValue(id, out var ownPos)
                         && depPos > ownPos)
                {
                    issues.Add(new PreflightIssue(
                        PreflightKind.DependencyAfterDependent,
                        id,
                        $"{plugin.Name} 排在了它的前置「{NameOf(depId, byId)}」之后（装配会因依赖未就绪而失败）",
                        FixHint: $"把 {NameOf(depId, byId)} 移到 {plugin.Name} 之前"));
                }
            }
        }

        return issues;
    }

    /// <summary>
    /// injects 的第一版语义：既可能是插件 id，也可能是契约服务类型名。
    /// 这里做温和解析——按插件 id 匹配；匹配不到返回 null（不瞎报错）。
    /// </summary>
    private static string? ResolveInject(string inject, IReadOnlyList<PluginDescriptor> catalog)
    {
        foreach (var p in catalog)
        {
            if (string.Equals(p.Id, inject, StringComparison.Ordinal))
            {
                return p.Id;
            }
        }

        return null;
    }

    private static string NameOf(string pluginId, IReadOnlyDictionary<string, PluginDescriptor> byId)
        => byId.TryGetValue(pluginId, out var p) ? p.Name : pluginId;
}
