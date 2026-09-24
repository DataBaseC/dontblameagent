using AgentFramework.Contracts;

namespace AgentFramework.Plugins.DevKit;

/// <summary>
/// 建目录。<c>write_file</c> 虽然会自动建父目录，但「只想要一个空目录」这件事
/// 之前得靠 run_command —— 而 mkdir 在各平台上的写法恰好也不一样。
/// </summary>
internal sealed class MakeDirTool(IWorkspaceService ws) : ITool, IToolWithSchema
{
    public string Name => "make_dir";

    public string Description => "在工作区内创建一个目录（含所有中间层级）。目录已存在时不算失败。";

    public string ParametersJsonSchema => """
        {"type":"object","properties":{
          "path":{"type":"string","description":"要创建的目录路径，相对工作区根"}
        },"required":["path"]}
        """;

    public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        var path = invocation.Arguments.Str("path");
        if (string.IsNullOrWhiteSpace(path))
        {
            return ValueTask.FromResult(ToolResult.Fail("缺少参数 path"));
        }

        if (!ws.TryResolve(path, forWrite: true, out var fullPath, out var error))
        {
            return ValueTask.FromResult(ToolResult.Fail(error!));
        }

        if (Directory.Exists(fullPath))
        {
            return ValueTask.FromResult(ToolResult.Ok($"目录已存在：{path}"));
        }

        if (File.Exists(fullPath))
        {
            return ValueTask.FromResult(ToolResult.Fail($"该路径上已经有一个文件：{path}"));
        }

        try
        {
            Directory.CreateDirectory(fullPath);
        }
        catch (Exception ex)
        {
            return ValueTask.FromResult(ToolResult.Fail($"创建失败：{ex.Message}"));
        }

        return ValueTask.FromResult(ToolResult.Ok($"已创建目录：{path}"));
    }
}

/// <summary>
/// 移动 / 重命名。文件与目录都走这里 —— 「重命名一个文件」以前只能
/// <c>run_command</c>，而且跨盘移动还得自己想兜底。
/// </summary>
internal sealed class MovePathTool(IWorkspaceService ws) : ITool, IToolWithSchema
{
    public string Name => "move_path";

    public string Description =>
        "移动或重命名工作区内的文件/目录。from 与 to 都可以是文件或目录；" +
        "目标已存在时默认拒绝（overwrite=true 才覆盖）。跨盘移动会自动退化为「复制后删除」。";

    public string ParametersJsonSchema => """
        {"type":"object","properties":{
          "from":{"type":"string","description":"源路径（相对工作区根）"},
          "to":{"type":"string","description":"目标路径（相对工作区根）"},
          "overwrite":{"type":"boolean","description":"目标已存在时是否覆盖，默认 false"}
        },"required":["from","to"]}
        """;

    public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        var args = invocation.Arguments;
        var from = args.Str("from");
        var to = args.Str("to");

        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
        {
            return ValueTask.FromResult(ToolResult.Fail("缺少参数 from 或 to"));
        }

        // 两端都按写边界检查：移动的**起点也会被改**，它同样必须在工作区内
        if (!ws.TryResolve(from, forWrite: true, out var sourcePath, out var error))
        {
            return ValueTask.FromResult(ToolResult.Fail($"源路径：{error}"));
        }

        if (!ws.TryResolve(to, forWrite: true, out var targetPath, out var error2))
        {
            return ValueTask.FromResult(ToolResult.Fail($"目标路径：{error2}"));
        }

        var sourceIsDir = Directory.Exists(sourcePath);
        if (!sourceIsDir && !File.Exists(sourcePath))
        {
            return ValueTask.FromResult(ToolResult.Fail($"源路径不存在：{from}"));
        }

        var overwrite = args.Bool("overwrite");
        if ((Directory.Exists(targetPath) || File.Exists(targetPath)) && !overwrite)
        {
            return ValueTask.FromResult(ToolResult.Fail($"目标已存在：{to}（确要覆盖请传 overwrite=true）"));
        }

