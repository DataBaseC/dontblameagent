using System.Text.Json;
using AgentFramework.Contracts;

namespace AgentFramework.Data;

/// <summary>
/// 一次上下文投影的结果。
///
/// 它同时是「喂给模型的东西」和「水位报告」—— 界面、压缩决策都读它。
/// 体积是估算值（<see cref="TokenEstimator"/>），只用于判断趋势，不用于计费。
/// </summary>
public sealed record ContextProjection(
    IReadOnlyList<LlmMessage> Messages,
    int EstimatedTokens,
    int Budget,
    int TotalTurns,
    int KeptTurns,
    int MaskedResults,
    IReadOnlyList<long> MaskedSeqs,
    IReadOnlyList<LlmMessage> OlderMessages,
    bool SummaryApplied,
    bool TaskCardInjected,
    bool NeedsCompression,
    string? TaskCard)
{
    /// <summary>水位（0–1+）—— 界面那根进度条读它，压缩决策也读它。</summary>
    public double WaterLevel => Budget <= 0 ? 0 : (double)EstimatedTokens / Budget;
}

/// <summary>
/// 从事件流投影出「模型上下文」。
///
/// <b>上下文是投影，不是日志</b> —— 这是本节的中心思想。
/// 日志 append-only 永不修改；至于"这一轮喂给模型什么"，换个投影函数就行。
/// 于是压缩变得可逆、可审计、可回放，而真相源一字未动。
///
/// 投影规则（DESIGN.md 4.15）：
///   头 —— 窗口外的对话：按需折叠为摘要（L5）或保留原文
///   中 —— 窗口外的工具结果 → 一行占位符（L3）；被历史压缩事件钉死的序号永不展开
///   尾 —— 最近 N 轮逐字保留 + 任务卡（L4）常驻
///
/// 「模型可见即已记录」依然成立：投影里的每一条都能由日志 + 压缩事件推出来。
/// </summary>
public static class SessionContextBuilder
{
    /// <summary>全量投影（不裁剪）—— 旧接口保持原语义，老调用方与既有验证不受影响。</summary>
    public static List<LlmMessage> Build(IEnumerable<SessionEvent> events)
        => [.. Project(events, ContextOptions.Full).Messages];

    public static List<LlmMessage> BuildFromFile(string path)
        => Build(JsonlEventLog.Read(path));

    /// <summary>带治理的投影。</summary>
    /// <param name="forceCollapse">
    /// 超预算且无摘要时是否仍折叠旧轮（P0）：折叠掉放一句如实占位，
    /// 好过让正文永远不收缩、水位永远降不下来。
    /// </param>
    public static ContextProjection Project(
        IEnumerable<SessionEvent> events,
        ContextOptions? options = null,
        bool forceCollapse = false)
    {
        var opt = options ?? new ContextOptions();
        var all = events as IReadOnlyList<SessionEvent> ?? [.. events];

        // L6 两遍投影：第一遍不骨架化 —— 无压力时零信息损失；
        // 估算水位超限才用骨架化再投影一遍（纯函数、无 IO，成本是常数次遍历）。
        var first = ProjectCore(all, opt, skeletonize: false, forceCollapse);
        if (!opt.SkeletonizeOldAssistant || !first.NeedsCompression)
        {
            return first;
        }

        var second = ProjectCore(all, opt, skeletonize: true, forceCollapse);
        return second.EstimatedTokens < first.EstimatedTokens ? second : first;
    }

