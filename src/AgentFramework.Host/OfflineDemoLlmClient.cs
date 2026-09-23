using System.Runtime.CompilerServices;
using AgentFramework.Contracts;

namespace AgentFramework.Host;

/// <summary>
/// 离线演示模型。
///
/// 存在的理由：<b>没配 API key 时，整条链路也应当能跑起来</b>。
/// 一个框架如果必须先掏钱配 key 才能验证自己装对了，那它的可验证性就是不合格的。
/// 它只做两件事：回显消息；识别到"写"字时演示一次真实工具调用。
/// </summary>
public sealed class OfflineDemoLlmClient : ILlmClient
{
    public string Name => "offline-demo";

    public async IAsyncEnumerable<LlmStreamChunk> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();

        var lastUser = request.Messages.LastOrDefault(m => m.Role == LlmRole.User)?.Content ?? string.Empty;
        var alreadyUsedTool = request.Messages.Any(m => m.Role == LlmRole.Tool);

        if (lastUser.Contains('写') && !alreadyUsedTool && request.Tools.Any(t => t.Name == "write_file"))
        {
            yield return new LlmStreamChunk.TextDelta("（离线演示）我来写一个文件。\n");
            yield return new LlmStreamChunk.ToolCallsReady(
            [
                new ToolCallRequest("demo_call_1", "write_file",
                    """{"path":"offline-demo.md","content":"这是离线演示模式写入的内容"}"""),
            ]);
            yield return new LlmStreamChunk.Completed("tool_calls");
            yield break;
        }

        var toolCount = request.Tools.Count;
        yield return new LlmStreamChunk.TextDelta(
            $"（离线演示模式）收到：{lastUser}\n"
            + $"当前可用工具 {toolCount} 个：{string.Join(", ", request.Tools.Select(t => t.Name))}");
        yield return new LlmStreamChunk.Completed("stop");
    }
}
