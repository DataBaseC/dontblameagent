namespace AgentFramework.Contracts;

/// <summary>
/// 子进程环境白名单 —— 「密钥不出门」这条纪律的唯一实现。
///
/// <para>
/// 现有代码里所有「起子进程跑东西」的地方都从这里取白名单，做法统一是：
/// <b>先 <c>Environment.Clear()</c> 清空继承的宿主环境，再只放行白名单里的变量</b>。
/// 宿主环境里可能有模型 / 搜索 API key（<c>AGENT_*_KEY</c> 等），子进程一旦能读到，
/// 一条 <c>echo</c> 就把密钥外带了 —— 这一步必须在每个起子进程的地方生效，一处漏了等于没做。
/// </para>
///
/// <para>
/// 两档白名单，按「子进程要干什么」分：
/// <list type="bullet">
///   <item><see cref="Allowlist"/> —— <b>命令沙箱</b>（<c>Sandbox.ProcessRunner</c>）用：
///     shell / 常见命令行工具真正需要的那些（PATH、TMP、SystemRoot…），面窄。</item>
///   <item><see cref="ToolingAllowlist"/> —— <b>MCP 子进程</b>（<c>Host.Mcp.McpClient</c>）用：
///     典型 MCP server 是 <c>npx</c> / <c>uvx</c> / <c>python</c>，除 shell 那批外还要
///     家目录 / 缓存位置（npm、pip 靠它找缓存与依赖），故在其上补一档，但**仍然排除一切密钥**。</item>
/// </list>
/// </para>
///
/// <para>
/// 放在契约层是刻意的：白名单本身与「谁来跑子进程」无关，两个上层（Sandbox / Host）
/// 共享同一份，纪律才不会在两处各写一遍慢慢分叉。
/// </para>
/// </summary>
public static class ProcessEnvironment
{
    // 命令沙箱 · POSIX shell / pwsh：除基本路径外，Git Bash / MSYS 还靠 TMP/SystemRoot 等找挂载点。
    private static readonly string[] PosixShellAllow =
    [
        "PATH", "HOME", "USER", "LOGNAME", "SHELL", "LANG", "LC_ALL", "LC_CTYPE", "TERM", "TZ",
        "TMP", "TEMP", "TMPDIR", "SystemRoot", "SYSTEMROOT", "ComSpec", "COMSPEC", "PATHEXT",
        "windir", "WINDIR", "SystemDrive", "SYSTEMDRIVE",
    ];

    // 命令沙箱 · 原生 Windows（cmd）：命令解释器只需要系统目录与路径相关变量。
    private static readonly string[] WindowsShellAllow =
    [
        "PATH", "SystemRoot", "SYSTEMROOT", "ComSpec", "COMSPEC", "PATHEXT",
        "windir", "WINDIR", "SystemDrive", "SYSTEMDRIVE",
        "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE",
    ];

    // MCP 工具链 · POSIX 额外：node / python 的缓存与配置目录（XDG 规范）。
    private static readonly string[] PosixToolingExtra =
    [
        "XDG_CACHE_HOME", "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_RUNTIME_DIR",
    ];

    // MCP 工具链 · Windows 额外：npm / pip 缓存、家目录、临时目录、程序目录。
    // （Windows 环境变量名大小写不敏感，GetEnvironmentVariable 已能命中。）
    private static readonly string[] WindowsToolingExtra =
    [
        "TEMP", "TMP", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "HOMEDRIVE", "HOMEPATH",
        "PROGRAMFILES", "ProgramFiles(x86)", "PROGRAMW6432", "PROGRAMDATA", "CommonProgramFiles",
        "OS", "COMPUTERNAME",
    ];

    /// <summary>
    /// 命令沙箱的环境白名单：只放行 shell / 常见工具链真正需要的变量。
    /// 调用方应在取用前先 <c>Environment.Clear()</c>。
    /// </summary>
    public static IEnumerable<KeyValuePair<string, string>> Allowlist(bool posix)
        => Read(posix ? PosixShellAllow : WindowsShellAllow);

    /// <summary>
    /// MCP 等「跑 node / python 工具」场景用的白名单：在 shell 名单之上补上
    /// 家目录 / 缓存变量，让 npx / uvx / pip 能找到自己的缓存与依赖，但**仍不含任何密钥**。
    /// 调用方应在取用前先 <c>Environment.Clear()</c>。
    /// </summary>
    public static IEnumerable<KeyValuePair<string, string>> ToolingAllowlist()
    {
        var posix = !OperatingSystem.IsWindows();
        return Read(Merge(
            posix ? PosixShellAllow : WindowsShellAllow,
            posix ? PosixToolingExtra : WindowsToolingExtra));
    }

    private static IEnumerable<KeyValuePair<string, string>> Read(IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
            {
                yield return new KeyValuePair<string, string>(name, value);
            }
        }
    }

    /// <summary>合并两组名单并去重（Windows 名大小写不敏感，统一按 OrdinalIgnoreCase 去重）。</summary>
    private static IEnumerable<string> Merge(string[] first, string[] second)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in first)
        {
            if (seen.Add(name))
            {
                yield return name;
            }
        }

        foreach (var name in second)
        {
            if (seen.Add(name))
            {
                yield return name;
            }
        }
    }
}
