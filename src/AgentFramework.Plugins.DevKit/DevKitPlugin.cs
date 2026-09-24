using AgentFramework.Contracts;
using Microsoft.Extensions.Logging;

namespace AgentFramework.Plugins.DevKit;

/// <summary>
/// 基石插件 · 编程扩展工具包。
///
/// <para>
/// 它补的是 agent 最要命的一块短板：<b>只会整篇重写，不会局部改</b>。
/// 一个只有 read/write/list 的 agent 改一行代码要重发整个文件 ——
/// token 花在重复上，风险却落在「没读到的部分被凭印象重写」。
/// </para>
///
/// <para>
/// 与官方文件工具的关系：<b>同一个工作区、同一份边界检查</b>。
/// 边界不是自己实现的，是从宿主提供的 <see cref="IWorkspaceService"/> 拿的 ——
/// 插件跑在独立 ALC 里，碰不到宿主内部的工具类型，所以这是它唯一的路，也应该是。
/// </para>
///
/// <para>
/// 注意 <see cref="ActivateAsync"/> 里依然<b>没有任何清理代码</b>：
/// 七个工具的生命周期全由内核的作用域逆序撤销。卸载这个插件，等于它们从没来过。
/// </para>
/// </summary>
public sealed class DevKitPlugin : IPlugin
{
    public Task ActivateAsync(IPluginContext ctx, CancellationToken ct = default)
    {
        // 消费宿主提供的工作区 seam（清单里 injects 声明过，加载顺序由内核算）
        var workspace = ctx.Get<IWorkspaceService>();

        // 编辑
        ctx.RegisterTool(new EditFileTool(workspace));

        // 找东西：内容（grep）与名字（glob）是两条不同的路，缺一条就只能靠 run_command 拼字符串
        ctx.RegisterTool(new GrepFilesTool(workspace));
        ctx.RegisterTool(new FindFilesTool(workspace));

        // 读：整读之外还要能「按行读 + 带行号」，否则改长文件只能靠猜偏移
        ctx.RegisterTool(new ReadLinesTool(workspace));

        // 搬移
        ctx.RegisterTool(new MakeDirTool(workspace));
        ctx.RegisterTool(new MovePathTool(workspace));
        ctx.RegisterTool(new DeletePathTool(workspace));

        ctx.Log.LogInformation("devkit 已激活：工作区 {Root}，注册 7 个工具", workspace.Root);
        return Task.CompletedTask;
    }
}
