using AgentFramework.Contracts;
using AgentFramework.Plugins.ComputerUse.Desktop;
using AgentFramework.Plugins.ComputerUse.Tools;
using Microsoft.Extensions.Logging;

namespace AgentFramework.Plugins.ComputerUse;

/// <summary>
/// 基石插件 · 电脑操作（Computer Use）。
///
/// <para>
/// 让 agent 从「只能读写文件 / 跑命令」升级为<b>能像人一样操作桌面</b>：
/// 截屏看画面 → 点按钮 / 打字 / 快捷键 → 再看 → 再操作。这是图形界面软件（剪辑、设计、聊天工具……）
/// 唯一没有 CLI 替代的接管路径。
/// </para>
///
/// <para>
/// 设计对齐（照 dsh-orb / computer-control 的能力面）：
/// <list type="bullet">
///   <item><b>截图驱动闭环</b>：先 <c>screenshot</c> 看，再动，动完再看 —— 坐标永远基于刚看到的画面；</item>
///   <item><b>能力广播</b>：驱动按平台/依赖自报能做什么，工具在不支持时如实拒绝（不做半吊子模拟）；</item>
///   <item><b>安全交给审批链</b>：这里不判「危险性」，每个调用都过宿主的分级审批 ——
///     与 dsh-orb 的「安装即同意门」不同，我们保留逐次确认的可能（默认未知工具 = 询问）。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>边界（如实说明）</b>：本插件只提供<b>前台可见操作</b>，不含 dsh-orb 的悬浮球 UI、
/// 后台会话派发与截屏观察边框 —— 那些属于界面形态，dba 的入口仍是 Web UI / 桌面壳。
/// </para>
/// </summary>
public sealed class ComputerUsePlugin : IPlugin
{
    public Task ActivateAsync(IPluginContext ctx, CancellationToken ct = default)
    {
        var driver = DesktopDriverFactory.Create();
        ctx.Log.LogInformation("computer-use 已激活：驱动 {Driver} —— {Describe}", driver.Name, driver.Describe());

        ctx.RegisterTool(new ScreenshotTool(driver));
        ctx.RegisterTool(new ScreenSizeTool(driver));
        ctx.RegisterTool(new ClickTool(driver));
        ctx.RegisterTool(new MoveTool(driver));
        ctx.RegisterTool(new DragTool(driver));
        ctx.RegisterTool(new ScrollTool(driver));
        ctx.RegisterTool(new TypeTool(driver));
        ctx.RegisterTool(new KeyTool(driver));
        ctx.RegisterTool(new WaitTool());
        ctx.RegisterTool(new ListWindowsTool(driver));
        ctx.RegisterTool(new FocusWindowTool(driver));
        ctx.RegisterTool(new OpenAppTool(driver));
        ctx.RegisterTool(new OpenUrlTool(driver));

        ctx.Log.LogInformation("computer-use 已注册 13 个桌面操作工具");
        return Task.CompletedTask;
    }
}
