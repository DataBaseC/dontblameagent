using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentFramework.Launcher;

/// <summary>
/// 插件装配档案（对应设计里的 <b>Profile</b> 概念）。
/// 启动器勾选什么、排什么顺序，就存成这个文件；Host 启动时按它决定装哪些插件、什么顺序。
///
/// 存在形式刻意是纯 JSON：人能读、能改、能拿给别人，也方便将来被 Web UI 或命令行直接生成。
/// </summary>
public sealed class PluginProfile
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Name { get; set; } = "default";

    /// <summary>被勾选启用的插件 id。不在列表里的一律不加载。</summary>
    public List<string> EnabledPlugins { get; set; } = [];

    /// <summary>
    /// 手动加载顺序（v2 新增，可空）。存的是插件 id，<b>从上到下 = 装配顺序</b>。
    ///
    /// <para>
    /// · null / 缺失 = 老档案，装配退回目录扫描序（向后兼容，老文件不用改）；
    /// · 含未知 id = 容忍（扫描时目录里没有它就忽略，不报错）；
    /// · 未列出的已启用插件排在手写顺序之后（目录扫描序）。
    /// </para>
    /// </summary>
    public List<string>? LoadOrder { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool IsEnabled(string pluginId)
        => EnabledPlugins.Contains(pluginId, StringComparer.Ordinal);

    /// <summary>
    /// 按档案排序输出启用列表：手写 LoadOrder 优先（过滤掉未启用的），
    /// 剩下的已启用插件按目录扫描序补在后面。给 <c>--plugins-enabled</c> 的顺序就是装配顺序。
    /// </summary>
    public IReadOnlyList<string> OrderedEnabled(IReadOnlyList<string> catalogOrder)
    {
        var enabledSet = new HashSet<string>(EnabledPlugins, StringComparer.Ordinal);
        var result = new List<string>();

        if (LoadOrder is { Count: > 0 })
        {
            foreach (var id in LoadOrder)
            {
                if (enabledSet.Contains(id))
                {
                    result.Add(id);
                    enabledSet.Remove(id);
                }
            }
        }

        foreach (var id in catalogOrder)
        {
            if (enabledSet.Contains(id))
            {
                result.Add(id);
                enabledSet.Remove(id);
            }
        }

        return result;
    }

    /// <summary>把某个插件移到另一个插件之前（拖拽排序落库）。两者都必须在 LoadOrder 里；缺失时先补齐。</summary>
    public bool MoveBefore(string dragId, string targetId, IReadOnlyList<string> catalogOrder)
    {
        EnsureLoadOrderCovers(catalogOrder);
        if (LoadOrder is null || !LoadOrder.Contains(dragId) || !LoadOrder.Contains(targetId) || dragId == targetId)
        {
            return false;
        }

        LoadOrder.Remove(dragId);
        LoadOrder.Insert(LoadOrder.IndexOf(targetId), dragId);
        return true;
    }

    /// <summary>把 LoadOrder 补齐到覆盖目录里的全部插件（新装的插件排在末尾）。</summary>
    public void EnsureLoadOrderCovers(IReadOnlyList<string> catalogOrder)
    {
        LoadOrder ??= [];
        foreach (var id in catalogOrder)
        {
            if (!LoadOrder.Contains(id, StringComparer.Ordinal))
            {
                LoadOrder.Add(id);
            }
        }
    }

    public static PluginProfile Load(string path)
    {
        if (!File.Exists(path))
        {
            return new PluginProfile();
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<PluginProfile>(json, SerializerOptions) ?? new PluginProfile();
        }
        catch (JsonException)
        {
            // 档案坏了不能拖垮启动 —— 退回空档案，等于"一个都不装"
            return new PluginProfile();
        }
    }

    public void Save(string path)
    {
        UpdatedAt = DateTimeOffset.UtcNow;
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(this, SerializerOptions));
    }
}
