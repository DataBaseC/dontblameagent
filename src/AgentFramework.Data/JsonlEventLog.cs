using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentFramework.Contracts;

namespace AgentFramework.Data;

/// <summary>
/// 仅追加的 JSONL 事件日志。
///
/// 为什么是 JSONL 而不是数据库（DESIGN.md 4.2 / 决策记录）：
///   1. <b>一行 = 一条完整记录</b> → 写入天然原子。崩在中间最多坏最后一行，前面全部可读。
///   2. 纯文本、可读、可 grep、可 diff、可迁移，出问题肉眼能查。
///   3. 顺序追加、无需事务、无需索引，写入路径极短 —— 真相源就该这么简单。
///
/// 配套的 SQLite 只做「派生索引」，随时可从这个文件重建；索引坏了不心疼，本文件坏了才是灾难。
/// </summary>
public sealed class JsonlEventLog : IDisposable
{
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly object _gate = new();
    private readonly FileStream _stream;
    private readonly StreamWriter _writer;
    private long _lastSeq;
    private bool _disposed;

    private JsonlEventLog(string path, FileStream stream, StreamWriter writer, long lastSeq, long count)
    {
        Path = path;
        _stream = stream;
        _writer = writer;
        _lastSeq = lastSeq;
        Count = count;
    }

    /// <summary>日志文件路径。</summary>
    public string Path { get; }

    /// <summary>已写入的最大序号。</summary>
    public long LastSeq
    {
        get
        {
            lock (_gate)
            {
                return _lastSeq;
            }
        }
    }

    /// <summary>事件条数。</summary>
    public long Count { get; private set; }

