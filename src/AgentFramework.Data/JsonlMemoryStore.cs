using System.Text;
using System.Text.Json;
using AgentFramework.Contracts;

namespace AgentFramework.Data;

public sealed class MemoryStoreOptions
{
    /// <summary>全局记忆文件（跨项目、跨会话）。</summary>
    public required string GlobalPath { get; set; }

    /// <summary>项目记忆文件（随工作区走）。</summary>
    public required string ProjectPath { get; set; }

    /// <summary>
    /// 宿主工作区根（多项目规范化用）：抽象作用域 "project" 需要折算成
    /// project:{工作区根} 才能与动态项目作用域共享同一缓存键/文件。
    /// 不设则抽象与映射作用域视为不同层（无多项目需求的老用法）。
    /// </summary>
    public string? WorkspaceRoot { get; set; }

    /// <summary>单条记忆的最大字符数（记忆是"结论"，不该是"文档"）。</summary>
    public int MaxTextChars { get; set; } = 2_000;
}

/// <summary>
/// 分级记忆的 JSONL 实现 —— <b>append-only、零依赖</b>。
///
/// 为什么不用数据库：记忆是**真相源**（不可从事件流重建），
/// 而真相源的纪律我们已经定过 —— 纯文本、可读、可 grep、可 diff、崩溃最多坏最后一行。
/// 检索靠内存扫描：记忆是"结论集"，量级在几百条，不是几万条。
///
/// <b>文件里存的是「变更事件」，不是「当前状态」</b>（见 <see cref="MemoryKinds"/>）：
///   · 改主意 → 追加一条同 <c>slot</c> 的新事实，旧的自动退出视图
///   · 撤销   → 追加一条 <c>retract</c>，指向目标 id
/// 每一次读取都用 <see cref="Fold"/> 折叠出当前视图。
/// 于是「更新/撤销」不需要任何原地改写，历史永远可回放、可审计 ——
/// 这与会话事件流是同一套纪律，也是 Zep/Graphiti 那条「失效而非删除」。
///
/// 文件布局：
///   <c>&lt;sessions&gt;/memory/global.jsonl</c>  —— 全局（跨项目）
///   <c>&lt;workspace&gt;/.agent-memory/project.jsonl</c> —— 项目（随工作区带走）
/// </summary>
public sealed class JsonlMemoryStore(MemoryStoreOptions options, Func<string, string>? projectScopeResolver = null) : IMemoryStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>低于这个分就当作没命中 —— 挡住「沾一个字就算相关」的噪音。</summary>
    private const double MinScore = 1.0;

    /// <summary>
    /// 合并批次在文件里的 kind 标记。
    /// 它是「一行多 ops」的包装行种类，不是 <see cref="MemoryEntry.Kind"/> ——
    /// 读入时会展开成多条独立事件，折叠视图因此与旧格式完全一致。
    /// </summary>
    private const string BatchKindMerge = "merge";

    /// <summary>
    /// 合并的原子事件载荷：一行 JSON 里装着「全部 ops」（N 条 retract + 1 条 assert）。
    /// 崩溃要么整行落盘、要么只是半行被跳过 —— 不会留下「已撤销、未合并」。
    /// 读旧格式（单事件行）时不会遇到它；读到 batch 行则展开 ops 后再折叠。
    /// </summary>
    private sealed record MemoryBatch
    {
        public string Kind { get; init; } = BatchKindMerge;

        public string Scope { get; init; } = "";

        public List<MemoryEntry> Ops { get; init; } = [];
    }

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// 折叠结果缓存（P3c）：scope → 折叠后的视图。
    ///
    /// 一轮对话至少读两次记忆（常驻索引卡 + 按当轮输入召回），
    /// 而每次读都要「全量读文件 + 全量折叠」—— 文件越大这笔账越明显；
    /// 折叠本身还是**纯函数**，算第二遍纯属浪费。
    ///
    /// 失效只在 <see cref="WriteAsync"/> 里做（那是唯一写入口），
    /// 且与写盘处于同一把闸内 —— 否则「写完但还没失效」的那一瞬间，
    /// <see cref="Fold"/> 会把旧视图当成新视图返回。
    /// 进程外直接改文件不会自动发现：这是自用单进程工具，取这份简单。
    /// </summary>
    /// <summary>折叠视图：主视图（进索引卡/主检索）+ 归档层（只进管理面板）。</summary>
    private sealed record FoldView(List<MemoryEntry> Active, List<MemoryEntry> Archived);

    private readonly Dictionary<string, FoldView> _foldCache = new(StringComparer.Ordinal);

    /// <summary>
    /// 写入世代计数（v3.5 审查 P2）：用来判定「这次折叠是否与写入交叠」。
    /// 每次落盘 + 失效缓存时 +1；折叠开始时取快照，回填前比对 —— 不等就说明视图已过期，不回填。
    /// </summary>
    private long _writeGeneration;

    public string Kind => "jsonl";

    public async ValueTask<MemoryEntry> AppendAsync(
        string scope,
        string text,
        IEnumerable<string>? tags = null,
        string? sourceSession = null,
        string source = "agent",
        string? slot = null,
        CancellationToken ct = default)
    {
        var entry = new MemoryEntry
        {
            Kind = MemoryKinds.Assert,
            Scope = scope,
            Text = Truncate((text ?? string.Empty).Trim()),
            Tags = tags is null ? [] : [.. tags],
            SourceSession = sourceSession,
            Source = source,
            Slot = string.IsNullOrWhiteSpace(slot) ? null : slot.Trim(),
            // 初始温度分：新条目不至被老热门永远压住（Order：重要 > 分数 > 时间）
            Score = 1,
        };

        await WriteAsync(entry, ct).ConfigureAwait(false);
        return entry;
    }

    public async ValueTask<MemoryEntry?> RetractAsync(
        string scope,
        string id,
        string source = "user",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        // 目标必须真的存在（主视图或归档层）—— 撤销一条早被取代的记忆没有意义，
        // 也不该往日志里写无用的 retract（日志要能一眼看懂）。
        var view = Fold(scope);
        var exists = view.Active.Concat(view.Archived).Any(e => string.Equals(e.Id, id, StringComparison.Ordinal));
        if (!exists)
        {
            return null;
        }

        var entry = new MemoryEntry
        {
            Kind = MemoryKinds.Retract,
            Scope = scope,
            Text = string.Empty,
            TargetId = id.Trim(),
            Source = source,
        };

        await WriteAsync(entry, ct).ConfigureAwait(false);
        return entry;
    }

    public ValueTask<IReadOnlyList<MemoryEntry>> LoadAsync(string scope, int limit, CancellationToken ct = default)
    {
        var recent = Fold(scope).Active
            .TakeLast(Math.Max(1, limit))
            .ToList();

        return ValueTask.FromResult<IReadOnlyList<MemoryEntry>>(recent);
    }

    /// <inheritdoc />
    public async ValueTask<MemoryEntry?> ArchiveAsync(
        string scope,
        string id,
        string source = "user",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        // 目标必须在**主视图**里 —— 归档一条已归档条目没有意义，也不写重复事件。
        var current = Fold(scope).Active.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));
        if (current is null)
        {
            return null;
        }

        await WriteAsync(new MemoryEntry
        {
            Kind = MemoryKinds.Archive,
            Scope = scope,
            Text = string.Empty,
            TargetId = id.Trim(),
            Source = source,
        }, ct).ConfigureAwait(false);

        return current;
    }

    /// <inheritdoc />
    public async ValueTask<MemoryEntry?> RestoreAsync(
        string scope,
        string id,
        string source = "user",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var archived = Fold(scope).Archived.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));
        if (archived is null)
        {
            return null;
        }

        await WriteAsync(new MemoryEntry
        {
            Kind = MemoryKinds.Restore,
            Scope = scope,
            Text = string.Empty,
            TargetId = id.Trim(),
            Source = source,
        }, ct).ConfigureAwait(false);

        return archived;
    }

    /// <inheritdoc />
    public async ValueTask<MemoryEntry> MergeAsync(
        string scope,
        IReadOnlyList<string> ids,
        string text,
        string? sourceSession = null,
        string source = "user",
        CancellationToken ct = default)
    {
        if (ids is null || ids.Count < 2)
        {
            throw new ArgumentException("合并至少需要两条来源记忆（单条没有合并语义）", nameof(ids));
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("合并后的正文不能为空", nameof(text));
        }

        // 预检：全部来源必须在主视图（合并是对**活跃记忆**的整理动作）
        var view = Fold(scope);
        var sources = new List<MemoryEntry>();
        foreach (var id in ids)
        {
            var found = view.Active.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal))
                ?? throw new ArgumentException($"来源记忆不存在或已归档：{id}", nameof(ids));
            sources.Add(found);
        }

        // 逐条 retract（历史一个字不改）+ 追加合并后的新 assert，
        // 但落盘是**单条原子事件**（一行 batch）：中途崩溃 = 这一行要么完整、要么只是半行被跳过，
        // 不会出现「已撤销、未合并」的永久丢失 —— 记忆不可丢。
        var retracts = sources.ConvertAll(src => new MemoryEntry
        {
            Kind = MemoryKinds.Retract,
            Scope = scope,
            Text = string.Empty,
            TargetId = src.Id,
            Source = source,
        });

        var merged = new MemoryEntry
        {
            Kind = MemoryKinds.Assert,
            Scope = scope,
            Text = Truncate(text.Trim()),
            // tags = 来源 tags 的并集 + 全部来源 id（src:前缀）—— 审计与回滚的凭据
            Tags = sources.SelectMany(x => x.Tags).Concat(sources.Select(x => "src:" + x.Id))
                .Distinct(StringComparer.Ordinal).ToList(),
            Score = Math.Max(1, sources.Max(x => x.Score)),
            // v3.5 审查 P2：置顶标记也要继承 —— 一条被用户显式钉住的约定，
            // 不该因为「被合并」就丢掉 IsImportant 语义（否则下次清扫会把它当冷条目扫走）。
            IsImportant = sources.Any(x => x.IsImportant),
            SourceSession = sourceSession,
            Source = source,
        };

        await WriteBatchAsync(new MemoryBatch
        {
            Kind = BatchKindMerge,
            Scope = scope,
            Ops = [.. retracts, merged],
        }, ct).ConfigureAwait(false);
        return merged;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<MemoryEntry>> LoadArchivedAsync(string scope, int limit, CancellationToken ct = default)
    {
        var archived = Fold(scope).Archived
            .TakeLast(Math.Max(1, limit))
            .ToList();

        return ValueTask.FromResult<IReadOnlyList<MemoryEntry>>(archived);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<MemoryEntry>> PreviewSweepAsync(
        string scope,
        DateTimeOffset now,
        SweepPolicy policy,
        CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<MemoryEntry>>(SleepCandidates(scope, now, policy));

    /// <inheritdoc />
    public async ValueTask<int> SweepAsync(
        string scope,
        DateTimeOffset now,
        SweepPolicy policy,
        string source = "system",
        CancellationToken ct = default)
    {
        // 快照先行（每条 archive 都会失效缓存，边扫边写会自我干扰）
        var candidates = SleepCandidates(scope, now, policy);

        var count = 0;
        foreach (var entry in candidates)
        {
            var archived = await ArchiveAsync(scope, entry.Id, source, ct).ConfigureAwait(false);
            if (archived is not null)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// 休眠候选 —— 降档判据的**唯一**出处（预览与执行共用，杜绝两边口径漂移）。
    ///
    /// 判据是「使用频率」，不是「存了多久」：
    ///   热度 = (1 + 使用次数) × 0.5^(闲置天数 / 半衰期) &lt; 阈值  ⇒ 休眠。
    /// 时间只以「半衰期」的形式出现在衰减指数里，**从不单独作为门槛**：
    ///   · 越用越新 —— 每用一次，次数 +1、衰减时钟归零 → 热度只增不减 → 老而常用永不掉线；
    ///   · 寿命是频率的函数 —— 用过一次就把衰减阈值推后一个半衰期，不是"满 30 天必扫"。
    /// </summary>
    private List<MemoryEntry> SleepCandidates(string scope, DateTimeOffset now, SweepPolicy policy)
        => Fold(scope).Active
            .Where(e => !e.IsImportant && DecayedHeat(e, now, policy) < policy.MinEffectiveScore)
            .ToList();

    /// <summary>
    /// 按半衰期衰减后的有效热度（纯函数，可单测；时间在此以衰减曲线参与，不作门槛）。
    /// 公开——验证工程要直接断言频率与时间的关系。
    /// </summary>
    public static double DecayedHeat(MemoryEntry entry, DateTimeOffset now, SweepPolicy policy)
    {
        var last = entry.LastUsedAt ?? entry.CreatedAt;
        var idleDays = Math.Max(0.0, (now - last).TotalDays);
        var halfLife = Math.Max(0.1, policy.HalfLifeDays);
        return (1 + entry.UseCount) * Math.Pow(0.5, idleDays / halfLife);
    }

    /// <summary>热升温事件（score）本身不是记忆，不进视图 —— 热度已叠加到目标条目上。</summary>
    private static bool IsTemperatureEvent(MemoryEntry e)
        => string.Equals(e.Kind, MemoryKinds.Score, StringComparison.Ordinal);

    /// <summary>
    /// 按关键词检索（<paramref name="scope"/> 为 null 时跨层级）。
    ///
    /// **打分，而不是「包含 / 不包含」** —— 因为中文没有词边界，
    /// 朴素的子串匹配在中文上近乎失效（与 SQLite FTS5 默认分词器踩的是同一个坑：
    /// <c>unicode61</c> 会把一整串汉字当成一个 token）。
    ///
    /// 这里用 **bigram 切分 + 多信号打分**：
    ///   精确子串命中（最强） &gt; 标签命中 &gt; token 覆盖率
    /// 零依赖、不需要向量库；记忆是「结论集」，量级在几百条，这个精度足够。
    /// </summary>
    public ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(
        string query,
        string? scope,
        int limit,
        CancellationToken ct = default)
    {
        var needle = (query ?? string.Empty).Trim();
        if (needle.Length == 0)
        {
            return ValueTask.FromResult<IReadOnlyList<MemoryEntry>>([]);
        }

        var queryTokens = Tokenize(needle);
        var scored = new List<(MemoryEntry Entry, double Score)>();

        foreach (var target in Scopes(scope))
        {
            foreach (var entry in Fold(target).Active)
            {
                var score = Score(entry, needle, queryTokens);
                if (score >= MinScore)
                {
                    scored.Add((entry, score));
                }
            }
        }

        var ordered = scored
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Entry.CreatedAt)
            .Take(Math.Max(1, limit))
            .Select(x => x.Entry)
            .ToList();

        return ValueTask.FromResult<IReadOnlyList<MemoryEntry>>(ordered);
    }

    public ValueTask<int> CountAsync(string scope, CancellationToken ct = default)
        => ValueTask.FromResult(Fold(scope).Active.Count);

    /// <summary>
    /// 给一条记忆「升温」：追加一条 <see cref="MemoryKinds.Score"/> 事件，只增不减。
    /// 目标必须存在于**当前视图**（被撤销/被取代的条目升温没有意义，也不该写垃圾事件）。
    /// </summary>
    public async ValueTask<MemoryEntry?> RecordHitAsync(
        string scope,
        string id,
        int delta = 1,
        bool markImportant = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var current = Fold(scope).Active.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));
        if (current is null)
        {
            return null;
        }

        var entry = new MemoryEntry
        {
            Kind = MemoryKinds.Score,
            Scope = scope,
            Text = string.Empty,
            TargetId = id.Trim(),
            Score = Math.Max(0, delta),
            IsImportant = markImportant,
            SourceSession = current.SourceSession,
        };

        await WriteAsync(entry, ct).ConfigureAwait(false);
        return current with { Score = current.Score + entry.Score, IsImportant = current.IsImportant || markImportant };
    }

    /// <inheritdoc />
    public async ValueTask<int> TouchAsync(
        string scope,
        IReadOnlyCollection<string> ids,
        string? sourceSession = null,
        CancellationToken ct = default)
    {
        if (ids.Count == 0)
        {
            return 0;
        }

        // 目标必须存在于当前视图（撤销 / 被取代的不该写垃圾事件）。
        var live = Fold(scope).Active
            .Where(e => ids.Contains(e.Id, StringComparer.Ordinal))
            .ToList();

        if (live.Count == 0)
        {
            return 0;
        }

        // delta = 0：只记「被用过」（次数 +1、刷新最近使用），**不动排序热度** ——
        // 排序热度留给显式召回，避免常驻条目自我强化、把索引卡锁死。
        // 一轮合成**一条 batch** 落盘（与 merge 同一套"一行多 ops"机制），多次使用只付一次 fsync。
        var ops = live.Select(e => new MemoryEntry
        {
            Kind = MemoryKinds.Score,
            Scope = scope,
            Text = string.Empty,
            TargetId = e.Id,
            Score = 0,
            SourceSession = sourceSession ?? e.SourceSession,
            Source = "recall",
        }).ToList();

        await WriteBatchAsync(new MemoryBatch
        {
            Kind = "touch",
            Scope = scope,
            Ops = ops,
        }, ct).ConfigureAwait(false);

        return ops.Count;
    }

    private static IEnumerable<string> Scopes(string? scope)
        => scope is null ? [MemoryScope.Global, MemoryScope.Project] : [scope];

    /// <summary>
    /// 作用域规范化：抽象的 <see cref="MemoryScope.Project"/> 与映射后的
    /// <c>project:{目录}</c> 可能指向**同一个物理文件**（默认项目 = 宿主工作区）。
    /// 两个键共存会让写失效 A 键、读命中 B 键 —— 缓存陈旧。所有公开入口先过这里，
    /// 保证同一文件永远只有一个缓存键。
    /// </summary>
    private string NormalizeScope(string scope)
    {
        if (scope == MemoryScope.Project
            && projectScopeResolver is not null
            && !string.IsNullOrWhiteSpace(options.WorkspaceRoot))
        {
            return MemoryScope.ProjectFor(options.WorkspaceRoot);
        }

        return scope;
    }

    private string PathFor(string scope)
    {
        // 多项目作用域：project:{绝对目录} → 该目录下的 .agent-memory/project.jsonl。
        // 目录内嵌在作用域 id 里 —— 同一存储实例可以服务任意多个项目，互不串。
        if (scope is not null && scope.StartsWith(MemoryScope.ProjectPrefix, StringComparison.Ordinal))
        {
            var dir = MemoryScope.ProjectDirOf(scope)!;
            return projectScopeResolver?.Invoke(dir)
                ?? Path.Combine(dir, ".agent-memory", "project.jsonl");
        }

        return scope switch
        {
            MemoryScope.Global => options.GlobalPath,
            MemoryScope.Project => options.ProjectPath,
            _ => throw new ArgumentException($"未知记忆层级：{scope}", nameof(scope)),
        };
    }

    /// <summary>读出**原始事件**（含 assert 与 retract），不做任何折叠。</summary>
    private List<MemoryEntry> Read(string scope)
    {
        var path = PathFor(scope);
        var entries = new List<MemoryEntry>();

        if (!File.Exists(path))
        {
            return entries;
        }

        // 宽容共享读（与 JsonlEventLog.Read 同一套纪律）：写方随时可能持有追加句柄。
        // File.ReadLines 的默认共享模式不容忍并发写句柄，Windows 上会直接抛「文件被占用」。
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                // batch 行（合并的原子事件）→ 展开 ops；旧格式单事件行 → 原样收下。
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("ops", out _))
                {
                    var batch = JsonSerializer.Deserialize<MemoryBatch>(line, SerializerOptions);
                    if (batch is not null)
                    {
                        entries.AddRange(batch.Ops);
                    }

                    continue;
                }

                var entry = JsonSerializer.Deserialize<MemoryEntry>(line, SerializerOptions);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }
            catch (JsonException)
            {
                // 坏行跳过 —— 与事件日志同一条纪律：前面的记录仍然有效
            }
        }

        return entries;
    }

    /// <summary>
    /// 把事件流**折叠**成当前视图。三条规则，都很朴素：
    ///   1. <c>retract</c> 事件本身不是记忆，不出现
    ///   2. 被任何 <c>retract</c> 指到的条目退出
    ///   3. 同一 <c>slot</c> 上，后写的取代先写的
    /// 折叠是**纯函数**：同样的文件永远得到同样的视图，所以「现在是什么」可以随时重算。
    /// </summary>
    private FoldView Fold(string scope)
    {
        scope = NormalizeScope(scope);

        long generation;
        lock (_foldCache)
        {
            if (_foldCache.TryGetValue(scope, out var cached))
            {
                return cached;
            }

            generation = _writeGeneration;
        }

        var view = FoldCore(scope);

        lock (_foldCache)
        {
            // v3.5 审查 P2：折叠期间若发生过写入，这份视图已经过期 —— 不回填。
            // （原实现在锁外折叠、锁内无条件回填：读线程能把写线程刚失效掉的缓存
            //  又用旧内容盖回去，于是陈旧视图一直驻留到下一次写。）
            if (generation == _writeGeneration)
            {
                _foldCache[scope] = view;
            }
        }

        return view;
    }

    /// <summary>真正干活的那一半：读事件 + 折叠成（主视图, 归档层）。纯函数，所以可缓存。</summary>
    private FoldView FoldCore(string scope)
    {
        var events = Read(scope);
        var active = new List<MemoryEntry>();
        var archived = new List<MemoryEntry>();
        if (events.Count == 0)
        {
            return new FoldView(active, archived);
        }

        var retracted = new HashSet<string>(StringComparer.Ordinal);
        var scores = new Dictionary<string, int>(StringComparer.Ordinal);
        var important = new HashSet<string>(StringComparer.Ordinal);
        // 使用频率视图：由 score 事件折叠得出（次数 + 最近一次使用时间）—— 降档判据的主信号。
        // 与排序热度（scores）分开：隐式消费（delta=0）只涨次数、刷新时间，不动排序热度。
        var useCount = new Dictionary<string, int>(StringComparer.Ordinal);
        var lastUsed = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        // 归档是**最后一次事件赢**（archive → 入档；restore → 出档），可反复升降级
        var archivedIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var e in events)
        {
            if (e.IsRetraction && !string.IsNullOrWhiteSpace(e.TargetId))
            {
                retracted.Add(e.TargetId);
            }

            if (string.Equals(e.Kind, MemoryKinds.Archive, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(e.TargetId))
            {
                archivedIds.Add(e.TargetId);
            }

            if (string.Equals(e.Kind, MemoryKinds.Restore, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(e.TargetId))
            {
                archivedIds.Remove(e.TargetId);
            }

            // score 事件与 retract/slot 同一份时间序：按写入顺序累加，
            // 「现在的热度」永远可以由文件重算 —— 与折叠同一条纪律。
            if (string.Equals(e.Kind, MemoryKinds.Score, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(e.TargetId))
            {
                var target = e.TargetId;

                // 排序热度：score 事件的 delta 累加（显式召回 delta>0；隐式消费 delta=0，不动它）。
                scores.TryGetValue(target, out var soFar);
                scores[target] = soFar + e.Score;

                // 使用频率：每一次「使用事件」都算一次（不论 delta 是否为 0）。
                useCount.TryGetValue(target, out var uses);
                useCount[target] = uses + 1;

                // 最近使用：事件自带的写入时间，就是「这次使用发生在何时」。
                if (!lastUsed.TryGetValue(target, out var seen) || e.CreatedAt > seen)
                {
                    lastUsed[target] = e.CreatedAt;
                }

                if (e.IsImportant)
                {
                    important.Add(target);
                }
            }
        }

        foreach (var e in events)
        {
            // 视图只由 assert 事件构成 —— retract/score/archive/restore 全是"链接事件"，
            // 语义已在上方预扫时折叠进 retracted/scores/archivedIds，本体不再是记忆。
            // 漏掉这一条的话，archive/restore 事件会以空正文记忆的身份混进主视图（实测踩中）。
            if (e.IsRetraction || retracted.Contains(e.Id)
                || !string.Equals(e.Kind, MemoryKinds.Assert, StringComparison.Ordinal))
            {
                continue;
            }

            var entry = ApplyTemperature(e, scores, important, useCount, lastUsed);

            if (archivedIds.Contains(e.Id))
            {
                // 归档条目冻结：不参与槽位替换（回来时还是原来那条）。
                archived.Add(entry);
                continue;
            }

            if (e.Slot is not null)
            {
                var at = active.FindIndex(a => string.Equals(a.Slot, e.Slot, StringComparison.Ordinal));
                if (at >= 0)
                {
                    // 同槽位：后来的取代先前的（事件按写入顺序枚举）。
                    // 注意：取代是**整体退出**——被换下那条的累计热度不转移，
                    // 新条目带自己的初始分重新开始。
                    active[at] = entry;
                    continue;
                }
            }

            active.Add(entry);
        }

        return new FoldView(active, archived);
    }

    /// <summary>
    /// 把折叠出的热度叠加回条目（纯函数，只改 Score/IsImportant/UseCount/LastUsedAt 四个**视图字段**，
    /// 不落盘）。UseCount / LastUsedAt 是降档判据的主信号，与排序热度 Score 分开维护。
    /// </summary>
    private static MemoryEntry ApplyTemperature(
        MemoryEntry entry,
        Dictionary<string, int> scores,
        HashSet<string> important,
        Dictionary<string, int> useCount,
        Dictionary<string, DateTimeOffset> lastUsed)
    {
        scores.TryGetValue(entry.Id, out var bonus);
        useCount.TryGetValue(entry.Id, out var uses);
        lastUsed.TryGetValue(entry.Id, out var seen);

        return entry with
        {
            Score = entry.Score + bonus,
            IsImportant = entry.IsImportant || important.Contains(entry.Id),
            UseCount = uses,
            LastUsedAt = seen == default ? null : seen,
        };
    }

    private async ValueTask WriteAsync(MemoryEntry entry, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            entry = entry with { Scope = NormalizeScope(entry.Scope) };
            var path = PathFor(entry.Scope);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            await using var writer = new StreamWriter(stream);
            await writer.WriteLineAsync(JsonSerializer.Serialize(entry, SerializerOptions)).ConfigureAwait(false);
            await writer.FlushAsync(ct).ConfigureAwait(false);

            // 与事件日志同一条纪律：每条都刷到磁盘，崩溃安全优先于吞吐。
            // 记忆是真相源（不可从事件流重建），这里的代价完全值得。
            stream.Flush(flushToDisk: true);

            // ★ 失效放在**同一把闸里**（P3c）：写已经落盘，紧接着丢掉这份缓存。
            //   放到闸外就有个缝隙 —— 那一瞬间 Fold 会命中已经过期的视图。
            lock (_foldCache)
            {
                _foldCache.Remove(entry.Scope);
                _writeGeneration++;      // 让「正在折叠中」的读线程知道自己的结果已过期
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 写入一条**原子 batch**（一行装下全部 ops）。合并走这里：
    /// 中途崩溃时整行要么完整落盘、要么只是半行被跳过，不会出现「已撤销、未合并」。
    /// </summary>
    private async ValueTask WriteBatchAsync(MemoryBatch batch, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var scope = NormalizeScope(batch.Scope);
            batch = batch with
            {
                Scope = scope,
                Ops = [.. batch.Ops.Select(op => op with { Scope = scope })],
            };

            var path = PathFor(scope);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            await using var writer = new StreamWriter(stream);
            await writer.WriteLineAsync(JsonSerializer.Serialize(batch, SerializerOptions)).ConfigureAwait(false);
            await writer.FlushAsync(ct).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);

            lock (_foldCache)
            {
                _foldCache.Remove(scope);
                _writeGeneration++;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static double Score(MemoryEntry entry, string needle, IReadOnlyList<string> queryTokens)
    {
        var text = entry.Text;
        var score = 0.0;

        if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
        {
            score += 10;   // 直接命中关键词：最强信号
        }

        if (entry.Tags.Any(t => t.Contains(needle, StringComparison.OrdinalIgnoreCase)))
        {
            score += 6;
        }

        if (queryTokens.Count == 0)
        {
            return score;
        }

        var entryTokens = new HashSet<string>(Tokenize(text), StringComparer.Ordinal);

        foreach (var tag in entry.Tags)
        {
            foreach (var token in Tokenize(tag))
            {
                entryTokens.Add(token);
            }
        }

        var hits = queryTokens.Count(entryTokens.Contains);

        // 两个信号叠加，各自补对方的短处：
        //   · 覆盖率     —— 命中的 query token 占比（防「只沾一个字」）
        //   · 命中绝对量 —— 短 query 遇上长记忆时，覆盖率分母会虚高，
        //                   绝对量能把「确实命中了一个实词」的记忆救回来
        score += 5.0 * hits / queryTokens.Count + 1.5 * Math.Min(hits, 3);

        return score;
    }

    /// <summary>
    /// 切 token：拉丁文按词（转小写），CJK 按 **bigram**（相邻两字）。
    /// 中文没有空格，bigram 是零依赖且效果稳定的近似 ——
    /// 与 SQLite FTS5 的 bigram 修法是同一个思路。
    /// </summary>
    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var latin = new StringBuilder();
        var cjk = new List<char>();

        void FlushLatin()
        {
            if (latin.Length > 0)
            {
                tokens.Add(latin.ToString().ToLowerInvariant());
                latin.Clear();
            }
        }

        void FlushCjk()
        {
            if (cjk.Count == 1)
            {
                tokens.Add(cjk[0].ToString());
            }

            for (var i = 0; i + 1 < cjk.Count; i++)
            {
                tokens.Add(new string([cjk[i], cjk[i + 1]]));
            }

            cjk.Clear();
        }

        foreach (var ch in text)
        {
            if (IsCjk(ch))
            {
                FlushLatin();
                cjk.Add(ch);
            }
            else if (char.IsLetterOrDigit(ch))
            {
                FlushCjk();
                latin.Append(ch);
            }
            else
            {
                FlushLatin();
                FlushCjk();
            }
        }

        FlushLatin();
        FlushCjk();
        return tokens;
    }

    private static bool IsCjk(char ch)
        => (ch >= '\u3400' && ch <= '\u4DBF')     // 扩展 A
        || (ch >= '\u4E00' && ch <= '\u9FFF')     // 基本区
        || (ch >= '\uF900' && ch <= '\uFAFF');    // 兼容区

    private string Truncate(string text)
        => text.Length <= options.MaxTextChars ? text : text[..options.MaxTextChars];
}
