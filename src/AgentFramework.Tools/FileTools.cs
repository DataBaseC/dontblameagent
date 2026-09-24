using AgentFramework.Contracts;

namespace AgentFramework.Tools;

/// <summary>
/// 工作区内的路径解析与边界检查。
/// 所有文件工具共用 —— 越界访问必须在这一层被拦死，而不是靠每个工具自觉。
/// </summary>
internal static class WorkspacePath
{
    /// <summary>
    /// 路径前缀比较规则：Windows 文件系统大小写不敏感，用 OrdinalIgnoreCase；
    /// 其余（Linux 等大小写敏感文件系统）必须用 Ordinal ——
    /// 否则 /work 会误判 /Work/evil 为子路径，写边界在大小写敏感平台上被整段绕过。
    /// </summary>
    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() || Path.DirectorySeparatorChar == '\\'
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <summary>
    /// 解析路径并做边界检查。
    ///
    /// <para>
    /// <b>读与写的边界刻意不一样</b>：写**永远**只能落在会话的工作区内；
    /// 读默认放行到整台机器（见 <see cref="ToolkitOptions.AllowReadOutsideWorkspace"/>）。
    /// 「读得到、写不出去」才是本地 personal agent 想要的形状 ——
    /// 模型能翻任意位置的资料，但落笔只在项目里。
    /// </para>
    /// <para>
    /// 绝对路径直接用、相对路径相对工作区 —— 两种写法都认。
    /// </para>
    /// <para>
    /// <b>写返回的是穿透符号链接后的最终路径</b>（不是调用方传入的 candidate）：
    /// 校验与落盘用同一条真实路径，校验到写之间 junction/符号链接被换掉，
    /// 也不会把内容写到区外 —— TOCTOU 窗口从结构上关掉。
    /// </para>
    /// </summary>
    public static bool TryResolve(ToolkitOptions options, string path, bool forWrite, out string fullPath, out string? error)
    {
        fullPath = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "路径为空";
            return false;
        }

        var root = Path.GetFullPath(options.EffectiveRoot);

        string candidate;
        try
        {
            // 绝对路径直接用；相对路径仍相对工作区。两种写法都支持，
            // 于是「读区外」既可以是 /etc/hosts，也可以是 ../shared/x.txt。
            candidate = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(root, path));
        }
        catch (Exception ex)
        {
            error = $"路径非法：{ex.Message}";
            return false;
        }

        // 读：默认不受工作区约束（AllowReadOutsideWorkspace=false 时收回，走下面的边界检查）。
        if (!forWrite && options.AllowReadOutsideWorkspace)
        {
            fullPath = candidate;
            return true;
        }

        // 写（以及被收回的读）：必须**真实**落在工作区内 ——
        // 比较时带上结尾分隔符，否则 /work 会误判 /work-evil 为子路径。
        // v3.5 审查 P2：还要**穿透符号链接**再比 —— 只比字符串前缀的话，
        // 工作区里一个指向外部的链接（或其下的子路径）就能把写引到区外。
        var realRoot = RealPath(root);
        var realCandidate = RealPath(candidate);

        if (!IsInsideRoot(realCandidate, realRoot))
        {
            error = forWrite
                ? $"路径越出工作区（写只能落在会话项目目录内），已拒绝：{path}"
                : $"路径越出工作区，已拒绝：{path}";
            return false;
        }

        // ★ 写必须落盘到**已解析穿透后的最终路径**（realCandidate）：
        //   若仍用 candidate，校验通过后目录/链接被换成指向区外的 junction，
        //   File.WriteAllText 会顺着新链接写到区外 —— 这就是写边界的 TOCTOU。
        //   改成写 realCandidate 后，即便链接在中途被换，内容也只会落在校验过的那条真实路径上。
        fullPath = forWrite ? realCandidate : candidate;
        return true;
    }

    /// <summary>
    /// 判断 <paramref name="realPath"/> 是否落在 <paramref name="realRoot"/> 之内。
    /// 前缀比较带上结尾分隔符，并按平台选择大小写规则（见 <see cref="PathComparison"/>）。
    /// </summary>
    private static bool IsInsideRoot(string realPath, string realRoot)
    {
        var rootWithSeparator = realRoot.EndsWith(Path.DirectorySeparatorChar)
            ? realRoot
            : realRoot + Path.DirectorySeparatorChar;

        return realPath.StartsWith(rootWithSeparator, PathComparison);
    }

    /// <summary>
    /// 解析路径的**真实位置**（穿透符号链接）。
    ///
    /// v3.5 审查 P2：对还不存在的路径，解析「最深的已存在祖先」再把剩余尾段接回去 ——
    /// 否则「根/链接/尚未创建的文件」这条最常见的绕过路径会漏检（ResolveLinkTarget 在
    /// 整条路径不存在时解不出来）。平台不支持或解析失败时返回原值，交回前缀检查兜底。
    /// </summary>
    internal static string RealPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var rootPart = Path.GetPathRoot(full) ?? string.Empty;
            var current = rootPart;

            // ★ 逐段解析：**中间目录也可能是链接** —— 只解析最后一级会漏掉
            //   「链接目录/真实文件」这种最常见的形态（第一版就栽在这里，
            //   写成 34 通过 / 1 失败，测试当场抓住）。
            foreach (var segment in full[rootPart.Length..]
                .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);

                try
                {
                    // 目录链接与文件链接都要解：File.ResolveLinkTarget 在部分平台上
                    // 对目录链接无效，因此两条 API 都试，谁解出来用谁。
                    var target = File.ResolveLinkTarget(current, returnFinalTarget: true)
                        ?? Directory.ResolveLinkTarget(current, returnFinalTarget: true);
                    if (target is not null)
                    {
                        current = target.FullName;
                    }
                }
                catch (Exception)
                {
                    // 该段不存在 —— 没有链接可解，保留原值继续拼后面的段
                }
            }

            return current;
        }
        catch (Exception)
        {
            return path;
        }
    }
}