    /// <summary>
    /// 打开（或创建）日志。
    ///
    /// 打开时会扫描已有内容：既能确定序号起点（所以「重启后接着写」是天然成立的），
    /// 也会<b>修复</b>尾部可能存在的半行垃圾 —— 把文件截断到最后一个完整记录。
    /// 中段坏行不会被截掉（其后的完整事件仍是真相源），读取时按「坏行跳过」处理。
    /// 不做修复的话，崩溃留下的尾部半行会让后续追加写在同一截垃圾后面。
    /// </summary>
    public static JsonlEventLog Open(string path)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);

        var (lastSeq, count) = ScanAndRepair(fullPath);

        var stream = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        var writer = new StreamWriter(stream) { AutoFlush = false };

        return new JsonlEventLog(fullPath, stream, writer, lastSeq, count);
    }

    /// <summary>追加一条事件。序号与时间戳由日志分配（日志才是真相源）。</summary>
    public T Append<T>(T sessionEvent)
        where T : SessionEvent
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            sessionEvent.Seq = ++_lastSeq;
            if (sessionEvent.Timestamp == default)
            {
                sessionEvent.Timestamp = DateTimeOffset.UtcNow;
            }

            // 必须显式以 SessionEvent 为静态类型序列化。
            // 若让泛型重载推断成具体类型（如 SessionCreatedEvent），JSON 里就不会写
            // 多态判别字段，读回来时无法还原事件类型 —— 这个坑很隐蔽。
            _writer.Write(JsonSerializer.Serialize<SessionEvent>(sessionEvent, SerializerOptions));
            _writer.Write('\n');
            _writer.Flush();

            // 每条都刷到磁盘：崩溃安全优先于吞吐。
            // 事件日志是低频写入，这个代价完全值得。
            _stream.Flush(flushToDisk: true);

            Count++;
            return sessionEvent;
        }
    }

    /// <summary>读取本日志的全部事件。</summary>
    public IEnumerable<SessionEvent> ReadAll() => Read(Path);

    /// <summary>
    /// 读取 JSONL 文件（只读，不修改文件）。
    /// <b>崩溃安全</b>：遇到解析不了的行（典型是崩溃时写了一半的最后一行）即跳过，
    /// 继续读后面的行 —— 中段偶发坏行不该把其后完整事件一起藏起来。
    /// 若要让文件本身恢复一致（截掉尾部半行），用 <see cref="Open"/>。
    /// </summary>
    public static IEnumerable<SessionEvent> Read(string path)
    {
        if (!File.Exists(path))
        {
            yield break;
        }

        // 以宽容共享模式打开（FileShare.ReadWrite | Delete）：
        // 写方（JsonlEventLog.Open 持有的追加句柄）随时可能开着本文件 ——
        // 本进程内读自己正在写的日志（SessionRuntime 构造、快照增量、UI 计数）
        // 是常规路径；若这里用 File.ReadLines 的默认共享模式（FileShare.Read，
        // 不容忍别人持有写句柄），Windows 上必然抛「文件正由另一进程使用」。
        // 日志只追加、行内原子，读到「写到一半的最后一行」由 TryParse 兜底。
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parsed = TryParse(line);
            if (parsed is null)
            {
                // 坏行跳过（铁律 7）：其后的完整事件仍然有效
                continue;
            }

            yield return parsed;
        }
    }

    /// <summary>读取日志文件的**原始文本**（宽容共享模式，容忍写句柄还开着）。
    /// 供「落盘内容长什么样」的断言与人工检查用；结构化读取请走 <see cref="Read"/>。</summary>
    public static string ReadRawText(string path)
    {
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        return Encoding.UTF8.GetString(ReadAllBytesTolerant(path));
    }

    private static SessionEvent? TryParse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<SessionEvent>(line, SerializerOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // 损坏行、或缺少多态判别字段 —— 都算"读不动"，由调用方决定停在哪里
            return null;
        }
    }

    /// <summary>
    /// 扫描文件并修复尾部垃圾，返回 (最大序号, 事件条数)。
    ///
    /// 实现上把整个文件读进内存按行处理。事件日志单会话量级为 MB 以内，
    /// 启动时一次性扫描可以接受；将来会话规模上去了再换成分块流式扫描。
    ///
    /// <b>只许修尾巴</b>：崩溃留下的「半行」可以截掉；但若坏行之后还有完整好行
    /// （中段损坏：磁盘坏道、手工误编辑），那些好行是真相源的一部分，
    /// 绝不能跟着 <c>SetLength</c> 一起消失 —— 只截「第一个坏行起、且其后再无好行」的尾部。
    /// </summary>
    private static (long LastSeq, long Count) ScanAndRepair(string path)
    {
        if (!File.Exists(path))
        {
            return (0, 0);
        }

        // 与 Read 同理：容忍另一个实例（同会话目录多进程）持有写句柄。
        var bytes = ReadAllBytesTolerant(path);
        long lastSeq = 0;
        long count = 0;
        var lastGoodEnd = 0;
        var firstBadStart = -1;
        var sawGoodAfterBad = false;
        var start = 0;

        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != (byte)'\n')
            {
                continue;
            }

            var line = Encoding.UTF8.GetString(bytes, start, i - start);
            var lineStart = start;
            start = i + 1;

            if (string.IsNullOrWhiteSpace(line))
            {
                lastGoodEnd = i + 1;
                continue;
            }

            var parsed = TryParse(line);
            if (parsed is null)
            {
                if (firstBadStart < 0)
                {
                    firstBadStart = lineStart;
                }

                continue;
            }

            if (firstBadStart >= 0)
            {
                sawGoodAfterBad = true;
            }

            // 取最大序号：中段坏行之后的好行也要算进来，否则重启续写会撞号。
            if (parsed.Seq > lastSeq)
            {
                lastSeq = parsed.Seq;
            }

            count++;
            lastGoodEnd = i + 1;
        }

        // 尾部半行（没有换行收尾）：无论中段是否坏过，这截都是写到一半的垃圾。
        var truncateTo = start < bytes.Length ? lastGoodEnd : bytes.Length;

        if (!sawGoodAfterBad && firstBadStart >= 0)
        {
            // 尾部损坏（好行之后只剩坏行/半行）—— 从第一个坏行起截掉。
            truncateTo = firstBadStart;
        }

        if (bytes.Length > truncateTo)
        {
            // v3.6 审查修复：修复句柄也要宽容共享 —— 本文件读路径特意用 ReadWrite|Delete
            // 以容忍别的实例持有写句柄；这里若要求独占，崩溃半行 + 日志正被另一实例打开时会
            // 直接抛 IOException → 会话打不开。共享模式下截断，最坏让另一实例多扫一次坏行。
            try
            {
                using var repair = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                repair.SetLength(truncateTo);
            }
            catch (IOException)
            {
                // 无法安全截断时退化为「不修复、继续追加」：读端本就能跳坏行
            }
        }

        return (lastSeq, count);
    }

    /// <summary>以宽容共享模式读取整个文件（等价 File.ReadAllBytes，但容忍并发写句柄）。</summary>
    private static byte[] ReadAllBytesTolerant(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writer.Flush();
            _writer.Dispose();
        }
    }
}
