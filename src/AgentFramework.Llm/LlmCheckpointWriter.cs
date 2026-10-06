using System.Text;
using System.Text.Json;
using AgentFramework.Contracts;

namespace AgentFramework.Llm;

/// <summary>
/// checkpoint writer 的选项。
/// </summary>
public sealed class LlmCheckpointWriterOptions
{
    /// <summary>
    /// 用哪个模型跑提取 —— 沿用「旁路小模型」口径（<c>local</c> / <c>cloud</c> / 端点 id），
    /// 与 <see cref="LlmContextSummarizerOptions.Model"/> 同一套解析。
    /// </summary>
    public string Model { get; set; } = "auto";

    public string SystemPrompt { get; set; } =
        "你是一个精炼的工作状态提取器。你的输出会被另一个 AI 当作它自己的长期记忆使用，"
        + "所以只记录对话里真实出现过的事实 —— 不推断、不补充、不替用户做新决定。";

    /// <summary>
    /// 正文上限（字符）。与 <see cref="CheckpointOptions.MaxChars"/> 同口径 ——
    /// checkpoint 是<b>恒定大小</b>的锚点，不是账本；超了就砍，宁可少说，
    /// 也绝不让自己长成新的上下文负担。
    /// </summary>
    public int MaxChars { get; set; } = 2_000;

    /// <summary>太少的消息不值得提取（无旧块时）。</summary>
    public int MinMessages { get; set; } = 2;

    /// <summary>模型原始输出上限（字符）—— 防止解析前先跑飞。</summary>
    public int MaxRawChars { get; set; } = 8_000;
}

/// <summary>
/// checkpoint writer（Llm 实现）—— 独立于主 agent 的**状态提取者**。
///
/// <para>
/// 它做三件事，且只做这三件：
/// </para>
/// <list type="number">
///   <item>把「迄今对话 + 任务卡 + 工作小本 + 上一份块」拼成一次**旁路**模型调用（不进主循环）；</item>
///   <item>要求模型按固定字段吐 <b>JSON</b>（而不是自由文本 —— checkpoint 要进结构化事件）；</item>
///   <item>解析、裁剪到恒定大小，产出一条 <see cref="CheckpointEvent"/>；解析不出有效内容就返回 null。</item>
/// </list>
/// <para>
/// 纪律（与 <see cref="LlmContextSummarizer"/> 同源）：失败/超时<strong>返回 null 而非抛</strong> ——
/// writer 永不打扰主流程；调用方据此跳过，不产生空事件。
/// </para>
/// </summary>
public sealed class LlmCheckpointWriter : ICheckpointWriter
{
    private readonly ILlmClient _client;
    private readonly LlmCheckpointWriterOptions _options;

    public LlmCheckpointWriter(ILlmClient client, LlmCheckpointWriterOptions? options = null)
    {
        _client = client;
        _options = options ?? new LlmCheckpointWriterOptions();
    }

    public string Name => $"checkpoint-writer({_client.Name})";

