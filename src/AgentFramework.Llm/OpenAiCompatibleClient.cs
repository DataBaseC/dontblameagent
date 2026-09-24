using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;
using AgentFramework.Contracts;

namespace AgentFramework.Llm;

public sealed class OpenAiCompatibleOptions
{
    /// <summary>OpenAI 兼容端点。LM Studio 默认是 http://localhost:1234/v1</summary>
    public string BaseUrl { get; set; } = "https://api.deepseek.com/v1";

    public string ApiKey { get; set; } = "";

    public string DefaultModel { get; set; } = "deepseek-chat";

    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// 思考参数风格（见 ReasoningStyles）：openai = 顶层 reasoning_effort；
    /// qwen = 末条 user 消息带 enable_thinking/thinking_budget；none = 不注入。
    /// </summary>
    public string ReasoningStyle { get; set; } = ReasoningStyles.None;

    /// <summary>这个端点的默认思考强度（off/low/medium/high；空 = 端点默认）。</summary>
    public string ReasoningEffort { get; set; } = "";
}

/// <summary>思考参数风格常量（配置与比较都用）。</summary>
public static class ReasoningStyles
{
    public const string None = "none";
    public const string OpenAi = "openai";
    public const string Qwen = "qwen";
}

/// <summary>
/// OpenAI 兼容协议的客户端。
///
/// 关键事实：云端（DeepSeek 等）与本地 LM Studio <b>都是这套协议</b>，
/// 所以一个实现通吃，只是 BaseUrl / ApiKey / Model 三个字段不同 ——
/// 这正是「联网 + 本地混合路由」成本很低的原因。
/// </summary>
public sealed class OpenAiCompatibleClient : ILlmClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly OpenAiCompatibleOptions _options;

    public OpenAiCompatibleClient(string name, OpenAiCompatibleOptions options, HttpClient? http = null)
    {
        Name = name;
        _options = options;

        // v3.5 审查 P1-2：HttpClient.Timeout 是**整段流**的时限 ——
        // 长回复必然被硬截断，而流式请求真正的边界是「两帧之间不能停太久」，不是「整段不能超时」。
        // 所以总时限设为无限，另用 options.Timeout 做**帧间空闲超时**（见下方 ReadLineAsync）。
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>
    /// 把 Bearer 头挂在**单次请求**上，不改 <c>DefaultRequestHeaders</c> ——
    /// 注入的 HttpClient 可能是共享实例，改默认头会污染同一进程里其他端点的请求。
    /// </summary>
    private void ApplyAuth(HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(_options.ApiKey))
        {
            request.Headers.Remove("Authorization");
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _options.ApiKey);
        }
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
    public string Name { get; }

    public async IAsyncEnumerable<LlmStreamChunk> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var payload = BuildPayload(request);
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        // ★ ResponseHeadersRead：绝不缓冲响应体 ——
        // 默认的 ResponseContentRead 会等到**整段流**结束才让 await 返回，
        // SSE 的逐 token 增量全被憋在内存里，流式就退化成「最后一次性吐出」（v3.5 审查 P1-2）。
        // 走 SendAsync + HttpRequestMessage：这个重载最早出现、各处引用程序集都稳。
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"{_options.BaseUrl.TrimEnd('/')}/chat/completions")
        {
            Content = content,
        };
        ApplyAuth(httpRequest);

        using var response = await _http
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"LLM 调用失败 {(int)response.StatusCode}：{Truncate(body, 400)}");
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var toolCalls = new Dictionary<string, ToolCallAccumulator>(StringComparer.Ordinal);
        var finishReason = "stop";
        LlmUsage? usage = null;
        string? servedModel = null;

        // 坏帧计数：本地端点偶发残帧不该炸掉整轮，但连续大量坏帧必须断流（否则把问题藏起来）
        var badFrames = 0;
        const int maxBadFrames = 16;
        const int maxLineChars = 1_000_000;

        while (true)
        {
            string? line;
            try
            {
                // 帧间空闲超时（P1-2）：总时限已放开，这里守住「端点卡住」的情形 ——
                // 两帧之间超过 options.Timeout 没数据即判定无响应。
                line = await reader.ReadLineAsync(ct)
                    .AsTask()
                    .WaitAsync(_options.Timeout, ct)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new InvalidOperationException(
                    $"LLM 流空闲超过 {_options.Timeout.TotalSeconds:0} 秒，已中断本次流（端点无响应）");
            }

            if (line is null)
            {
                break;
            }

            if (line.Length > maxLineChars)
            {
                throw new InvalidOperationException(
                    $"SSE 单行超过 {maxLineChars} 字符，疑似端点异常，已中断本次流");
            }

            if (line.Length == 0 || !line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line[5..].Trim();
            if (data == "[DONE]")
            {
                break;
            }

            // ★ 坏帧跳过（P0-1）：别处一律「增强项失败就降级」，模型通道不该反而是零容忍。
            //   但连续坏帧过多即断流 —— 无限吞垃圾只会把问题藏得更深。
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(data);
            }
            catch (JsonException)
            {
                if (++badFrames > maxBadFrames)
                {
                    throw new InvalidOperationException(
                        $"连续收到 {badFrames} 个无法解析的 SSE 帧，已中断本次流（端点协议异常）");
                }

                continue;
            }

            // 审查 P2：这里计的是**连续**坏帧 —— 成功解析一帧即清零。
            // 否则长会话里零散出现的坏帧会一路累积，攒到 16 个就被误判为
            // 「端点协议异常」而断流（却根本不是连续的）。
            badFrames = 0;

            using (doc)
            {
                var root = doc.RootElement;

                if (root.TryGetProperty("model", out var modelNode) && modelNode.ValueKind == JsonValueKind.String)
                {
                    servedModel = modelNode.GetString();
                }

                // ★ usage 必须在 choices 判断**之前**读。
                //   两家的形态不一样：OpenAI 会额外发一个 choices 为空的收尾块来携带 usage，
                //   而 DeepSeek 是把它挂在**最后一个内容块**上（choices 非空）。
                //   所以不能写成「遇到空 choices 才读 usage」—— 那样 DeepSeek 永远读不到。
                //   两家唯一的共同点：**最后出现的那个非 null usage 才是最终账目**。
                if (root.TryGetProperty("usage", out var usageNode) && usageNode.ValueKind == JsonValueKind.Object)
                {
                    usage = ParseUsage(usageNode);
                }

                if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                {
                    continue;
                }

                var choice = choices[0];

                if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                {
                    finishReason = fr.GetString() ?? "stop";
                }

                if (!choice.TryGetProperty("delta", out var delta))
                {
                    continue;
                }

                // 思考：单独一条通道（DeepSeek 的 reasoning_content）。不进上下文，只给界面看。
                if (delta.TryGetProperty("reasoning_content", out var reasoningNode)
                    && reasoningNode.ValueKind == JsonValueKind.String)
                {
                    var reasoning = reasoningNode.GetString();
                    if (!string.IsNullOrEmpty(reasoning))
                    {
                        yield return new LlmStreamChunk.ReasoningDelta(reasoning);
                    }
                }

                // 文本：真流式吐出去
                if (delta.TryGetProperty("content", out var textNode) && textNode.ValueKind == JsonValueKind.String)
                {
                    var text = textNode.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        yield return new LlmStreamChunk.TextDelta(text);
                    }
                }

                // 工具调用：协议上是分片到达的（id/name/arguments 各来一点），必须累积
                if (delta.TryGetProperty("tool_calls", out var toolCallsNode) && toolCallsNode.ValueKind == JsonValueKind.Array)
                {
                    foreach (var call in toolCallsNode.EnumerateArray())
                    {
                        // 兼容不发 index 的端点（P2-11）：没有 index 就退回 id 做键，
                        // 否则多个并行调用会被并进同一个累积器，参数互相串味。
                        var key = ToolCallKey(call);

                        if (!toolCalls.TryGetValue(key, out var accumulator))
                        {
                            accumulator = new ToolCallAccumulator();
                            toolCalls[key] = accumulator;
                        }

                        if (call.TryGetProperty("id", out var idNode) && idNode.ValueKind == JsonValueKind.String)
                        {
                            accumulator.Id = idNode.GetString();
                        }

                        if (call.TryGetProperty("function", out var fn))
                        {
                            if (fn.TryGetProperty("name", out var nameNode) && nameNode.ValueKind == JsonValueKind.String)
                            {
                                // 名称协议上通常一次给全；个别端点每帧重发全名，用 += 会拼成
                                // read_fileread_file → 工具永远「不存在」。按片段语义合并而不是盲拼。
                                AccumulateName(accumulator, nameNode.GetString());
                            }

                            if (fn.TryGetProperty("arguments", out var argsNode) && argsNode.ValueKind == JsonValueKind.String)
                            {
                                accumulator.Arguments.Append(argsNode.GetString());
                            }
                        }
                    }
                }
            }
        }

        if (toolCalls.Count > 0)
        {
            // 纯数字键按数值序（协议里 index 本就是序号），非数字键排在后面按字典序
            var calls = toolCalls
                .OrderBy(kv => int.TryParse(kv.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                    ? n
                    : int.MaxValue)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new ToolCallRequest(
                    kv.Value.Id ?? $"call_{kv.Key}",
                    kv.Value.Name ?? string.Empty,
                    kv.Value.Arguments.ToString()))
                .ToList();

            yield return new LlmStreamChunk.ToolCallsReady(calls);
        }

        // 账目在最后一次性给出 —— 「这次调用花了多少」在过程中没有意义。
        // 流被中断时 usage 可能压根收不到，那就如实不发，上层显示「未知」。
        if (usage is not null)
        {
            yield return new LlmStreamChunk.UsageReady(usage, servedModel);
        }

        yield return new LlmStreamChunk.Completed(finishReason);
    }

    /// <summary>
    /// 合并流式工具名：首段直接记；全名重发不重复拼接；真后缀片段才追加。
    /// </summary>
    private static void AccumulateName(ToolCallAccumulator accumulator, string? part)
    {
        if (string.IsNullOrEmpty(part))
        {
            return;
        }

        if (string.IsNullOrEmpty(accumulator.Name))
        {
            accumulator.Name = part;
            return;
        }

        // 整名重发（相等 / 新值更长且以前缀覆盖）—— 以前值为准，不追加
        if (string.Equals(accumulator.Name, part, StringComparison.Ordinal))
        {
            return;
        }

        if (part.StartsWith(accumulator.Name, StringComparison.Ordinal))
        {
            accumulator.Name = part;
            return;
        }

        if (accumulator.Name.StartsWith(part, StringComparison.Ordinal))
        {
            return;
        }

        // 真·分片（少见）：只在尾段衔接时追加，避免拼重
        if (!accumulator.Name.EndsWith(part, StringComparison.Ordinal))
        {
            accumulator.Name += part;
        }
    }

    /// <summary>
    /// 取一次增量工具调用的累积键。
    /// 协议里应当是 index；有的端点不发，那就退回 id，最后退回 "0"（单调用场景）。
    /// </summary>
    private static string ToolCallKey(JsonElement call)
    {
        if (call.TryGetProperty("index", out var idx) && idx.ValueKind == JsonValueKind.Number)
        {
            return idx.GetInt32().ToString(CultureInfo.InvariantCulture);
        }

        if (call.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
        {
            return id.GetString() ?? "0";
        }

        return "0";
    }

    /// <summary>
    /// 按端点风格注入思考参数。请求显式给了档位就听请求的；
    /// 否则用端点默认（options.ReasoningEffort）。off 会显式关（Qwen 风格才有「关」的语义）。
    /// </summary>
    private void ApplyReasoningOptions(JsonObject payload, JsonArray messages, LlmRequest request)
    {
        var effort = request.ReasoningEffort ?? _options.ReasoningEffort;
        if (string.IsNullOrWhiteSpace(effort))
        {
            return;
        }

        effort = effort.Trim().ToLowerInvariant();
        if (_options.ReasoningStyle.Equals(ReasoningStyles.OpenAi, StringComparison.OrdinalIgnoreCase))
        {
            // OpenAI 系：请求体顶层 reasoning_effort（low/medium/high）
            payload["reasoning_effort"] = effort;
        }
        else if (_options.ReasoningStyle.Equals(ReasoningStyles.Qwen, StringComparison.OrdinalIgnoreCase))
        {
            // Qwen 系：最后一条 user 消息尾部拼控制标记（enable_thinking / thinking_budget）
            var enable = effort != "off";
            var budget = effort switch
            {
                "low" => 1024,
                "high" => 16384,
                _ => 8192,
            };
            var marker = $"<enable_thinking>{(enable ? "true" : "false")}</enable_thinking><thinking_budget>{budget}</thinking_budget>";
            for (var i = messages.Count - 1; i >= 0; i--)
            {
                var node = messages[i];
                if (node is not null && node["role"]?.ToString() == "user" && node["content"] is not null)
                {
                    node["content"] = node["content"]!.GetValue<string>() + marker;
                    break;
                }
            }
        }
        else if (effort != "off")
        {
            // 风格未配但档位配了：保守地按 OpenAI 风格顶层注入（最常见的兼容层都认）。
            payload["reasoning_effort"] = effort;
        }
    }

    private string BuildPayload(LlmRequest request)
    {
        var messages = new JsonArray();

        // ★ 本地模板（Qwen / vLLM Jinja）硬性要求「system 只能在最前、且只在开头」。
        //   从前 SystemPrompt 一条、冻结段一条、任务卡/笔记/召回又各一条 system 挂在末尾，
        //   端点直接 500：System message must be at the beginning —— 主对话里什么都看不到。
        //   这里把**全部 system 正文合并成一条**放在 index 0；消息流里不再出现 system。
        //   （任务卡/笔记/召回本身改用 user 角色承载，见调用方 —— 它们要留在近期注意力区。）
        var systemParts = new List<string>(2);
        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            systemParts.Add(request.SystemPrompt!);
        }

        foreach (var message in request.Messages)
        {
            // 角色比较容错：大小写 / 首尾空白都不该把 system 漏在消息流中部
            if (string.Equals(message.Role?.Trim(), LlmRole.System, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(message.Content))
                {
                    systemParts.Add(message.Content!);
                }

                continue;
            }

            var node = new JsonObject { ["role"] = message.Role };

            if (message.Content is not null)
            {
                node["content"] = message.Content;
            }
            else if (message.ToolCalls is { Count: > 0 })
            {
                // 个别端点（含部分 MiMo/OpenAI 兼容层）要求 assistant+tool_calls 也带 content 键
                node["content"] = string.Empty;
            }

            if (message.ToolCallId is not null)
            {
                node["tool_call_id"] = message.ToolCallId;
            }

            if (message.ToolCalls is { Count: > 0 })
            {
                var calls = new JsonArray();
                foreach (var call in message.ToolCalls)
                {
                    calls.Add(new JsonObject
                    {
                        ["id"] = call.CallId,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = call.ToolName,
                            ["arguments"] = call.ArgumentsJson,
                        },
                    });
                }

                node["tool_calls"] = calls;
            }

            messages.Add(node);
        }

        if (systemParts.Count > 0)
        {
            messages.Insert(0, new JsonObject
            {
                ["role"] = LlmRole.System,
                ["content"] = string.Join("\n\n", systemParts),
            });
        }

        // "auto" 表示交给端点自己的 DefaultModel —— 宿主的某个会话不必关心具体模型名
        var model = string.IsNullOrWhiteSpace(request.Model) || request.Model == "auto"
            ? _options.DefaultModel
            : request.Model;

        var payload = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages,
            ["stream"] = true,
            ["temperature"] = request.Temperature,
        };

        ApplyReasoningOptions(payload, messages, request);

        // OpenAI 系必须**显式要求**才会在流里回报用量；DeepSeek 无论开关都会报。
        // 做成开关是因为个别本地端点（老版 LM Studio）不认这个字段，会直接 400。
        if (request.IncludeUsage)
        {
            payload["stream_options"] = new JsonObject { ["include_usage"] = true };
        }

        if (request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var tool in request.Tools)
            {
                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = JsonNode.Parse(tool.ParametersJsonSchema),
                    },
                });
            }

            payload["tools"] = tools;
        }

        return payload.ToJsonString();
    }

    /// <summary>
    /// 解析用量账目，把两家的字段名归一到同一个形状。
    ///
    ///   · 缓存命中：OpenAI 是 <c>prompt_tokens_details.cached_tokens</c>（prompt 的**子集**）；
    ///     DeepSeek 是平铺的 <c>prompt_cache_hit_tokens</c>（prompt = hit + miss，**拆分**）。
    ///   · 读不到的一律留 null —— <b>「不知道」不能用 0 冒充</b>，
    ///     否则成本与命中率会被悄悄算错，而这类错最难发现。
    /// </summary>
    private static LlmUsage ParseUsage(JsonElement node)
    {
        int? Read(string name)
            => node.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;

        int? ReadNested(string parent, string child)
            => node.TryGetProperty(parent, out var obj)
               && obj.ValueKind == JsonValueKind.Object
               && obj.TryGetProperty(child, out var value)
               && value.TryGetInt32(out var number)
                ? number
                : null;

        return new LlmUsage
        {
            InputTokens = Read("prompt_tokens") ?? Read("input_tokens"),
            OutputTokens = Read("completion_tokens") ?? Read("output_tokens"),
            CachedTokens = ReadNested("prompt_tokens_details", "cached_tokens")
                           ?? Read("prompt_cache_hit_tokens")
                           ?? Read("cache_read_input_tokens"),
            CacheWriteTokens = Read("cache_creation_input_tokens"),
            ReasoningTokens = ReadNested("completion_tokens_details", "reasoning_tokens")
                              ?? ReadNested("output_tokens_details", "reasoning_tokens"),
        };
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "...";

    private sealed class ToolCallAccumulator
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public StringBuilder Arguments { get; } = new();
    }
}
