using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AgentFramework.Host;

/// <summary>
/// 一次「测试连接 / 拉取模型」的结果。
///
/// <see cref="Kind"/> 存在的意义是**让报错可操作**：
/// 「钥匙不对」「地址不对」「网不通」要给主人完全不同的下一步动作，
/// 混成一句「失败」等于没说。
/// </summary>
public sealed record ModelProbeResult(bool Ok, string Message, IReadOnlyList<string> Models, string Kind)
{
    public static ModelProbeResult Success(IReadOnlyList<string> models)
        => new(true, $"连接正常，拿到 {models.Count} 个模型", models, "ok");

    public static ModelProbeResult Fail(string kind, string message)
        => new(false, message, [], kind);
}

/// <summary>
/// 模型发现 —— 拉一次 <c>GET {baseUrl}/models</c>。
///
/// 为什么「测试连接」就是这件事：它一次同时验证了三样东西 ——
/// 地址对不对、钥不钥得开、端点认不认这套协议。
/// 所以业界（Cline / Cursor）的 Verify 按钮做的也正是这件事。
/// </summary>
public static class ModelDiscovery
{
    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static async Task<ModelProbeResult> ProbeAsync(
        ProviderConfig provider,
        ISecretProtector protector,
        HttpClient? http = null,
        CancellationToken ct = default)
    {
        var baseUrl = (provider.BaseUrl ?? string.Empty).Trim().TrimEnd('/');

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return ModelProbeResult.Fail("config", "还没填端点地址（baseUrl）");
        }

        var client = http ?? Shared;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/models");

            var key = provider.ResolveApiKey(protector);
            if (!string.IsNullOrWhiteSpace(key))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            }

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return ModelProbeResult.Fail("auth", $"认证失败（{(int)response.StatusCode}）：密钥无效，或这个密钥没有权限");
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return ModelProbeResult.Fail("not-found", "地址不对（404）——检查 baseUrl 是否少了 /v1，或该端点不支持列出模型");
            }

            if (!response.IsSuccessStatusCode)
            {
                return ModelProbeResult.Fail("http", $"端点返回 {(int)response.StatusCode}：{Truncate(body, 200)}");
            }

            var models = ParseModels(body);

            return models.Count > 0
                ? ModelProbeResult.Success(models)
                : ModelProbeResult.Fail("bad-response", "连上了，但没解析出模型列表 —— 可以直接手填模型名");
        }
        catch (TaskCanceledException)
        {
            return ModelProbeResult.Fail("network", "连接超时 —— 检查地址与网络");
        }
        catch (HttpRequestException ex)
        {
            return ModelProbeResult.Fail("network", $"连不上：{ex.Message}");
        }
    }

    /// <summary>
    /// 解析 OpenAI 兼容的 <c>{"data":[{"id":"…"}]}</c>；
    /// 顺带容忍几种常见变体（纯数组、字符串数组）—— 各家兼容实现的怪癖不值得让主人去踩。
    /// </summary>
    public static List<string> ParseModels(string body)
    {
        var result = new List<string>();

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data))
            {
                root = data;
            }

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        result.Add(item.GetString()!);
                        continue;
                    }

                    if (item.ValueKind == JsonValueKind.Object
                        && item.TryGetProperty("id", out var id)
                        && id.ValueKind == JsonValueKind.String)
                    {
                        result.Add(id.GetString()!);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // 不是 JSON 就当没解析出东西，交给上层给出「可手填」的提示
        }

        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";
}
