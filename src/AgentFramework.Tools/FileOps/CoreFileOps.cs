using System.Collections.Generic;
using AgentFramework.Contracts;
using AgentFramework.Data;

namespace AgentFramework.Tools.FileOps;

/// <summary>
/// 内核文件链入口：宿主装配时一次性收进 core 的工具。
/// 实现类保持 internal，避免与基石插件（DevKit 仍可提供同名工具）互相看见。
/// </summary>
public static class CoreFileOps
{
    public static IReadOnlyList<ITool> CreateAll(IWorkspaceService toolkit)
    {
        return new ITool[]
        {
            new ReadLinesTool(toolkit),
            new EditFileTool(toolkit),
            new MakeDirTool(toolkit),
            new MovePathTool(toolkit),
            new DeletePathTool(toolkit),
            new FindFilesTool(toolkit),
            new GrepFilesTool(toolkit),
        };
    }
}
