using AgentFramework.Contracts;
using AgentFramework.Plugins.ComputerUse.Desktop;

namespace AgentFramework.Plugins.ComputerUse.Tools;

/// <summary>鼠标点击：先移动、后按下抬起；count=2/3 是双击/三击。</summary>
internal sealed class ClickTool(IDesktopDriver driver) : DesktopTool(driver)
{
    public override string Name => "click";

    public override string Description =>
        "在屏幕坐标 (x,y) 点击鼠标。button 默认 left；count=2 双击、3 三击。" +
        "坐标基于最近一次 screenshot 的画面。点击前建议先 screenshot 确认位置。";

    public override string ParametersJsonSchema =>
        """
        {"type":"object","properties":{
        "x":{"type":"integer","description":"横坐标（像素）"},
        "y":{"type":"integer","description":"纵坐标（像素）"},
        "button":{"type":"string","enum":["left","right","middle"],"description":"鼠标键，默认 left"},
        "count":{"type":"integer","description":"点击次数，默认 1（2=双击，3=三击）"}},
        "required":["x","y"]}
        """;

    protected override ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken ct)
    {
        var x = RequireInt(invocation, "x");
        var y = RequireInt(invocation, "y");
        var button = ParseButton(OptionalString(invocation, "button"));
        var count = Math.Clamp(IntArg(invocation, "count", 1), 1, 3);

        Driver.Click(x, y, button, count);

        var what = count == 2 ? "双击" : count == 3 ? "三击" : "单击";
        return new ValueTask<ToolResult>(ToolResult.Ok(
            $"已{what}（{button.ToString().ToLowerInvariant()}）于 ({x},{y})。"));
    }
}

/// <summary>只移动指针、不点击（用于悬停展开菜单等）。</summary>
internal sealed class MoveTool(IDesktopDriver driver) : DesktopTool(driver)
{
    public override string Name => "move";

    public override string Description => "把鼠标移动到屏幕坐标 (x,y)，不点击（用于悬停）。";

    public override string ParametersJsonSchema =>
        """
        {"type":"object","properties":{
        "x":{"type":"integer","description":"横坐标（像素）"},
        "y":{"type":"integer","description":"纵坐标（像素）"}},
        "required":["x","y"]}
        """;

    protected override ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken ct)
    {
        var x = RequireInt(invocation, "x");
        var y = RequireInt(invocation, "y");
        Driver.MovePointer(x, y);
        return new ValueTask<ToolResult>(ToolResult.Ok($"指针已移动到 ({x},{y})。"));
    }
}

/// <summary>拖拽：按下起点、分步移动到终点、松开（分步是为了让目标程序收到中间移动）。</summary>
internal sealed class DragTool(IDesktopDriver driver) : DesktopTool(driver)
{
    public override string Name => "drag";

    public override string Description =>
        "从 (from_x,from_y) 拖拽到 (to_x,to_y)。用于拖文件、拉滑块、框选。" +
        "duration_ms 控制拖拽时长（默认 500ms）—— 太快有些程序会当成瞬移。";

    public override string ParametersJsonSchema =>
        """
        {"type":"object","properties":{
        "from_x":{"type":"integer","description":"起点横坐标"},
        "from_y":{"type":"integer","description":"起点纵坐标"},
        "to_x":{"type":"integer","description":"终点横坐标"},
        "to_y":{"type":"integer","description":"终点纵坐标"},
        "button":{"type":"string","enum":["left","right","middle"],"description":"鼠标键，默认 left"},
        "duration_ms":{"type":"integer","description":"拖拽时长（毫秒），默认 500"}},
        "required":["from_x","from_y","to_x","to_y"]}
        """;

    protected override ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken ct)
    {
        var fx = RequireInt(invocation, "from_x");
        var fy = RequireInt(invocation, "from_y");
        var tx = RequireInt(invocation, "to_x");
        var ty = RequireInt(invocation, "to_y");
        var button = ParseButton(OptionalString(invocation, "button"));
        var duration = Math.Clamp(IntArg(invocation, "duration_ms", 500), 0, 10_000);

        Driver.Drag(fx, fy, tx, ty, button, duration);
        return new ValueTask<ToolResult>(ToolResult.Ok($"已从 ({fx},{fy}) 拖拽到 ({tx},{ty})（{duration}ms）。"));
    }
}

