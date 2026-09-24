using System.Globalization;
using System.Text;
using AgentFramework.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentFramework.Index;

public sealed class SqliteIndexOptions
{
    /// <summary>单条记录最多索引多少字符 —— 一条巨长的工具输出不该把索引撑爆。</summary>
    public int MaxTextChars { get; set; } = 20_000;

    /// <summary>检索命中时，片段保留多少字符。</summary>
    public int SnippetChars { get; set; } = 400;
}

/// <summary>
/// SQLite 派生索引（DESIGN.md 4.2 / 4.16）。
///
/// <b>只做派生</b>：所有内容都能从 JSONL 重建。删掉这个 .db 文件，最坏结果是重建一次。
/// 所以任何失败都只能降级、不能阻断 —— <see cref="OpenAsync"/> 打不开就返回
/// <see cref="NullSessionIndex.Instance"/>，让主流程当"没有索引"照常跑。
///
/// 为什么是 SQLite 而不是"扫日志"：
///   1. <b>检索</b>：长任务里上下文被折叠后，得有个地方能按关键词把旧内容捞回来
///   2. <b>结构化</b>：按会话 / 类型 / 时间过滤与统计，是日志文件做不动的
///   3. <b>增量</b>：写一条进一条，不必次次全量重放
///
/// 为什么检索是「LIKE 召回 + 自己打分」，而不是 FTS5：
///   1. FTS5 的 trigram 分词要求至少 3 个字符，而中文里两字词（「报错」「超时」）太常见，
///      用它反而查不到；换成 bigram 能解决，但要额外维护一张影子表与同步逻辑。
///   2. 而中文**没有词形变化**，LIKE 子串匹配的召回本来就是准的 —— 真正缺的只是**排序**。
///   于是取舍落在「少一张表 = 少一类 bug」：**召回交给 LIKE，排序自己打分**
///   （见 <see cref="SearchAsync"/>：整串命中 &gt; 命中片段数）。
///   单会话万条以内，LIKE 扫描只要几毫秒，够用。
///   真需要 FTS（比如英文大规模检索）时，换一个 <see cref="ISessionIndex"/> 实现即可。
/// </summary>
public sealed class SqliteSessionIndex : ISessionIndex
{
    private readonly SqliteConnection _connection;
    private readonly SqliteIndexOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private SqliteSessionIndex(SqliteConnection connection, SqliteIndexOptions options)
    {
        _connection = connection;
        _options = options;
    }

    public string Kind => "sqlite";

    public bool IsAvailable => true;

    /// <summary>打开（或创建）索引；任何失败都降级为「无索引」。</summary>
    public static async Task<ISessionIndex> OpenAsync(
        string databasePath,
        SqliteIndexOptions? options = null,
        CancellationToken ct = default)
    {
        try
        {
            var fullPath = Path.GetFullPath(databasePath);
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = fullPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
            }.ToString());

            try
            {
                await connection.OpenAsync(ct).ConfigureAwait(false);

                var index = new SqliteSessionIndex(connection, options ?? new SqliteIndexOptions());
                await index.InitializeAsync(ct).ConfigureAwait(false);
                return index;
            }
            catch
            {
                // 打开/建表失败时连接已经创建 —— 不 Dispose 就成了孤儿句柄
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch (Exception)
        {
            // 原生库缺失 / 文件被占 / 磁盘满 —— 都只是"没有索引"，不是"不能用"
            return NullSessionIndex.Instance;
        }
    }

    private async Task InitializeAsync(CancellationToken ct)
    {
        await ExecuteAsync("""
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS events (
                session_id TEXT    NOT NULL,
                seq        INTEGER NOT NULL,
                type       TEXT    NOT NULL,
                ts         TEXT    NOT NULL,
                text       TEXT    NOT NULL,
                PRIMARY KEY (session_id, seq)
            );
            CREATE INDEX IF NOT EXISTS ix_events_type ON events(type);
            CREATE TABLE IF NOT EXISTS watermark (
                session_id TEXT    PRIMARY KEY,
                max_seq    INTEGER NOT NULL
            );
            """, ct).ConfigureAwait(false);
    }