public sealed class ReadFileTool(ToolkitOptions options) : ITool, IToolWithSchema
{
    public string Name => "read_file";

    public string Description => "读取工作区内的文本文件内容。";

    public string ParametersJsonSchema =>
        """{"type":"object","properties":{"path":{"type":"string","description":"相对工作区根目录的路径"}},"required":["path"]}""";

    public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        if (!invocation.Arguments.TryGetValue("path", out var path) || string.IsNullOrWhiteSpace(path))
        {
            return ValueTask.FromResult(ToolResult.Fail("缺少参数 path"));
        }

        // 读：forWrite=false —— 默认允许越出工作区（「读得到、写不出去」）。
        if (!WorkspacePath.TryResolve(options, path, forWrite: false, out var fullPath, out var error))
        {
            return ValueTask.FromResult(ToolResult.Fail(error!));
        }

        if (!File.Exists(fullPath))
        {
            return ValueTask.FromResult(ToolResult.Fail($"文件不存在：{path}"));
        }

        var text = File.ReadAllText(fullPath);
        if (text.Length > options.MaxReadChars)
        {
            text = string.Concat(text.AsSpan(0, options.MaxReadChars), "\n...[内容已截断]");
        }

        // L2：大文件不该整段躺在上下文里被反复重发 —— 落盘，只留摘要 + 路径 + 头尾
        return ValueTask.FromResult(ToolResult.Ok(options.ShrinkResult("read_file", text)));
    }
}

public sealed class WriteFileTool(ToolkitOptions options) : ITool, IToolWithSchema
{
    public string Name => "write_file";

    public string Description => "把内容写入工作区内的文件，会自动创建父目录并覆盖已有文件。";

    public string ParametersJsonSchema =>
        """{"type":"object","properties":{"path":{"type":"string","description":"相对工作区根目录的路径"},"content":{"type":"string","description":"要写入的完整内容"}},"required":["path","content"]}""";

    public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        if (!invocation.Arguments.TryGetValue("path", out var path) || string.IsNullOrWhiteSpace(path))
        {
            return ValueTask.FromResult(ToolResult.Fail("缺少参数 path"));
        }

        if (!invocation.Arguments.TryGetValue("content", out var content))
        {
            return ValueTask.FromResult(ToolResult.Fail("缺少参数 content"));
        }

        content ??= string.Empty;

        if (content.Length > options.MaxWriteChars)
        {
            return ValueTask.FromResult(ToolResult.Fail($"内容过长：{content.Length} > {options.MaxWriteChars}"));
        }

        // 写：forWrite=true —— 落笔只能落在会话项目目录内。
        // TryResolve 对写返回的是**穿透链接后的真实落点**，下面必须用它落盘，
        // 不能退回原始 candidate（否则 junction 中途被换就能写到区外）。
        if (!WorkspacePath.TryResolve(options, path, forWrite: true, out var fullPath, out var error))
        {
            return ValueTask.FromResult(ToolResult.Fail(error!));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);

        // 回显真实落点：让「经符号链接写入」的最终位置可审计（也方便测试钉住落盘路径）。
        return ValueTask.FromResult(ToolResult.Ok($"已写入 {path}（真实落点：{fullPath}，{content.Length} 字符）"));
    }
}

public sealed class ListDirTool(ToolkitOptions options) : ITool, IToolWithSchema
{
    public string Name => "list_dir";

    public string Description => "列出工作区内某个目录下的条目。";

    public string ParametersJsonSchema =>
        """{"type":"object","properties":{"path":{"type":"string","description":"相对路径；留空表示工作区根目录"}}}""";

    public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        invocation.Arguments.TryGetValue("path", out var path);
        path = string.IsNullOrWhiteSpace(path) ? "." : path;

        // 列目录与读同一条边界（默认允许越出工作区）。
        if (!WorkspacePath.TryResolve(options, path, forWrite: false, out var fullPath, out var error) && path != ".")
        {
            return ValueTask.FromResult(ToolResult.Fail(error!));
        }

        if (path == ".")
        {
            fullPath = Path.GetFullPath(options.EffectiveRoot);
        }

        if (!Directory.Exists(fullPath))
        {
            return ValueTask.FromResult(ToolResult.Fail($"目录不存在：{path}"));
        }

        var lines = Directory
            .EnumerateFileSystemEntries(fullPath)
            .Select(entry =>
            {
                var isDirectory = Directory.Exists(entry);
                var name = Path.GetFileName(entry);
                var size = isDirectory ? 0 : new FileInfo(entry).Length;
                return isDirectory ? $"[dir]  {name}/" : $"[file] {name}  ({size} 字节)";
            })
            .OrderBy(line => line, StringComparer.Ordinal)
            .ToList();

        return ValueTask.FromResult(lines.Count == 0
            ? ToolResult.Ok("（空目录）")
            : ToolResult.Ok(string.Join('\n', lines)));
    }
}
