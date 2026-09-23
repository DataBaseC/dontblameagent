using System.Text;
using AgentFramework.Contracts;

namespace AgentFramework.Data;

/// <summary>
/// 记忆进入上下文的**两种形态** —— 对应两种完全不同的加载方式：
///
///   <b>常驻索引卡</b>：稳定、硬限长、会话内冻结 → 进「冻结段」（缓存前缀的一部分）
///   <b>按需检索块</b>：按**当轮输入**检索出来 → 进「动态段」（每轮可变，绝不许进前缀）
///
/// 为什么必须分开（这是被实测与官方文档双重逼出来的）：
/// 索引卡一旦变化就会打掉前缀缓存，所以它必须**稳定**；
/// 而「这一轮真正用得上的记忆」天生每轮不同，只能放尾部。
/// 把每轮变化的内容放进前缀，正是 Anthropic 官方 prompt-caching 文档点名的
/// "common mistake"，也是本框架原先 <c>messages.Insert(0, 记忆块)</c> 的病根。
///
/// 另一种理解：**索引卡回答「我是谁」，检索块回答「这件事我知道什么」。**
/// 前者少量常驻，后者按需捞取。两者都**硬性按字符预算截断** ——
/// 记忆是「结论集」，进上下文的部分必须是有界的。
/// </summary>
public static class MemoryBlocks
{
    /// <summary>索引卡的默认标题。</summary>
    public const string IndexCardLabel = "记忆·常驻索引";

    /// <summary>检索块的默认标题。</summary>
    public const string RecallLabel = "相关记忆·本轮检索";

    /// <summary>
    /// 构建常驻索引卡。输入应为**已排序**的少量条目（如最近的若干条）。
    /// 放不下的条目直接留给检索块去捞 —— 索引卡宁可少，也不可无界增长。
    /// </summary>
    public static string? BuildIndexCard(IReadOnlyList<MemoryEntry> entries, int maxChars, string label = IndexCardLabel)
        => Build(label, "\n", entries, maxChars);

    /// <summary>
    /// 构建「本轮检索」块。按字符预算装箱，放不下的丢弃（顺序即优先级）。
    /// 末尾那句「可能过时」是刻意的：记忆是过往认定的事实，与当前对话冲突时应以对话为准。
    /// </summary>
    public static string? BuildRecallBlock(IEnumerable<MemoryEntry> entries, int maxChars, string label = RecallLabel)
        => Build(label, "（可能过时，冲突时以当前对话为准）\n", entries, maxChars);

    private static string? Build(string label, string note, IEnumerable<MemoryEntry> entries, int maxChars)
    {
        if (maxChars <= 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        builder.Append('【').Append(label).Append('】').Append(note);

        var header = builder.Length;

        foreach (var entry in entries)
        {
            var line = "- " + SourceMark(entry) + OneLine(entry.Text) + "\n";

            // 至少要放得下第一条；之后越界就停。
            if (builder.Length + line.Length - header > maxChars)
            {
                break;
            }

            builder.Append(line);
        }

        return builder.Length > header ? builder.ToString() : null;
    }

    /// <summary>
    /// 标出「这条是谁说的」。
    ///
    /// 只标 agent 自记的 —— 用户亲口说的不必标（那是默认可信的来源，也省 token）。
    /// 这是防污染里唯一真正便宜又有效的一招：
    /// <b>框架不替模型判断真假，只把来源摆出来，让判断有依据</b>
    /// （与 4.10 的注入防护同一条方针：只标注、不删改）。
    /// </summary>
    private static string SourceMark(MemoryEntry entry)
        => string.Equals(entry.Source, "user", StringComparison.Ordinal)
            ? string.Empty
            : "（我自己记的，未必可靠）";

    /// <summary>记忆条目压成一行 —— 换行会把「一条记忆」在视觉上拆成好几条。</summary>
    private static string OneLine(string text)
        => text.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
