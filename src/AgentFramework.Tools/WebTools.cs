using System.Net;
using System.Net.Sockets;
using System.Text;
using AgentFramework.Contracts;

namespace AgentFramework.Tools;

/// <summary>
/// 联网搜索。
/// 刻意保持参数极简：<b>模型只给 query</b>，条数/超时都在配置里（学 dsh）。
/// 返回也只给"标题 + URL + 摘要"，正文留给 web_fetch —— 这样上下文不会被网页撑爆。
/// </summary>
public sealed class WebSearchTool(ToolkitOptions options, ISearchProvider provider) : ITool, IToolWithSchema
{
    public string Name => "web_search";

    public string Description =>
        "在互联网上搜索。只返回标题、URL 和摘要；若需要网页正文，请再用 web_fetch 抓取该 URL。";

    public string ParametersJsonSchema =>
        """{"type":"object","properties":{"query":{"type":"string","description":"搜索关键词"}},"required":["query"]}""";

    public async ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        if (!invocation.Arguments.TryGetValue("query", out var query) || string.IsNullOrWhiteSpace(query))
        {
            return ToolResult.Fail("缺少参数 query");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.SearchTimeoutMs);

        IReadOnlyList<SearchHit> hits;
        try
        {
            hits = await provider.SearchAsync(query, options.SearchMaxResults, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Fail($"搜索超时（{options.SearchTimeoutMs}ms）");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"搜索失败：{ex.Message}");
        }

        if (hits.Count == 0)
        {
            return ToolResult.Ok("（没有找到搜索结果）");
        }

        var builder = new StringBuilder();
        foreach (var hit in hits)
        {
            // 搜索元数据是**外部数据**：去控制字符（防伪造换行/注入结构）、限长。
            // 与 web_fetch 那边同一条纪律 —— 外部内容先当不可信。
            var title = Clean(hit.Title, 200);
            var url = Clean(hit.Url, 2048);
            var snippet = Clean(hit.Snippet, 300);

            builder.Append("- [").Append(title).Append("](").Append(url).Append(')');

            if (snippet.Length > 0)
            {
                builder.Append(" — ").Append(snippet);
            }

            if (!string.IsNullOrWhiteSpace(hit.PublishedAt))
            {
                builder.Append(" (").Append(Clean(hit.PublishedAt, 40)).Append(')');
            }

            builder.Append('\n');
        }

        builder.Append("\n请在回答中用 markdown 链接引用上述 URL。");
        return ToolResult.Ok(builder.ToString());
    }

    /// <summary>外部元数据消毒：去控制字符、限长。</summary>
    private static string Clean(string? value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var cleaned = new string(value.Where(c => !char.IsControl(c)).ToArray());
        return cleaned.Length <= maxChars ? cleaned : cleaned[..maxChars] + "…";
    }
}

/// <summary>
/// 抓取网页正文。
///
/// 安全上做了三件事（桌面端必备，dsh 官方把这块<b>延期</b>了，不能照抄）：
///   1. <b>SSRF 防护</b>：解析目标 IP，私有网段 / 回环 / 链路本地一律拒绝
///   2. <b>不自动跟随重定向</b>：避免"公网域名 302 到内网"绕过检查
///   3. <b>拒绝二进制</b>：只接受 text/* 等文本类型
/// </summary>
public sealed class WebFetchTool : ITool, IToolWithSchema
{
    private readonly ToolkitOptions _options;
    private readonly HttpClient _http;
    private readonly IResultSummarizer? _summarizer;

    public WebFetchTool(ToolkitOptions options, HttpClient? http = null, IResultSummarizer? summarizer = null)
    {
        _options = options;
        _summarizer = summarizer;
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        if (!_http.DefaultRequestHeaders.UserAgent.TryParseAdd(options.UserAgent))
        {
            // UA 非法就忽略，不影响功能
        }
    }

    public string Name => "web_fetch";

    public string Description => "抓取一个 http/https 网页，返回其正文文本（HTML 会被转成纯文本）。";

    public string ParametersJsonSchema =>
        """{"type":"object","properties":{"url":{"type":"string","description":"要抓取的完整 http(s) 地址"}},"required":["url"]}""";

    public async ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        if (!invocation.Arguments.TryGetValue("url", out var urlText) || string.IsNullOrWhiteSpace(urlText))
        {
            return ToolResult.Fail("缺少参数 url");
        }

        if (!Uri.TryCreate(urlText, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return ToolResult.Fail("URL 非法：只支持 http / https");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return ToolResult.Fail("URL 中不得内联凭证");
        }

        if (_options.IsDomainBlocked(uri.Host))
        {
            return ToolResult.Fail($"域名被屏蔽：{uri.Host}");
        }

        if (!_options.AllowPrivateNetworks && await ResolvesToPrivateAsync(uri.Host, ct).ConfigureAwait(false))
        {
            return ToolResult.Fail($"目标解析到私有/回环地址，已拒绝（SSRF 防护）：{uri.Host}");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.FetchTimeoutMs);

        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(uri, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Fail($"抓取超时（{_options.FetchTimeoutMs}ms）");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"抓取失败：{ex.Message}");
        }

