using AgentFramework.Contracts;
using AgentFramework.Plugins.ComputerUse.Desktop;

namespace AgentFramework.Plugins.ComputerUse.Tools;

/// <summary>
/// 截屏：把当前屏幕画面<b>注入上下文</b>给视觉模型看。
///
/// <para>
/// 这是整个 Computer Use 的<b>眼睛</b>：坐标从哪来？从这张图来。所以操作前先截屏、
/// 操作后再截屏，是这套工具最稳的用法 —— 否则模型是在「凭记忆点坐标」。
/// </para>
/// </summary>
internal sealed class ScreenshotTool(IDesktopDriver driver) : DesktopTool(driver)
{
    public override string Name => "screenshot";

    /// <summary>截图只读：不改变任何状态（画面只进上下文，不落盘）。</summary>
    public override ToolRisk Risk => ToolRisk.ReadOnly;

    public override string Description =>
        "截取电脑屏幕并注入本轮上下文（视觉模型直接看到画面）。" +
        "可选 region 只截一块（省 token）：\"x,y,宽,高\"。坐标以屏幕物理像素为准。" +
        "用于观察当前界面状态、确认上一步操作是否生效。";

    public override string ParametersJsonSchema =>
        """
        {"type":"object","properties":{
        "region":{"type":"string","description":"可选：只截一块，格式 \"x,y,宽,高\"，如 \"0,0,400,300\""}},
        "required":[]}
        """;

    protected override ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken ct)
    {
        var region = ParseRegion(OptionalString(invocation, "region"));

        var png = Driver.CaptureScreenshot(region);
        var size = Driver.GetScreenSize();

        if (png.Length > 8 * 1024 * 1024)
        {
            throw new DesktopException(
                $"截图过大（{png.Length / 1024 / 1024} MB）—— 请用 region 参数只截需要的区域");
        }

        var where = region is { } r
            ? $"区域 ({r.X},{r.Y}) {r.Width}×{r.Height}"
            : $"全屏 {size.Width}×{size.Height}";
        var text = $"已截屏：{where}（PNG {png.Length / 1024} KB）。画面已注入上下文，坐标即以该画面像素为准。";

        var image = new LlmImage
        {
            MediaType = "image/png",
            Base64Data = Convert.ToBase64String(png),
            FileName = "screenshot.png",
        };

        return new ValueTask<ToolResult>(ToolResult.OkWithImages(text, [image]));
    }
}

/// <summary>屏幕尺寸：给模型一个坐标边界，省得它点一个屏幕外的坐标。</summary>
internal sealed class ScreenSizeTool(IDesktopDriver driver) : DesktopTool(driver)
{
    public override string Name => "screen_size";

    public override ToolRisk Risk => ToolRisk.ReadOnly;

    public override string Description =>
        "取屏幕像素尺寸（宽 × 高）。用于把「相对位置」换算成绝对坐标，或确认点击目标是否在屏内。";

    public override string ParametersJsonSchema => """{"type":"object","properties":{},"required":[]}""";

    protected override ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken ct)
    {
        var size = Driver.GetScreenSize();
        return new ValueTask<ToolResult>(ToolResult.Ok(
            $"屏幕尺寸：{size.Width} × {size.Height} 像素（驱动 {Driver.Name}）。"));
    }
}
