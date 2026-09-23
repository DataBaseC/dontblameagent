using AgentFramework.Contracts;
using Microsoft.Extensions.Logging;

namespace AgentFramework.SamplePlugin;

/// <summary>
/// 示例插件。
///
/// 请特别留意 ActivateAsync 里【一行清理代码都没有】——
/// 工具、事件订阅、定时器全部由内核自动撤销。
/// 这就是契约要达到的效果：插件作者（很可能是编码 AI）
/// 在结构上不可能「忘了 dispose」。
/// </summary>
public sealed class HelloPlugin : IPlugin
{
    public Task ActivateAsync(IPluginContext ctx, CancellationToken ct = default)
    {
        // 1) 消费内核提供的基础服务（seam 的 Consumer 角色）
        var clock = ctx.Get<IClockService>();

        // 2) 注册工具 —— 卸载时自动撤销
        ctx.RegisterTool(new HelloTool(clock));

        // 3) 订阅审批事件 —— 卸载时自动撤销
        ctx.On<ToolPreExecuteEvent>((e, _) =>
        {
            ctx.Log.LogInformation("工具执行前：{Tool}", e.ToolName);
            return ValueTask.CompletedTask;
        });

        // 4) 托管一个外部资源（定时器）—— 卸载时自动撤销
        var timer = new Timer(
            _ => ctx.Log.LogDebug("hello 插件心跳"),
            state: null,
            dueTime: TimeSpan.FromSeconds(30),
            period: TimeSpan.FromSeconds(30));

        ctx.Effect(() => timer);

        return Task.CompletedTask;
    }
}

/// <summary>插件内部实现（不跨 ALC 边界暴露，因此可以是 internal）。</summary>
internal sealed class HelloTool(IClockService clock) : ITool
{
    public string Name => "hello";

    public string Description => "打招呼，并返回当前时间";

    public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        var who = invocation.Arguments.TryGetValue("name", out var value) && value is not null
            ? value.ToString()!
            : "world";

        return ValueTask.FromResult(ToolResult.Ok($"Hello, {who}! 现在是 {clock.Now:O}"));
    }
}
