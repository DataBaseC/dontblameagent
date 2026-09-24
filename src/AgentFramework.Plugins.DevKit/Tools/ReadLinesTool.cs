using System.Text;
using AgentFramework.Contracts;

namespace AgentFramework.Plugins.DevKit;

/// <summary>
/// 按行读取（带行号）。
///
/// <para>
/// <c>read_file</c> 适合「整篇了解」，但改一处细节时需要的是<b>精确的行</b> ——
/// 尤其配合 <c>edit_file</c> 与 <c>grep_files</c> 报出的行号：
/// 「看到第 128 行有问题」之后，下一步就该是「读 120-140 行」，
/// 而不是把整个 3000 行文件再灌一遍上下文。
/// </para>
/// </summary>
internal sealed class ReadLinesTool(IWorkspaceService ws) : ITool, IToolWithSchema
{
    private const int HardMaxLines = 400;

    public string Name => "read_lines";

    public string Description =>
        "读取工作区内某个文件的一段连续行，输出带行号（从 1 开始）。" +
        "用于配合 grep_files 报出的行号做局部精读；一次最多 400 行。";

    public string ParametersJsonSchema => """
        {"type":"object","properties":{
          "path":{"type":"string","description":"相对工作区根的路径"},
          "start":{"type":"integer","description":"起始行号，从 1 开始，默认 1"},
          "end":{"type":"integer","description":"结束行号（含），默认 start+199，一次最多 400 行"}
        },"required":["path"]}
        """;

    public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        var args = invocation.Arguments;
        var path = args.Str("path");
        if (string.IsNullOrWhiteSpace(path))
        {
            return ValueTask.FromResult(ToolResult.Fail("缺少参数 path"));
        }

        var start = Math.Max(1, args.Int("start", 1));
        var end = args.Int("end", start + 199);
        if (end < start)
        {
            end = start;
        }

        end = Math.Min(end, start + HardMaxLines - 1);

        if (!ws.TryResolve(path, forWrite: false, out var fullPath, out var error))
        {
            return ValueTask.FromResult(ToolResult.Fail(error!));
        }

        if (!File.Exists(fullPath))
        {
            return ValueTask.FromResult(ToolResult.Fail($"文件不存在：{path}"));
        }

        string[] lines;
        try
        {
            lines = File.ReadAllText(fullPath).Replace("\r\n", "\n").Split('\n');
        }
        catch (Exception ex)
        {
            return ValueTask.FromResult(ToolResult.Fail($"读取失败：{ex.Message}"));
        }

        if (start > lines.Length)
        {
            return ValueTask.FromResult(ToolResult.Ok($"请求的起始行超出文件末尾（{path} 共 {lines.Length} 行）"));
        }

        var last = Math.Min(end, lines.Length);
        var sb = new StringBuilder();
        sb.Append($"{path}（第 {start}-{last} 行，共 {lines.Length} 行）\n");

        for (var i = start; i <= last; i++)
        {
            sb.Append($"{i,5} | {lines[i - 1]}\n");
        }

        if (last < lines.Length && end >= start + HardMaxLines)
        {
            sb.Append($"（已达到 {HardMaxLines} 行上限，请续读：start={last + 1}）");
        }

        return ValueTask.FromResult(ToolResult.Ok(ws.Shrink("read_lines", sb.ToString())));
    }
}
