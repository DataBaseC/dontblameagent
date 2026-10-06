namespace AgentFramework.Sandbox;

/// <summary>
/// 在 PATH 里找一个可执行文件 —— bwrap 与容器后端共用的同一份「探测」实现。
///
/// <para>
/// 抽出来是怕两份慢慢分叉：探测规则（绝对路径直取、否则逐项拼 PATH 并吞掉非法项）
/// 只要有一处漏了 try/catch，某个畸形 PATH 就能让整个后端在装配期炸掉。
/// </para>
/// </summary>
internal static class PathLookup
{
    /// <summary>找到返回完整路径，找不到返回 null（= 这个后端在本机不可用）。</summary>
    public static string? Find(string exe)
    {
        try
        {
            if (Path.IsPathRooted(exe) && File.Exists(exe))
            {
                return exe;
            }
        }
        catch
        {
            // 路径怪异：继续走 PATH
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(dir, exe);
                if (File.Exists(full))
                {
                    return full;
                }
            }
            catch
            {
                // 非法 PATH 项跳过
            }
        }

        return null;
    }
}
