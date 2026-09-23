using System.Text;

namespace AgentFramework.Host;

/// <summary>
/// 内置路由 · <b>SSE 长连接</b>（P5）。
///
/// <para>
/// 这是唯一一个「不像普通请求」的端点：它不写一次响应就结束，
/// 而是把连接留在 <see cref="WebUiServer"/> 的客户端表里，
/// 由每个人自己的写循环把队列里的帧推出去。
/// </para>
/// <para>
/// 之所以仍然能做成路由：路由只负责「接住这次请求并把它挂上去」，
/// 后续的推送与它无关 —— 那是 <c>Broadcast</c> 与写循环的事。
/// </para>
/// </summary>
public sealed partial class WebUiServer
{
    /// <summary>注册 SSE 长连接路由。</summary>
    private void RegisterStreamRoute()
    {
        Map(new DelegateRoute("GET", "/api/stream", (request, _) =>
        {
            var response = request.Http.Response;

            response.StatusCode = 200;
            response.ContentType = "text/event-stream; charset=utf-8";
            response.Headers.Add("Cache-Control", "no-cache");
            response.SendChunked = true;

            var client = new SseClient(response);
            lock (_gate)
            {
                _clients.Add(client);
            }

            // 每个客户端一个写循环：队列 → 网络（网络写不占回合线程，P1-F3）
            StartWriter(client);

            // 先送一帧注释，浏览器不等数据就知道流已经通了
            var hello = Encoding.UTF8.GetBytes(": connected\n\n");
            client.Queue.Writer.TryWrite(hello);

            return ValueTask.CompletedTask;
        }));
    }
}
