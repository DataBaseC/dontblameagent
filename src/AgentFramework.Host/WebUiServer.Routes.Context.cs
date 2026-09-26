using System.Text.Json;
using AgentFramework.Contracts;

namespace AgentFramework.Host;

/// <summary>
/// 内置路由 · <b>上下文设置域</b>（任务 1）：读 / 写长任务上下文治理配置。
///
/// <para>
/// 两个水位是两件事，别绑成一个：
///   · <b>写盘</b>（checkpoint）—— 上下文一个字节都不动，可以早、可以频繁；
///   · <b>裁剪</b>（压缩）—— 会打断前缀缓存，宁晚不频。
/// 所以「裁剪水位」必须**高于**「写盘水位」，否则就是「还没写盘就先裁剪」的语义颠倒。
/// </para>
///
/// <para>
/// 写入的是 <see cref="HostOptions.Context"/> / <see cref="HostOptions.Checkpoint"/> 这两个
/// <b>活实例</b>的字段，因而**改完即生效、不重启**；同时落盘（<c>context.json</c>）以便重启后仍在。
/// </para>
/// </summary>
public sealed partial class WebUiServer
{
    /// <summary>注册上下文设置域的内置端点。</summary>
    private void RegisterContextRoutes()
    {
        // 读当前值 + 当前水位 + 最近一次 compaction / checkpoint 摘要。
        Map(new DelegateRoute("GET", "/api/context", (request, _) =>
        {
            request.Json(ContextPayload());
            return ValueTask.CompletedTask;
        }));

        // 写回（活实例）。先对「应用后的值」做范围校验，通过才写 —— 拒绝时活实例一个字节都不动。
        Map(new DelegateRoute("POST", "/api/context", async (request, _) =>
        {
            var body = await request.ReadBodyAsync().ConfigureAwait(false);
            if (body is null)
            {
                request.Json(new { ok = false, error = "请求体不是合法 JSON" }, 400);
                return;
            }

            var context = _host.Options.Context;
            var checkpoint = _host.Options.Checkpoint;

            // ── 预算 ──
            var nextBudget = context.TokenBudget;
            if (TryReadInt(body.Value, "tokenBudget", out var budget))
            {
                if (budget <= 0)
                {
                    request.Json(new { ok = false, error = "tokenBudget 必须 > 0" }, 400);
                    return;
                }

                nextBudget = budget;
            }

            // ── 写盘水位（0.10–0.80）──
            var nextCheckpointRatio = checkpoint.TriggerRatio;
            if (TryReadDouble(body.Value, "checkpointTriggerRatio", out var ckRatio))
            {
                if (ckRatio is < 0.1 or > 0.8)
                {
                    request.Json(new { ok = false, error = "写盘水位必须在 0.10–0.80 之间" }, 400);
                    return;
                }

                nextCheckpointRatio = ckRatio;
            }

            // ── 压缩水位（0.40–0.95）──
            var nextCompressionRatio = context.CompressionTriggerRatio;
            if (TryReadDouble(body.Value, "compressionTriggerRatio", out var cpRatio))
            {
                if (cpRatio is < 0.4 or > 0.95)
                {
                    request.Json(new { ok = false, error = "压缩水位必须在 0.40–0.95 之间" }, 400);
                    return;
                }

                nextCompressionRatio = cpRatio;
            }

            // ── 关系约束：压缩水位必须高于写盘水位 ──
            if (nextCompressionRatio <= nextCheckpointRatio)
            {
                request.Json(new
                {
                    ok = false,
                    error = $"压缩水位（{nextCompressionRatio:0.00}）必须高于写盘水位（{nextCheckpointRatio:0.00}）"
                            + "—— 否则会「还没写盘就先裁剪」",
                }, 400);
                return;
            }

            // ── 遮蔽相关（高级）──
            var nextMaskedChars = context.MaskedNoteMaxChars;
            if (TryReadInt(body.Value, "maskedNoteMaxChars", out var masked))
            {
                if (masked <= 0)
                {
                    request.Json(new { ok = false, error = "maskedNoteMaxChars 必须 > 0" }, 400);
                    return;
                }

                nextMaskedChars = masked;
            }

            var nextMinSaving = context.MinMaskSavingChars;
            if (TryReadInt(body.Value, "minMaskSavingChars", out var minSaving))
            {
                if (minSaving < 0)
                {
                    request.Json(new { ok = false, error = "minMaskSavingChars 不能为负" }, 400);
                    return;
                }

                nextMinSaving = minSaving;
            }

            var nextMask = context.MaskOldToolResults;
            if (TryReadBool(body.Value, "maskOldToolResults", out var mask))
            {
                nextMask = mask;
            }

            var nextCheckpointEnabled = checkpoint.Enabled;
            if (TryReadBool(body.Value, "checkpointEnabled", out var ckEnabled))
            {
                nextCheckpointEnabled = ckEnabled;
            }

            // ── 全部通过：写回活实例 + 落盘 ──
            context.TokenBudget = nextBudget;
            context.CompressionTriggerRatio = nextCompressionRatio;
            context.MaskedNoteMaxChars = nextMaskedChars;
            context.MinMaskSavingChars = nextMinSaving;
            context.MaskOldToolResults = nextMask;
            checkpoint.TriggerRatio = nextCheckpointRatio;
            checkpoint.Enabled = nextCheckpointEnabled;

            _host.SaveContextSettings();

            request.Json(ContextPayload());
        }));

        // 手动压缩（「立即压缩」按钮）
        Map(new DelegateRoute("POST", "/api/context/compact", async (request, _) =>
        {
            var result = await _host.ManualCompactAsync().ConfigureAwait(false);
            request.Json(new
            {
                ok = result.ok,
                summary = result.summary,
                preTokens = result.preTokens,
                postTokens = result.postTokens,
                error = result.error,
            });
        }));
    }

