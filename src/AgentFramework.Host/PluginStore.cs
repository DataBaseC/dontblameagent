namespace AgentFramework.Host;

/// <summary>
/// 统一插件仓库 —— agent 自己写的插件都落在这里（目录纪律借自技能系统的"创意工坊"）。
///
/// <para>
/// 为什么不干脆让 agent 用 <c>write_file</c> 写进插件目录：三个理由。
/// ① <b>写入要受约束</b>：只能落在仓库内、只能是文本文件、id 必须合法；
/// ② <b>热更新要能回滚</b>：换版本之前先拍快照，装坏了能退回去；
/// ③ <b>结构要稳定</b>：<c>{root}/{id}/plugin.json</c> + 脚本，启动时才扫得到、认得出。
/// </para>
/// <para>
/// 写入用「临时目录 + 整体换上去」，避免"半写状态"被扫描器或装载器撞见 ——
/// 一个只写了一半的插件，比一个干脆不存在的插件危险得多。
/// </para>
/// </summary>
public sealed class PluginStore
{
    public PluginStore(string root, string backupRoot)
    {
        Root = root;
        BackupRoot = backupRoot;
    }

    /// <summary>插件仓库根（每个子目录 = 一个插件）。</summary>
    public string Root { get; }

    /// <summary>快照根（换版本前的备份，回滚用）。</summary>
    public string BackupRoot { get; }

    /// <summary>
    /// 插件 id 白名单：字母、数字、点、下划线、连字符。
    /// id 会直接参与拼路径 —— 不校验的话，<c>"../../x"</c> 就能把文件写到仓库外面去。
    /// <c>"."</c> / <c>".."</c> / 纯点段也必须拒：它们在字符白名单下合法，
    /// 但 <c>Path.Combine(Root, "..")</c> 会指到仓库父目录，递归删除时就是灾难。
    /// </summary>
    public static bool IsValidId(string? id)
        => !string.IsNullOrWhiteSpace(id)
           && id.Length <= 64
           && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
           && id is not "." and not ".."
           && !id.All(c => c is '.' or '-' or '_');

    public string DirectoryFor(string id)
    {
        if (!IsValidId(id))
        {
            throw new InvalidOperationException($"插件 id 非法：{id}（只允许字母数字 . _ -，最长 64，且不能是 . 或 ..）");
        }

        var rootFull = Path.GetFullPath(Root);
        var combined = Path.GetFullPath(Path.Combine(rootFull, id));
        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;

        // 二次防线：即便未来 IsValidId 被放宽，路径也绝不许越出仓库根。
        if (!combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"插件 id 越出仓库根：{id}");
        }

        return combined;
    }

    /// <summary>仓库里已有哪些插件（只有含 plugin.json 的目录才算）。</summary>
    public IReadOnlyList<string> ListInstalled()
    {
        if (!Directory.Exists(Root))
        {
            return [];
        }

        return
        [
            .. Directory.EnumerateDirectories(Root)
                .Where(dir => File.Exists(Path.Combine(dir, "plugin.json")))
                .Select(dir => Path.GetFileName(dir))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .OrderBy(name => name, StringComparer.Ordinal)
        ];
    }

    /// <summary>把一个插件的文件整体写进仓库（先写暂存目录，再原子换上去）。</summary>
    public void WriteFiles(string id, IReadOnlyDictionary<string, string> files)
    {
        if (!IsValidId(id))
        {
            throw new InvalidOperationException($"插件 id 非法：{id}（只允许字母数字 . _ -，最长 64，且不能是 . 或 ..）");
        }

        if (files.Count == 0)
        {
            throw new InvalidOperationException("没有要写入的文件");
        }

        Directory.CreateDirectory(Root);

        var target = DirectoryFor(id);
        var staging = target + ".staging-" + Guid.NewGuid().ToString("N")[..6];

        try
        {
            Directory.CreateDirectory(staging);

            foreach (var (name, content) in files)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new InvalidOperationException("文件名不能为空");
                }

                // 文件必须落在暂存目录之内 —— 与「id 白名单」是同一道防线，这里防的是文件名。
                var full = Path.GetFullPath(Path.Combine(staging, name));
                if (!full.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"文件名越出插件目录：{name}");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, content);
            }

            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }

            Directory.Move(staging, target);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                try
                {
                    Directory.Delete(staging, recursive: true);
                }
                catch
                {
                    // 清理失败不影响主流程；残留的 *.staging-* 不会被当成插件（没有 plugin.json 的正常位置）
                }
            }
        }
    }

    /// <summary>给当前版本拍一份快照（换版本前调用）；没有这个插件时返回 null。</summary>
    public string? Snapshot(string id)
    {
        var source = DirectoryFor(id);
        if (!Directory.Exists(source))
        {
            return null;
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var candidate = BackupDirFor(id, stamp);
        var suffix = 1;
        while (Directory.Exists(candidate))
        {
            candidate = BackupDirFor(id, $"{stamp}-{suffix++}");
        }

        CopyDirectory(source, candidate);
        return candidate;
    }

    /// <summary>
    /// 「上次**装载成功**的那一版」存在哪儿（回滚目标）；没有就是 null。
    ///
    /// <para>
    /// 注意它<b>不是</b>「写盘前的文件」：坏版本在写进仓库的那一刻就已经覆盖了磁盘，
    /// 所以"写盘前"完全可能也是坏的那一版。真正安全的回滚目标只有
    /// <b>确实装载成功过</b>的那一份 —— 那才是"已知可用"。
    /// </para>
    /// </summary>
    public string? GetLastGood(string id)
    {
        if (!IsValidId(id))
        {
            throw new InvalidOperationException($"插件 id 非法：{id}");
        }

        var path = BackupDirFor(id, "last-good");
        return Directory.Exists(path) ? path : null;
    }

    /// <summary>把当前版本标记为「已知可用」（装载成功后调用）。</summary>
    public void MarkLastGood(string id)
    {
        var source = DirectoryFor(id);
        if (!Directory.Exists(source))
        {
            return;
        }

        var target = BackupDirFor(id, "last-good");
        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }

        CopyDirectory(source, target);
    }

    /// <summary>用快照回滚；回滚本身失败不该抛出去盖掉原始错误。</summary>
    public bool TryRestore(string id, string? snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot) || !Directory.Exists(snapshot))
        {
            return false;
        }

        try
        {
            var target = DirectoryFor(id);
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }

            CopyDirectory(snapshot, target);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool Remove(string id)
    {
        if (!IsValidId(id))
        {
            throw new InvalidOperationException($"插件 id 非法：{id}（只允许字母数字 . _ -，最长 64，且不能是 . 或 ..）");
        }

        var dir = DirectoryFor(id);
        if (!Directory.Exists(dir))
        {
            return false;
        }

        Directory.Delete(dir, recursive: true);
        return true;
    }

    private string BackupDirFor(string id, string leaf)
    {
        if (!IsValidId(id))
        {
            throw new InvalidOperationException($"插件 id 非法：{id}");
        }

        var rootFull = Path.GetFullPath(BackupRoot);
        var combined = Path.GetFullPath(Path.Combine(rootFull, id, leaf));
        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;

        if (!combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"备份路径越出快照根：{id}/{leaf}");
        }

        return combined;
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }
}
