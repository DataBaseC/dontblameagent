using System.Text;
using AgentFramework.Contracts;

namespace AgentFramework.Plugins.DevKit;

/// <summary>
/// 精确替换 —— agent 编辑代码的主工具。
///
/// <para>
/// 为什么必须有它：<c>write_file</c> 是整篇覆盖。改一行要重发整文件，
/// token 花在重复上，风险却落在「没读到的部分被凭印象重写」。
/// 这里以「旧串唯一匹配」为锚点，改哪儿写哪儿。
/// </para>
///
/// <para>
/// <b>唯一性校验是刻意设的坎</b>：旧串出现多次时拒绝执行，而不是猜第一处。
/// 猜错一次要用户花十句话纠正；拒绝一次只要模型补两行上下文。
/// 这是「明确失败优于静默猜错」在编辑场景的落点。
/// </para>
/// </summary>
internal sealed class EditFileTool(IWorkspaceService ws) : ITool, IToolWithSchema
{
    public string Name => "edit_file";

    public string Description =>
        "对工作区内的文本文件做精确替换：把 old_string 改成 new_string。" +
        "old_string 必须与文件内容逐字符一致（含缩进与换行），且通常是唯一的 —— " +
        "若它在文件中出现多次，请扩写上下文使其唯一，或显式传 replace_all=true。";

    public string ParametersJsonSchema => """
        {"type":"object","properties":{
          "path":{"type":"string","description":"相对工作区根的路径"},
          "old_string":{"type":"string","description":"要被替换的原文，需逐字符一致"},
          "new_string":{"type":"string","description":"替换后的新文本；传空字符串表示删除这段"},
          "replace_all":{"type":"boolean","description":"true 时替换全部出现；默认 false（要求唯一）"}
        },"required":["path","old_string","new_string"]}
        """;

    public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        var args = invocation.Arguments;
        var path = args.Str("path");
        if (string.IsNullOrWhiteSpace(path))
        {
            return Fail("缺少参数 path");
        }

        var oldText = args.Str("old_string");
        if (string.IsNullOrEmpty(oldText))
        {
            return Fail("缺少参数 old_string（要替换的原文不能为空）");
        }

        var newText = args.Str("new_string") ?? string.Empty;
        var replaceAll = args.Bool("replace_all");

        // 写：只能落在工作区内
        if (!ws.TryResolve(path, forWrite: true, out var fullPath, out var error))
        {
            return Fail(error!);
        }

        if (!File.Exists(fullPath))
        {
            return Fail($"文件不存在：{path}");
        }

        string text;
        try
        {
            text = File.ReadAllText(fullPath);
        }
        catch (Exception ex)
        {
            return Fail($"读取失败：{ex.Message}");
        }

        // ── 换行符宽容 ────────────────────────────────────────────
        // 模型写 old_string 时常用 \n，而 Windows 上的文件是 \r\n。
        // 直接按原样比会「明明看着一样却匹配不上」，这类失败最消耗来回。
        // 策略：先原样匹配；不中再按文件自身的换行符归一「搜索串与替换串」，
        //       但**替换始终发生在原始文本上** —— 绝不去改写用户没碰过的字节。
        var candidate = (Needs: (string?)null, Replacement: (string?)null);
        var count = CountOccurrences(text, oldText);
        if (count == 0)
        {
            var usesCrlf = text.Contains("\r\n");
            if (usesCrlf && !oldText.Contains("\r\n"))
            {
                candidate = (oldText.Replace("\n", "\r\n"), newText.Replace("\n", "\r\n"));
            }
            else if (!usesCrlf && oldText.Contains("\r\n"))
            {
                candidate = (oldText.Replace("\r\n", "\n"), newText.Replace("\r\n", "\n"));
            }

            if (candidate.Needs is not null)
            {
                count = CountOccurrences(text, candidate.Needs);
            }
        }

        if (count == 0)
        {
            return Fail($"文件里找不到 old_string 的原文（{path}）。请先用 read_file / read_lines 读取该处，" +
                        "把 old_string 连缩进与空行一起复制过来 —— 注意逐字符一致。");
        }

        if (count > 1 && !replaceAll)
        {
            var firstLine = LineOf(text, text.IndexOf(oldText, StringComparison.Ordinal) is var i && i >= 0 ? i : 0);
            return Fail($"old_string 在文件中出现了 {count} 次（首次约在第 {firstLine} 行），" +
                        "无法确定改哪一处。请把上下文写得更长使匹配唯一，或传 replace_all=true 全部替换。");
        }

        var before = text;
        var needle = candidate.Needs ?? oldText;
        var replacement = candidate.Needs is null ? newText : candidate.Replacement!;
        var after = replaceAll
            ? before.Replace(needle, replacement, StringComparison.Ordinal)
            : ReplaceFirst(before, needle, replacement);

        try
        {
            File.WriteAllText(fullPath, after);
        }
        catch (Exception ex)
        {
            return Fail($"写入失败：{ex.Message}");
        }

        var replaced = replaceAll ? count : 1;
        var affectedLine = LineOf(after, after.IndexOf(replacement, StringComparison.Ordinal) is var p && p >= 0 ? p : 0);
        var beforeLines = CountLines(before);
        var afterLines = CountLines(after);

        var report = new StringBuilder();
        report.Append($"已替换 {replaced} 处（首个约在第 {affectedLine} 行）；{path}：{beforeLines} 行 → {afterLines} 行");
        if (candidate.Needs is not null)
        {
            report.Append("\n（old_string 的换行符与文件不一致，已按文件自身的换行符归一后匹配）");
        }

        report.Append('\n').Append(Excerpt(after, affectedLine, CountLines(replacement)));

        return ValueTask.FromResult(ToolResult.Ok(ws.Shrink("edit_file", report.ToString())));
    }

    private static ValueTask<ToolResult> Fail(string error) => ValueTask.FromResult(ToolResult.Fail(error));

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string ReplaceFirst(string text, string needle, string replacement)
    {
        var index = text.IndexOf(needle, StringComparison.Ordinal);
        return index < 0 ? text : string.Concat(text.AsSpan(0, index), replacement, text.AsSpan(index + needle.Length));
    }

    private static int LineOf(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    private static int CountLines(string text) => text.Length == 0 ? 0 : LineOf(text, text.Length - 1);

    /// <summary>改动处附近的行（带行号）—— 让模型立刻看到「改成什么样了」，省掉一次 read。</summary>
    private static string Excerpt(string text, int line, int replacedLines)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var from = Math.Max(1, line - 2);
        var to = Math.Min(lines.Length, line + Math.Max(1, replacedLines) + 1);

        var sb = new StringBuilder();
        for (var i = from; i <= to; i++)
        {
            sb.Append($"  {i,4} | {lines[i - 1]}");
            if (i < to)
            {
                sb.Append('\n');
            }
        }

        return sb.ToString();
    }
}