    private static ContextProjection ProjectCore(
        IReadOnlyList<SessionEvent> all,
        ContextOptions opt,
        bool skeletonize,
        bool forceCollapse)
    {
        // ── 0) 先读历史压缩留痕 ─────────────────────────────
        //   · MaskedSeqs：钉死遮蔽态 —— 「当时遮蔽过的，之后不会又展开」
        //   · Summary  ：老历史的"替身"，取最后一条
        var frozenMasked = new HashSet<long>();
        string? summary = null;

        foreach (var sessionEvent in all)
        {
            if (sessionEvent is not ContextCompactedEvent compacted)
            {
                continue;
            }

            foreach (var seq in compacted.MaskedSeqs)
            {
                frozenMasked.Add(seq);
            }

            if (!string.IsNullOrWhiteSpace(compacted.Summary))
            {
                summary = compacted.Summary;
            }
        }

        // ── 0.5) 划分轮次（以 user 消息为界）+ 悬空 / 孤儿工具调用检测（P0-2）──
        // 「requested 已落盘、completed 未落盘」= 回合中途被杀或崩了
        // （审批等待窗口最长 5 分钟，是高发点）。直接投影会产出
        // assistant(tool_calls) → user 的**协议非法**序列，端点以 400 拒绝；
        // 又因为上下文每轮重建，会话会从此每轮都 400 —— 必须在投影层兜住。
        //
        // 反过来的「孤儿 completed」（只有 completed、没有 requested）同样非法：
        // role=tool 消息必须对应上一条 assistant 的 tool_calls，否则端点也 400。
        // 丢弃孤儿结果 —— 等价于「这一次调用没发生过」，序列才合法。
        //
        // v3.6 审查修复：判定按**轮次**配对，而不是全局 CallId 集合 ——
        // 端点跨轮复用 tool call id（本地模型常见 call_0）时，全局集合会把本轮的悬空
        // 误判为「上轮有同 id 的 completed」→ 投影出协议非法序列 → 每轮 400（正是本段要防的病）。
        // 键 = `{轮次}:{CallId}`，于是不同轮次的同名 id 天然隔离。
        var turnOfSeq = new Dictionary<long, int>();
        var callNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var completedCallIds = new HashSet<string>(StringComparer.Ordinal);
        var requestedCallIds = new HashSet<string>(StringComparer.Ordinal);
        var danglingCallIds = new HashSet<string>(StringComparer.Ordinal);
        var turn = 0;

        foreach (var sessionEvent in all)
        {
            if (sessionEvent is UserMessageEvent)
            {
                turn++;
            }

            turnOfSeq[sessionEvent.Seq] = turn;

            if (sessionEvent is ToolCallRequestedEvent requested)
            {
                callNames[$"{turn}:{requested.CallId}"] = requested.ToolName;
            }
            else if (sessionEvent is ToolCallCompletedEvent completed)
            {
                completedCallIds.Add($"{turn}:{completed.CallId}");
            }
        }

        foreach (var sessionEvent in all)
        {
            if (sessionEvent is ToolCallRequestedEvent requested)
            {
                var key = $"{turnOfSeq[requested.Seq]}:{requested.CallId}";
                requestedCallIds.Add(key);
                if (!completedCallIds.Contains(key))
                {
                    danglingCallIds.Add(key);
                }
            }
        }

        var totalTurns = turn;
        var keptTurns = Math.Clamp(opt.RecentTurnsKeptVerbatim, 0, totalTurns);
        var cutoff = totalTurns - keptTurns;

        // 折叠条件：窗口外确实有轮次，且「有摘要」或「显式强制」。
        // 从前要求「有摘要才折叠」—— 摘要器没接上时（多端点配置下 localClient 恒 null 是常态）
        // 老轮次永远逐字保留，上下文永不收缩，水位永远降不下来。
        // 现在：超预算时由外层 Project 带 forceCollapse 进来，无摘要则给一段如实的占位说明。
        var collapseOld = cutoff > 0 && (summary is not null || forceCollapse);

        var messages = new List<LlmMessage>();
        var maskedSeqs = new List<long>();
        var olderMessages = new List<LlmMessage>();

        if (collapseOld)
        {
            messages.Add(new LlmMessage
            {
                Role = LlmRole.User,
                Content = summary is not null
                    ? "【早期对话摘要·非用户发言】原始事件仍在会话日志中，需要细节时可回到原文检索。\n" + summary
                    : "【早期对话已折叠·非用户发言】以下为最近几轮对话。更早的 "
                      + cutoff
                      + " 轮已折叠以节省上下文；原始事件仍在会话日志中，需要细节时用 search_history 检索，或向用户确认。",
            });
        }

        foreach (var sessionEvent in all)
        {
            var isOld = cutoff > 0
                && turnOfSeq.TryGetValue(sessionEvent.Seq, out var owner)
                && owner <= cutoff;

            // 窗口外的对话消息 —— 无论折不折叠都收集起来：L5 的滚动摘要要用它
            if (isOld)
            {
                switch (sessionEvent)
                {
                    case UserMessageEvent oldUser:
                        olderMessages.Add(new LlmMessage { Role = LlmRole.User, Content = oldUser.ModelVisibleText });
                        break;

                    case AssistantMessageEvent oldAssistant:
                        olderMessages.Add(new LlmMessage { Role = LlmRole.Assistant, Content = oldAssistant.Text });
                        break;
                }
            }

            // 老轮次已被摘要覆盖 → 整段跳过，免得摘要与原文重复一遍
            if (isOld && collapseOld)
            {
                continue;
            }

            switch (sessionEvent)
            {
                case UserMessageEvent user:
                    messages.Add(new LlmMessage
                    {
                        Role = LlmRole.User,
                        Content = user.ModelVisibleText,
                        Images = user.Images is { Count: > 0 } imgs ? imgs : null,
                    });
                    break;

                case AssistantMessageEvent assistant:
                {
                    var text = assistant.Text;

                    // L6：窗口外的纯文本旧回复骨架化。
                    // 与 L3 同一套钉子语义：被历史压缩事件钉住的序号，即使放宽窗口也保持骨架
                    // （视图不来回跳；前缀缓存才稳定）。
                    // 协议安全：带 tool_calls 的 assistant 由 AppendToolCall 并进上一条，
                    // 不走此分支改 Content；拿不到 Seq 时不骨架化 —— 宁保真，不冒险。
                    var pinned = frozenMasked.Contains(assistant.Seq);
                    var worthIt = text.Length > opt.MinMaskSavingChars;

                    if (text.Length > 0
                        && assistant.Seq > 0
                        && worthIt
                        && (pinned || (skeletonize && isOld && opt.SkeletonizeOldAssistant)))
                    {
                        // 与工具遮蔽同一条约定：在骨架视图里的序号都计入 MaskedSeqs
                        //（UI 显示与下一次压缩事件的钉子清单共用这份账）。
                        maskedSeqs.Add(assistant.Seq);

                        messages.Add(new LlmMessage
                        {
                            Role = LlmRole.Assistant,
                            Content = $"[已骨架化：这段旧回答约 {text.Length} 字符，原文在会话日志 #{assistant.Seq}；"
                                + "需要细节时用 search_history 以原文特征词检索，或向用户确认是否重新生成]",
                        });
                        break;
                    }

                    messages.Add(new LlmMessage { Role = LlmRole.Assistant, Content = text });
                    break;
                }

                case ToolCallRequestedEvent requested:
                    // 悬空调用不进上下文：没有结果的 tool_calls 是协议非法序列。
                    // 助手消息若带正文仍照常出现（合法）；只有工具意图的那种，
                    // 整条不出现 —— 等价于「这一轮没发生过」，同样是合法序列。
                    // 键按轮次配对（见上），避免跨轮复用 id 的误判。
                    if (!danglingCallIds.Contains($"{turnOfSeq.GetValueOrDefault(requested.Seq)}:{requested.CallId}"))
                    {
                        AppendToolCall(messages, requested);
                    }

                    break;

                case ToolCallCompletedEvent completed:
                {
                    // 孤儿结果：没有对应的 requested —— 投影成 role=tool 会产出
                    // 没有配对 tool_calls 的非法序列。直接丢弃。
                    var completedKey = $"{turnOfSeq.GetValueOrDefault(completed.Seq)}:{completed.CallId}";
                    if (!requestedCallIds.Contains(completedKey))
                    {
                        break;
                    }

                    var raw = completed.Success
                        ? completed.Output ?? string.Empty
                        : $"ERROR: {completed.Error}";

                    var inScope = frozenMasked.Contains(completed.Seq) || (isOld && opt.MaskOldToolResults);

                    if (inScope)
                    {
                        var note = MaskNote(
                            callNames.GetValueOrDefault(completedKey, "tool"),
                            completed,
                            opt.MaskedNoteMaxChars);

                        // 门槛：折叠必须真的省下可观空间，否则只是换个说法、白丢信息。
                        // （实测逼出来的 —— L2 之后旧结果本就不长，无谓替换会让"压缩"反而更占地方）
                        if (note.Length + opt.MinMaskSavingChars <= raw.Length)
                        {
                            maskedSeqs.Add(completed.Seq);
                            messages.Add(new LlmMessage
                            {
                                Role = LlmRole.Tool,
                                ToolCallId = completed.CallId,
                                Content = note,
                            });
                            break;
                        }
                    }

                    messages.Add(new LlmMessage
                    {
                        Role = LlmRole.Tool,
                        ToolCallId = completed.CallId,
                        Content = raw,
                    });

                    // 视觉：tool 角色不收图，紧跟一条 user 多模态（与 AgentRunner 在环内同一手法）
                    if (completed.Images is { Count: > 0 })
                    {
                        messages.Add(new LlmMessage
                        {
                            Role = LlmRole.User,
                            Content = $"（工具附带了 {completed.Images.Count} 张图，请结合图像继续）",
                            Images = completed.Images,
                        });
                    }

                    break;
                }
            }
        }

        // ── 2) 任务卡（L4）：挂在末尾，紧邻下一轮的 user 消息 ──
        //   位置是刻意的：调研里最值钱的一条 —— 计划放在注意力弱的中部会被忽略，
        //   放在近期区才真正起作用。空会话不挂（没什么可说的）。
        var taskCardInjected = false;
        string? taskCard = null;

        if (opt.InjectTaskCard && totalTurns > 0)
        {
            taskCard = TaskCardBuilder.Build(all, opt);
            if (taskCard.Length > 0)
            {
                // 用 user 而不是 system：Qwen/vLLM 模板要求 system 只能在最前。
                // 任务卡要挂在末尾（近期注意力区），role 改 user、加前缀标明不是用户发言。
                messages.Add(new LlmMessage { Role = LlmRole.User, Content = "【任务卡·非用户发言】\n" + taskCard });
                taskCardInjected = true;
            }
        }

        // ── 3) 水位 ──────────────────────────────────────────
        var estimated = TokenEstimator.Estimate(messages);
        var needsCompression = estimated > opt.TokenBudget * opt.CompressionTriggerRatio;

        return new ContextProjection(
            messages,
            estimated,
            opt.TokenBudget,
            totalTurns,
            keptTurns,
            maskedSeqs.Count,
            maskedSeqs,
            olderMessages,
            collapseOld,
            taskCardInjected,
            needsCompression,
            taskCard);
    }

