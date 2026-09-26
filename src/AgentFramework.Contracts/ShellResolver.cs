namespace AgentFramework.Contracts;

/// <summary>
/// shell 解析：优先 POSIX（bash → sh），Windows 找不到才回落 cmd。
/// 解析结果由调用方缓存（见 <c>ToolkitOptions.EffectiveShell</c>）—— 本类型只负责「这一次怎么找」。
/// </summary>
public static class ShellResolver
{
    /// <summary>
    /// 按偏好解析 shell。
    /// <paramref name="preferred"/> 支持：显式路径、<c>bash</c>/<c>sh</c>/<c>cmd</c>/<c>powershell</c>/<c>pwsh</c>、
    /// 或 <c>posix</c>/<c>auto</c>（= 默认优先链）。认不出来时回落默认链并记 Note。
    /// </summary>
    public static ShellSpec Resolve(string? preferred = null)
    {
        var p = string.IsNullOrWhiteSpace(preferred) ? null : preferred.Trim();

        if (p is not null && !IsKeyword(p))
        {
            // 显式路径 / 文件名：人自己钉的，不再「优先」什么
            var id = IdFromFileName(p);
            return new ShellSpec(id, p, IsPosixId(id), $"显式指定：{p}");
        }

        if (p is not null && IsKeyword(p) && !IsAutoKeyword(p))
        {
            var forced = ResolveId(p);
            if (forced is not null)
            {
                return forced with { Note = $"显式指定：{p}" };
            }

            // 显式指定但机器上没有：必须说清楚，而不是悄悄换成 cmd 再跑
            return Fallback(p);
        }

        return ResolveAuto();
    }

    /// <summary>默认优先链：POSIX 先于 cmd/powershell。</summary>
    private static ShellSpec ResolveAuto()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Unix：bash → sh；PATH 里的 bash 也认（有些发行版 /bin/bash 不在）
            if (TryFind("bash") is { } bash)
            {
                return new ShellSpec("bash", bash, IsPosix: true, "POSIX bash");
            }

            if (TryFind("sh") is { } sh)
            {
                return new ShellSpec("sh", sh, IsPosix: true, "POSIX sh");
            }

            return new ShellSpec("sh", "/bin/sh", IsPosix: true, "系统固定 /bin/sh");
        }

        // Windows：Git Bash / MSYS2 / Cygwin 的 bash.exe 在 PATH 上就能用
        if (TryFind("bash") is { } winBash)
        {
            return new ShellSpec("bash", winBash, IsPosix: true, "POSIX bash（Git Bash / MSYS2）");
        }

        if (TryFind("sh") is { } winSh)
        {
            return new ShellSpec("sh", winSh, IsPosix: true, "POSIX sh");
        }

        // 无 POSIX shell：退回 cmd，但把「这不是 POSIX」写在脸上
        var comspec = Environment.GetEnvironmentVariable("ComSpec");
        var cmd = string.IsNullOrWhiteSpace(comspec) ? "cmd.exe" : comspec;
        return new ShellSpec("cmd", cmd, IsPosix: false, "回落 cmd.exe（未找到 bash/sh —— 命令请用 cmd 语法，或装 Git Bash）");
    }

    private static ShellSpec? ResolveId(string keyword) => keyword.ToLowerInvariant() switch
    {
        "bash" => TryFind("bash") is { } b
            ? new ShellSpec("bash", b, true, "POSIX bash")
            : null,
        "sh" => TryFind("sh") is { } s
            ? new ShellSpec("sh", s, true, "POSIX sh")
            : OperatingSystem.IsWindows()
                ? null
                : new ShellSpec("sh", "/bin/sh", true, "系统固定 /bin/sh"),
        "cmd" => OperatingSystem.IsWindows()
            ? new ShellSpec("cmd", Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } c ? c : "cmd.exe", false, "cmd.exe")
            : null,
        "powershell" => TryFind("powershell") is { } ps
            ? new ShellSpec("powershell", ps, false, "Windows PowerShell")
            : null,
        "pwsh" => TryFind("pwsh") is { } pw
            ? new ShellSpec("pwsh", pw, false, "PowerShell 7+")
            : null,
        _ => null,
    };

    private static ShellSpec Fallback(string requested) => ResolveAuto() with
    {
        Note = $"要的「{requested}」在本机不可用，已回落 —— " + ResolveAuto().Note,
    };

    private static bool IsKeyword(string p) =>
        p.Contains(Path.DirectorySeparatorChar) is false
        && p.Contains(Path.AltDirectorySeparatorChar) is false
        && (IsAutoKeyword(p) || p.Equals("bash", StringComparison.OrdinalIgnoreCase)
            || p.Equals("sh", StringComparison.OrdinalIgnoreCase)
            || p.Equals("cmd", StringComparison.OrdinalIgnoreCase)
            || p.Equals("powershell", StringComparison.OrdinalIgnoreCase)
            || p.Equals("pwsh", StringComparison.OrdinalIgnoreCase));

    private static bool IsAutoKeyword(string p) =>
        p.Equals("auto", StringComparison.OrdinalIgnoreCase)
        || p.Equals("posix", StringComparison.OrdinalIgnoreCase);

    private static bool IsPosixId(string id) => id is "bash" or "sh";

    private static string IdFromFileName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.ToLowerInvariant() switch
        {
            "bash" => "bash",
            "sh" => "sh",
            "cmd" => "cmd",
            "powershell" => "powershell",
            "pwsh" => "pwsh",
            _ => name.ToLowerInvariant(),
        };
    }

    private static string? TryFind(string fileName)
    {
        try
        {
            if (Path.IsPathRooted(fileName) && File.Exists(fileName))
            {
                return fileName;
            }
        }
        catch
        {
            // 路径怪异：继续走 PATH
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        var exts = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];

        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in exts)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(dir.Trim(), fileName + ext);
                }
                catch
                {
                    continue;
                }

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
