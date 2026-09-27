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
///
/// <para>
/// <b>退役旧端点</b>（安全审查 P1-1）：内层可能是 <see cref="IDisposable"/>
/// （如持 HttpMessageHandler / 连接池的 <c>OpenAiCompatibleClient</c>）。
/// 若 <see cref="Switch"/> 只改引用不释放，UI 每切一次模型就泄漏一个连接池。
/// 但旧端点可能正被「正在跑的那一轮」使用，不能当场 Dispose ——
/// 于是记下活跃请求数，等最后一个请求结束再统一退役（见 <see cref="RetireIfIdle"/>）。
/// </para>
/// </summary>
public sealed class SwitchableLlmClient(ILlmClient initial) : ILlmClient, IDisposable
{
    private readonly object _gate = new();
    private ILlmClient _current = initial;
    private readonly List<ILlmClient> _retired = [];   // 待退役：等活跃请求归零再 Dispose
    private int _activeRequests;
    private bool _disposed;

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
        ArgumentNullException.ThrowIfNull(next);

        var dispose = new List<ILlmClient>();
        lock (_gate)
        {
            if (ReferenceEquals(_current, next))
            {
                return;
            }

            var old = _current;
            _current = next;

            if (_activeRequests == 0)
            {
                dispose.Add(old);      // 没人在用 → 立刻退役
            }
            else
            {
                _retired.Add(old);     // 有人在用 → 记为待退役，等它跑完再释放
            }
        }

        DisposeAll(dispose);
    }

    public async IAsyncEnumerable<LlmStreamChunk> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ILlmClient current;

        lock (_gate)
        {
            current = _current;
            _activeRequests++;
        }

        try
        {
            await foreach (var chunk in current.StreamAsync(request, ct).WithCancellation(ct).ConfigureAwait(false))
            {
                yield return chunk;
            }
        }
        finally
        {
            RetireIfIdle();
        }
    }

    /// <summary>活跃请求归零时，把待退役的旧端点一次性释放掉。</summary>
    private void RetireIfIdle()
    {
        List<ILlmClient>? dispose = null;
        lock (_gate)
        {
            _activeRequests--;
            if (_activeRequests <= 0 && _retired.Count > 0)
            {
                dispose = [.. _retired];
                _retired.Clear();
            }
        }

        DisposeAll(dispose);
    }

    public void Dispose()
    {
        List<ILlmClient> dispose = [];
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            dispose.Add(_current);
            if (_retired.Count > 0)
            {
                dispose.AddRange(_retired);
                _retired.Clear();
            }
        }

        DisposeAll(dispose);
    }

    private static void DisposeAll(IReadOnlyList<ILlmClient>? clients)
    {
        if (clients is null)
        {
            return;
        }

        foreach (var client in clients)
        {
            if (client is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
