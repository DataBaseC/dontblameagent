using System.Text;
using AgentFramework.Contracts;

namespace AgentFramework.Host.Mcp;

/// <summary>
/// 把一个 MCP server 的**资源面**暴露成只读工具（对齐 dsh-mcp-resources）。
///
/// <para>
/// 与 <see cref="McpTool"/> 同一条纪律：一旦落进内核注册表，审批、工具包开关、延迟加载、
/// 事件流、UI 工具卡全部自动生效，不为资源另开一条竖井。
/// </para>
/// <para>
/// 两个工具都落在该 server 的 <c>mcp:&lt;id&gt;</c> 包里（默认收起）——
/// 只有 server 在 <c>initialize</c> 里声明了 <c>resources</c> 能力时才会被注册。
/// </para>
/// </summary>
public sealed class McpResourceListTool(
    McpClient client,
    string exposedName,
    string toolset) : IToolWithRisk, ITool, IToolWithSchema, IToolWithToolset
{
    public string Name => exposedName;

    public ToolRisk Risk => ToolRisk.ReadOnly;

    public string Description =>
        $"列出 MCP server「{client.ServerId}」暴露的资源（每行：uri、名称、说明）。";

    public string ParametersJsonSchema => ToolSchemas.Empty;

    public string Toolset => toolset;

    public async ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        try
        {
            var resources = await client.ListResourcesAsync(ct).ConfigureAwait(false);
            if (resources.Count == 0)
            {
                return ToolResult.Ok($"server「{client.ServerId}」没有暴露资源。");
            }

            var sb = new StringBuilder();
            foreach (var resource in resources)
            {
                sb.Append(resource.Uri).Append('\t').Append(resource.Name);

                if (!string.IsNullOrWhiteSpace(resource.MimeType))
                {
                    sb.Append(" [").Append(resource.MimeType).Append(']');
                }

                if (!string.IsNullOrWhiteSpace(resource.Description))
                {
                    sb.Append(" — ").Append(resource.Description);
                }

                sb.Append('\n');
            }

            return ToolResult.Ok(sb.ToString().TrimEnd());
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Fail("已取消");
        }
        catch (McpException ex)
        {
            return ToolResult.Fail($"MCP 资源列举失败（{client.ServerId}）：{ex.Message}");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"MCP 资源列举异常（{client.ServerId}）：{ex.GetType().Name}: {ex.Message}");
        }
    }
}

/// <summary>按 uri 读一个 MCP 资源（<c>resources/read</c>），内容以文本带回上下文。</summary>
public sealed class McpResourceReadTool(
    McpClient client,
    string exposedName,
    string toolset) : IToolWithRisk, ITool, IToolWithSchema, IToolWithToolset
{
    public string Name => exposedName;

    public ToolRisk Risk => ToolRisk.ReadOnly;

    public string Description =>
        $"读取 MCP server「{client.ServerId}」上的一个资源（uri 见 resource_list 的输出）。";

    public string ParametersJsonSchema =>
        """{"type":"object","properties":{"uri":{"type":"string","description":"资源 uri"}},"required":["uri"]}""";

    public string Toolset => toolset;

    public async ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        if (!invocation.Arguments.TryGetValue("uri", out var uri) || string.IsNullOrWhiteSpace(uri))
        {
            return ToolResult.Fail("缺少参数 uri");
        }

        try
        {
            var text = await client.ReadResourceAsync(uri, ct).ConfigureAwait(false);
            return ToolResult.Ok(text);
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Fail("已取消");
        }
        catch (McpException ex)
        {
            return ToolResult.Fail($"MCP 资源读取失败（{client.ServerId}）：{ex.Message}");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"MCP 资源读取异常（{client.ServerId}）：{ex.GetType().Name}: {ex.Message}");
        }
    }
}
