using AgentFramework.Contracts;
using Microsoft.Extensions.Logging;

namespace AgentFramework.Plugins.WritingKit;

/// <summary>
/// 基石插件 · 写作扩展工具包。
///
/// <para>
/// 写作任务里有一类问题<b>模型特别不擅长自测</b>：字数够不够、哪几个词反复出现、
/// 标点是不是中英混着来、段落长短是否失衡。让它「目测」等于让它猜 ——
/// 而这些都是确定性的计数问题，就该交给代码。
/// </para>
///
/// <para>
/// 口径说明：全部工具都接受 <c>text</c>（直接给内容）或 <c>path</c>（读工作区里的文件），
/// 二者取一。文件走的是宿主提供的 <see cref="IWorkspaceService"/> —— 与官方工具同一份边界。
/// </para>
/// </summary>
public sealed class WritingKitPlugin : IPlugin
{
    public Task ActivateAsync(IPluginContext ctx, CancellationToken ct = default)
    {
        var workspace = ctx.Get<IWorkspaceService>();

        ctx.RegisterTool(new WordCountTool(workspace));
        ctx.RegisterTool(new ParagraphReportTool(workspace));
        ctx.RegisterTool(new RepeatWordsTool(workspace));
        ctx.RegisterTool(new CheckPunctuationTool(workspace));
        ctx.RegisterTool(new OutlineTool(workspace));

        ctx.Log.LogInformation("writing-kit 已激活：注册 5 个写作工具");
        return Task.CompletedTask;
    }
}
