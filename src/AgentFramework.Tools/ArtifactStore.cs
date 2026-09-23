using System.Text;

namespace AgentFramework.Tools;

/// <summary>
/// 工具结果的「工件存储」（L2：大输出引用化）。
///
/// 为什么做它：上下文膨胀的大头从来不是对话，而是**工具输出**
/// （网页正文、文件内容、命令回显）。让它们整段躺在上下文里，
/// 之后每一轮都要重发一遍 —— 这才是长任务成本与注意力的黑洞。
///
/// 做法：全文落盘，上下文里只留「摘要 + 路径 + 头尾」。
/// 模型真需要细节时，一条 <c>read_file</c> 就取回来了 —— 需要时才付费。
///
/// 落盘位置刻意放在**工作区内**（<c>.agent-artifacts/</c>）：
/// 只有这样文件工具才够得着它（文件工具被限制在工作区内）。
/// </summary>
public sealed class ArtifactStore
{
    /// <summary>工件目录名（相对工作区根）。</summary>
    public const string DirectoryName = ".agent-artifacts";

    private readonly string _root;
    private readonly object _gate = new();
    private int _counter;

    public ArtifactStore(string workspaceRoot)
        => _root = Path.Combine(Path.GetFullPath(workspaceRoot), DirectoryName);

    /// <summary>工件目录的绝对路径。</summary>
    public string Root => _root;

    /// <summary>把全文落盘，返回**相对工作区**的路径（模型能直接拿去 read_file）。</summary>
    public string Save(string toolName, string content)
    {
        Directory.CreateDirectory(_root);

        string fileName;
        lock (_gate)
        {
            _counter++;
            fileName = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Sanitize(toolName)}-{_counter}.txt";
        }

        File.WriteAllText(Path.Combine(_root, fileName), content, Encoding.UTF8);

        // 统一用正斜杠：模型在 Windows / Linux 上拿到的引用字符串是同一个样子
        return $"{DirectoryName}/{fileName}";
    }

    /// <summary>
    /// 超过上限就「引用化」：落盘 + 只在上下文里留摘要与头尾；没超限则原样返回。
    /// </summary>
    public string Shrink(string toolName, string content, int limit, int headChars, int tailChars)
    {
        if (limit <= 0 || content.Length <= limit)
        {
            return content;
        }

        var path = Save(toolName, content);

        var head = content[..Math.Min(headChars, content.Length)];
        var tailStart = Math.Max(head.Length, content.Length - Math.Max(tailChars, 0));
        var tail = content[tailStart..];
        var omitted = tailStart - head.Length;

        var sb = new StringBuilder();
        sb.Append("[结果过大，已落盘引用化]\n");
        sb.Append("原始长度：").Append(content.Length).Append(" 字符\n");
        sb.Append("完整内容：").Append(path).Append("（可用 read_file 读取）\n");
        sb.Append("—— 开头 ——\n").Append(head);
        sb.Append("\n—— 结尾 ——\n").Append(tail);
        sb.Append("\n（中间 ").Append(omitted).Append(" 字符已省略；需要细节请用 read_file 读上面那个路径）");

        return sb.ToString();
    }

    private static string Sanitize(string toolName)
    {
        var chars = toolName
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')
            .ToArray();

        return new string(chars);
    }
}