    public static ContextProjection ProjectFile(string path, ContextOptions? options = null)
        => Project(JsonlEventLog.Read(path), options);

    /// <summary>
    /// 工具调用在协议上属于「assistant 那一轮」，
    /// 所以要并进上一条 assistant 消息，而不是新开一条。
    /// </summary>
    private static void AppendToolCall(List<LlmMessage> messages, ToolCallRequestedEvent requested)
    {
        var call = new ToolCallRequest(
            requested.CallId,
            requested.ToolName,
            JsonSerializer.Serialize(requested.Arguments));

        if (messages.Count > 0 && messages[^1] is { Role: LlmRole.Assistant } last)
        {
            messages[^1] = last with { ToolCalls = [.. last.ToolCalls ?? [], call] };
        }
        else
        {
            messages.Add(new LlmMessage
            {
                Role = LlmRole.Assistant,
                ToolCalls = [call],
            });
        }
    }

    /// <summary>
    /// 把一条旧工具结果换成**极简**占位符。
    ///
    /// 刻意不再保留"头部片段" —— 实测表明那 240 字头的头部纯粹是负担：
    /// 7 条折叠下来白占上千 token，折叠就白折了。
    /// 真要细节，模型有两条路：<c>search_history</c> 检索（索引里有全文），
    /// 或者干脆重新调用一次工具。**折叠的底气来自"随时取得回来"**，
    /// 而不是来自"再塞一点在眼前"。
    /// </summary>
    private static string MaskNote(string toolName, ToolCallCompletedEvent completed, int maxChars)
    {
        var length = completed.Success
            ? completed.Output?.Length ?? 0
            : completed.Error?.Length ?? 0;

        var note = $"[已折叠 {toolName} 的历史结果：原始 {length} 字符；"
            + "需要细节可用 search_history 检索，或重新调用该工具]";

        return note.Length <= maxChars ? note : note[..maxChars];
    }
}
