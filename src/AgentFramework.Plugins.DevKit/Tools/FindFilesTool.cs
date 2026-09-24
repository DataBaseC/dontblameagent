using System.Text;
using AgentFramework.Contracts;

namespace AgentFramework.Plugins.DevKit;

/// <summary>
/// 按名字找文件（glob）。
///
/// <para>
/// 与 grep 是<b>两条不同的路</b>：grep 答「这行字在哪儿」，find 答「叫这名的东西在哪儿」。
/// 只有 grep 的 agent 想列一批文件时只能 <c>run_command</c> 拼 <c>dir /s</c> / <c>find</c>，
/// 输出格式还随平台变。
/// </para>
///
/// <para>
/// 模式语义取 <c>.gitignore</c> 的口径：<c>*</c> 段内、<c>**</c> 跨段。
/// <c>*.cs</c> 这种不带 '/' 的写法按「任意目录下」处理 —— 那是绝大多数的真实意图。
/// </para>
/// </summary>
internal sealed class FindFilesTool(IWorkspaceService ws) : ITool, IToolWithSchema
{
    private const int HardMaxResults = 500;

    public string Name => "find_files";

    public string Description =>
        "按文件名模式（glob）查找文件，返回相对路径列表。" +
        "支持 * （段内任意）、** （跨目录）、? （单个字符），例如 *.cs、src/**/*.json。" +
        "默认忽略 .git / node_modules / bin / obj 等目录。";

    public string ParametersJsonSchema => """
        {"type":"object","properties":{
          "pattern":{"type":"string","description":"文件名模式，如 *.md、**/*.test.ts"},
          "path":{"type":"string","description":"起点目录，默认工作区根"},
          "max_results":{"type":"integer","description":"最多返回多少个（默认 100，上限 500）"}
        },"required":["pattern"]}
        """;

    public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        var args = invocation.Arguments;
        var pattern = args.Str("pattern");
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return ValueTask.FromResult(ToolResult.Fail("缺少参数 pattern"));
        }

        var start = args.StrOr("path", ".");
        var maxResults = Math.Clamp(args.Int("max_results", 100), 1, HardMaxResults);

        if (!ws.TryResolve(start, forWrite: false, out var fullPath, out var error))
        {
            return ValueTask.FromResult(ToolResult.Fail(error!));
        }

        if (!Directory.Exists(fullPath))
        {
            return ValueTask.FromResult(ToolResult.Fail(File.Exists(fullPath)
                ? $"起点是文件，find_files 需要目录：{start}"
                : $"目录不存在：{start}"));
        }

        var matches = new List<(string Relative, long Size)>();
        var truncated = false;

        foreach (var file in FileWalker.EnumerateFiles(fullPath, 20_000))
        {
            ct.ThrowIfCancellationRequested();

            var relative = FileWalker.Rel(fullPath, file);
            if (!Glob.IsMatch(pattern, relative))
            {
                continue;
            }

            if (matches.Count >= maxResults)
            {
                truncated = true;
                break;
            }

            long size = 0;
            try
            {
                size = new FileInfo(file).Length;
            }
            catch (Exception)
            {
                // 拿不到大小不影响「找到了」这个事实
            }

            matches.Add((relative, size));
        }

        matches.Sort((a, b) => string.CompareOrdinal(a.Relative, b.Relative));

        if (matches.Count == 0)
        {
            return ValueTask.FromResult(ToolResult.Ok(
                $"没有匹配「{pattern}」的文件（起点：{start}）。注意模式是相对起点的 glob，不要带盘符。"));
        }

        var report = new StringBuilder();
        report.Append($"匹配 {matches.Count} 个文件{(truncated ? $"（已达到 {maxResults} 上限，可能还有更多）" : string.Empty)}\n");

        foreach (var (relative, size) in matches)
        {
            report.Append(relative).Append("  (").Append(Human(size)).Append(")\n");
        }

        return ValueTask.FromResult(ToolResult.Ok(ws.Shrink("find_files", report.ToString())));
    }

    private static string Human(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB",
    };
}
