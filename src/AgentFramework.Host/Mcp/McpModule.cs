using System.Text;
using AgentFramework.Contracts;
using AgentFramework.Host.Hosting;

namespace AgentFramework.Host.Mcp;

/// <summary>
/// MCP 接入模块：把配置里声明的外部 MCP server 拉进宿主。
///
/// <para>
/// 每个 server 一个内核作用域（<c>mcp:&lt;id&gt;</c>）+ 一个同名工具包。
/// 工具包默认 <b>Eager=false（延迟）</b>：工具注册着、但不进模型可见的 schema ——
/// 多个 server 加起来往往几十上百个工具，全量塞进上下文纯属浪费。
/// 要用时由 <c>use_toolset</c> 把它拉进来（复用「包开关」这套现成机制，不另造通路）。
/// </para>
///
/// <para>
/// 失败隔离：单个 server 起不来只记录、不抛 —— 一个坏配置不该让整个宿主起不来。
/// 进程生命周期交给作用域（<c>scope.Effect</c>），宿主关停时随逆序撤销一起 Kill。
/// </para>
/// </summary>
public sealed class McpModule : IHostModule
{
    public string Name => "mcp";

    /// <summary>排在 plugins(400) 之后：MCP 与插件同属「外部能力接入」。</summary>
    public int Order => 450;

    public async ValueTask ConfigureAsync(HostState state, CancellationToken ct = default)
    {
        var servers = state.Options.McpServers
            .Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Id) && !string.IsNullOrWhiteSpace(s.Command))
            .ToList();

        if (servers.Count == 0)
        {
            return;
        }

        var failures = new List<string>();

        foreach (var config in servers)
        {
            ct.ThrowIfCancellationRequested();

            var toolsetId = $"mcp:{config.Id}";
            var scope = state.Kernel.CreateKernelScope(toolsetId);
            McpClient? client = null;

            try
            {
                client = await McpClient.StartAsync(config, state.Options.WorkspaceRoot, ct).ConfigureAwait(false);

                // 进程生命周期托管给作用域：宿主关停 / 卸载时逆序 Dispose（Kill 子进程）。
                var started = client;
                scope.Effect(() => new ClientHandle(started));

                var tools = await client.ListToolsAsync(ct).ConfigureAwait(false);
                foreach (var tool in tools)
                {
                    var exposed = $"mcp__{Sanitize(config.Id)}__{Sanitize(tool.Name)}";
                    scope.RegisterTool(
                        new McpTool(client, exposed, tool.Name, toolsetId, tool.Description, tool.InputSchemaJson ?? ""),
                        toolsetId);
                }

                state.Kernel.DescribeToolset(new ToolsetDescriptor
                {
                    Id = toolsetId,
                    Name = client.ServerName ?? config.Id,
                    Description = $"MCP server「{config.Id}」：{tools.Count} 个外部工具（默认收起，用到再开）",
                    Eager = false,
                    Protected = false,
                    Source = toolsetId,
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add($"{config.Id}: {ex.Message}");

                if (client is not null)
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    // 连客户端都没建起来：作用域里可能已挂了一半东西，主动撤掉，别留半截包。
                    scope.Dispose();
                }
            }
        }

        state.McpFailures = failures;
    }

    /// <summary>把任意字符串收敛成安全的工具名片段（工具名只允许字母数字与 _ -）。</summary>
    private static string Sanitize(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            sb.Append(char.IsLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_');
        }

        return sb.Length == 0 ? "server" : sb.ToString();
    }

    private sealed class ClientHandle(McpClient client) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                client.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // 关停路径尽力而为：Kill 失败也不该阻断宿主退出
            }
        }
    }
}
