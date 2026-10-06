using System.Text;

namespace AgentFramework.Tools;

/// <summary>
/// 原子写：先写同目录的临时文件，再 <c>File.Move(overwrite: true)</c> 换上去
/// （对齐 dsh 的 <c>dsh-atomic-write</c>）。
///
/// <para>
/// 为什么值得做：<c>File.WriteAllText</c> 直接截断原文件再写 —— 崩在中间 / 断电 / 进程被杀，
/// 工作区里就留下一个「写了一半」的文件，而它看起来是正常的（这是最难查的一类损坏）。
/// 改文件的工作本来就该是「要么旧的、要么新的」，不该有第三种状态。
/// </para>
/// <para>
/// 附带好处：目标是符号链接时，<c>File.Move</c> 换掉的是**链接本身**，而不是顺着链接写到别处 ——
/// 与写边界防护（<c>WorkspacePath.TryResolve</c> 的 realpath 落盘）方向一致。
/// </para>
/// </summary>
internal static class AtomicFile
{
    public static void WriteAllText(string path, string content) => WriteAllText(path, content, Encoding.UTF8);

    public static void WriteAllText(string path, string content, Encoding encoding)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = TempPathFor(path);

        try
        {
            File.WriteAllText(temp, content, encoding);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // 写入本身失败（磁盘满 / 权限 / 取消）也要清掉半截临时文件 ——
            // 否则目录里会攒下一堆 `.tmp-xxxx`，看起来像正常文件。
            TryDelete(temp);
            throw;
        }
    }

    public static async Task WriteAllTextAsync(string path, string content, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = TempPathFor(path);

        try
        {
            await File.WriteAllTextAsync(temp, content, ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>
    /// 临时文件与目标**同目录**：<c>File.Move</c> 只在同卷内是原子的，
    /// 放到 /tmp 就退化成「跨卷复制」，等于没做。
    /// </summary>
    private static string TempPathFor(string path)
        => path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // 清理临时文件失败不值得覆盖真正的写入异常
        }
    }
}
