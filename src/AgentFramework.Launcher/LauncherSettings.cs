using System.Text.Json;

namespace AgentFramework.Launcher;

/// <summary>
/// 启动器自己的设置 —— 插件档案（Profile）之外的「这台机器上想怎么用」。
///
/// <para>
/// 目前只有一项：<b>启动时要不要自动用系统浏览器打开管理页</b>。
/// </para>
/// <para>
/// 为什么要有它：启动器本身就是一个网页，默认自动弹浏览器最省事；
/// 但有人就是不想每次启动都多出一个浏览器标签页（尤其是日常只把启动器
/// 当「点一下把宿主拉起来」的开关时）。做成「开关 + 持久化」之后，
/// 默认行为不变（照旧自动打开），关掉一次，之后的运行就安静了。
/// </para>
/// </summary>
public sealed class LauncherSettings
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>启动时自动用系统浏览器打开管理页。默认开 —— 保持一直以来的行为。</summary>
    public bool OpenBrowserOnStart { get; set; } = true;

    /// <summary>读设置文件；不存在或损坏都退回默认（启动不该因为一个设置文件起不来）。</summary>
    public static LauncherSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            return new LauncherSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(path), SerializerOptions)
                   ?? new LauncherSettings();
        }
        catch (JsonException)
        {
            return new LauncherSettings();
        }
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(this, SerializerOptions));
    }
}
