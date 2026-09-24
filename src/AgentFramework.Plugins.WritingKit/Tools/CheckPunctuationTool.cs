using System.Text;
using AgentFramework.Contracts;

namespace AgentFramework.Plugins.WritingKit;

/// <summary>
/// 标点体检：中英标点混用、重复标点、汉字间空格。
///
/// <para>
/// 这三类问题在中文写作里高频且刺眼，但模型校对时几乎必然漏掉 ——
/// 它读的是「意思」，不是「字符」。所以这里逐字符看，宁可报得笨一点。
/// </para>
///
/// <para>
/// 只报确定的问题，不做风格建议：<c>！！</c> <c>……</c> 这类连写是有意为之，
/// 会误报的规则不如不报 —— 误报多了，用户就不看这份体检了。
/// </para>
/// </summary>
internal sealed class CheckPunctuationTool(IWorkspaceService ws) : ITool, IToolWithSchema
{
    public string Name => "check_punctuation";

    public string Description =>
        "标点检查：同一句里中文标点与英文标点混用、重复标点（。。，，）、汉字之间夹半角空格。返回问题清单（带行号）。";

    public string ParametersJsonSchema => """
        {"type":"object","properties":{
          "text":{"type":"string","description":"要检查的文本"},
          "path":{"type":"string","description":"或给出工作区内的文件路径（与 text 二选一）"},
          "max_issues":{"type":"integer","description":"最多列出多少条，默认 20"}
        }}
        """;

    public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        var args = invocation.Arguments;
        if (!TextInput.TryRead(ws, args, out var text, out var error))
        {
            return ValueTask.FromResult(ToolResult.Fail(error!));
        }

        var maxIssues = Math.Clamp(args.Int("max_issues", 20), 1, 100);
        var lines = ChineseText.Lines(text);
        var issues = new List<string>();
        var truncated = false;

        for (var i = 0; i < lines.Length && !truncated; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0)
            {
                continue;
            }

            // 1) 同句混用（句内既用中文标点又用英文标点）
            foreach (var sentence in ChineseText.Sentences(line))
            {
                var hasChinese = sentence.Any(ChineseText.IsChinesePunctuation);
                var hasLatin = sentence.Any(c => c is ',' or ';' or ':' or '!' or '?');
                if (hasChinese && hasLatin)
                {
                    issues.Add($"第 {i + 1} 行 标点混用：{Clip(sentence)}");
                    if (issues.Count >= maxIssues) { truncated = true; break; }
                }
            }

            if (truncated)
            {
                break;
            }

            // 2) 重复标点（只报「不会是故意」的那几种）
            for (var j = 1; j < line.Length; j++)
            {
                var prev = line[j - 1];
                var cur = line[j];
                if (prev == cur && prev is '。' or '，' or '、' or '；' or '：')
                {
                    issues.Add($"第 {i + 1} 行 重复标点：{Clip(line, j)}");
                    if (issues.Count >= maxIssues) { truncated = true; break; }
                }
            }

            if (truncated)
            {
                break;
            }

            // 3) 汉字之间夹半角空格（「中 文」这种，多半是输入法/复制来的）
            for (var j = 1; j + 1 < line.Length; j++)
            {
                if (line[j] == ' ' && ChineseText.IsHan(line[j - 1]) && ChineseText.IsHan(line[j + 1]))
                {
                    issues.Add($"第 {i + 1} 行 汉字间空格：{Clip(line, j)}");
                    if (issues.Count >= maxIssues) { truncated = true; break; }
                }
            }
        }

        var report = new StringBuilder();
        if (issues.Count == 0)
        {
            report.Append("标点检查：没有发现问题（中英标点混用 / 重复标点 / 汉字间空格）");
        }
        else
        {
            report.Append($"── 标点体检：{issues.Count} 处{(truncated ? "（已达上限，可能还有更多）" : string.Empty)} ──\n");
            foreach (var issue in issues)
            {
                report.Append("  ").Append(issue).Append('\n');
            }
        }

        return ValueTask.FromResult(ToolResult.Ok(ws.Shrink("check_punctuation", report.ToString())));
    }

    private static string Clip(string text, int at = -1)
    {
        const int Window = 24;

        if (at < 0)
        {
            return text.Length <= Window * 2 ? text : text[..(Window * 2)] + "…";
        }

        var from = Math.Max(0, at - Window);
        var to = Math.Min(text.Length, at + Window);
        var head = from > 0 ? "…" : string.Empty;
        var tail = to < text.Length ? "…" : string.Empty;
        return $"{head}{text[from..to]}{tail}";
    }
}
