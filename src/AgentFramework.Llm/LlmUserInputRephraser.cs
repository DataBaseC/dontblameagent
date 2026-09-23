using System.Diagnostics;
using System.Text;
using AgentFramework.Contracts;

namespace AgentFramework.Llm;

/// <summary>
/// 输入转述器的默认实现：调一个（通常是便宜的 / 本地的）模型把用户的话说清楚。
///
/// <b>它绕过路由器</b>：路由规则是「长上下文 + 无工具 → 本地」，而转述请求恰好长这样，
/// 但它必须去<b>指定的</b>那个端点。所以这里直接持有目标客户端（靠一个解析委托拿到），
/// 不走 <see cref="RouterLlmClient"/>。
///
/// <b>失败一律回退原文</b>：转述是省钱、降噪的增强项，
/// 绝不能成为「发不出消息」的原因。
/// </summary>
public sealed class LlmUserInputRephraser : IUserInputRephraser
{
    private readonly Func<string, ILlmClient?> _resolve;

    /// <param name="resolve">按端点名（local / cloud）取客户端；取不到返回 null。</param>
    public LlmUserInputRephraser(Func<string, ILlmClient?> resolve) => _resolve = resolve;

    public async Task<RephraseResult> RephraseAsync(
        string input,
        RephraseOptions options,
        CancellationToken ct = default)
    {
        var text = (input ?? string.Empty).Trim();

        // ── 先筛掉「不值得转述」的情形（不花任何代价）──
        if (!options.Enabled)
        {
            return RephraseResult.Skip(text, RephraseSkip.Disabled);
        }

        if (text.Length < options.MinChars)
        {
            return RephraseResult.Skip(text, RephraseSkip.TooShort);
        }

        if (text.Length > options.MaxInputChars)
        {
            return RephraseResult.Skip(text, RephraseSkip.TooLong);
        }

        var client = _resolve(options.Model);
        if (client is null)
        {
            return RephraseResult.Skip(text, RephraseSkip.NoModel);
        }

        var stopwatch = Stopwatch.StartNew();

        // 超时是独立于外部取消的：外部取消要抛出，超时只回退原文。
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Math.Max(250, options.TimeoutMs));

        try
        {
            var request = new LlmRequest
            {
                Model = options.Model,
                SystemPrompt = options.BuildSystemPrompt(),
                Messages = [new LlmMessage { Role = LlmRole.User, Content = text }],
                Tools = [],
                Temperature = options.Temperature,
            };

            var builder = new StringBuilder();

            await foreach (var chunk in client.StreamAsync(request, timeout.Token).ConfigureAwait(false))
            {
                if (chunk is LlmStreamChunk.TextDelta delta)
                {
                    builder.Append(delta.Text);
                }
            }

            stopwatch.Stop();
            var cleaned = Clean(builder.ToString());

            if (cleaned.Length == 0)
            {
                return RephraseResult.Fail(text, "转述模型返回了空内容", stopwatch.ElapsedMilliseconds);
            }

            return new RephraseResult
            {
                Text = cleaned,
                Original = text,
                Rephrased = true,
                Model = client.Name,
                ElapsedMs = stopwatch.ElapsedMilliseconds,
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return RephraseResult.Fail(text, $"转述超时（> {options.TimeoutMs} ms）", stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return RephraseResult.Fail(text, ex.Message, stopwatch.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// 去掉模型爱多带的外壳：代码围栏、首尾引号、以及「优化后：」这类前缀。
    /// 提示词里已经要求它别加，但**别指望模型永远听话** —— 这里做一次便宜的后处理。
    /// </summary>
    private static string Clean(string raw)
    {
        var text = raw.Trim();

        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstBreak = text.IndexOf('\n');
            if (firstBreak >= 0)
            {
                text = text[(firstBreak + 1)..];
            }

            var fence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (fence >= 0)
            {
                text = text[..fence];
            }

            text = text.Trim();
        }

        foreach (var prefix in Prefixes)
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
            {
                text = text[prefix.Length..].Trim();
                break;
            }
        }

        if (text.Length >= 2
            && ((text[0] == '"' && text[^1] == '"') || (text[0] == '\u201c' && text[^1] == '\u201d')))
        {
            text = text[1..^1].Trim();
        }

        return text;
    }

    private static readonly string[] Prefixes =
    [
        "澄清后：", "澄清后的任务描述：", "改写后：", "优化后：", "任务描述：",
        "Rephrased:", "Rewritten:", "Optimized:",
    ];
}
