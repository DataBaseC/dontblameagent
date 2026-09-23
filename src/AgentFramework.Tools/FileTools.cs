using AgentFramework.Contracts;

namespace AgentFramework.Tools;

/// <summary>
/// 工作区内的路径解析与边界检查。
/// 所有文件工具共用 —— 越界访问必须在这一层被拦死，而不是靠每个工具自觉。
/// </summary>
internal static class WorkspacePath
{
    public static bool TryResolve(ToolkitOptions options, string relative, out string fullPath, out string? error)
    {
        fullPath = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(relative))
        {
            error = "路径为空";
            return false;
        }

        var root = Path.GetFullPath(options.EffectiveRoot);

        string candidate;
        try
        {
            candidate = Path.GetFullPath(Path.Combine(root, relative));
        }
        catch (Exception ex)
        {
            error = $"路径非法：{ex.Message}";
            return false;
        }

        // 关键：比较时带上结尾分隔符，否则 /work 会误判 /work-evil 为子路径。
        // v3.5 审查 P2：还要**穿透符号链接**再比 —— 只比字符串前缀的话，
        // 工作区里一个指向外部的链接（或其下的子路径）就能把读写引到区外。
        var realRoot = RealPath(root);
        var realRootWithSeparator = realRoot.EndsWith(Path.DirectorySeparatorChar)
            ? realRoot
            : realRoot + Path.DirectorySeparatorChar;

        if (!RealPath(candidate).StartsWith(realRootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            error = $"路径越出工作区，已拒绝：{relative}";
            return false;
        }

        fullPath = candidate;
        return true;
    }

    /// <summary>
    /// 解析路径的**真实位置**（穿透符号链接）。
    ///
    /// v3.5 审查 P2：对还不存在的路径，解析「最深的已存在祖先」再把剩余尾段接回去 ——
    /// 否则「根/链接/尚未创建的文件」这条最常见的绕过路径会漏检（ResolveLinkTarget 在
    /// 整条路径不存在时解不出来）。平台不支持或解析失败时返回原值，交回前缀检查兜底。
    /// </summary>
    private static string RealPath(string path)
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
                    var target = File.ResolveLinkTarget(current, returnFinalTarget: true);
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

        if (!WorkspacePath.TryResolve(options, path, out var fullPath, out var error))
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

        if (!WorkspacePath.TryResolve(options, path, out var fullPath, out var error))
        {
            return ValueTask.FromResult(ToolResult.Fail(error!));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);

        return ValueTask.FromResult(ToolResult.Ok($"已写入 {path}（{content.Length} 字符）"));
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

        if (!WorkspacePath.TryResolve(options, path, out var fullPath, out var error) && path != ".")
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
