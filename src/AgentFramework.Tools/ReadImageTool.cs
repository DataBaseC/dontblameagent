using System.Text.Json;
using AgentFramework.Contracts;

namespace AgentFramework.Tools;

/// <summary>
/// 读工作区里的图，交给视觉模型看。
///
/// <para>
/// 为什么不能用 <c>read_file</c>：那条路给的是文本。图要进的是 **content 数组**，
/// 于是结果走 <see cref="ToolResult.Attachments"/> —— 主循环在 tool 消息后
/// 以 user 多模态消息注入（OpenAI 系 tool 角色不收图）。
/// </para>
/// </summary>
public sealed class ReadImageTool(IWorkspaceService workspace) : ITool, IToolWithSchema
{
    private static readonly HashSet<string> AllowedExt = new(StringComparer.OrdinalIgnoreCase)
    { ".png", ".jpg", ".jpeg", ".webp", ".gif" };

    public string Name => "read_image";

    public string Description =>
        "读取工作区中的图片并注入本轮上下文（视觉模型可直接看图）。支持 png/jpg/webp/gif。";

    public string ParametersJsonSchema =>
        """{"type":"object","properties":{"path":{"type":"string","description":"工作区内图片路径"}},"required":["path"]}""";

    public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        if (!invocation.Arguments.TryGetValue("path", out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return ValueTask.FromResult(ToolResult.Fail("缺少参数 path"));
        }

        if (!workspace.TryResolve(raw, forWrite: false, out var full, out var error))
        {
            return ValueTask.FromResult(ToolResult.Fail(error ?? "路径不合法"));
        }

        var ext = Path.GetExtension(full);
        if (!AllowedExt.Contains(ext))
        {
            return ValueTask.FromResult(ToolResult.Fail($"不支持的图像类型：{ext}（可用：png/jpg/webp/gif）"));
        }

        if (!File.Exists(full))
        {
            return ValueTask.FromResult(ToolResult.Fail($"文件不存在：{raw}"));
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(full);
        }
        catch (Exception ex)
        {
            return ValueTask.FromResult(ToolResult.Fail($"读取失败：{ex.Message}"));
        }

        // 单张上限 8MB（base64 后约 10MB）—— 超大图先压缩再给，别把上下文打爆
        if (bytes.Length > 8 * 1024 * 1024)
        {
            return ValueTask.FromResult(ToolResult.Fail($"图片过大（{bytes.Length / 1024} KB），请先缩到 8MB 以内"));
        }

        var media = ext.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => "application/octet-stream",
        };

        var image = new LlmImage
        {
            MediaType = media,
            Base64Data = Convert.ToBase64String(bytes),
            FileName = Path.GetFileName(full),
        };

        return ValueTask.FromResult(ToolResult.OkWithImages(
            $"已读取图像 {raw}（{media}，{bytes.Length / 1024} KB）。图像已注入上下文，请结合画面作答。",
            [image]));
    }
}
