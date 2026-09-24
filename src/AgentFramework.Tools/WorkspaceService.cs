using AgentFramework.Contracts;

namespace AgentFramework.Tools;

/// <summary>
/// <see cref="IWorkspaceService"/> 的宿主实现。
///
/// <para>
/// 它薄得几乎是透明的 —— 这正是重点：<b>官方文件工具与插件走的是同一份边界代码</b>
/// （<c>WorkspacePath.TryResolve</c>），所以「插件能不能写到工作区外」这件事
/// 不需要单独审计一遍。多一层实现就等于多一个漂移点。
/// </para>
///
/// <para>
/// 由宿主在装配期 <c>Provide</c> 给内核（工具模块的作用域），随即可以被任何
/// 声明了 <c>injects: ["IWorkspaceService"]</c> 的插件取用。
/// </para>
/// </summary>
public sealed class WorkspaceService(ToolkitOptions options) : IWorkspaceService
{
    /// <summary>当前生效的工作区根（会话级解析器优先）。</summary>
    public string Root => options.EffectiveRoot;

    public bool AllowReadOutsideWorkspace => options.AllowReadOutsideWorkspace;

    public int MaxReadChars => options.MaxReadChars;

    public int MaxWriteChars => options.MaxWriteChars;

    public bool TryResolve(string path, bool forWrite, out string fullPath, out string? error)
        => WorkspacePath.TryResolve(options, path, forWrite, out fullPath, out error);

    public string Shrink(string toolName, string content) => options.ShrinkResult(toolName, content);
}
