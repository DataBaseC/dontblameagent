using System.Net;
using System.Text.RegularExpressions;

namespace AgentFramework.Tools;

/// <summary>
/// HTML 搜索后端共用的解析小工具：结果块切分、标签剥离、空白归一。
/// 抓回来的只是「标题 + URL + 摘要」三件套，当外部数据清洗 —— 与 web_fetch 同一条纪律。
/// </summary>
internal static partial class HtmlSearchParsing
{
    public static string StripTags(string value)
    {
        var text = TagRegex().Replace(value, " ");
        text = WebUtility.HtmlDecode(text);
        text = text.Replace('\u00a0', ' ');
        return WhitespaceRegex().Replace(text, " ").Trim();
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();
}

/// <summary>
/// Bing 搜索后端（cn.bing.com，免 key）。
///
/// 为什么是 HTML 抓取：Bing 没有免费官方接口，HTML 版对无头请求最宽容、结构也最稳定。
/// 没抓到（页面对抗改版）就抛异常 —— 让降级链换下一个后端，而不是静默返回空。
/// </summary>
public sealed partial class BingSearchProvider : ISearchProvider
{
    private readonly HttpClient _http;

    public BingSearchProvider(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
    }

    public string Name => "bing";

    public async ValueTask<IReadOnlyList<SearchHit>> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        var url = "https://cn.bing.com/search?q=" + Uri.EscapeDataString(query) + "&count=" + Math.Max(maxResults, 10);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // Bing 对无 Accept/语言头的请求会返回简略版或重定向循环 —— 补上最小集合。
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.6");

        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var hits = Parse(html, maxResults);

        if (hits.Count == 0)
        {
            throw new InvalidOperationException("Bing 返回的页面里没有解析到结果（可能是改版或风控页）");
        }

        return hits;
    }

    public static IReadOnlyList<SearchHit> Parse(string html, int maxResults)
    {
        var hits = new List<SearchHit>();

        // Bing 结果块：<li class="b_algo"> … <h2><a href="URL">TITLE</a></h2> … <p>SNIPPET</p> … </li>
        foreach (Match block in BlockRegex().Matches(html))
        {
            if (hits.Count >= maxResults)
            {
                break;
            }

            var text = block.Groups[1].Value;
            var link = LinkRegex().Match(text);
            if (!link.Success)
            {
                continue;
            }

            var href = WebUtility.HtmlDecode(link.Groups[1].Value);
            if (href.StartsWith("/", StringComparison.Ordinal))
            {
                href = "https://cn.bing.com" + href;
            }

            if (!href.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var title = HtmlSearchParsing.StripTags(WebUtility.HtmlDecode(link.Groups[2].Value));
            if (title.Length == 0)
            {
                continue;
            }

            var snippetMatch = SnippetRegex().Match(text);
            var snippet = snippetMatch.Success
                ? HtmlSearchParsing.StripTags(WebUtility.HtmlDecode(snippetMatch.Groups[1].Value))
                : null;

            hits.Add(new SearchHit(href, title, string.IsNullOrEmpty(snippet) ? null : snippet, null));
        }

        return hits;
    }

    // 按「下一个结果块开头」切块，不依赖 </li> 闭合（Bing 的 li 里常嵌套结构，惰性匹配不可靠）。
    [GeneratedRegex("<li class=\"b_algo\"(.*?)(?=<li class=\"b_algo\"|</ol>|</body>|$)", RegexOptions.Singleline)]
    private static partial Regex BlockRegex();

    [GeneratedRegex("<h2[^>]*>\\s*<a[^>]*href=\"([^\"]+)\"[^>]*>(.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex LinkRegex();

    [GeneratedRegex("<p[^>]*>(.*?)</p>", RegexOptions.Singleline)]
    private static partial Regex SnippetRegex();
}

/// <summary>
/// 百度搜索后端（www.baidu.com，免 key）。
/// 结果链接是百度跳转链（/link?url=…）—— 点击可达；抓不到正文摘要就留空，标题足够定位。
/// </summary>
public sealed partial class BaiduSearchProvider : ISearchProvider
{
    private readonly HttpClient _http;

    public BaiduSearchProvider(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
    }

    public string Name => "baidu";

    public async ValueTask<IReadOnlyList<SearchHit>> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        var url = "https://www.baidu.com/s?wd=" + Uri.EscapeDataString(query) + "&rn=" + Math.Max(maxResults, 10);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");

        // 百度对 https 首访会回一个「location.replace(https->http)」的 JS 跳转壳：
        // HttpClient 不会跟 JS 跳转，这里识别后手动跟一次。
        // 注意：先读完 Content 再 Dispose —— response 释放后 Content 就不可读了。
        using (var response = await _http.GetAsync(url, ct).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var firstHtml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (firstHtml.Contains("location.replace", StringComparison.Ordinal) && firstHtml.Length < 4096)
            {
                var httpUrl = url.Replace("https://", "http://", StringComparison.Ordinal);
                using var retry = await _http.GetAsync(httpUrl, ct).ConfigureAwait(false);
                retry.EnsureSuccessStatusCode();
                return Finalize(await retry.Content.ReadAsStringAsync(ct).ConfigureAwait(false), maxResults);
            }

            return Finalize(firstHtml, maxResults);
        }

    }

    private IReadOnlyList<SearchHit> Finalize(string html, int maxResults)
    {
        var hits = Parse(html, maxResults);

        if (hits.Count == 0)
        {
            throw new InvalidOperationException("百度返回了验证页或改版页面（无可用结果）—— 交给降级链换后端");
        }

        return hits;
    }

    public static IReadOnlyList<SearchHit> Parse(string html, int maxResults)
    {
        var hits = new List<SearchHit>();

        // 百度结果标题统一在 <h3 …><a href="…">标题</a></h3> 里；按 h3 切块，向后借一段当摘要。
        var links = LinkRegex().Matches(html);
        foreach (Match link in links)
        {
            if (hits.Count >= maxResults)
            {
                break;
            }

            var href = WebUtility.HtmlDecode(link.Groups[1].Value);
            if (!href.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var title = HtmlSearchParsing.StripTags(WebUtility.HtmlDecode(link.Groups[2].Value));
            if (title.Length == 0)
            {
                continue;
            }

            // 摘要：从这条 h3 之后截一段页面文本找 <p>/abstract 容器；找不到就留空。
            var tailStart = link.Index + link.Length;
            var tail = html.Substring(Math.Min(tailStart, html.Length), Math.Min(2500, Math.Max(0, html.Length - tailStart)));
            var snippetMatch = SnippetRegex().Match(tail);
            var snippet = snippetMatch.Success
                ? HtmlSearchParsing.StripTags(WebUtility.HtmlDecode(snippetMatch.Value))
                : string.Empty;

            hits.Add(new SearchHit(href, title, string.IsNullOrEmpty(snippet) ? null : snippet, null));
        }

        return hits;
    }

    [GeneratedRegex("<h3[^>]*>\\s*<a[^>]*href=\"([^\"]+)\"[^>]*>(.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex LinkRegex();

    [GeneratedRegex("<(?:p|span|div)[^>]*(?:class=\"[^\"]*(?:abstract|content-right|c-)[^\"]*\"|)[^>]*>(.*?)</(?:p|span|div)>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex SnippetRegex();
}
