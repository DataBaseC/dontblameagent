using System.Text;
using AgentFramework.Contracts;

namespace AgentFramework.Plugins.WritingKit;

/// <summary>
/// 口头禅检测（重复用词）。
///
/// <para>
/// 这是写作者最需要、又最难自查的一项：同一个词连着用三遍，读的人立刻出戏，
/// 写的人毫无察觉。模型的「自觉」在这里完全不可靠 —— 它连自己上一段写了什么都数不清。
/// </para>
///
/// <para>
/// 做法：把正文抽成纯汉字序列（去掉标点与字母，避免把「的，我」当短语），
/// 统计 2–4 字窗口的出现次数，超过阈值的按次数排出来。
/// </para>
/// </summary>
internal sealed class RepeatWordsTool(IWorkspaceService ws) : ITool, IToolWithSchema
{
    public string Name => "repeat_words";

    public string Description =>
        "检测正文里反复出现的词或短语（2-4 字），用于发现口头禅与用词单一。" +
        "返回出现次数最多的若干条。默认阈值 3 次，只看长度 2-3 字。";

    public string ParametersJsonSchema => """
        {"type":"object","properties":{
          "text":{"type":"string","description":"要分析的文本"},
          "path":{"type":"string","description":"或给出工作区内的文件路径（与 text 二选一）"},
          "min_count":{"type":"integer","description":"至少出现几次才算重复，默认 3"},
          "max_n":{"type":"integer","description":"最长统计几个字，默认 3（可选 2-4）"},
          "top":{"type":"integer","description":"最多列出多少条，默认 15"}
        }}
        """;

    public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        var args = invocation.Arguments;
        if (!TextInput.TryRead(ws, args, out var text, out var error))
        {
            return ValueTask.FromResult(ToolResult.Fail(error!));
        }

        var minCount = Math.Clamp(args.Int("min_count", 3), 2, 50);
        var maxN = Math.Clamp(args.Int("max_n", 3), 2, 4);
        var top = Math.Clamp(args.Int("top", 15), 1, 50);

        var sequence = ChineseText.HanSequence(text);
        if (sequence.Length < 8)
        {
            return ValueTask.FromResult(ToolResult.Ok("（正文太短，不做重复词统计）"));
        }

        var counters = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var n = 2; n <= maxN; n++)
        {
            for (var i = 0; i + n <= sequence.Length; i++)
            {
                var gram = sequence.Substring(i, n);
                counters[gram] = counters.TryGetValue(gram, out var c) ? c + 1 : 1;
            }
        }

        var hits = counters
            .Where(kv => kv.Value >= minCount)
            // 长短语里的短词会自动重复出现：同次数的优先报长的（信息量更大）
            .Where(kv => !counters.Any(other =>
                other.Key.Length > kv.Key.Length
                && other.Value >= kv.Value
                && other.Key.Contains(kv.Key, StringComparison.Ordinal)))
            .OrderByDescending(kv => kv.Value)
            .ThenByDescending(kv => kv.Key.Length)
            .Take(top)
            .ToList();

        if (hits.Count == 0)
        {
            return ValueTask.FromResult(ToolResult.Ok(
                $"没有出现 {minCount} 次以上的重复词（正文共 {sequence.Length} 个汉字）—— 用词是干净的"));
        }

        var report = new StringBuilder();
        report.Append($"── 重复用词（{sequence.Length} 个汉字里，出现 ≥{minCount} 次的片段）──\n");
        foreach (var (gram, count) in hits)
        {
            report.Append($"  {gram}  ×{count}\n");
        }

        report.Append("提示：出现次数多不等于有问题 —— 承上启下的连接词本就该重复。");
        report.Append("真正要盯的是「本想换说法却懒得换」的那种。");

        return ValueTask.FromResult(ToolResult.Ok(ws.Shrink("repeat_words", report.ToString())));
    }
}

/// <summary>
/// 提纲抽取：每段首句。
/// 用于「先看骨架再决定怎么改」——长文改稿最怕通读一遍才发现结构不对。
/// </summary>
internal sealed class OutlineTool(IWorkspaceService ws) : ITool, IToolWithSchema
{
    public string Name => "outline";

    public string Description =>
        "抽取文本骨架：逐段取首句拼成提纲，附带每段字数。用于长文快速通览与结构检查。";

    public string ParametersJsonSchema => """
        {"type":"object","properties":{
          "text":{"type":"string","description":"要抽取的文本"},
          "path":{"type":"string","description":"或给出工作区内的文件路径（与 text 二选一）"},
          "max_paragraphs":{"type":"integer","description":"最多列出多少段，默认 50"}
        }}
        """;

    public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        var args = invocation.Arguments;
        if (!TextInput.TryRead(ws, args, out var text, out var error))
        {
            return ValueTask.FromResult(ToolResult.Fail(error!));
        }

        var paragraphs = ChineseText.Paragraphs(text);
        if (paragraphs.Length == 0)
        {
            return ValueTask.FromResult(ToolResult.Ok("（没有正文）"));
        }

        var max = Math.Clamp(args.Int("max_paragraphs", 50), 1, 300);
        var report = new StringBuilder();
        report.Append($"── 提纲（共 {paragraphs.Length} 段）──\n");

        for (var i = 0; i < Math.Min(paragraphs.Length, max); i++)
        {
            report.Append($"{i + 1,3}. （{ChineseText.HanCount(paragraphs[i])} 字）{ChineseText.FirstSentence(paragraphs[i])}\n");
        }

        if (paragraphs.Length > max)
        {
            report.Append($"… 还有 {paragraphs.Length - max} 段未列出\n");
        }

        return ValueTask.FromResult(ToolResult.Ok(ws.Shrink("outline", report.ToString())));
    }
}
