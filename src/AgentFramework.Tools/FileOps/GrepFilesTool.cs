using System.Text;
using System.Text.RegularExpressions;
using AgentFramework.Contracts;

namespace AgentFramework.Tools.FileOps;

/// <summary>
/// 内容检索（grep）—— 「这行字在哪儿出现过」。
///
/// <para>
/// 补的是最原始的一道缺口：没有它，agent 想找一处代码只能
/// <c>run_command</c> 拼 <c>findstr</c> / <c>grep</c>，各平台语法还不一样。
/// 有了它，「先搜后读」才成为默认动作 —— 而这一步是省下大量盲目 read 的关键。
/// </para>
///
/// <para>
/// 默认按<b>字面量</b>搜（<c>regex=false</c>）：模型与用户搜的绝大多数是普通字符串，
/// 里面带 <c>(</c> <c>[</c> 时正则要转义，默认正则只会制造无谓的失败。
/// </para>
/// </summary>
internal sealed class GrepFilesTool(IWorkspaceService ws) : ITool, IToolWithSchema
{
    private const int HardMaxResults = 200;
    private const int HardMaxContext = 5;

    public string Name => "grep_files";

    public string Description =>
        "在工作区的文件内容里检索。返回「文件:行号: 内容」列表。" +
        "默认按普通字符串匹配（regex=true 时按正则），可用 include 限定文件名模式（如 *.cs），" +
        "用 context_lines 带上匹配行前后的上下文。默认忽略 .git / node_modules / bin / obj 等目录。";

    public string ParametersJsonSchema => """
        {"type":"object","properties":{
          "pattern":{"type":"string","description":"要查找的内容（默认字面量，regex=true 时为正则）"},
          "path":{"type":"string","description":"起点目录或文件，默认工作区根"},
          "include":{"type":"string","description":"文件名 glob 过滤，如 *.cs、**/*.json"},
          "regex":{"type":"boolean","description":"true 时把 pattern 当正则表达式"},
          "ignore_case":{"type":"boolean","description":"忽略大小写"},
          "context_lines":{"type":"integer","description":"每个匹配附近多带几行（0-5）"},
          "max_results":{"type":"integer","description":"最多返回多少处匹配（默认 50，上限 200）"}
        },"required":["pattern"]}
        """;

    public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        var args = invocation.Arguments;
        var pattern = args.Str("pattern");
        if (string.IsNullOrEmpty(pattern))
        {
            return ValueTask.FromResult(ToolResult.Fail("缺少参数 pattern"));
        }

        var start = args.StrOr("path", ".");
        var include = args.Str("include");
        var maxResults = Math.Clamp(args.Int("max_results", 50), 1, HardMaxResults);
        var context = Math.Clamp(args.Int("context_lines", 0), 0, HardMaxContext);
        var ignoreCase = args.Bool("ignore_case");

        if (!ws.TryResolve(start, forWrite: false, out var fullPath, out var error))
        {
            return ValueTask.FromResult(ToolResult.Fail(error!));
        }

        Regex? regex = null;
        if (args.Bool("regex"))
        {
            try
            {
                regex = new Regex(
                    pattern,
                    RegexOptions.CultureInvariant
                        | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None),
                    // 正则来自模型：给一个超时上限，别让一次回溯爆炸把回合卡死
                    TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                return ValueTask.FromResult(ToolResult.Fail($"正则表达式无效：{ex.Message}"));
            }
        }

        var files = ResolveFiles(fullPath, include);
        if (files.Count == 0)
        {
            return ValueTask.FromResult(ToolResult.Ok($"没有可扫描的文件（起点：{start}）"));
        }

        var report = new StringBuilder();
        var hits = 0;
        var scanned = 0;
        var skippedBig = 0;
        var skippedBinary = 0;
        var filesWithHits = new HashSet<string>(StringComparer.Ordinal);
        var truncated = false;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            if (hits >= maxResults)
            {
                truncated = true;
                break;
            }

            FileInfo info;
            try
            {
                info = new FileInfo(file);
                if (info.Length > FileWalker.MaxFileBytes)
                {
                    skippedBig++;
                    continue;
                }
            }
            catch (Exception)
            {
                continue;
            }

            string[] lines;
            try
            {
                var text = File.ReadAllText(file);
                if (FileWalker.LooksBinary(text))
                {
                    skippedBinary++;
                    continue;
                }

                lines = text.Replace("\r\n", "\n").Split('\n');
            }
            catch (Exception)
            {
                continue;
            }

            scanned++;
            var relative = FileWalker.Rel(ws.Root, file);
            var emitted = new HashSet<int>();

            for (var i = 0; i < lines.Length && hits < maxResults; i++)
            {
                bool match;
                try
                {
                    match = regex is null
                        ? lines[i].Contains(pattern, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                        : regex.IsMatch(lines[i]);
                }
                catch (Exception)
                {
                    // 单行正则超时：跳过这行，不炸整次检索
                    continue;
                }

                if (!match)
                {
                    continue;
                }

                hits++;
                filesWithHits.Add(relative);

                var from = Math.Max(1, i + 1 - context);
                var to = Math.Min(lines.Length, i + 1 + context);

                for (var line = from; line <= to; line++)
                {
                    if (!emitted.Add(line))
                    {
                        continue;
                    }

                    var isHit = line == i + 1;
                    var separator = isHit ? ':' : '-';
                    report.Append(relative).Append(separator).Append(line).Append(separator).Append(' ')
                        .Append(lines[line - 1]).Append('\n');
                }
            }
        }

        var header = new StringBuilder();
        header.Append(hits == 0
            ? $"没有匹配「{(pattern.Length > 60 ? pattern[..60] + "…" : pattern)}」（扫了 {scanned} 个文件）"
            : $"匹配 {hits} 处，分布在 {filesWithHits.Count} 个文件（扫了 {scanned} 个文件）");

        if (skippedBig > 0)
        {
            header.Append($"；跳过 {skippedBig} 个超大文件");
        }

        if (skippedBinary > 0)
        {
            header.Append($"；跳过 {skippedBinary} 个二进制文件");
        }

        if (truncated)
        {
            header.Append($"；已达到 {maxResults} 处上限，可能还有更多 —— 请缩小 path/include 或调大 max_results");
        }

        return ValueTask.FromResult(hits == 0
            ? ToolResult.Ok(header.ToString())
            : ToolResult.Ok(ws.Shrink("grep_files", header.Append('\n').Append(report).ToString())));
    }

    private static List<string> ResolveFiles(string fullPath, string? include)
    {
        if (File.Exists(fullPath))
        {
            return [fullPath];
        }

        if (!Directory.Exists(fullPath))
        {
            return [];
        }

        var files = new List<string>();
        foreach (var file in FileWalker.EnumerateFiles(fullPath, 5000))
        {
            if (!string.IsNullOrWhiteSpace(include))
            {
                var relative = Path.GetRelativePath(fullPath, file).Replace('\\', '/');
                if (!Glob.IsMatch(include, relative))
                {
                    continue;
                }
            }

            files.Add(file);
        }

        // 顺序稳定：结果可预期，也便于模型对照前后两次检索
        files.Sort(StringComparer.Ordinal);
        return files;
    }
}
