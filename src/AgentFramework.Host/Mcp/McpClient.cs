using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentFramework.Host.Mcp;

/// <summary>
/// MCP server 客户端：一条 stdio 子进程 + 换行分隔的 JSON-RPC 2.0 会话。
///
/// <para>
/// 只实现「接工具」所需的最小面：<c>initialize</c> 握手、<c>tools/list</c>、<c>tools/call</c>。
/// 其余能力（resources / prompts / sampling）留白 —— 用不到就不假装支持，
/// 免得留下「看着有、其实没实现」的假门面。
/// </para>
///
/// <para>
/// 传输格式按 MCP 规约：每条消息一行 JSON（UTF-8、不含内嵌换行），
/// 而非 LSP 的 Content-Length 分帧 —— 搞错分帧是这类客户端最常见的扣分点。
/// </para>
///
/// <para>
/// 生命周期归宿主作用域：进程由 <see cref="McpModule"/> 经 <c>scope.Effect</c> 托管，
/// 宿主关停时逆序回收（连子进程一起收），调用方不必记得 Kill。
/// </para>
/// </summary>
public sealed class McpClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly StreamReader _stdout;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readLoop;
    private long _nextId;

    private McpClient(string serverId, Process process)
    {
        ServerId = serverId;
        _process = process;
        _stdin = process.StandardInput;
        _stdout = process.StandardOutput;
        _readLoop = Task.Run(ReadLoopAsync);
    }

    public string ServerId { get; }

    /// <summary>协商出的协议版本（initialize 结果里的 protocolVersion）。</summary>
    public string? NegotiatedProtocol { get; private set; }

    /// <summary>server 自报的名字（没有就退回配置里的 id）。</summary>
    public string? ServerName { get; private set; }

    public bool IsRunning => !_process.HasExited;

    public static async Task<McpClient> StartAsync(McpServerConfig config, string workingDirectory, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = config.Command,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // 必须用「不带 BOM」的 UTF8：Encoding.UTF8 会在流首写出 BOM(EF BB BF)，
            // 而 MCP 对端按行做 json.parse，BOM 会让它第一行就解析失败 ——
            // 症状极具迷惑性：「我明明发了 initialize，对端就是不回」。
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };

        foreach (var arg in config.Args)
        {
            psi.ArgumentList.Add(arg);
        }

        foreach (var (key, value) in config.Env)
        {
            psi.Environment[key] = value;
        }

        var process = Process.Start(psi)
            ?? throw new McpException($"无法启动 MCP server「{config.Id}」：{config.Command}");

        var client = new McpClient(config.Id, process);

        // stderr 只做旁路排空（不读会塞满管道把 server 卡死），server 的诊断信息不进对话。
        _ = Task.Run(() =>
        {
            try
            {
                while (process.StandardError.ReadLine() is not null)
                {
                }
            }
            catch
            {
                // 进程退出即结束
            }
        });

        try
        {
            await client.HandshakeAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return client;
    }

    private async Task HandshakeAsync(CancellationToken ct)
    {
        var result = await RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = "2024-11-05",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject
            {
                ["name"] = "AgentFramework",
                ["version"] = "1.0",
            },
        }, ct).ConfigureAwait(false);

        if (result.TryGetProperty("protocolVersion", out var protocolVersion))
        {
            NegotiatedProtocol = protocolVersion.GetString();
        }

        if (result.TryGetProperty("serverInfo", out var serverInfo)
            && serverInfo.TryGetProperty("name", out var name))
        {
            ServerName = name.GetString();
        }

        await NotifyAsync("notifications/initialized", new JsonObject(), ct).ConfigureAwait(false);
    }

    /// <summary>取 server 暴露的工具清单（name / description / 原始 inputSchema）。</summary>
    public async Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken ct)
    {
        var result = await RequestAsync("tools/list", new JsonObject(), ct).ConfigureAwait(false);

        var list = new List<McpToolDescriptor>();
        if (!result.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var tool in tools.EnumerateArray())
        {
            var name = tool.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var description = tool.TryGetProperty("description", out var d) ? d.GetString() : null;
            var schema = tool.TryGetProperty("inputSchema", out var s) && s.ValueKind == JsonValueKind.Object
                ? s.GetRawText()
                : null;

            list.Add(new McpToolDescriptor(name, description ?? "", schema));
        }

        return list;
    }

    /// <summary>调用一个远程工具，把 MCP 的 content[] 展平成文本。</summary>
    public async Task<string> CallToolAsync(string toolName, JsonObject arguments, CancellationToken ct)
    {
        var result = await RequestAsync("tools/call", new JsonObject
        {
            ["name"] = toolName,
            ["arguments"] = arguments,
        }, ct).ConfigureAwait(false);

        var text = McpContent.Flatten(result);
        var isError = result.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True;
        return isError ? "ERROR: " + text : text;
    }

    private async Task<JsonElement> RequestAsync(string method, JsonNode? parameters, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };

        if (parameters is not null)
        {
            message["params"] = parameters;
        }

        await WriteAsync(message.ToJsonString(), ct).ConfigureAwait(false);

        using var registration = ct.Register(() => tcs.TrySetCanceled(ct));
        return await tcs.Task.ConfigureAwait(false);
    }

    private async Task NotifyAsync(string method, JsonNode? parameters, CancellationToken ct)
    {
        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
        };

        if (parameters is not null)
        {
            message["params"] = parameters;
        }

        await WriteAsync(message.ToJsonString(), ct).ConfigureAwait(false);
    }

    private async Task WriteAsync(string json, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await _stdin.WriteLineAsync(json).ConfigureAwait(false);
        await _stdin.FlushAsync().ConfigureAwait(false);
    }

    private async Task ReadLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await _stdout.ReadLineAsync(_cts.Token).ConfigureAwait(false);
            }
            catch
            {
                break;
            }

            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;

                if (!root.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.Number)
                {
                    continue; // 通知（无 id）或无关输出
                }

                if (!_pending.TryRemove(idElement.GetInt64(), out var tcs))
                {
                    continue;
                }

                if (root.TryGetProperty("error", out var error))
                {
                    tcs.TrySetException(new McpException(error.ToString()));
                }
                else if (root.TryGetProperty("result", out var result))
                {
                    tcs.TrySetResult(result.Clone());
                }
                else
                {
                    tcs.TrySetException(new McpException("MCP 响应既无 result 也无 error"));
                }
            }
            catch (JsonException)
            {
                // 非 JSON 行（server 的杂项输出）忽略 —— 不让它弄坏整个会话
            }
        }

        // 进程结束：让所有未决请求尽快失败，而不是永远挂着
        foreach (var pair in _pending)
        {
            pair.Value.TrySetException(new McpException($"MCP server「{ServerId}」已退出"));
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _cts.Cancel();
        }
        catch
        {
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }

        try
        {
            await _readLoop.ConfigureAwait(false);
        }
        catch
        {
        }

        _stdin.Dispose();
        _stdout.Dispose();
        _process.Dispose();
        _cts.Dispose();
    }
}

/// <summary>MCP 工具的元信息（来自 tools/list）。</summary>
public sealed record McpToolDescriptor(string Name, string Description, string? InputSchemaJson);

/// <summary>把 MCP 的 content[] 结果展平成模型可读文本。</summary>
public static class McpContent
{
    public static string Flatten(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Undefined)
        {
            return string.Empty;
        }

        if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return result.GetRawText();
        }

        var sb = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            var type = block.TryGetProperty("type", out var t) ? t.GetString() : null;
            sb.Append(type == "text" && block.TryGetProperty("text", out var text)
                ? text.GetString()
                : block.GetRawText());
        }

        return sb.ToString();
    }
}
