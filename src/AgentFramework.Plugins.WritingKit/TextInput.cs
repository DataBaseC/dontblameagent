namespace AgentFramework.Plugins.WritingKit;

/// <summary>
/// 入参读取（参数一律以字符串到达，这里收干净类型差异）。
/// </summary>
internal static class ToolArgs
{
    public static string? Str(this IReadOnlyDictionary<string, string?> args, string key)
        => args.TryGetValue(key, out var value) ? value : null;

    public static string StrOr(this IReadOnlyDictionary<string, string?> args, string key, string fallback)
        => args.Str(key) is { Length: > 0 } value ? value : fallback;

    public static bool Bool(this IReadOnlyDictionary<string, string?> args, string key, bool fallback = false)
        => args.Str(key)?.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "y" or "on" => true,
            "false" or "0" or "no" or "n" or "off" => false,
            _ => fallback,
        };

    public static int Int(this IReadOnlyDictionary<string, string?> args, string key, int fallback)
        => int.TryParse(args.Str(key)?.Trim(), out var value) ? value : fallback;
}

/// <summary>
/// 待分析文本的来源：<c>text</c>（直接给）或 <c>path</c>（读工作区文件）。
/// 五个工具共用 —— 免得每个工具各写一遍「先看 text 再看 path」。
/// </summary>
internal static class TextInput
{
    public static bool TryRead(
        Contracts.IWorkspaceService workspace,
        IReadOnlyDictionary<string, string?> args,
        out string text,
        out string? error)
    {
        text = string.Empty;
        error = null;

        var inline = args.Str("text");
        if (!string.IsNullOrEmpty(inline))
        {
            text = inline;
            return true;
        }

        var path = args.Str("path");
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "需要提供 text（直接给内容）或 path（文件路径）之一";
            return false;
        }

        if (!workspace.TryResolve(path, forWrite: false, out var fullPath, out var resolveError))
        {
            error = resolveError;
            return false;
        }

        if (!File.Exists(fullPath))
        {
            error = $"文件不存在：{path}";
            return false;
        }

        try
        {
            text = File.ReadAllText(fullPath);
        }
        catch (Exception ex)
        {
            error = $"读取失败：{ex.Message}";
            return false;
        }

        return true;
    }
}

/// <summary>
/// 中文文本的公共口径。全部工具都用这一份 —— 否则「字数」在五个工具里有五种算法。
/// </summary>
internal static class ChineseText
{
    /// <summary>汉字（基本区 + 扩展 A）。</summary>
    public static bool IsHan(char c) => (c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3400 && c <= 0x4DBF);

    /// <summary>中文全角标点（写作体检要区别对待的那批）。</summary>
    public const string ChinesePunctuation = "，。、；：？！“”‘’（）《》〈〉【】「」『』—…·～";

    public static bool IsChinesePunctuation(char c) => ChinesePunctuation.Contains(c);

    public const string LatinPunctuation = ",.;:?!()[]{}'\"<>-";

    public static bool IsLatinPunctuation(char c) => LatinPunctuation.Contains(c);

    /// <summary>按换行切行（统一按 \n 口径）。</summary>
    public static string[] Lines(string text) => text.Replace("\r\n", "\n").Split('\n');

    /// <summary>非空行即段落 —— 中文写作的基本习惯是一段一行（比「空行分段」更贴合实际）。</summary>
    public static string[] Paragraphs(string text)
        => [.. Lines(text).Select(l => l.Trim()).Where(l => l.Length > 0)];

    /// <summary>按句末标点切句（保留标点，去掉空白句）。</summary>
    public static string[] Sentences(string text)
    {
        var result = new List<string>();
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is '。' or '！' or '？' or '…' or '!' or '?' or '.')
            {
                // 省略号/多标点连写时，合并成一个句末
                var end = i;
                while (end + 1 < text.Length && text[end + 1] is '。' or '！' or '？' or '…' or '!' or '?' or '.')
                {
                    end++;
                }

                var sentence = text[start..(end + 1)].Trim();
                if (sentence.Length > 0)
                {
                    result.Add(sentence);
                }

                start = end + 1;
                i = end;
            }
        }

        var tail = text[start..].Trim();
        if (tail.Length > 0)
        {
            result.Add(tail);
        }

        return [.. result];
    }

    /// <summary>连续汉字序列（重复词检测的输入：去掉标点与拉丁字母后的骨架）。</summary>
    public static string HanSequence(string text)
        => string.Concat(text.Where(IsHan));

    public static int HanCount(string text) => text.Count(IsHan);

    /// <summary>首句：到第一个句末标点为止（超长时截断，用于提纲）。</summary>
    public static string FirstSentence(string paragraph)
    {
        var sentences = Sentences(paragraph);
        var first = sentences.Length > 0 ? sentences[0] : paragraph;
        return first.Length > 60 ? first[..60] + "…" : first;
    }
}
