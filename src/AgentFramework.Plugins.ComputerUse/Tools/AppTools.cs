using System.Text;
using AgentFramework.Contracts;
using AgentFramework.Plugins.ComputerUse.Desktop;

namespace AgentFramework.Plugins.ComputerUse.Tools;

/// <summary>列出可见窗口：拿到标题 / 位置 / 尺寸，才好决定操作谁。</summary>
internal sealed class ListWindowsTool(IDesktopDriver driver) : DesktopTool(driver)
{
    public override string Name => "list_windows";

    /// <summary>列窗只是看，不改任何东西。</summary>
    public override ToolRisk Risk => ToolRisk.ReadOnly;

    public override string Description =>
        "列出当前可见窗口（标题、句柄、位置与尺寸、哪个在前台）。" +
        "用于确认目标窗口是否已打开、它占据屏幕哪块区域。";

    public override string ParametersJsonSchema => """{"type":"object","properties":{},"required":[]}""";

    protected override ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken ct)
    {
        var windows = Driver.ListWindows();
        if (windows.Count == 0)
        {
            return new ValueTask<ToolResult>(ToolResult.Ok("当前没有可见窗口。"));
        }

        var report = new StringBuilder();
        report.Append($"共 {windows.Count} 个可见窗口：\n");
        foreach (var w in windows)
        {
            report.Append(w.IsForeground ? "● " : "  ");
            report.Append(w.Title);
            if (w.Handle != 0)
            {
                report.Append($"  [句柄 {w.Handle}]");
            }

            if (w.Width > 0 && w.Height > 0)
            {
                report.Append($"  位置 ({w.X},{w.Y}) 尺寸 {w.Width}×{w.Height}");
            }

            report.Append('\n');
        }

        report.Append("（● = 当前前台窗口）");
        return new ValueTask<ToolResult>(ToolResult.Ok(report.ToString()));
    }
}

/// <summary>聚焦窗口：按标题子串或句柄把它带到前台。</summary>
internal sealed class FocusWindowTool(IDesktopDriver driver) : DesktopTool(driver)
{
    public override string Name => "focus_window";

    public override string Description =>
        "把指定窗口带到前台。window 可以是标题片段（子串匹配）或句柄（list_windows 给出的数字）。" +
        "找不到 / 系统拒绝置前时如实返回失败。";

    public override string ParametersJsonSchema =>
        """
        {"type":"object","properties":{
        "window":{"type":"string","description":"窗口标题片段，或句柄（如 132456 / 0x1f2a）"}},
        "required":["window"]}
        """;

    protected override ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken ct)
    {
        var target = RequireString(invocation, "window", "窗口标题片段或句柄");
        var ok = Driver.FocusWindow(target);
        return new ValueTask<ToolResult>(ok
            ? ToolResult.Ok($"已聚焦窗口：{target}")
            : ToolResult.Fail($"未找到标题含「{target}」的窗口（可先用 list_windows 看清单）。"));
    }
}

/// <summary>启动本机应用。</summary>
internal sealed class OpenAppTool(IDesktopDriver driver) : DesktopTool(driver)
{
    public override string Name => "open_app";

    public override string Description =>
        "启动本机应用（如 Windows 的 notepad / calc、macOS 的应用名、Linux 的可执行名）。";

    public override string ParametersJsonSchema =>
        """
        {"type":"object","properties":{
        "app":{"type":"string","description":"应用名或可执行文件路径"}},
        "required":["app"]}
        """;

    protected override ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken ct)
    {
        var app = RequireString(invocation, "app", "应用名");
        Driver.OpenApp(app);
        return new ValueTask<ToolResult>(ToolResult.Ok($"已启动：{app}"));
    }
}

/// <summary>用默认浏览器打开链接。</summary>
internal sealed class OpenUrlTool(IDesktopDriver driver) : DesktopTool(driver)
{
    public override string Name => "open_url";

    public override string Description => "用系统默认浏览器打开一个链接。";

    public override string ParametersJsonSchema =>
        """
        {"type":"object","properties":{
        "url":{"type":"string","description":"要打开的网址"}},
        "required":["url"]}
        """;

    protected override ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken ct)
    {
        var url = RequireString(invocation, "url", "网址");
        Driver.OpenUrl(url);
        return new ValueTask<ToolResult>(ToolResult.Ok($"已打开：{url}"));
    }
}
