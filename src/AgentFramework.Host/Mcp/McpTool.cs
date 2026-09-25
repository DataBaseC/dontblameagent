using System.Text.Json.Nodes;
using AgentFramework.Contracts;

namespace AgentFramework.Host.Mcp;

/// <summary>
/// 把一个 MCP server 上的远端工具，包装成宿主认识的 <see cref="ITool"/>。
///
/// <para>
/// 包装的意义：MCP 工具一旦落进内核注册表，就与官方工具、插件工具走同一条路 ——
/// <b>审批</b>（<c>ToolPreExecuteEvent</c>）、<b>工具包开关</b>、<b>延迟加载</b>、
/// 事件流、UI 工具卡全部自动生效，不必为 MCP 另开一条竖井。
/// </para>
///
/// <para>
/// 对外名加了 <c>mcp__&lt;server&gt;__</c> 前缀：远端工具名不可控，直接落库迟早与
/// 官方/插件工具撞名，而注册表对重名是抛异常的 —— 前缀是最省事的命名空间隔离。
/// </para>
/// </summary>
public sealed class McpTool(
    McpClient client,
    string exposedName,
    string remoteName,
    string toolset,
    string description,
    string parametersJsonSchema) : ITool, IToolWithSchema, IToolWithToolset
{
    public string Name => exposedName;

    public string Description => description;

    public string ParametersJsonSchema =>
        string.IsNullOrWhiteSpace(parametersJsonSchema) ? ToolSchemas.Empty : parametersJsonSchema;

    public string Toolset => toolset;

    public async ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        var arguments = new JsonObject();
        foreach (var (key, value) in invocation.Arguments)
        {
            if (value is not null)
            {
                arguments[key] = value;
            }
        }

        try
        {
            var text = await client.CallToolAsync(remoteName, arguments, ct).ConfigureAwait(false);
            return text.StartsWith("ERROR: ", StringComparison.Ordinal)
                ? ToolResult.Fail(text["ERROR: ".Length..])
                : ToolResult.Ok(text);
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Fail("已取消");
        }
        catch (McpException ex)
        {
            return ToolResult.Fail($"MCP 调用失败（{client.ServerId}）：{ex.Message}");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"MCP 调用异常（{client.ServerId}）：{ex.GetType().Name}: {ex.Message}");
        }
    }
}
