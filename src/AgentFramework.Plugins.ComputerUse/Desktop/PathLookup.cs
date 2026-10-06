namespace AgentFramework.Plugins.ComputerUse.Desktop;

/// <summary>
/// 在 PATH 里找一个可执行文件（探测 cliclick / xdotool / import…）。
///
/// <para>
/// 插件跑在独立 ALC 里，只能看到契约程序集 —— 宿主 Sandbox 工程里那份同名工具够不着，
/// 所以这里自带一份最小实现。探测规则本身很简单（绝对路径直取、否则逐项拼 PATH 并吞掉非法项），
/// 重复这一小段比把它抬进契约层更划算。
/// </para>
/// </summary>
internal static class PathLookup
{
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
