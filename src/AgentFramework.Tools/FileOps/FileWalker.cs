using System.Text;
using System.Text.RegularExpressions;

namespace AgentFramework.Tools.FileOps;

/// <summary>
/// 目录遍历与 glob —— grep 与 find 共用的地基。
///
/// <para>
/// 两件事必须是**同一份**代码：忽略哪些目录、什么样的文件算「文本」。
/// 否则 grep 和 find 会各有一套脾气，模型得分别记住两套行为（这最烦人）。
/// </para>
/// </summary>
internal static class FileWalker
{
    /// <summary>
    /// 默认忽略的目录名。与主流工具同一取舍（.git / node_modules / bin / obj …）：
    /// 这些东西一进结果就把有效信息淹掉，而且几乎永远不是要找的。
    /// </summary>
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", ".vs", ".idea", ".vscode",
        "node_modules", "bin", "obj", "dist", "build", "out",
        "__pycache__", ".venv", "venv", "env", "target", ".next", ".nuxt",
        "coverage", ".cache", ".gradle", ".mypy_cache", ".pytest_cache",
        "packages", "TestResults",
    };

    /// <summary>超过这个大小的文件不当文本扫（日志与产物常在这里，扫了纯浪费）。</summary>
    public static long MaxFileBytes { get; set; } = 2 * 1024 * 1024;

    public static IEnumerable<string> EnumerateFiles(string root, int limit)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        var yielded = 0;

        while (stack.Count > 0 && yielded < limit)
        {
            var dir = stack.Pop();

            string[] subdirs;
            string[] files;
            try
            {
                subdirs = Directory.GetDirectories(dir);
                files = Directory.GetFiles(dir);
            }
            catch (Exception)
            {
                // 没权限的目录跳过就行，不必让整次检索失败
                continue;
            }

            foreach (var file in files)
            {
                if (yielded >= limit)
                {
                    yield break;
                }

                yielded++;
                yield return file;
            }

            foreach (var sub in subdirs)
            {
                if (!IgnoredDirectories.Contains(Path.GetFileName(sub)))
                {
                    stack.Push(sub);
                }
            }
        }
    }

    /// <summary>看着像二进制的文本（含 NUL 字节）就跳过 —— 免得把乱码灌进上下文。</summary>
    public static bool LooksBinary(string text) => text.Contains('\0');

    /// <summary>统一的相对路径写法：一律用 '/' 分隔（跨平台一致的展示与匹配）。</summary>
    public static string Rel(string root, string path)
        => Path.GetRelativePath(root, path).Replace('\\', '/');
}

/// <summary>
/// 极简 glob：<c>*</c>（段内任意）、<c>?</c>（段内一个）、<c>**</c>（跨段任意）。
/// 与 <c>.gitignore</c> 的口径一致，够用且可预测 —— 不做花哨的字符类。
/// </summary>
internal static class Glob
{
    public static bool IsMatch(string pattern, string path)
    {
        var normalized = path.Replace('\\', '/');

        // 不带 '/' 的模式按「任意目录下的这个名字」处理：*.cs 就该找到所有 .cs，
        // 而不是只找根目录下那几个（这是 find -name 的直觉，也最常用）。
        if (!pattern.Contains('/'))
        {
            pattern = "**/" + pattern;
        }

        try
        {
            return Regex.IsMatch(normalized, ToRegex(pattern), RegexOptions.IgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static string ToRegex(string pattern)
    {
        var sb = new StringBuilder("^");

        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];

            if (c == '*')
            {
                var isDouble = i + 1 < pattern.Length && pattern[i + 1] == '*';
                if (isDouble)
                {
                    i++;
                    if (i + 1 < pattern.Length && pattern[i + 1] == '/')
                    {
                        i++;
                        sb.Append("(?:.*/)?");   // **/ 允许匹配零层目录
                    }
                    else
                    {
                        sb.Append(".*");
                    }
                }
                else
                {
                    sb.Append("[^/]*");
                }
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }

        return sb.Append('$').ToString();
    }
}