/// <summary>滚轮：在 (x,y) 处滚动；delta_y 正数向上、负数向下。</summary>
internal sealed class ScrollTool(IDesktopDriver driver) : DesktopTool(driver)
{
    public override string Name => "scroll";

    public override string Description =>
        "在屏幕坐标 (x,y) 处滚轮。delta_y 正=向上滚、负=向下滚；delta_x 正=向右。一格 ≈ 1。";

    public override string ParametersJsonSchema =>
        """
        {"type":"object","properties":{
        "x":{"type":"integer","description":"滚轮位置横坐标"},
        "y":{"type":"integer","description":"滚轮位置纵坐标"},
        "delta_y":{"type":"integer","description":"纵向滚动量（正=上，负=下）"},
        "delta_x":{"type":"integer","description":"横向滚动量（正=右）"}},
        "required":["x","y"]}
        """;

    protected override ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken ct)
    {
        var x = RequireInt(invocation, "x");
        var y = RequireInt(invocation, "y");
        var dx = IntArg(invocation, "delta_x", 0);
        var dy = IntArg(invocation, "delta_y", 0);

        if (dx == 0 && dy == 0)
        {
            throw new DesktopException("delta_x 与 delta_y 不能同时为 0");
        }

        Driver.Scroll(x, y, dx, dy);
        return new ValueTask<ToolResult>(ToolResult.Ok($"已在 ({x},{y}) 滚动（Δx={dx}, Δy={dy}）。"));
    }
}

/// <summary>输入文本（Unicode）：与键盘布局无关。</summary>
internal sealed class TypeTool(IDesktopDriver driver) : DesktopTool(driver)
{
    public override string Name => "type";

    public override string Description =>
        "向当前焦点窗口输入一段文本（支持中文 / emoji，不依赖键盘布局）。" +
        "输入前请先用 click 把光标点到目标输入框。";

    public override string ParametersJsonSchema =>
        """
        {"type":"object","properties":{
        "text":{"type":"string","description":"要输入的文本"}},
        "required":["text"]}
        """;

    protected override ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken ct)
    {
        var text = RequireString(invocation, "text", "要输入的文本");
        Driver.TypeText(text);
        return new ValueTask<ToolResult>(ToolResult.Ok($"已输入 {text.Length} 个字符。"));
    }
}

/// <summary>按组合键，如 ctrl+c / alt+tab / F5 / Return。</summary>
internal sealed class KeyTool(IDesktopDriver driver) : DesktopTool(driver)
{
    public override string Name => "key";

    public override string Description =>
        "按下一个键或组合键。例：\"ctrl+c\"、\"alt+tab\"、\"ctrl+shift+t\"、\"Return\"、\"F5\"、\"ArrowUp\"。" +
        "修饰键可用 ctrl / alt / shift / win（macOS 的 cmd 也写 win）。";

    public override string ParametersJsonSchema =>
        """
        {"type":"object","properties":{
        "keys":{"type":"string","description":"键或组合键，如 ctrl+c / alt+tab / Return / F5"}},
        "required":["keys"]}
        """;

    protected override ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken ct)
    {
        var chord = RequireString(invocation, "keys", "如 ctrl+c");
        var keys = KeyNames.Parse(chord);
        Driver.PressKey(keys);
        return new ValueTask<ToolResult>(ToolResult.Ok($"已按键：{string.Join('+', keys)}。"));
    }
}

/// <summary>等待：动作之间留出界面稳定 / 渲染的时间。</summary>
internal sealed class WaitTool : IToolWithRisk, ITool, IToolWithSchema
{
    public string Name => "wait";

    public ToolRisk Risk => ToolRisk.ReadOnly;

    public string Description =>
        "等待若干毫秒，让界面完成渲染 / 动画 / 加载，再截屏确认。默认 500ms，上限 10000ms。";

    public string ParametersJsonSchema =>
        """
        {"type":"object","properties":{
        "ms":{"type":"integer","description":"等待毫秒数，默认 500，上限 10000"}},
        "required":[]}
        """;

    public async ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        var ms = 500;
        if (invocation.Arguments.TryGetValue("ms", out var raw) && int.TryParse(raw, out var parsed))
        {
            ms = parsed;
        }

        ms = Math.Clamp(ms, 0, 10_000);
        try
        {
            await Task.Delay(ms, ct).ConfigureAwait(false);
            return ToolResult.Ok($"已等待 {ms}ms。");
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Fail("等待被取消");
        }
    }
}
