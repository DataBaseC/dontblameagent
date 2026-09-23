using System.Text.Json;
using AgentFramework.Contracts;

namespace AgentFramework.Data;

/// <summary>
/// 会话投影快照缓存。
///
/// <b>要解决的问题</b>：不必每次从第一条事件全量重放 ——
/// 那在长会话里是 O(n) 的启动开销，而每次打开界面都要付一遍。
///
/// <b>做法</b>：把投影结果存成快照，下次只重放「快照之后」的新事件。
///
/// <b>为什么不是 SQLite</b>（设计里原本写的是"SQLite 派生索引"）：
///   SQLite 会带来原生依赖（SQLitePCLRaw），而这个缓存真正要解决的只有"避免全量重放"，
///   快照文件完全够用，且零依赖、跨平台无忧。
///   SQLite 的真正价值在复杂查询与统计 —— 那目前用不上。
///   所以这里先把「索引」做成可替换的一层：将来真需要复杂查询，换实现即可，调用方不动。
///
/// 快照永远是可以丢的：坏了、删了，最坏结果只是重放一遍 —— 真相源始终是 JSONL。
/// </summary>
public sealed class SnapshotProjectionCache
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _cacheDir;

    public SnapshotProjectionCache(string cacheDir) => _cacheDir = cacheDir;

    /// <summary>命中快照（只重放了尾部）的次数。</summary>
    public int IncrementalHits { get; private set; }

    /// <summary>全量重放的次数。</summary>
    public int FullRebuilds { get; private set; }

    public SessionState GetOrRebuild(string logPath)
    {
        Directory.CreateDirectory(_cacheDir);

        var snapshotPath = Path.Combine(_cacheDir, Path.GetFileName(logPath) + ".snapshot.json");
        var cached = TryLoad(snapshotPath);

        if (cached is null)
        {
            FullRebuilds++;
            var state = SessionProjector.Project(JsonlEventLog.Read(logPath));
            TrySave(snapshotPath, state);
            return state;
        }

        IncrementalHits++;
        var tail = JsonlEventLog.Read(logPath).Where(e => e.Seq > cached.LastSeq);
        var merged = SessionProjector.Project(tail, cached);
        TrySave(snapshotPath, merged);
        return merged;
    }

    /// <summary>丢弃某个会话的快照（会话被删或日志被外部改动时用）。</summary>
    public void Invalidate(string logPath)
    {
        var snapshotPath = Path.Combine(_cacheDir, Path.GetFileName(logPath) + ".snapshot.json");
        try
        {
            if (File.Exists(snapshotPath))
            {
                File.Delete(snapshotPath);
            }
        }
        catch
        {
            // 删不掉也没关系，最多重放一遍
        }
    }

    private static SessionState? TryLoad(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SessionState>(File.ReadAllText(path), SerializerOptions);
        }
        catch
        {
            // 快照坏了不算事 —— 重放即可，真相源是 JSONL
            return null;
        }
    }

    private static void TrySave(string path, SessionState state)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(state, SerializerOptions));
        }
        catch
        {
            // 写不进去不影响功能，只是下次还得重放
        }
    }
}
