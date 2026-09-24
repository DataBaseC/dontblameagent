using System.Text;
using System.Text.RegularExpressions;
using AgentFramework.Contracts;

namespace AgentFramework.Plugins.WritingKit;

/// <summary>
/// 字数统计。
///
/// <para>
/// 中文字数的口径最容易出岔子：Word 的「字数」、编辑器的「字符数」、
/// 模型的「目测」三者从不一致。这里把口径写死并逐项列出，
/// 让「还差多少字」这种要求有唯一答案，而不是各说各话。
/// </para>
/// </summary>
internal sealed class WordCountTool(IWorkspaceService ws) : ITool, IToolWithSchema
{
    private static readonly Regex LatinWord = new("[A-Za-z][A-Za-z'’-]*", RegexOptions.Compiled);
    private static readonly Regex NumberRun = new(@"\d+(?:\.\d+)?", RegexOptions.Compiled);

    public string Name => "word_count";

    public string Description =>
        "统计文本字数。给出汉字数、英文单词数、数字串数、中文标点数、总字符数与不含空白字符数。" +
        "用于核对「写够 X 字」这类要求 —— 不要靠目测。";

    public string ParametersJsonSchema => """
        {"type":"object","properties":{
          "text":{"type":"string","description":"要统计的文本"},
          "path":{"type":"string","description":"或给出工作区内的文件路径（与 text 二选一）"},
          "detail":{"type":"boolean","description":"true 时逐段列出字数，默认 false"}
        }}
        """;

    public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        if (!TextInput.TryRead(ws, invocation.Arguments, out var text, out var error))
        {
            return ValueTask.FromResult(ToolResult.Fail(error!));
        }

        var lines = ChineseText.Lines(text);
        var paragraphs = ChineseText.Paragraphs(text);
        var han = ChineseText.HanCount(text);
        var latinWords = LatinWord.Matches(text).Count;
        var numbers = NumberRun.Matches(text).Count;
        var chinesePunctuation = text.Count(ChineseText.IsChinesePunctuation);
        var noWhitespace = text.Count(c => !char.IsWhiteSpace(c));

        var report = new StringBuilder();
        report.Append("── 字数统计 ──\n");
        report.Append($"汉字数      ：{han}");
        if (latinWords > 0 || numbers > 0)
        {
            report.Append($"（另含英文单词 {latinWords} 个、数字 {numbers} 个）");
        }

        report.Append($"\n中文标点    ：{chinesePunctuation}\n");
        report.Append($"总字符数    ：{text.Length}\n");
        report.Append($"不含空白字符：{noWhitespace}\n");
        report.Append($"行数        ：{lines.Length}（其中非空 {paragraphs.Length} 行）");
        report.Append($"\n常说的「字数」：约 {han + latinWords + numbers}");

        if (invocation.Arguments.Bool("detail") && paragraphs.Length > 0)
        {
            report.Append("\n\n── 逐段 ──\n");
            for (var i = 0; i < paragraphs.Length; i++)
            {
                report.Append($"{i + 1}. {ChineseText.HanCount(paragraphs[i])} 字  {ChineseText.FirstSentence(paragraphs[i])}\n");
            }
        }

        return ValueTask.FromResult(ToolResult.Ok(ws.Shrink("word_count", report.ToString())));
    }
}

/// <summary>
/// 段落体检：段落长短是否失衡、有没有一整段几十字不分句、对话占多少。
/// 这些是「读起来累」的常见结构性原因，模型很难靠感觉发现。
/// </summary>
internal sealed class ParagraphReportTool(IWorkspaceService ws) : ITool, IToolWithSchema
{
    public string Name => "paragraph_report";

    public string Description =>
        "段落结构体检：段落数、最长/最短段（带首句）、平均段长、句长分布、对话段占比。" +
        "用于发现「大段堆砌」「全是短句」这类节奏问题。";

    public string ParametersJsonSchema => """
        {"type":"object","properties":{
          "text":{"type":"string","description":"要分析的文本"},
          "path":{"type":"string","description":"或给出工作区内的文件路径（与 text 二选一）"}
        }}
        """;

    public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        if (!TextInput.TryRead(ws, invocation.Arguments, out var text, out var error))
        {
            return ValueTask.FromResult(ToolResult.Fail(error!));
        }

        var paragraphs = ChineseText.Paragraphs(text);
        if (paragraphs.Length == 0)
        {
            return ValueTask.FromResult(ToolResult.Ok("（没有可分析的正文：文本为空）"));
        }

        var lengths = paragraphs.Select(ChineseText.HanCount).ToArray();
        var longest = Array.IndexOf(lengths, lengths.Max());
        var shortest = Array.IndexOf(lengths, lengths.Min());
        var dialogues = paragraphs.Count(p => p.StartsWith('“') || p.StartsWith('「') || p.StartsWith('"'));
        var sentences = ChineseText.Sentences(text);

        var report = new StringBuilder();
        report.Append("── 段落体检 ──\n");
        report.Append($"段落数      ：{paragraphs.Length}\n");
        report.Append($"平均段长    ：{lengths.Average():0.#} 字\n");
        report.Append($"最长段      ：第 {longest + 1} 段，{lengths[longest]} 字 → {ChineseText.FirstSentence(paragraphs[longest])}\n");
        report.Append($"最短段      ：第 {shortest + 1} 段，{lengths[shortest]} 字 → {ChineseText.FirstSentence(paragraphs[shortest])}\n");
        report.Append($"句数        ：{sentences.Length}");
        if (sentences.Length > 0)
        {
            report.Append($"（平均句长 {sentences.Average(s => ChineseText.HanCount(s)):0.#} 字）");
        }

        report.Append($"\n对话段      ：{dialogues} 段（占 {dialogues * 100.0 / paragraphs.Length:0.#}%）\n");

        var longOnes = lengths.Select((len, i) => (len, i)).Where(x => x.len > 200).Take(3).ToList();
        if (longOnes.Count > 0)
        {
            report.Append("偏长的段    ：");
            report.Append(string.Join("、", longOnes.Select(x => $"第 {x.i + 1} 段（{x.len} 字）")));
            report.Append(" —— 超过 200 字的段落读起来容易断气，考虑拆分\n");
        }

        return ValueTask.FromResult(ToolResult.Ok(ws.Shrink("paragraph_report", report.ToString())));
    }
}
