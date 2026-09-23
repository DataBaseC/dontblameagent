using System.Text.Json;

namespace AgentFramework.Tools;

/// <summary>一条搜索结果 —— 只带 URL / 标题 / 摘要，<b>不含正文</b>（正文要靠 web_fetch）。</summary>
public sealed record SearchHit(string Url, string Title, string? Snippet, string? PublishedAt);

/// <summary>
/// 搜索后端 seam。
/// 换一个后端就换一种检索来源，而工具、主循环、提示词全都不用动。
/// </summary>
public interface ISearchProvider
{
    string Name { get; }

    ValueTask<IReadOnlyList<SearchHit>> SearchAsync(string query, int maxResults, CancellationToken ct);
}

/// <summary>
/// SearXNG —— 默认后端。
/// 免 key、可自建，真正符合"本地优先"；代价是结果质量取决于实例。
/// </summary>
public sealed class SearxngSearchProvider : ISearchProvider
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public SearxngSearchProvider(string baseUrl, HttpClient? http = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = http ?? new HttpClient();
    }

    public string Name => "searxng";

    public async ValueTask<IReadOnlyList<SearchHit>> SearchAsync(
        string query,
        int maxResults,
        CancellationToken ct)
    {
        var url = $"{_baseUrl}/search?q={Uri.EscapeDataString(query)}&format=json&safesearch=1";

        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        var hits = new List<SearchHit>();

        if (!doc.RootElement.TryGetProperty("results", out var results))
        {
            return hits;
        }

        foreach (var item in results.EnumerateArray())
        {
            if (hits.Count >= maxResults)
            {
                break;
            }

            hits.Add(new SearchHit(
                ReadString(item, "url") ?? string.Empty,
                ReadString(item, "title") ?? string.Empty,
                ReadString(item, "content"),
                ReadString(item, "publishedDate")));
        }

        return hits;
    }

    private static string? ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>
/// 降级链：按顺序尝试各后端，第一个拿到非空结果的胜出。
/// 记录尝试过程 —— 静默返回空是最糟的失败方式，必须留下痕迹。
/// </summary>
public sealed class FallbackSearchProvider : ISearchProvider
{
    private readonly IReadOnlyList<ISearchProvider> _providers;
    private readonly List<string> _attemptLog = [];

    public FallbackSearchProvider(params ISearchProvider[] providers) => _providers = providers;

    public string Name => "fallback(" + string.Join(",", _providers.Select(p => p.Name)) + ")";

    public IReadOnlyList<string> AttemptLog
    {
        get
        {
            lock (_attemptLog)
            {
                return [.. _attemptLog];
            }
        }
    }

    public async ValueTask<IReadOnlyList<SearchHit>> SearchAsync(
        string query,
        int maxResults,
        CancellationToken ct)
    {
        // 按次产出：这次的记录只属于这次查询。
        // 老实现是累积的 —— 越滚越长，还把不同查询的失败混在一锅，
        // 读日志的人反而看不出「这一次到底试了谁、谁挂了」。
        lock (_attemptLog)
        {
            _attemptLog.Clear();
        }

        foreach (var provider in _providers)
        {
            try
            {
                var hits = await provider.SearchAsync(query, maxResults, ct).ConfigureAwait(false);
                lock (_attemptLog)
                {
                    _attemptLog.Add($"{provider.Name}: ok({hits.Count})");
                }

                if (hits.Count > 0)
                {
                    return hits;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lock (_attemptLog)
                {
                    _attemptLog.Add($"{provider.Name}: {ex.GetType().Name}");
                }
            }
        }

        lock (_attemptLog)
        {
            _attemptLog.Add("所有后端均未返回结果");
        }

        return [];
    }
}
