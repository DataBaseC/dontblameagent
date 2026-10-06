namespace AgentFramework.Tools;

/// <summary>
/// 文件类工具的失败文案 —— 「错在哪 + 下一步怎么走」。
///
/// <para>
/// 与 <see cref="CommandHints"/> 同一精神（v3.30）：命令类工具的失败要给出路，
/// 文件类工具同样。一句光秃秃的「文件不存在」，模型只能换个写法再试；
/// 补上「用 find_files 找 → 用 list_dir 看」才是可执行的下一步。
/// </para>
///
/// <para>
/// 文案集中在这里，改一次全族生效 —— 分散在各工具里必然走样。
/// </para>
/// </summary>
internal static class FileHints
{
    /// <summary>路径参数的形状提示（模型漏参数时，顺手告诉它该长什么样）。</summary>
    public const string PathShape = "（相对工作区根的路径，如 src/main.cs；绝对路径也认）";

    /// <summary>文件不存在 → 给两条找法（按名字找 / 看目录）。</summary>
    public static string NotFound(string path)
    {
        var name = Path.GetFileName(path);
        var glob = string.IsNullOrEmpty(name) || name is "." or ".." ? "**/*" : $"**/*{name}";
        return $"{path}；用 find_files 按名字找（如 pattern={glob}），或用 list_dir 看该目录有什么";
    }

    /// <summary>目录不存在 → 看上一级 / 让 make_dir 建。</summary>
    public static string DirMissing(string path) =>
        $"{path}；用 list_dir 看上一级有哪些目录，或用 find_files 定位";

    /// <summary>源路径不存在（move / delete：路径多半变了）。</summary>
    public static string SourceMissing(string path) =>
        $"{path}；路径可能已变 —— 用 list_dir 看当前目录，或用 find_files 按名字找";

    /// <summary>越出工作区（写）。</summary>
    public const string OutsideWrite =
        "；写只能落在会话项目目录内，请改用工作区内的相对路径（如 src/main.cs）";

    /// <summary>越出工作区（读，仅在读也被配置收回时）。</summary>
    public const string OutsideRead =
        "；读默认放行到整机，此处被配置收回 —— 请改用工作区内路径，或让用户开启 allowReadOutsideWorkspace";

    /// <summary>正则写错（最省事的出路其实是「别用正则」）。</summary>
    public const string RegexHint =
        "；不打算用正则就去掉 regex=true（默认按字面量搜，括号、加号、斜杠都无需转义）";

    /// <summary>内容过长 → 分段写。</summary>
    public static string TooLongToWrite(int actual, int max) =>
        $"内容 {actual} 字符，超过上限 {max}；请分两次写：write_file 先写前半段，再用 edit_file 把余下内容补上";
}
