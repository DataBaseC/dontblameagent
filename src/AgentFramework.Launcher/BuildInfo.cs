using System.Reflection;

namespace AgentFramework.Launcher;

/// <summary>
/// 启动器自身的构建标识。控制台与管理页都显示 —— 重编后时间戳会变，
/// 手测时一眼就能认出「跑的是不是刚编出来的那份」。
/// </summary>
public static class BuildInfo
{
    public static string Version { get; } =
        typeof(BuildInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>构建时间：入口程序集（或单文件 exe）的文件写入时间，每次重编都会变。</summary>
    public static string BuiltAt { get; } = ResolveBuiltAt();

    public static string Stamp => $"v{Version} · build {BuiltAt}";

    private static string ResolveBuiltAt()
    {
        try
        {
#pragma warning disable IL3000
            var path = typeof(BuildInfo).Assembly.Location;
#pragma warning restore IL3000
            if (string.IsNullOrEmpty(path))
            {
                path = Environment.ProcessPath;
            }

            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                return File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm");
            }
        }
        catch
        {
            // 时间戳拿不到就退化成 unknown，绝不因此挡启动
        }

        return "unknown";
    }
}
