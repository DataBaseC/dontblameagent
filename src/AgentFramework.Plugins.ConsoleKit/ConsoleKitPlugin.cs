using AgentFramework.Contracts;
using Microsoft.Extensions.Logging;

namespace AgentFramework.Plugins.ConsoleKit;

/// <summary>
/// 基石插件 · 基础 UI 美化控制台。
///
/// <para>
/// 这个插件<b>一个工具都不注册</b> —— 它要证明的是插件契约的另一半：
/// 插件不只是「给 agent 加工具」，也可以是「给人改界面」。
/// 界面改动的出口是清单里的 <c>ui.styles</c> / <c>ui.scripts</c>：
/// 宿主把它们挂进主页面，于是 CSS 变量一覆盖，整个主题就换了。
/// </para>
///
/// <para>
/// 为什么不能靠 <c>panel.html</c> 搞定：面板跑在 iframe 里，只能改自己。
/// 「换肤、调阅读密度」这类需求改的是<b>主界面</b>，所以必须有这条注入通道。
/// </para>
/// </summary>
public sealed class ConsoleKitPlugin : IPlugin
{
    public Task ActivateAsync(IPluginContext ctx, CancellationToken ct = default)
    {
        // 界面部分由声明驱动（plugin.json 的 ui 段），这里没有工具要注册。
        // 但「装载成功」这件事仍然要留痕 —— 否则面板没出现时无从判断是
        // 插件没加载、还是 ui 声明写错了。
        ctx.Log.LogInformation(
            "console-kit 已激活：样式与脚本已挂进主界面（主题经面板切换，设置存本机 localStorage）");

        return Task.CompletedTask;
    }
}
