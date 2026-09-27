using AgentFramework.Contracts;

namespace AgentFramework.Plugins.WritingKit;

/// <summary>
/// 读办公文档正文（写作扩展包）：
/// PDF / Word(docx) / Excel(xlsx) / PPT(pptx) 以及 txt/md/csv/json/xml/html 等文本文件。
///
/// <para>
/// 为什么单列一个工具：写作类任务第一步往往是「把素材读进来」。模型看不到二进制，
/// 与其让它猜文件内容，不如给它一个确定的取文工具。取出的文本还会喂给
/// <c>word_count</c> / <c>outline</c> 等工具做体检 —— 于是「读 → 体检 → 改」串成一条链。
/// </para>
/// </summary>
internal sealed class ReadDocumentTool(IWorkspaceService ws) : ITool, IToolWithSchema
{
    public string Name => "read_document";

    public string Description =>
        "读取文档正文为纯文本：支持 pdf、docx、xlsx、pptx，以及 txt/md/csv/json/xml/html/yaml/log 等。" +
        "写作前先用它把素材读进来（返回值即正文文本，超长会由宿主截断或引用化）。";

    public string ParametersJsonSchema => """
        {"type":"object","properties":{
          "path":{"type":"string","description":"要读取的文件路径（相对工作区或绝对路径）"}
        },"required":["path"]}
        """;

    public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        var path = invocation.Arguments.Str("path");
        if (string.IsNullOrWhiteSpace(path))
        {
            return ValueTask.FromResult(ToolResult.Fail("需要 path：要读取的文件路径"));
        }

        if (!ws.TryResolve(path, forWrite: false, out var fullPath, out var resolveError))
        {
            return ValueTask.FromResult(ToolResult.Fail(resolveError ?? "路径无法解析"));
        }

        if (!File.Exists(fullPath))
        {
            return ValueTask.FromResult(ToolResult.Fail($"文件不存在：{path}"));
        }

        if (!DocumentText.TryExtract(fullPath, out var text, out var extractError))
        {
            return ValueTask.FromResult(ToolResult.Fail(extractError ?? "未能提取文本"));
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return ValueTask.FromResult(ToolResult.Fail(
                "未提取到文本 —— 可能是扫描版 PDF（纯图片）或空文档；可尝试 OCR 后再读。"));
        }

        var header = $"── {Path.GetFileName(fullPath)} · {text.Length} 字符 ──\n";
        return ValueTask.FromResult(ToolResult.Ok(ws.Shrink("read_document", header + text)));
    }
}