    public async ValueTask<CheckpointEvent?> WriteAsync(CheckpointRequest request, CancellationToken ct = default)
    {
        // INV-C1：合成复述块（摘要/任务卡/计划注入块）本身是压缩产物，不能再当**被提取的正文**。
        // 旧 checkpoint 只以 PreviousBlock 身份出现在背景段。
        var subjects = request.Messages
            .Where(m => !SyntheticContent.IsSynthetic(m.Content))
            .ToList();

        if (subjects.Count < _options.MinMessages && string.IsNullOrWhiteSpace(request.PreviousBlock))
        {
            return null;
        }

        if (subjects.Count == 0)
        {
            return null;
        }

        var prompt = BuildPrompt(BuildTranscript(subjects), request);

        var llmRequest = new LlmRequest
        {
            Model = _options.Model,
            SystemPrompt = _options.SystemPrompt,
            Messages = [new LlmMessage { Role = LlmRole.User, Content = prompt }],
            Temperature = 0.1,
        };

        var builder = new StringBuilder();
        var started = DateTimeOffset.UtcNow;

        try
        {
            await foreach (var chunk in _client.StreamAsync(llmRequest, ct).ConfigureAwait(false))
            {
                if (chunk is LlmStreamChunk.TextDelta delta)
                {
                    builder.Append(delta.Text);
                    if (builder.Length > _options.MaxRawChars)
                    {
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // writer 不吃异常：提取失败就是这次不写（调用方沿用旧块）
            return null;
        }

        var raw = builder.ToString().Trim();
        if (raw.Length == 0)
        {
            return null;
        }

        var checkpoint = Parse(raw);
        if (checkpoint is null)
        {
            return null;
        }

        checkpoint.Trigger = request.Trigger;
        checkpoint.WaterLevelPermille = request.WaterLevelPermille;
        // PreTokens 由调用方按当时的真实投影覆写 —— writer 不假装知道主 agent 的估算值。
        checkpoint.FromSeq = request.FromSeq;
        checkpoint.ToSeq = request.ToSeq;
        checkpoint.Model = _client.Name;
        checkpoint.ElapsedMs = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds;

        TrimToLimit(checkpoint, _options.MaxChars);
        return checkpoint;
    }

    /// <summary>
    /// 从模型输出里取出 JSON 对象并映射成 <see cref="CheckpointEvent"/>。
    /// 宽容但不含糊：解析不出、或所有字段都空，就返回 null（宁可不写也不写废稿）。
    /// </summary>
    internal static CheckpointEvent? Parse(string raw)
    {
        var json = ExtractJsonObject(raw);
        if (json is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var checkpoint = new CheckpointEvent
            {
                Intent = Str(root, "intent"),
                NextAction = Str(root, "next_action"),
                CurrentWork = Str(root, "current_work"),
                ErrorsAndFixes = Str(root, "errors_and_fixes"),
                Notes = Str(root, "notes"),
                Constraints = StrList(root, "constraints"),
                FilesTouched = StrList(root, "files_touched"),
                Discoveries = StrList(root, "discoveries"),
                Decisions = StrList(root, "decisions"),
            };

            // 全空 = 模型什么也没说 → 不写空事件
            if (string.IsNullOrWhiteSpace(checkpoint.Intent)
                && string.IsNullOrWhiteSpace(checkpoint.NextAction)
                && string.IsNullOrWhiteSpace(checkpoint.CurrentWork)
                && string.IsNullOrWhiteSpace(checkpoint.ErrorsAndFixes)
                && string.IsNullOrWhiteSpace(checkpoint.Notes)
                && checkpoint.Constraints.Count == 0
                && checkpoint.FilesTouched.Count == 0
                && checkpoint.Discoveries.Count == 0
                && checkpoint.Decisions.Count == 0)
            {
                return null;
            }

            return checkpoint;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>找第一个 <c>{</c> 与最后一个 <c>}</c> 之间的子串（容忍 ```json 围栏与前后废话）。</summary>
    internal static string? ExtractJsonObject(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        return raw[start..(end + 1)];
    }

    private static string? Str(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value))
        {
            return null;
        }

        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Array => string.Join("；", value.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString())
                .Where(s => !string.IsNullOrWhiteSpace(s))),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value.ToString(),
        };

        return Normalize(text);
    }

    private static List<string> StrList(JsonElement obj, string name)
    {
        var result = new List<string>();
        if (!obj.TryGetProperty(name, out var value))
        {
            return result;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var normalized = Normalize(item.GetString());
                    if (normalized is not null)
                    {
                        result.Add(normalized);
                    }
                }

                break;

            case JsonValueKind.String:
                // 容忍把数组写成 "- a；- b" / "a；b" 的形态
                foreach (var part in (value.GetString() ?? string.Empty)
                    .Split(['；', ';', '\n'], StringSplitOptions.RemoveEmptyEntries))
                {
                    var normalized = Normalize(part.Trim().TrimStart('-', '*', ' '));
                    if (normalized is not null)
                    {
                        result.Add(normalized);
                    }
                }

                break;
        }

        return result;
    }

    /// <summary>把「无 / none / null / -」这类占位归一成 null（空字段不该占行）。</summary>
    private static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        return trimmed switch
        {
            "无" or "none" or "None" or "N/A" or "n/a" or "null" or "NULL" or "-" or "—" => null,
            _ => trimmed,
        };
    }

    /// <summary>
    /// 超限就按「最不重要先丢」的顺序裁：杂项 → 错误修复 → 发现 → 文件 → 约束 → 决策 →
    /// 当前工作 → 下一步 →（最后才动）意图。
    /// 意图是锚点的锚点，留到最后。（恒定大小锚点的实现方式，可测。）
    /// </summary>
    internal static void TrimToLimit(CheckpointEvent checkpoint, int maxChars)
    {
        if (maxChars <= 0)
        {
            return;
        }

        var steps = new List<Action> { () => checkpoint.Notes = null, () => checkpoint.ErrorsAndFixes = null };

        for (var i = checkpoint.Discoveries.Count; i > 0; i--)
        {
            var index = i - 1;
            steps.Add(() => { if (checkpoint.Discoveries.Count > index) checkpoint.Discoveries.RemoveAt(index); });
        }

        for (var i = checkpoint.FilesTouched.Count; i > 0; i--)
        {
            var index = i - 1;
            steps.Add(() => { if (checkpoint.FilesTouched.Count > index) checkpoint.FilesTouched.RemoveAt(index); });
        }

        for (var i = checkpoint.Constraints.Count; i > 0; i--)
        {
            var index = i - 1;
            steps.Add(() => { if (checkpoint.Constraints.Count > index) checkpoint.Constraints.RemoveAt(index); });
        }

        for (var i = checkpoint.Decisions.Count; i > 0; i--)
        {
            var index = i - 1;
            steps.Add(() => { if (checkpoint.Decisions.Count > index) checkpoint.Decisions.RemoveAt(index); });
        }

        steps.Add(() => checkpoint.CurrentWork = null);
        steps.Add(() => checkpoint.NextAction = null);
        steps.Add(() =>
        {
            if (checkpoint.Intent is { Length: > 200 } intent)
            {
                checkpoint.Intent = intent[..200];
            }
        });

        foreach (var step in steps)
        {
            if (checkpoint.RenderBlock().Length <= maxChars)
            {
                return;
            }

            step();
        }
    }

    private static string BuildTranscript(IReadOnlyList<LlmMessage> messages)
    {
        var sb = new StringBuilder();

        foreach (var message in messages)
        {
            if (string.IsNullOrWhiteSpace(message.Content))
            {
                continue;
            }

            var role = message.Role switch
            {
                LlmRole.User => "用户",
                LlmRole.Assistant => "助手",
                _ => message.Role,
            };

            sb.Append('[').Append(role).Append("] ").AppendLine(message.Content.Trim());
        }

        return sb.ToString();
    }

    private static string BuildPrompt(string transcript, CheckpointRequest request) => $$"""
        从下面这段对话里提取「当前工作状态」，写成 JSON。这份状态会被另一个 AI 当成它自己的记忆继续干活。

        只输出一个 JSON 对象，不要任何前后缀、不要 Markdown 围栏。字段与类型（没有内容就用 null 或空数组，不要编）：
        {
          "intent": "这一路在干什么（用户的目标与硬性约束，重要表述保留原话）",
          "next_action": "明确、可执行的下一步",
          "current_work": "手上这个具体活",
          "constraints": ["不能违反的规则；失败过且不应重试的做法"],
          "files_touched": ["文件路径 + 各自的角色/状态"],
          "discoveries": ["在别处也成立的跨任务发现"],
          "errors_and_fixes": "踩过的坑：报错与当时的修复方式",
          "decisions": ["已确认的结论与采用它的理由"],
          "notes": "其余仍需记住的信息"
        }

        规则：
        - 只记录对话里真实出现过的信息，不推断、不补充、不替用户做决定
        - 用中文，信息密度优先；工具原始输出只留结论
        - 字段值尽量短，整体不超过 1500 字
        {{(string.IsNullOrWhiteSpace(request.PreviousBlock) ? string.Empty : "\n已有的状态（这是增量更新：在其基础上合并新进展、修正过时信息，输出完整的新版，不要逐字照抄，也不要丢掉仍有效的条目）：\n" + request.PreviousBlock + "\n")}}{{(string.IsNullOrWhiteSpace(request.TaskCard) ? string.Empty : "\n当前任务卡（供你判断什么重要）：\n" + request.TaskCard + "\n")}}{{(string.IsNullOrWhiteSpace(request.Notes) ? string.Empty : "\n工作小本本：\n" + request.Notes + "\n")}}
        对话：
        {{transcript}}
        """;
}
