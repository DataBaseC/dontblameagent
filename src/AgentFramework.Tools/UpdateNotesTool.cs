using AgentFramework.Contracts;
using AgentFramework.Data;

namespace AgentFramework.Tools;

/// <summary>
/// 更新工作小本本。
///
/// 只做一件事：**改指定小节**（可选追加），其余内容原样保留 ——
/// 于是「agent 不会覆盖人写的东西」成了**结构上的保证**，而不是靠模型自觉。
/// 这也正是需要单独一个工具、而不是让它直接用 <c>write_file</c> 的原因：
/// 整篇覆盖太容易出事。
///
/// 每次写入前都重新读文件（不缓存内存副本），所以人在中间改过的内容一定会被读到 ——
/// 这就是调研里那句「人机共写，业界没有成熟冲突方案」最朴素也最可靠的一半答案。
/// </summary>
public sealed class UpdateNotesTool(Func<string> notesPathProvider) : ITool, IToolWithSchema
{
    public string Name => "update_notes";

    public string Description =>
        "更新工作小本本（工作目录里的 AGENT_NOTES.md）—— 你的计划与进度写在这儿。"
        + "只改指定小节，人写的内容会被保留。开工时写「目标」「计划」，推进中更新「进度」，想到什么记「随手记」。";

    public string ParametersJsonSchema =>
        """{"type":"object","properties":{"section":{"type":"string","description":"小节名：目标 / 计划 / 进度 / 随手记 / 阻塞"},"content":{"type":"string","description":"该小节的新内容（Markdown 列表即可）；空字符串 = 清空本小节"},"append":{"type":"boolean","description":"true = 追加到该小节末尾（随手记常用）；false = 整体替换该小节（默认）"}},"required":["section","content"]}""";

    public async ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        if (!invocation.Arguments.TryGetValue("section", out var section) || string.IsNullOrWhiteSpace(section))
        {
            return ToolResult.Fail("缺少参数 section");
        }

        // content 允许空串 —— 那是「清空本小节」的合法意图。
        // 只有**没传** content 才算缺参；空字符串 ≠ 缺参。
        if (!invocation.Arguments.TryGetValue("content", out var content))
        {
            return ToolResult.Fail("缺少参数 content（空字符串 = 清空本小节）");
        }

        content ??= string.Empty;

        // append 可选；缺省或解析不出就按「替换」处理（保守：不猜用户意图）
        var append = invocation.Arguments.TryGetValue("append", out var raw)
            && bool.TryParse(raw, out var parsed)
            && parsed;

        var path = notesPathProvider();
        var sectionName = section.Trim();

        try
        {
            var existing = WorkNotes.TryRead(path) ?? WorkNotes.Template;
            var updated = WorkNotes.UpdateSection(existing, sectionName, content.Trim(), append);

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllTextAsync(path, updated, ct).ConfigureAwait(false);

            return ToolResult.Ok($"已更新小本本「{sectionName}」（{(append ? "追加" : "替换")}）：{path}");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"更新小本本失败：{ex.Message}");
        }
    }
}
