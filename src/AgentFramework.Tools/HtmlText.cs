using System.Net;
using System.Text.RegularExpressions;

namespace AgentFramework.Tools;

/// <summary>
/// 极简 HTML → 纯文本。
///
/// 第一版用内置实现，<b>刻意不引第三方包</b>：目标是让搜索/抓取链路先跑通，
/// 而不是一上来就背上一个正文抽取库。将来若要更准的正文抽取（去掉导航/页脚），
/// 换成 AngleSharp / ReverseMarkdown 即可 —— 调用点只有一处。
/// </summary>
public static partial class HtmlText
{
    public static string ToText(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        var text = html;

        // 1) 去掉脚本与样式（它们不是内容）
        text = ScriptStyleRegex().Replace(text, " ");

        // 1b) 兜底：**未闭合**的 <script>/<style> —— 从它开始一路切到结尾。
        //     上面那条正则要求成对闭合，遇到畸形页面（很多站靠 JS 拼内容、
        //     标签被截断）就整段漏网，于是大段脚本源码被当成正文喂进上下文。
        //     切掉是安全的：标签都未闭合了，后面的「正文」本来也定位不了。
        text = UnclosedScriptRegex().Replace(text, " ");

        // 2) 块级标签变换行，避免整页挤成一行
        text = BlockTagRegex().Replace(text, "\n");
        text = BrRegex().Replace(text, "\n");

        // 3) 去掉其余标签
        text = TagRegex().Replace(text, string.Empty);

        // 4) 实体解码
        text = WebUtility.HtmlDecode(text);

        // 5) 归一化空白
        text = SpacesRegex().Replace(text, " ");
        text = BlankLineRegex().Replace(text, "\n\n");

        return text.Trim();
    }

    /// <summary>从 HTML 中提取 &lt;title&gt;（用于搜索结果展示）。</summary>
    public static string? ExtractTitle(string html)
    {
        var match = TitleRegex().Match(html);
        return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value).Trim() : null;
    }

    [GeneratedRegex(@"<(script|style|noscript)\b[^>]*>.*?</\1\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptStyleRegex();

    /// <summary>未闭合的 &lt;script&gt;/&lt;style&gt;：从这个标签一路切到文末（畸形页面的兜底）。</summary>
    [GeneratedRegex(@"<(script|style)\b[^>]*>[\s\S]*$", RegexOptions.IgnoreCase)]
    private static partial Regex UnclosedScriptRegex();

    [GeneratedRegex(@"</?(p|div|section|article|header|footer|li|tr|h[1-6]|blockquote|pre)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTagRegex();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BrRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"[ \t\u00A0]+")]
    private static partial Regex SpacesRegex();

    [GeneratedRegex(@"\n\s*\n\s*\n+")]
    private static partial Regex BlankLineRegex();

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TitleRegex();
}