    public async ValueTask IndexAsync(string sessionId, SessionEvent sessionEvent, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 水位先行：空文本事件也要推进水位。
            // 否则「最后几条恰好没有可检索文本」时 MAX(seq) 永远落后于日志，
            // 用条数/水位判断「索引是否跟上」会一直误报落后、每次启动都白重建。
            await UpsertWatermarkAsync(sessionId, sessionEvent.Seq, ct).ConfigureAwait(false);

            var text = Describe(sessionEvent);
            if (text.Length == 0)
            {
                // 没有可检索文本的事件（比如纯粹的会话创建）不进索引 —— 但水位已经推进
                return;
            }

            await using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO events(session_id, seq, type, ts, text)
                VALUES ($session, $seq, $type, $ts, $text)
                ON CONFLICT(session_id, seq) DO UPDATE SET type = excluded.type, text = excluded.text;
                """;
            Bind(command, sessionId, sessionEvent, text);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            // 单条写失败不该冒泡：日志才是真相源，索引漏一条最多是检索不到它
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask RebuildAsync(
        string sessionId,
        IEnumerable<SessionEvent> events,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var transaction = await _connection.BeginTransactionAsync(ct).ConfigureAwait(false);

            await using (var delete = _connection.CreateCommand())
            {
                delete.Transaction = (SqliteTransaction)transaction;
                delete.CommandText = "DELETE FROM events WHERE session_id = $session;";
                delete.Parameters.AddWithValue("$session", sessionId);
                await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            long maxSeq = 0;
            foreach (var sessionEvent in events)
            {
                if (sessionEvent.Seq > maxSeq)
                {
                    maxSeq = sessionEvent.Seq;
                }

                var text = Describe(sessionEvent);
                if (text.Length == 0)
                {
                    continue;
                }

                await using var insert = _connection.CreateCommand();
                insert.Transaction = (SqliteTransaction)transaction;
                insert.CommandText = """
                    INSERT OR REPLACE INTO events(session_id, seq, type, ts, text)
                    VALUES ($session, $seq, $type, $ts, $text);
                    """;
                Bind(insert, sessionId, sessionEvent, text);
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            // 重建后水位直接推到日志最大 Seq（含未进索引的空文本事件），
            // 否则「尾部几条没文本」会让水位永远差一截。
            if (maxSeq > 0)
            {
                await using var mark = _connection.CreateCommand();
                mark.Transaction = (SqliteTransaction)transaction;
                mark.CommandText = """
                    INSERT INTO watermark(session_id, max_seq) VALUES ($session, $seq)
                    ON CONFLICT(session_id) DO UPDATE SET max_seq = MAX(max_seq, excluded.max_seq);
                    """;
                mark.Parameters.AddWithValue("$session", sessionId);
                mark.Parameters.AddWithValue("$seq", maxSeq);
                await mark.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            // 重建失败就维持原样，下次再试
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 索引水位：该会话已处理到的最大事件 Seq（含未进索引的空文本事件）。
    ///
    /// 为什么不用 <see cref="CountAsync"/> 判断落后：空文本事件不进 events 表、
    /// 新类型也可能压不出可检索文本，<c>indexedCount &lt; logged.Count</c> 永不对齐，
    /// 于是每次启动都误判「落后」并白重建。Seq 水位与「日志写到哪」同一把尺子。
    /// </summary>
    public async ValueTask<long> GetIndexedWatermarkAsync(string sessionId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = "SELECT max_seq FROM watermark WHERE session_id = $session;";
            command.Parameters.AddWithValue("$session", sessionId);
            var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (value is not null && value is not DBNull)
            {
                return Convert.ToInt64(value, CultureInfo.InvariantCulture);
            }

            // 老库没有 watermark 表里的行时，退回 MAX(seq) —— 至少不比已索引的最后一条更旧
            await using var fallback = _connection.CreateCommand();
            fallback.CommandText = "SELECT MAX(seq) FROM events WHERE session_id = $session;";
            fallback.Parameters.AddWithValue("$session", sessionId);
            var max = await fallback.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return max is null || max is DBNull ? 0L : Convert.ToInt64(max, CultureInfo.InvariantCulture);
        }
        catch (SqliteException)
        {
            return 0L;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task UpsertWatermarkAsync(string sessionId, long seq, CancellationToken ct)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO watermark(session_id, max_seq) VALUES ($session, $seq)
            ON CONFLICT(session_id) DO UPDATE SET max_seq = MAX(max_seq, excluded.max_seq);
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$seq", seq);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>query 最多拆成几个片段 —— 防止一个超长 query 把 SQL 参数撑爆。</summary>
    private const int MaxFragments = 8;

    /// <summary>
    /// 检索会话历史。
    ///
    /// 两处刻意的选择：
    ///   1. <b>召回用 LIKE 子串匹配，不用 FTS5</b>。中文没有词形变化，
    ///      子串匹配的召回本来就是准的；而 FTS5 的 trigram 对「报错」「超时」这类两字词失效。
    ///   2. <b>多词 query 拆成片段、任一命中即召回</b>。整串包含太苛刻 ——
    ///      「超时 报错」这种 query，正文里语序不同就一条都捞不回来。
    ///      召回放松之后，靠下面的**打分排序**把最相关的顶到前面。
    ///
    /// 一句话：<b>召回交给 LIKE（准、零依赖），排序交给自己（可解释、可调）。</b>
    /// </summary>
    public async ValueTask<IReadOnlyList<HistoryHit>> SearchAsync(
        string query,
        string? sessionId,
        int limit,
        CancellationToken ct = default)
    {
        var needle = (query ?? string.Empty).Trim();
        if (needle.Length == 0)
        {
            return [];
        }

        var fragments = SplitFragments(needle);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var capped = Math.Clamp(limit, 1, 100);

            // 候选多取一些，给打分排序留余地
            var candidates = Math.Max(capped * 5, 50);

            var conditions = string.Join(
                " OR ",
                Enumerable.Range(0, fragments.Count).Select(i => $"text LIKE $p{i} ESCAPE '\\'"));

            var sessionClause = sessionId is null ? string.Empty : " AND session_id = $session";

            await using var command = _connection.CreateCommand();
            command.CommandText = $"""
                SELECT session_id, seq, type, ts, text
                FROM events
                WHERE ({conditions}){sessionClause}
                LIMIT $candidates;
                """;

            for (var i = 0; i < fragments.Count; i++)
            {
                command.Parameters.AddWithValue($"$p{i}", $"%{Escape(fragments[i])}%");
            }

            if (sessionId is not null)
            {
                command.Parameters.AddWithValue("$session", sessionId);
            }

            command.Parameters.AddWithValue("$candidates", candidates);

            var scored = new List<(HistoryHit Hit, double Score)>();

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var text = reader.GetString(4);

                var hit = new HistoryHit(
                    reader.GetString(0),
                    reader.GetInt64(1),
                    reader.GetString(2),
                    DateTimeOffset.TryParse(reader.GetString(3), CultureInfo.InvariantCulture, out var ts)
                        ? ts
                        : default,
                    Snippet(text, needle, fragments, _options.SnippetChars));

                scored.Add((hit, Score(text, needle, fragments)));
            }

            return scored
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Hit.Seq)
                .Take(capped)
                .Select(x => x.Hit)
                .ToList();
        }
        catch (SqliteException)
        {
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<int> CountAsync(string? sessionId = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = sessionId is null
                ? "SELECT COUNT(*) FROM events;"
                : "SELECT COUNT(*) FROM events WHERE session_id = $session;";

            if (sessionId is not null)
            {
                command.Parameters.AddWithValue("$session", sessionId);
            }

            var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return value is null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch (SqliteException)
        {
            return 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask RemoveSessionAsync(string sessionId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = """
                DELETE FROM events WHERE session_id = $session;
                DELETE FROM watermark WHERE session_id = $session;
                """;
            command.Parameters.AddWithValue("$session", sessionId);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            // 派生物清理失败不该冒泡 —— 索引本来就是可以丢的
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _gate.Dispose();
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private void Bind(SqliteCommand command, string sessionId, SessionEvent sessionEvent, string text)
    {
        if (text.Length > _options.MaxTextChars)
        {
            text = text[.._options.MaxTextChars];
        }

        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$seq", sessionEvent.Seq);
        command.Parameters.AddWithValue("$type", TypeName(sessionEvent));
        command.Parameters.AddWithValue("$ts", sessionEvent.Timestamp.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$text", text);
    }

    private async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>把一条事件压成「可检索文本」。没有文本的事件返回空串（不进索引）。</summary>
    private static string Describe(SessionEvent sessionEvent) => sessionEvent switch
    {
        UserMessageEvent user => user.ModelVisibleText,
        AssistantMessageEvent assistant => assistant.Text,
        ToolCallRequestedEvent requested =>
            $"{requested.ToolName} " + string.Join(
                ' ',
                requested.Arguments.Select(pair => $"{pair.Key}={pair.Value}")),
        ToolCallCompletedEvent completed => completed.Success
            ? completed.Output ?? string.Empty
            : $"ERROR: {completed.Error}",
        TaskCreatedEvent created => $"任务创建：{created.Title}",
        TaskStatusChangedEvent changed => $"任务状态：{changed.TaskId} → {changed.Status} {changed.Reason}",
        SessionCreatedEvent created => created.Title,
        UserInputRephrasedEvent rephrased => $"{rephrased.Original}\n→\n{rephrased.Rephrased}",
        ContextCompactedEvent compacted => $"{compacted.Summary}\n{compacted.TaskCard}",
        ModelUsageEvent usage => DescribeUsage(usage),
        ReasoningEvent reasoning => reasoning.Text,
        SubAgentDispatchedEvent dispatched => $"子Agent派发：{dispatched.Task}",
        SubAgentCompletedEvent subDone => $"子Agent完成：{subDone.Summary}",
        PlanCreatedEvent plan => $"计划创建：{string.Join('；', plan.Steps)}",
        PlanStepUpdatedEvent step => $"计划步骤 {step.Index} → {step.Status}",
        CheckpointEvent checkpoint => DescribeCheckpoint(checkpoint),
        _ => string.Empty,
    };

    /// <summary>
    /// 用量事件也得能被检索到 ——「上一轮到底花了多少」是复盘时会问的问题。
    /// 它没有天然文本，所以这里合成一句。
    /// </summary>
    private static string DescribeUsage(ModelUsageEvent usage)
        => $"用量 {usage.Model} in={usage.InputTokens} out={usage.OutputTokens} cache={usage.CachedTokens}";

    /// <summary>checkpoint 也合成一句可检索摘要（RenderBlock 可能为空，这里保证非空）。</summary>
    private static string DescribeCheckpoint(CheckpointEvent checkpoint)
        => $"checkpoint {checkpoint.Trigger} #{checkpoint.FromSeq}-{checkpoint.ToSeq} {checkpoint.RenderBlock()}";

    private static string TypeName(SessionEvent sessionEvent) => sessionEvent switch
    {
        SessionCreatedEvent => "session-created",
        UserMessageEvent => "user-message",
        AssistantMessageEvent => "assistant-message",
        ToolCallRequestedEvent => "tool-call-requested",
        ToolCallCompletedEvent => "tool-call-completed",
        TaskCreatedEvent => "task-created",
        TaskStatusChangedEvent => "task-status-changed",
        UserInputRephrasedEvent => "user-input-rephrased",
        ContextCompactedEvent => "context-compacted",
        ModelUsageEvent => "model-usage",
        ReasoningEvent => "reasoning",
        SubAgentDispatchedEvent => "subagent-dispatched",
        SubAgentCompletedEvent => "subagent-completed",
        PlanCreatedEvent => "plan-created",
        PlanStepUpdatedEvent => "plan-step-updated",
        CheckpointEvent => "checkpoint",
        _ => "unknown",
    };

    private static string Escape(string value)
        => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>片段分隔符：空白与常见中英文标点。</summary>
    private static readonly char[] FragmentSeparators =
    [
        ' ', '\t', '\r', '\n',
        ',', '，', '.', '。', '?', '？', '!', '！',
        ';', '；', ':', '：', '、', '|', '/', '\\',
        '(', ')', '（', '）', '[', ']', '《', '》', '「', '」',
        '"', '\'',
    ];

    /// <summary>
    /// 把 query 拆成检索片段。
    ///
    /// 中文短语（没有分隔符）会**整体**作为一个片段 —— 那正是 LIKE 子串匹配最擅长的情况，
    /// 不需要、也不应该再切。
    /// </summary>
    private static List<string> SplitFragments(string query)
    {
        var parts = query
            .Split(FragmentSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxFragments)
            .ToList();

        return parts.Count == 0 ? [query] : parts;
    }

    /// <summary>
    /// 相关度打分。两个信号：
    ///   · 整串命中（10 分）—— 最强，说明这段内容就是为它写的
    ///   · 命中片段数（每片 3 分）—— 多词 query 里命中越全越相关
    /// 同分时按序号倒序，保证「最近看到的在前」。
    /// </summary>
    private static double Score(string text, string needle, IReadOnlyList<string> fragments)
    {
        var score = text.Contains(needle, StringComparison.OrdinalIgnoreCase) ? 10.0 : 0.0;
        score += 3.0 * fragments.Count(f => text.Contains(f, StringComparison.OrdinalIgnoreCase));
        return score;
    }

    /// <summary>
    /// 截出命中位置附近的一小段 —— 检索结果要能一眼看出「是不是我要的」。
    /// 整串没命中时退而用命中的片段定位（多词 query 的常见情形）。
    /// </summary>
    private static string Snippet(string text, string needle, IReadOnlyList<string> fragments, int maxChars)
    {
        var oneLine = text.Replace('\r', ' ').Replace('\n', ' ');

        if (oneLine.Length <= maxChars)
        {
            return oneLine;
        }

        var at = oneLine.IndexOf(needle, StringComparison.OrdinalIgnoreCase);

        if (at < 0)
        {
            foreach (var fragment in fragments)
            {
                at = oneLine.IndexOf(fragment, StringComparison.OrdinalIgnoreCase);
                if (at >= 0)
                {
                    break;
                }
            }
        }

        if (at < 0)
        {
            return oneLine[..maxChars] + "…";
        }

        var start = Math.Max(0, at - (maxChars / 3));
        var length = Math.Min(maxChars, oneLine.Length - start);

        var builder = new StringBuilder();
        if (start > 0)
        {
            builder.Append('…');
        }

        builder.Append(oneLine.AsSpan(start, length));

        if (start + length < oneLine.Length)
        {
            builder.Append('…');
        }

        return builder.ToString();
    }
}
