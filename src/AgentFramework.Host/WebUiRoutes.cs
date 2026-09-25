using System.Net;
using System.Text.Json;

namespace AgentFramework.Host;

/// <summary>
/// 一次请求的上下文 —— 路由模块拿到的全部东西。
///
/// 它存在的意义是把「写响应」和「读请求体」这两件事从服务器里解放出来：
/// 路由模块不必认识服务器的内部结构，只要会读请求、会写响应。
/// </summary>
public sealed class WebUiRequest(HttpListenerContext http, AgentHost host, string method, string path)
{
    private JsonElement? _body;
    private bool _bodyRead;

    public HttpListenerContext Http { get; } = http;

    /// <summary>宿主。功能模块要什么（工具、记忆、会话……）都从这儿找。</summary>
    public AgentHost Host { get; } = host;

    public string Method { get; } = method;

    public string Path { get; } = path;

    /// <summary>
    /// 查询参数（F8 导出等 GET 端点用）。
    /// 为什么是方法而不是属性：HttpListener 的 QueryStringParse 惰性且无锁，
    /// 每次调用都重新解析 —— 不缓存就不会有「读时序」问题；
    /// 但调用方拿到的 NameValueCollection 是只读视图，改它没有意义。
    /// </summary>
    public System.Collections.Specialized.NameValueCollection Query => Http.Request.QueryString;

    /// <summary>读请求体。**同一个请求只能读一次**（读完就没了），所以这里缓存了结果。</summary>
    public async Task<JsonElement?> ReadBodyAsync()
    {
        if (!_bodyRead)
        {
            _body = await WebUiServer.ReadJsonBodyAsync(Http).ConfigureAwait(false);
            _bodyRead = true;
        }

        return _body;
    }

    /// <summary>回 JSON。</summary>
    public void Json(object payload, int statusCode = 200) => WebUiServer.WriteJson(Http, payload, statusCode);

    /// <summary>
    /// 回一段**已经序列化好**的 JSON（不给它二次序列化的机会）。
    /// 事件流这种大数组值得省下这一步。
    /// </summary>
    public void RawJson(string json, int statusCode = 200) => WebUiServer.WriteRawJson(Http, json, statusCode);

    /// <summary>回纯文本。</summary>
    public void Text(string content, string contentType = "text/plain; charset=utf-8", int statusCode = 200)
        => WebUiServer.WriteText(Http, contentType, content, statusCode);

    /// <summary>回二进制（favicon / 图片等）。字节原样写出，不做编码转换。</summary>
    public void Bytes(byte[] content, string contentType, int statusCode = 200)
        => WebUiServer.WriteBytes(Http, contentType, content, statusCode);
}

/// <summary>
/// 一段可注册的路由。
///
/// 为什么要有它：界面上的东西会一直长（技能面板、作业面板、问询卡片……），
/// 而「每加一个端点就改一次主分发」是个坏模式 —— 主文件越来越厚，
/// 每改一次都要重新过一遍所有人的验收。
/// 有了它，新功能**自带路由**：加端点 = 加一个类 + 在入口注册一行。
/// </summary>
public interface IWebRoute
{
    /// <summary>HTTP 方法；<c>"*"</c> 表示不限方法。</summary>
    string Method { get; }

    /// <summary>路径（精确匹配）。</summary>
    string Path { get; }

    ValueTask HandleAsync(WebUiRequest request, CancellationToken ct);
}

/// <summary>路由表：按 (方法, 路径) 精确匹配，读多写少。</summary>
internal sealed class WebRouteTable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IWebRoute> _exact = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IWebRoute> _anyMethod = new(StringComparer.Ordinal);

    private static string Key(string method, string path) => method + " " + path;

    public void Add(IWebRoute route)
    {
        lock (_gate)
        {
            if (route.Method == "*")
            {
                _anyMethod[route.Path] = route;
            }
            else
            {
                _exact[Key(route.Method, route.Path)] = route;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _exact.Count + _anyMethod.Count;
            }
        }
    }

    public IWebRoute? Match(string method, string path)
    {
        lock (_gate)
        {
            return _exact.GetValueOrDefault(Key(method, path))
                ?? _anyMethod.GetValueOrDefault(path);
        }
    }
}

/// <summary>
/// 用**委托**实现的路由 —— 给「本来就是一段直白逻辑」的端点用。
///
/// <para>
/// 有它之后，把主分发里的某个 <c>case</c> 搬出来只剩两件事：
/// 那段代码原样剪过来，把 <c>context</c> 换成 <c>request</c>。
/// 于是「按域拆文件」不再需要给每个端点设计一个类 ——
/// 拆起来就没有借口往后拖了。
/// </para>
/// </summary>
internal sealed class DelegateRoute(
    string method,
    string path,
    Func<WebUiRequest, CancellationToken, ValueTask> handler) : IWebRoute
{
    public string Method { get; } = method;

    public string Path { get; } = path;

    public ValueTask HandleAsync(WebUiRequest request, CancellationToken ct) => handler(request, ct);
}

/// <summary>
/// 内置的可注册路由：工具清单（含**来源**）。
///
/// 单独写成「注册路由」而不是塞进主分发，是个示范：
/// 「这个工具是谁挂上来的」这类诊断面以后只会更多，它们都该长成这个形状。
/// </summary>
internal sealed class ToolsRoute : IWebRoute
{
    public string Method => "GET";

    public string Path => "/api/tools";

    public ValueTask HandleAsync(WebUiRequest request, CancellationToken ct)
    {
        var host = request.Host;

        request.Json(new
        {
            tools = host.ToolNames
                .Select(name => new
                {
                    name,
                    source = host.ToolSources.GetValueOrDefault(name),
                })
                .ToList(),
            exposed = host.ExposedToolNames,
        });

        return ValueTask.CompletedTask;
    }
}