    /// <summary>当前设置 + 水位 + 最近一次 compaction / checkpoint 摘要。</summary>
    private object ContextPayload()
    {
        var context = _host.Options.Context;
        var checkpoint = _host.Options.Checkpoint;

        var events = _host.Events();
        var lastCompaction = events.OfType<ContextCompactedEvent>().LastOrDefault();
        var lastCheckpoint = events.OfType<CheckpointEvent>().LastOrDefault();

        return new
        {
            ok = true,
            context = new
            {
                tokenBudget = context.TokenBudget,
                compressionTriggerRatio = context.CompressionTriggerRatio,
                recentTurnsKeptVerbatim = context.RecentTurnsKeptVerbatim,
                maskOldToolResults = context.MaskOldToolResults,
                maskedNoteMaxChars = context.MaskedNoteMaxChars,
                minMaskSavingChars = context.MinMaskSavingChars,
            },
            checkpoint = new
            {
                enabled = checkpoint.Enabled,
                triggerRatio = checkpoint.TriggerRatio,
                maxChars = checkpoint.MaxChars,
                minNewEvents = checkpoint.MinNewEvents,
            },
            // 「恢复默认」按钮对照的就是这一组源码默认值（与 ContextOptions / CheckpointOptions 初值一致）。
            defaults = new
            {
                tokenBudget = 24_000,
                compressionTriggerRatio = 0.8,
                checkpointTriggerRatio = 0.35,
                maskedNoteMaxChars = 240,
                minMaskSavingChars = 200,
                maskOldToolResults = true,
            },
            waterLevel = ContextStatus(),
            lastCompaction = lastCompaction is null
                ? null
                : new
                {
                    seq = lastCompaction.Seq,
                    at = lastCompaction.Timestamp,
                    strategy = lastCompaction.Trigger,
                    // 摘要正文必须回给面板 —— 只回 seq/at 时用户「看不到摘要」，
                    // 而摘要本该是压缩后 Agent 与人共有的那份「早期对话替身」。
                    summary = lastCompaction.Summary,
                    maskedCount = lastCompaction.MaskedCount,
                    collapsedTurns = lastCompaction.CollapsedTurns,
                    preTokens = lastCompaction.PreTokens,
                    postTokens = lastCompaction.PostTokens,
                },
            lastCheckpoint = lastCheckpoint is null
                ? null
                : new
                {
                    seq = lastCheckpoint.Seq,
                    at = lastCheckpoint.Timestamp,
                    waterLevelPermille = lastCheckpoint.WaterLevelPermille,
                    intent = lastCheckpoint.Intent,
                    nextAction = lastCheckpoint.NextAction,
                },
            // 任务卡：用户可见的工作状态卡（模型每轮看到的就是这份）
            taskCard = AgentFramework.Data.TaskCardBuilder.Build(events, context),
        };
    }

    private static bool TryReadDouble(JsonElement body, string name, out double value)
    {
        value = 0;
        if (body.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.Number)
        {
            return node.TryGetDouble(out value);
        }

        return false;
    }
}