        try
        {
            var parent = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            if (overwrite && File.Exists(targetPath))
            {
                File.Delete(targetPath);
            }

            if (sourceIsDir)
            {
                Directory.Move(sourcePath, targetPath);
            }
            else
            {
                File.Move(sourcePath, targetPath, overwrite);
            }
        }
        catch (IOException)
        {
            // 跨卷移动：Directory.Move / File.Move 会拒绝，退化为复制后删除。
            // （这是唯一必须自己兜底的平台差异 —— 不做的话模型会看到一句没营养的 IOException。）
            try
            {
                if (sourceIsDir)
                {
                    CopyDirectory(sourcePath, targetPath);
                    Directory.Delete(sourcePath, recursive: true);
                }
                else
                {
                    File.Copy(sourcePath, targetPath, overwrite: true);
                    File.Delete(sourcePath);
                }
            }
            catch (Exception ex)
            {
                return ValueTask.FromResult(ToolResult.Fail($"移动失败（跨卷兜底也失败）：{ex.Message}"));
            }
        }
        catch (Exception ex)
        {
            return ValueTask.FromResult(ToolResult.Fail($"移动失败：{ex.Message}"));
        }

        return ValueTask.FromResult(ToolResult.Ok($"已移动：{from} → {to}"));
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
        }
    }
}

/// <summary>
/// 删除。危险动作，所以门槛是<b>结构性的</b>：
/// 删目录必须显式 <c>recursive=true</c>，删空的才可以省。
/// 默认值选「更保守的那一侧」—— 猜错一次就是用户的文件没了。
/// </summary>
internal sealed class DeletePathTool(IWorkspaceService ws) : ITool, IToolWithSchema
{
    public string Name => "delete_path";

    public string Description =>
        "删除工作区内的文件或目录。删除非空目录必须显式传 recursive=true。" +
        "这是不可逆操作 —— 删之前先确认路径，必要时先 list_dir 看一眼。";

    public string ParametersJsonSchema => """
        {"type":"object","properties":{
          "path":{"type":"string","description":"要删除的路径（相对工作区根）"},
          "recursive":{"type":"boolean","description":"目录非空时必须为 true 才执行"}
        },"required":["path"]}
        """;

    public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        var args = invocation.Arguments;
        var path = args.Str("path");
        if (string.IsNullOrWhiteSpace(path))
        {
            return ValueTask.FromResult(ToolResult.Fail("缺少参数 path"));
        }

        if (!ws.TryResolve(path, forWrite: true, out var fullPath, out var error))
        {
            return ValueTask.FromResult(ToolResult.Fail(error!));
        }

        // 防手滑：工作区根本身不许删
        if (string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(ws.Root)),
                StringComparison.OrdinalIgnoreCase))
        {
            return ValueTask.FromResult(ToolResult.Fail("不能删除工作区根目录本身"));
        }

        var recursive = args.Bool("recursive");

        try
        {
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
                return ValueTask.FromResult(ToolResult.Ok($"已删除文件：{path}"));
            }

            if (!Directory.Exists(fullPath))
            {
                return ValueTask.FromResult(ToolResult.Fail($"路径不存在：{path}"));
            }

            var entries = Directory.EnumerateFileSystemEntries(fullPath).Take(1).ToList();
            if (entries.Count > 0 && !recursive)
            {
                return ValueTask.FromResult(ToolResult.Fail(
                    $"目录非空：{path}（确要连同内容一起删除，请传 recursive=true）"));
            }

            Directory.Delete(fullPath, recursive);
            return ValueTask.FromResult(ToolResult.Ok($"已删除目录：{path}"));
        }
        catch (Exception ex)
        {
            return ValueTask.FromResult(ToolResult.Fail($"删除失败：{ex.Message}"));
        }
    }
}