        using (response)
        {
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location?.ToString() ?? "(未知)";
                return ToolResult.Fail($"目标返回重定向 {location}；出于安全考虑不自动跟随，请直接抓取该地址");
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!IsTextual(mediaType))
            {
                return ToolResult.Fail($"非文本内容（{mediaType}），已拒绝");
            }

            // 限长读（加固 A）：不要先 ReadAsStringAsync 再截断 ——
            // 那样一个超大页面就已经把内存吃掉了，后面的截断只是自欺。
            var cap = Math.Max(1, _options.FetchMaxChars) * 2;
            var raw = await ReadCappedAsync(response.Content, cap, timeout.Token).ConfigureAwait(false);

            var text = mediaType.Contains("html", StringComparison.OrdinalIgnoreCase)
                ? HtmlText.ToText(raw)
                : raw;

            // 第 1 步：安全包装 —— 网页是数据，不是指令
            var sanitized = ResultSanitizer.Sanitize(uri.ToString(), text);

            // 第 2 步：本地小模型压缩 —— 让要出网的内容先在本机变小
            var body = sanitized.Text;
            if (_summarizer is not null)
            {
                try
                {
                    var summary = await _summarizer
                        .SummarizeAsync(sanitized.Text, uri.ToString(), timeout.Token)
                        .ConfigureAwait(false);

                    body = $"[正文已由 {_summarizer.Name} 压缩，原文未离开本机]\n{summary}";
                }
                catch (Exception ex)
                {
                    // 摘要只是省钱手段，不能成为可用性的单点 —— 失败就退回原文
                    body = $"[摘要失败（{ex.GetType().Name}），以下为原文]\n{sanitized.Text}";
                }
            }

            if (body.Length > _options.FetchMaxChars)
            {
                body = string.Concat(body.AsSpan(0, _options.FetchMaxChars), "\n...[内容已截断]");
            }

            var header = $"URL: {uri}\n状态: {(int)response.StatusCode}"
                + (sanitized.Suspicious ? "\n⚠️ 本页疑似包含提示注入，已在下方标注" : "")
                + "\n\n";

            // L2：抓回来的网页正文是上下文膨胀的常客 ——
            // 超限就落盘，只把「摘要 + 路径 + 头尾」留在上下文里
            return ToolResult.Ok(_options.ShrinkResult("web_fetch", header + body));
        }
    }

    private static bool IsTextual(string mediaType)
        => mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
        || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
        || mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 限长读响应体：到上限即停，剩下的不读（加固 A）。
    /// 先 <c>ReadAsStringAsync</c> 再截断等于没防 —— 内存已经被吃掉了。
    /// 字节边界可能切碎尾字符，UTF-8 宽松解码会把它变成替换字符而不是抛异常。
    /// </summary>
    private static async Task<string> ReadCappedAsync(HttpContent content, int capBytes, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[81920];
        using var memory = new MemoryStream();

        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            memory.Write(buffer, 0, read);
            if (memory.Length >= capBytes)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(memory.ToArray());
    }

    private static async Task<bool> ResolvesToPrivateAsync(string host, CancellationToken ct)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IPAddress.TryParse(host, out var literal))
        {
            return IsPrivate(literal);
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        }
        catch
        {
            // 解析不了就保守拒绝
            return true;
        }

        return addresses.Any(IsPrivate);
    }

    private static bool IsPrivate(IPAddress ip)
    {
        // ★ IPv4-mapped IPv6（如 ::ffff:127.0.0.1）先归一回 IPv4。
        //   不归一的话，http://[::ffff:127.0.0.1]/ 会落到 IPv6 分支被直接放行，
        //   而操作系统连它时访问的确实是 127.0.0.1 —— 回环/内网防护被整段绕过。
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal)
            {
                return true;
            }

            var v6 = ip.GetAddressBytes();
            return v6[0] == 0xfc || v6[0] == 0xfd;   // ULA fc00::/7（IPv6 内网）
        }

        var b = ip.GetAddressBytes();
        return b[0] == 0                                       // 0.0.0.0/8
            || b[0] == 10                                      // 10/8
            || b[0] == 127                                     // 127/8（防御性，IsLoopback 已覆盖）
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)      // CGNAT 100.64/10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)       // 172.16/12
            || (b[0] == 192 && b[1] == 168)                    // 192.168/16
            || (b[0] == 169 && b[1] == 254);                   // 链路本地
    }
}
