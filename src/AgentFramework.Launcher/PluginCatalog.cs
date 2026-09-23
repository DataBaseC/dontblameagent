using System.Text.Json;

namespace AgentFramework.Launcher;

/// <summary>插件目录里的一个候选插件。</summary>
public sealed record PluginDescriptor(
    string Id,
    string Name,
    string Version,
    string ApiVersion,
    string Directory,
    IReadOnlyList<string> Injects,
    string? Description = null);

/// <summary>扫描结果。<b>坏清单不会被静默跳过</b> —— 会出现在 Errors 里。</summary>
public sealed record CatalogResult(IReadOnlyList<PluginDescriptor> Plugins, IReadOnlyList<string> Errors);

/// <summary>
/// 插件目录扫描器。
/// 启动器要能"列出可装的 mod"，靠的就是它 —— 它只读清单，不加载任何程序集，
/// 所以扫描本身是零风险的。
/// </summary>
public static class PluginCatalog
{
    public static CatalogResult Scan(string? pluginsDirectory)
    {
        var plugins = new List<PluginDescriptor>();
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(pluginsDirectory) || !Directory.Exists(pluginsDirectory))
        {
            return new CatalogResult(plugins, errors);
        }

        foreach (var dir in Directory.EnumerateDirectories(pluginsDirectory).OrderBy(d => d, StringComparer.Ordinal))
        {
            var manifestPath = Path.Combine(dir, "plugin.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
                var root = doc.RootElement;
                var folderName = Path.GetFileName(dir);

                var id = ReadString(root, "id");
                var assembly = ReadString(root, "assembly");
                var entry = ReadString(root, "entry");

                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(assembly) || string.IsNullOrWhiteSpace(entry))
                {
                    errors.Add($"{folderName}：清单缺少 id / assembly / entry");
                    continue;
                }

                if (!File.Exists(Path.Combine(dir, assembly)))
                {
                    errors.Add($"{folderName}：入口程序集缺失（{assembly}）");
                    continue;
                }

                plugins.Add(new PluginDescriptor(
                    id,
                    ReadString(root, "name") ?? id,
                    ReadString(root, "version") ?? "0.0.0",
                    ReadString(root, "apiVersion") ?? "",
                    dir,
                    ReadStringArray(root, "injects"),
                    ReadString(root, "description")));
            }
            catch (JsonException ex)
            {
                errors.Add($"{Path.GetFileName(dir)}：清单解析失败（{ex.Message}）");
            }
        }

        return new CatalogResult(plugins, errors);
    }

    private static string? ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. value.EnumerateArray()
            .Where(v => v.ValueKind == JsonValueKind.String)
            .Select(v => v.GetString()!)
            .Where(v => !string.IsNullOrWhiteSpace(v))];
    }
}
