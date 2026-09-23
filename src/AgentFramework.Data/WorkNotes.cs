using System.Text;

namespace AgentFramework.Data;

/// <summary>
/// 工作小本本 —— 工作目录里那份**人和 agent 一起写**的计划/待办文本。
///
/// 像人干活时手边那个本子：记目标、列计划、勾进度、留随手想到的东西。
///
/// 与另外几样东西的分工（别混）：
///   · 事件流   —— 「发生了什么」（机器写，可回放）
///   · 项目记忆 —— 「我认定了什么」（结论，跨会话）
///   · <b>小本本</b> —— 「我打算怎么做、做到哪了」（过程，**人也能改**）
///   · 任务卡   —— 由事件流投影出的每轮快照（内存里，不落盘）
///
/// 为什么值得在文件系统里留这么一份：调研里 Manus 那条最值钱 ——
/// **把计划外化，并反复复述进上下文末尾**，能显著压低任务漂移。
/// 落成文件还多两个好处：跨会话存活（上下文压缩冲不掉它），以及**人可监督**。
///
/// 加载方式是刻意的：<b>全文不常驻</b>，只把一份**有界摘要**复述进动态段，
/// 细节让模型自己按需读文件 —— 与记忆的「索引卡 + 按需检索」是同一个思路。
///
/// 还有一个刻意的设计：更新走 <see cref="UpdateSection"/>，**只替换指定小节**。
/// 于是「不覆盖人的编辑」不是靠模型自觉，而是结构上做不到 —— 其余内容原样搬过去。
/// </summary>
public static class WorkNotes
{
    /// <summary>默认文件名 —— 放在工作目录根部，方便人随时打开改。</summary>
    public const string DefaultFileName = "AGENT_NOTES.md";

    /// <summary>小节顺序即摘要顺序。</summary>
    public static readonly IReadOnlyList<string> Sections = ["目标", "计划", "进度", "随手记", "阻塞"];

    /// <summary>新建小本本时的初始内容。</summary>
    public const string Template = """
        # 工作小本本

        > 人机共写的工作笔记。agent 只改动它负责的小节，人手改的内容会被保留。

        ## 目标

        ## 计划

        ## 进度

        ## 随手记

        ## 阻塞

        """;

    /// <summary>读取小本本；不存在返回 null（是否创建由调用方决定）。</summary>
    public static string? TryRead(string path)
        => File.Exists(path) ? File.ReadAllText(path) : null;

    /// <summary>
    /// 抽出一份**有界**摘要：按小节顺序取内容，超出预算就停。
    /// 空小节直接跳过 —— 本子空着就不该占上下文（这也是它默认零成本的原因）。
    /// </summary>
    public static string? BuildSummary(string? content, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(content) || maxChars <= 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        builder.Append("【工作小本本】\n");

        var header = builder.Length;

        foreach (var section in Sections)
        {
            var body = ExtractSection(content, section);
            if (string.IsNullOrWhiteSpace(body))
            {
                continue;
            }

            var line = $"{section}：{Collapse(body)}\n";

            if (builder.Length + line.Length - header > maxChars)
            {
                break;
            }

            builder.Append(line);
        }

        return builder.Length > header ? builder.ToString() : null;
    }

    /// <summary>抽取某个 <c>## 小节</c> 的正文（到下一个二级标题为止）。</summary>
    public static string ExtractSection(string content, string section)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');

        var start = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (IsHeading(lines[i], out var name) && name == section)
            {
                start = i + 1;
                break;
            }
        }

        if (start < 0)
        {
            return string.Empty;
        }

        var end = lines.Length;
        for (var i = start; i < lines.Length; i++)
        {
            if (IsHeading(lines[i], out _))
            {
                end = i;
                break;
            }
        }

        return string.Join('\n', lines[start..end]).Trim();
    }

    /// <summary>
    /// 更新指定小节，并**原样保留其余内容**。
    ///
    /// 这是「不覆盖人的编辑」的结构性保证：无论如何都只动一个区间。
    /// <paramref name="append"/> 为 true 时在该小节末尾追加（随手记用），否则整体替换。
    /// </summary>
    public static string UpdateSection(string content, string section, string newBody, bool append = false)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n').ToList();
        var bodyLines = newBody.Replace("\r\n", "\n").Split('\n').ToList();

        var start = lines.FindIndex(l => IsHeading(l, out var name) && name == section);

        if (start < 0)
        {
            // 小节不存在 → 补在文件末尾（保留原内容，绝不重写整篇）
            if (lines.Count > 0 && lines[^1].Length > 0)
            {
                lines.Add(string.Empty);
            }

            lines.Add($"## {section}");
            lines.Add(string.Empty);
            lines.AddRange(bodyLines);

            return string.Join('\n', lines).TrimEnd() + "\n";
        }

        var end = lines.Count;
        for (var i = start + 1; i < lines.Count; i++)
        {
            if (IsHeading(lines[i], out _))
            {
                end = i;
                break;
            }
        }

        var replacement = new List<string> { $"## {section}", string.Empty };

        if (append)
        {
            var existing = lines[(start + 1)..end]
                .SkipWhile(string.IsNullOrWhiteSpace)
                .ToList();

            while (existing.Count > 0 && string.IsNullOrWhiteSpace(existing[^1]))
            {
                existing.RemoveAt(existing.Count - 1);
            }

            replacement.AddRange(existing);
            replacement.AddRange(bodyLines);
        }
        else
        {
            replacement.AddRange(bodyLines);
        }

        replacement.Add(string.Empty);

        lines.RemoveRange(start, end - start);
        lines.InsertRange(start, replacement);

        return string.Join('\n', lines).TrimEnd() + "\n";
    }

    private static bool IsHeading(string line, out string name)
    {
        var trimmed = line.TrimStart();

        if (trimmed.StartsWith("## ", StringComparison.Ordinal))
        {
            name = trimmed[3..].Trim();
            return name.Length > 0;
        }

        name = string.Empty;
        return false;
    }

    /// <summary>把小节压成单行 —— 摘要只求「够用」，不必把整篇计划搬进上下文。</summary>
    private static string Collapse(string body)
    {
        var parts = body
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.TrimStart('-', '*', ' ', '\t').Trim())
            .Where(l => l.Length > 0);

        var joined = string.Join("；", parts);

        return joined.Length <= 240 ? joined : joined[..240] + "…";
    }
}
