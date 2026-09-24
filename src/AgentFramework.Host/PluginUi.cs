using System.Text.Json;
using AgentFramework.Contracts;

namespace AgentFramework.Host;

/// <summary>一个插件贡献给主界面的 UI 片段。</summary>
public sealed record PluginUiContribution(string Id, IReadOnlyList<string> Styles, IReadOnlyList<string> Scripts);

/// <summary>
/// 插件界面贡献的定位与安全读取。
///
/// <para>
/// 三个用途共用一份逻辑：主页注入什么、<c>/api/plugins</c> 报什么、<c>/plugin-ui</c> 允许取什么。
/// 分散实现的话，最容易出的错是「报出来的和让取的不是一回事」——
/// 界面上一片空白，日志里什么都没有。
/// </para>
///
/// <para>
/// <b>安全口径</b>：只认清单里声明过的文件（<c>ui.styles</c> / <c>ui.scripts</c>），
/// 且必须是插件目录内的相对路径 —— 插件不能借这个通道去读盘上任意文件。
/// </para>
/// </summary>
public static class PluginUi
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".css", ".js", ".mjs", ".svg", ".png", ".jpg", ".webp", ".woff", ".woff2", ".ttf",
    };

    /// <summary>按插件 id 找它的目录（找不到返回 null）。</summary>
    public static string? FindDirectory(string? pluginsDir, string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginsDir) || !Directory.Exists(pluginsDir) || string.IsNullOrWhiteSpace(pluginId))
        {
            return null;
        }

        foreach (var dir in Directory.EnumerateDirectories(pluginsDir).OrderBy(d => d, StringComparer.Ordinal))
        {
            var manifest = TryReadManifest(Path.Combine(dir, "plugin.json"));
            if (manifest is not null && string.Equals(manifest.Id, pluginId, StringComparison.Ordinal))
            {
                return dir;
            }
        }

        return null;
    }

    public static PluginManifest? TryReadManifest(string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath), JsonOptions);
        }
        catch (Exception)
        {
            // 清单坏掉的插件由加载流程去报错；这里只负责「看不见它」
            return null;
        }
    }

    /// <summary>某插件声明的界面贡献（没有或全是非法条目时返回 null）。</summary>
    public static PluginUiContribution? Describe(string? pluginsDir, string pluginId)
    {
        var dir = FindDirectory(pluginsDir, pluginId);
        if (dir is null)
        {
            return null;
        }

        var manifest = TryReadManifest(Path.Combine(dir, "plugin.json"));
        if (manifest?.Ui is null)
        {
            return null;
        }

        var styles = manifest.Ui.Styles.Where(IsSafeRelativeFile).Distinct(StringComparer.Ordinal).ToList();
        var scripts = manifest.Ui.Scripts.Where(IsSafeRelativeFile).Distinct(StringComparer.Ordinal).ToList();

        if (styles.Count == 0 && scripts.Count == 0)
        {
            return null;
        }

        // 只保留真实存在的文件：清单写错时宁可不注入，也不要制造 404 噪声
        styles = [.. styles.Where(f => File.Exists(Path.Combine(dir, f)))];
        scripts = [.. scripts.Where(f => File.Exists(Path.Combine(dir, f)))];

        return styles.Count == 0 && scripts.Count == 0
            ? null
            : new PluginUiContribution(pluginId, styles, scripts);
    }

    /// <summary>
    /// 全部「已加载且声明了界面」的插件，按 id 排序 —— 顺序稳定，
    /// 于是插件之间的覆盖关系可预期（后加载的赢，与 CSS 本来的规则一致）。
    /// </summary>
    public static IReadOnlyList<PluginUiContribution> Collect(string? pluginsDir, IEnumerable<string> pluginIds)
    {
        var result = new List<PluginUiContribution>();

        foreach (var id in pluginIds.OrderBy(i => i, StringComparer.Ordinal))
        {
            if (Describe(pluginsDir, id) is { } contribution)
            {
                result.Add(contribution);
            }
        }

        return result;
    }

    /// <summary>
    /// 解析一个界面文件的真实路径。<b>只认清单里声明过的文件</b> ——
    /// 未声明的路径一律拒绝，哪怕它确实躺在插件目录里。
    /// </summary>
    public static string? ResolveFile(string? pluginsDir, string pluginId, string fileName)
    {
        if (!IsSafeRelativeFile(fileName))
        {
            return null;
        }

        var dir = FindDirectory(pluginsDir, pluginId);
        if (dir is null)
        {
            return null;
        }

        var manifest = TryReadManifest(Path.Combine(dir, "plugin.json"));
        if (manifest?.Ui is null)
        {
            return null;
        }

        var declared = manifest.Ui.Styles.Contains(fileName, StringComparer.Ordinal)
            || manifest.Ui.Scripts.Contains(fileName, StringComparer.Ordinal);

        if (!declared)
        {
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(dir, fileName));

        // 双保险：即便清单里写了带 .. 的路径（上面 IsSafeRelativeFile 已拦），
        // 这里再确认解析结果没跑出插件目录。
        var rootWithSeparator = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return File.Exists(full) ? full : null;
    }

    public static string ContentTypeFor(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".css" => "text/css; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".ttf" => "font/ttf",
        _ => "application/octet-stream",
    };

    /// <summary>相对路径 + 允许的扩展名 + 不含上跳段。</summary>
    public static bool IsSafeRelativeFile(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 200)
        {
            return false;
        }

        if (fileName.Contains('\0') || Path.IsPathRooted(fileName))
        {
            return false;
        }

        var normalized = fileName.Replace('\\', '/');
        if (normalized.Split('/').Any(segment => segment is ".." or "." or ""))
        {
            return false;
        }

        return AllowedExtensions.Contains(Path.GetExtension(fileName));
    }
}
