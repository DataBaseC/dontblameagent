using System.Runtime.CompilerServices;
using AgentFramework.Contracts;

namespace AgentFramework.Llm;

/// <summary>
/// 可热替换的模型客户端。
///
/// 主循环在装配时就抓住了一个 <see cref="ILlmClient"/>。如果那个东西是不可变的，
/// 「换模型」就只剩一条路：把整个宿主重建一遍 —— 会话、工具、索引全跟着重来。
///
/// 这里让那一格变成**可替换的内层**：换模型只换内层，
/// 主循环、上下文、会话历史一概不动。于是「运行时切换」变成了改一个引用的事。
/// </summary>
public sealed class SwitchableLlmClient(ILlmClient initial) : ILlmClient
{
    private readonly object _gate = new();
    private ILlmClient _current = initial;

    /// <summary>当前实际在用的端点名（诊断 / 界面显示用）。</summary>
    public string Name
    {
        get
        {
            lock (_gate)
            {
                return _current.Name;
            }
        }
    }

    /// <summary>换成另一个端点。下一个请求立即生效 —— 正在跑的那次不受影响。</summary>
    public void Switch(ILlmClient next)
    {
        lock (_gate)
        {
            _current = next;
        }
    }

    public async IAsyncEnumerable<LlmStreamChunk> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ILlmClient current;

        lock (_gate)
        {
            current = _current;
        }

        await foreach (var chunk in current.StreamAsync(request, ct).WithCancellation(ct).ConfigureAwait(false))
        {
            yield return chunk;
        }
    }
}
