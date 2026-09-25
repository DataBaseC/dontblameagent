using System.Diagnostics;
using System.Net;
using System.Text;
using AgentFramework.Launcher;

namespace AgentFramework.Launcher;

/// <summary>启动器的运行时状态。</summary>
public sealed class LauncherState
{
    /// <summary>
    /// 宿主进程句柄的锁（F2）：/launch、/stop 与进程退出回调三方并发，
    /// 无锁时 ReferenceEquals 判断与赋值之间存在窗口（回调与手动 stop 同时发生）。
    /// 进程守卫的读取统一走 <see cref="GetRunningHost"/>。
    /// </summary>
    public object HostGate { get; } = new();

    /// <summary>锁内读取：返回还活着的宿主进程，没有则 null（顺手清掉死句柄）。</summary>
    public Process? GetRunningHost()
    {
        lock (HostGate)
        {
            if (HostProcess is { HasExited: false } p)
            {
                return p;
            }

            HostProcess = null;
            HostUrl = null;
            return null;
        }
    }

    public required string PluginsDir { get; init; }

    public required string ProfilePath { get; init; }

    public required PluginProfile Profile { get; set; }

    public required IReadOnlyList<PluginDescriptor> Catalog { get; init; }

    public required IReadOnlyList<string> Errors { get; init; }

    public List<string> Log { get; } = [];

    /// <summary>启动 Host 时使用的命令行（由 Program 注入，便于测试时不真的拉起进程）。</summary>
    public Func<IReadOnlyList<string>, string>? Launcher { get; set; }

    /// <summary>已拉起的宿主进程（v2：启动器负责它的生与死）。测试注入 Launcher 时保持 null。</summary>
    public Process? HostProcess { get; set; }

    /// <summary>宿主 Web UI 地址（启动后从进程输出或约定端口得知；供"打开工作区"链接用）。</summary>
    public string? HostUrl { get; set; }

    /// <summary>
    /// 宿主 stdout/stderr 的环形缓冲（F6）：debug 窗显示尾部，
    /// 实测时不用切控制台就能看到宿主最后一口气说了什么。
    /// </summary>
    public FixedSizeLog HostOutput { get; } = new(200);

    /// <summary>
    /// 启动器自己的设置（目前只有「启动时要不要自动打开浏览器」）。
    /// 默认实例 = 默认值；Program 启动时用磁盘上的文件覆盖，页面上的开关改它并回写。
    /// </summary>
    public LauncherSettings Settings { get; set; } = new();

    /// <summary>设置文件的落点（页面改开关后保存到这里）。空 = 只改内存、不落盘。</summary>
    public string SettingsPath { get; set; } = "";

    // ── 内置载荷解压状态（仅单文件形态非默认）──────────────
    // Program.cs 的后台解压任务写，HTTP 线程读 —— 三个都是简单赋值/读，
    // 用 volatile 语义的属性足够（进度字符串替换是原子的）。

    /// <summary>true = 后台解压还在进行，此刻不能启动宿主。</summary>
    public volatile bool BundleBusyField;

    public bool BundleBusy
    {
        get => BundleBusyField;
        set => BundleBusyField = value;
    }

    /// <summary>解压进度描述（如「解压中… 100/524」）。</summary>
    public string BundleProgress { get; set; } = "";

    /// <summary>解压失败原因（null = 没失败或还没跑完）。</summary>
    public string? BundleError { get; set; }
}

/// <summary>
/// 启动器的本地 HTTP 服务 + 插件管理页面（v2）。
///
/// v1 是"勾选 + 保存"；v2 补齐 Mod 管理器的另外两块：
///   · <b>加载顺序</b> —— 上移/下移按钮改序（服务端渲染零 JS，排序进 profile.LoadOrder）；
///   · <b>前置解析</b> —— 按 injects 标出前置徽标，预检发现缺前置/顺序违规/坏清单时给出修法；
///   · <b>进程管理</b> —— 启动 = 拉起宿主进程并记住它，停止 = Kill 它，"打开工作区"直达宿主 Web UI。
///
/// 为什么仍是纯链接 + 服务端渲染（零 JS）：拖拽等交互后续可以加，但"点一下就生效"的可靠性优先。
/// </summary>
public sealed class LauncherServer
{
    private readonly HttpListener _listener = new();
    private readonly LauncherState _state;

    public LauncherServer(LauncherState state, int port)
    {
        _state = state;
        Url = $"http://localhost:{port}/";
        _listener.Prefixes.Add(Url);
    }

    public string Url { get; }

    public async Task RunAsync(CancellationToken ct)
    {
        _listener.Start();

        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch
            {
                break;
            }

            try
            {
                Handle(context);
            }
            catch (Exception ex)
            {
                _state.Log.Add($"处理请求出错：{ex.Message}");
                TryRedirect(context, "/");
            }
        }
    }

    public void Stop()
    {
        try
        {
            if (_listener.IsListening)
            {
                _listener.Stop();
            }
        }
        catch
        {
            // 关闭时的异常无所谓
        }
    }

    /// <summary>启动器自身退出时顺手收掉宿主进程（否则会留下孤儿 dotnet）。</summary>
    public void Shutdown()
    {
        Stop();
        KillHost();
    }

    private void Handle(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? "/";

        // F1：与 WebUI 同一道防线。启动器现在能真操作宿主进程（launch/stop），
        // 被恶意网页 fetch 等于远程开关你的宿主。
        //
        // 安全审查 P0-1/P0-2 加固：
        //   · Host 头白名单 —— 挡 DNS rebinding（rebind 后请求"同源"，CORS/Origin 都拦不住，
        //     但 Host 头还是恶意域名）；
        //   · 状态变更端点（toggle/move/save/launch/stop）必须是 POST ——
        //     跨站 <img>/<a> 不带 Origin 头，空 Origin 放行正好命中；
        //     GET 版本一律 405，页面上的按钮改为小段 JS 提交。
        if (!IsAllowedHost(context.Request)
            || !IsAllowedOrigin(context.Request))
        {
            try
            {
                context.Response.StatusCode = 403;
                context.Response.Close();
            }
            catch
            {
                // 连接已断
            }

            return;
        }

        // 状态变更端点：只按 POST（页面 JS 提交）；GET 一律 405。
        // 页面渲染仍走 GET（/、/debug、/api/state）—— 它们只读不写。
        var isStateChange = path is "/toggle" or "/move" or "/save" or "/launch" or "/stop" or "/toggle-open";
        if (isStateChange && !string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                context.Response.StatusCode = 405;
                context.Response.Headers.Add("Allow", "POST");
                context.Response.Close();
            }
            catch
            {
                // 连接已断
            }

            return;
        }

        switch (path)
        {
            case "/toggle":
            {
                var id = context.Request.QueryString["id"];
                if (!string.IsNullOrWhiteSpace(id))
                {
                    if (_state.Profile.IsEnabled(id))
                    {
                        _state.Profile.EnabledPlugins.Remove(id);
                        _state.Log.Add($"取消启用：{id}");
                    }
                    else
                    {
                        _state.Profile.EnabledPlugins.Add(id);
                        _state.Log.Add($"启用：{id}");
                    }

                    _state.Profile.EnsureLoadOrderCovers(CatalogOrder());
                    _state.Profile.Save(_state.ProfilePath);
                }

                TryRedirect(context, "/");
                return;
            }

            // v2：加载顺序 —— 把 from 移到 to 之前
            case "/move" when context.Request.QueryString["from"] is { Length: > 0 } from
                && context.Request.QueryString["to"] is { Length: > 0 } to:
            {
                var catalogOrder = CatalogOrder();
                if (_state.Profile.MoveBefore(from, to, catalogOrder))
                {
                    _state.Profile.Save(_state.ProfilePath);
                    _state.Log.Add($"加载顺序：{from} 移到 {to} 之前");
                }
                else
                {
                    _state.Log.Add($"移动失败：{from} → {to} 之前（id 不在目录里？）");
                }

                TryRedirect(context, "/");
                return;
            }

            case "/save":
            {
                _state.Profile.Save(_state.ProfilePath);
                _state.Log.Add($"已保存档案：{_state.ProfilePath}");
                TryRedirect(context, "/");
                return;
            }

            case "/launch":
            {
                LaunchHost();
                TryRedirect(context, "/");
                return;
            }

            case "/stop":
            {
                KillHost();
                _state.Log.Add("已停止宿主进程");
                TryRedirect(context, "/");
                return;
            }

            // ④：切换「启动时自动打开浏览器」。持久化 —— 下次运行才起作用，
            //     本次已经开着的页面不受影响（所以它是"设置"，不是"动作"）。
            case "/toggle-open":
            {
                _state.Settings.OpenBrowserOnStart = !_state.Settings.OpenBrowserOnStart;
                if (!string.IsNullOrWhiteSpace(_state.SettingsPath))
                {
                    try
                    {
                        _state.Settings.Save(_state.SettingsPath);
                    }
                    catch (Exception ex)
                    {
                        _state.Log.Add($"设置保存失败（本次内存里仍生效）：{ex.Message}");
                    }
                }

                _state.Log.Add("启动时自动打开浏览器：" + (_state.Settings.OpenBrowserOnStart ? "开" : "关"));
                TryRedirect(context, "/");
                return;
            }

            case "/open":
            {
                if (!string.IsNullOrWhiteSpace(_state.HostUrl))
                {
                    TryRedirect(context, _state.HostUrl);
                }
                else
                {
                    TryRedirect(context, "/");
                }

                return;
            }

            // ── debug 信息窗（v2 新增，自用产品不做伪装）────────────
            case "/debug":
            {
                WriteHtml(context, RenderDebug());
                return;
            }

            case "/api/state":
            {
                WriteJson(context, BuildStateJson());
                return;
            }

            // 标签页 favicon：与本体同一张内嵌图，不依赖工作目录。
            case "/icon.png":
            {
                var icon = LoadEmbeddedIcon();
                if (icon is null)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                }
                else
                {
                    WriteBytes(context, "image/png", icon);
                }

                return;
            }

            default:
                WriteHtml(context, RenderPage());
                return;
            }
        }

    // ── 启动 / 停止宿主 ────────────────────────────────────────

    private IReadOnlyList<string> EnabledInLoadOrder()
        => _state.Profile.OrderedEnabled(CatalogOrder());

    private IReadOnlyList<string> CatalogOrder()
        => _state.Catalog.Select(p => p.Id).ToList();

    private void LaunchHost()
    {
        _state.Profile.Save(_state.ProfilePath);

        var enabled = EnabledInLoadOrder();
        var args = new List<string> { "--plugins", _state.PluginsDir };
        if (enabled.Count > 0)
        {
            args.Add("--plugins-enabled");
            // 顺序即装配顺序：前置必然排在依赖者之前（预检保证；保证不了就先报警）
            args.Add(string.Join(',', enabled));
        }

        if (_state.GetRunningHost() is not null)
        {
            _state.Log.Add("宿主已在运行（先停止再重新启动）");
            return;
        }

        var message = _state.Launcher is not null
            ? _state.Launcher(args)
            : "（未配置启动方式）";
        _state.Log.Add(message);
    }

    private void KillHost()
    {
        try
        {
            lock (_state.HostGate)
            {
                if (_state.HostProcess is { HasExited: false } process)
                {
                    process.Kill(entireProcessTree: true);
                    _state.HostProcess = null;
                    _state.HostUrl = null;
                }
            }
        }
        catch (Exception ex)
        {
            _state.Log.Add($"停止宿主失败：{ex.Message}");
        }
    }

    private bool IsRunning => _state.GetRunningHost() is not null;

    // ── 预检 ──────────────────────────────────────────────────

    private IReadOnlyList<PreflightIssue> RunPreflight()
        => Preflight.Run(_state.Catalog, EnabledInLoadOrder(), _state.Errors);

    // ── 页面 ──────────────────────────────────────────────────

    private string RenderPage()
    {
        var builder = new StringBuilder();
        var issues = RunPreflight();
        var runningHost = _state.GetRunningHost();
        var blocking = issues.Where(i => i.Kind is PreflightKind.CatalogError or PreflightKind.DuplicateId).ToList();
        var warnings = issues.Where(i => i.Kind is PreflightKind.MissingDependency or PreflightKind.DependencyAfterDependent).ToList();

        builder.Append("""
            <!doctype html>
            <html lang="zh-CN">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Agent 启动器 · 插件管理</title>
            <link rel="icon" type="image/png" href="/icon.png">
            <style>
              :root { color-scheme: dark;
                      --bg: #14161a; --card: #1c1f26; --border: #2a2f39; --line: #242832;
                      --text: #e6e8ec; --dim: #8b93a1; --faint: #666e7c;
                      --accent: #2f6bff; --ok: #44d07b; --ok-text: #7ee2a8; --warn: #e8c07a; --bad: #ff8f9d;
                      --anim: .16s cubic-bezier(.2, .7, .3, 1); }
              * { box-sizing: border-box; }
              body { margin: 0; padding: 32px 20px; background: var(--bg); color: var(--text);
                     font-family: -apple-system, "Segoe UI", "Microsoft YaHei", "PingFang SC", system-ui, sans-serif;
                     -webkit-font-smoothing: antialiased; }
              ::-webkit-scrollbar { width: 10px; }
              ::-webkit-scrollbar-thumb { background: #262c37; border-radius: 6px; }
              ::selection { background: rgba(47, 107, 255, .35); }
              :focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; border-radius: 4px; }
              .wrap { max-width: 860px; margin: 0 auto; }
              h1 { font-size: 21px; margin: 0 0 4px; font-weight: 600; letter-spacing: .5px; }
              h1 .ver { font-size: 12px; color: var(--dim); font-weight: 400; letter-spacing: 0;
                        margin-left: 10px; vertical-align: middle; }
              .sub { color: var(--dim); font-size: 13px; margin: 0 0 20px; }
              .sub b { color: #aab3c2; }
              .card { background: var(--card); border: 1px solid var(--border); border-radius: 10px; overflow: hidden;
                      box-shadow: 0 8px 28px rgba(0, 0, 0, .25); }
              .head { display: flex; align-items: center; gap: 14px; padding: 12px 18px; border-bottom: 1px solid var(--line);
                      font-size: 11px; color: #7c8595; letter-spacing: .08em; }
              .row { display: flex; align-items: center; gap: 12px; padding: 12px 18px; border-bottom: 1px solid var(--line);
                     transition: background var(--anim); }
              .row:hover { background: #20242d; }
              .row:last-child { border-bottom: 0; }
              .num { font-family: Consolas, monospace; font-size: 12px; color: var(--faint); width: 24px; flex: 0 0 auto; }
              .dot { width: 9px; height: 9px; border-radius: 50%; background: #3a4150; flex: 0 0 auto;
                     transition: background var(--anim); }
              .row.on .dot { background: var(--ok); box-shadow: 0 0 8px #44d07b88; animation: breathe 2.4s ease-in-out infinite; }
              .info { flex: 1 1 auto; min-width: 0; }
              .name { font-size: 14px; font-weight: 600; }
              .name em { font-style: normal; color: #7c8595; font-weight: 400; font-size: 12px; margin-left: 8px; }
              .desc { font-size: 12px; color: #7c8595; margin-top: 3px; }
              .tags { margin-top: 5px; display: flex; gap: 6px; flex-wrap: wrap; }
              .tag { font-size: 10.5px; padding: 2px 8px; border-radius: 5px; border: 1px solid #39404e; color: #8b93a1; }
              .tag.dep { color: #6ea8fe; border-color: #2b4a7a; }
              .tag.req { color: #e8c07a; border-color: #6b4a1f; }
              .tag.bad { color: #ff8f9d; border-color: #6b2f3a; }
              a.btn { flex: 0 0 auto; text-decoration: none; font-size: 12px; padding: 6px 14px; border-radius: 6px;
                      border: 1px solid #39404e; color: #aab3c2; white-space: nowrap; cursor: pointer;
                      transition: border-color var(--anim), color var(--anim), background var(--anim), transform var(--anim); }
              a.btn:hover { border-color: var(--accent); color: #dce3f0; }
              a.btn:active { transform: translateY(1px); }
              a.btn.ghost { opacity: .55; }
              .row.on a.btn { background: #1d3f2c; border-color: #2f6b48; color: var(--ok-text); }
              .bar { margin-top: 20px; display: flex; gap: 12px; align-items: center; flex-wrap: wrap; }
              a.primary { text-decoration: none; padding: 10px 22px; border-radius: 8px; font-size: 14px;
                          background: var(--accent); color: #fff; font-weight: 600; cursor: pointer;
                          transition: filter var(--anim), transform var(--anim), box-shadow var(--anim); }
              a.primary:hover { filter: brightness(1.12); box-shadow: 0 2px 14px rgba(47, 107, 255, .3); }
              a.primary:active { transform: translateY(1px); }
              a.primary.stop { background: #6b2f3a; }
              @keyframes breathe { 0%, 100% { opacity: 1; } 50% { opacity: .45; } }
              @media (prefers-reduced-motion: reduce) { *, *::before, *::after { animation: none !important; transition: none !important; } }
              a.primary.ghost { background: transparent; border: 1px solid #39404e; color: #aab3c2; font-weight: 400; }
              .meta { margin-top: 22px; font-size: 12px; color: #666e7c; line-height: 1.9; }
              .meta code { color: #9aa4b4; }
              .box { margin-top: 14px; padding: 12px 16px; border-radius: 8px; font-size: 12px; line-height: 1.9; }
              .box.warn { background: #3a2a16; border: 1px solid #6b4a1f; color: #e8c07a; }
              .box.bad { background: #3a1a20; border: 1px solid #6b2f3a; color: #ff8f9d; }
              .box.ok { background: #16281e; border: 1px solid #2f6b48; color: #7ee2a8; }
              .box .fix { color: #9ecbff; }
              .log { margin-top: 14px; font-size: 12px; color: #6f7787; line-height: 1.8; }
              .dbg { position: fixed; right: 14px; bottom: 14px; font-size: 11px; color: #666e7c;
                     background: #1c1f26ee; border: 1px solid #2a2f39; border-radius: 8px; padding: 8px 12px; }
              .dbg a { color: #6ea8fe; text-decoration: none; }
              .run-dot { display: inline-block; width: 8px; height: 8px; border-radius: 50%; background: var(--ok);
                         box-shadow: 0 0 8px #44d07b88; margin-right: 4px; vertical-align: 1px;
                         animation: breathe 2.4s ease-in-out infinite; }
            </style>
            </head>
            <body>
            <div class="wrap">
              <h1>Agent 启动器
            """);
        builder.Append($" <span class=\"ver\">{Escape(BuildInfo.Stamp)}</span></h1>\n");
        builder.Append("""
              <p class="sub">列表顺序 = <b>装配顺序</b>（前置必须在上）。用 ↑↓ 调序，勾选决定装不装，预检拦住会炸的组合。</p>
              <div class="card">
                <div class="head"><span>#</span><span style="flex:1">插件（按加载顺序）</span><span>状态</span></div>
            """);

        if (_state.Catalog.Count == 0)
        {
            builder.Append("""<div class="row"><div class="info"><div class="desc">插件目录下没有发现任何插件。</div></div></div>""");
        }

        var ordered = EnabledInLoadOrder();
        var posOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < ordered.Count; i++)
        {
            posOf[ordered[i]] = i + 1;
        }

        // 页面按「启用者按加载序在前、未启用按目录序在后」呈现——与装配语义一致
        var renderOrder = ordered.Concat(_state.Catalog.Select(p => p.Id).Where(id => !_state.Profile.IsEnabled(id))).Distinct().ToList();
        var byId = _state.Catalog.ToDictionary(p => p.Id, StringComparer.Ordinal);

        foreach (var id in renderOrder)
        {
            if (!byId.TryGetValue(id, out var plugin))
            {
                continue;   // 已启用但目录缺失（坏清单）—— Errors 区已展示
            }

            var enabled = _state.Profile.IsEnabled(id);
            var idx = enabled && posOf.TryGetValue(id, out var p) ? p.ToString("00") : "--";

            builder.Append(enabled ? """<div class="row on">""" : """<div class="row">""");
            builder.Append($"""<span class="num">{idx}</span>""");
            builder.Append("""<span class="dot"></span>""");
            builder.Append("""<div class="info">""");
            builder.Append($"""<div class="name">{Escape(plugin.Name)}<em>{Escape(plugin.Version)} · id={Escape(plugin.Id)}</em></div>""");

            var desc = plugin.Description ?? (plugin.Injects.Count > 0 ? $"依赖：{string.Join(", ", plugin.Injects)}" : "无描述");
            builder.Append($"""<div class="desc">{Escape(desc)}</div>""");

            var tags = new StringBuilder();
            if (enabled)
            {
                foreach (var inject in plugin.Injects)
                {
                    var depEnabled = _state.Profile.IsEnabled(inject);
                    var depExists = byId.ContainsKey(inject);
                    if (!depExists)
                    {
                        tags.Append($"""<span class="tag">inject: {Escape(inject)}（服务）</span>""");
                    }
                    else if (depEnabled)
                    {
                        tags.Append($"""<span class="tag dep">前置: {Escape(byId[inject].Name)}</span>""");
                    }
                    else
                    {
                        tags.Append($"""<span class="tag bad">前置未启用: {Escape(byId[inject].Name)}</span>""");
                    }
                }

                if (plugin.Injects.Count == 0)
                {
                    tags.Append("""<span class="tag">无前置</span>""");
                }
            }
            else
            {
                tags.Append("""<span class="tag">已停用</span>""");
            }

            builder.Append($"""<div class="tags">{tags}</div>""");
            builder.Append("</div>");

            // 排序按钮（只在启用行之间移动）
            if (enabled)
            {
                // IndexOf 用逐个比较（IReadOnlyList<string> 没有 List 的 IndexOf 扩展）；
                // 列表规模 = 启用插件数，量级极小，无需优化。
                var rowIdx = -1;
                for (var i = 0; i < ordered.Count; i++)
                {
                    if (string.Equals(ordered[i], id, StringComparison.Ordinal))
                    {
                        rowIdx = i;
                        break;
                    }
                }

                var above = rowIdx > 0 ? ordered[rowIdx - 1] : null;
                var below = rowIdx >= 0 && rowIdx < ordered.Count - 1 ? ordered[rowIdx + 1] : null;
                // 安全加固配套 UI：状态变更统一 POST（见页面尾部 post() 脚本）
                builder.Append($"""<a class="btn" title="上移（更早装配）" href="#" onclick="return post('/move?from={Uri.EscapeDataString(id)}&to={Uri.EscapeDataString(above ?? id)}')" style="opacity:{(above is null ? ".3" : "1")}">↑</a>""");
                builder.Append($"""<a class="btn" title="下移（更晚装配）" href="#" onclick="return post('/move?from={Uri.EscapeDataString(id)}&to={Uri.EscapeDataString(below ?? id)}')" style="opacity:{(below is null ? ".3" : "1")}">↓</a>""");
            }

            builder.Append($"""<a class="btn{(enabled ? "" : " ghost")}" href="#" onclick="return post('/toggle?id={Uri.EscapeDataString(id)}')">{(enabled ? "已启用" : "未启用")}</a>""");
            builder.Append("</div>");
        }

        builder.Append("</div>");

        // 启动条
        builder.Append("""<div class="bar">""");
        if (IsRunning)
        {
            // 安全加固配套 UI：状态变更端点只按 POST，按钮统一走 JS post()（见页面尾部脚本）
            builder.Append("""<a class="primary stop" href="#" onclick="return post('/stop', '已停止宿主进程')">停止宿主</a>""");
            builder.Append("""<a class="primary ghost" href="/open">打开工作区 ↗</a>""");
            builder.Append($"""<span class="meta" style="margin:0"><span class="run-dot"></span> PID {runningHost!.Id} · 运行中</span>""");
        }
        else
        {
            var hasBlocking = blocking.Count > 0;
            var confirmPrefix = hasBlocking ? "if (!confirm('预检发现问题，确定仍要启动？')) return false; " : string.Empty;
            builder.Append($"""<a class="primary" href="#" onclick="{confirmPrefix}return post('/launch', '已保存并启动宿主')">保存并启动</a>""");
        }

        builder.Append("""<a class="primary ghost" href="#" onclick="return post('/save', '档案已保存')">仅保存档案</a>""");
        builder.Append("</div>");

        // 预检结果窗
        if (blocking.Count > 0)
        {
            builder.Append("""<div class="box bad"><b>启动会被拦下</b>（这些是装配期必炸的）：<br>""");
            foreach (var issue in blocking)
            {
                builder.Append($"· {Escape(issue.Message)}<br>");
            }

            builder.Append("</div>");
        }

        if (warnings.Count > 0)
        {
            builder.Append("""<div class="box warn"><b>前置问题</b>（不拦启动，但插件会装不上或白装）：<br>""");
            foreach (var issue in warnings)
            {
                builder.Append($"· {Escape(issue.Message)}");
                if (issue.FixHint is not null)
                {
                    builder.Append($"<span class=\"fix\">　→ {Escape(issue.FixHint)}</span>");
                }

                builder.Append("<br>");
            }

            builder.Append("</div>");
        }

        if (issues.Count == 0)
        {
            builder.Append("""<div class="box ok">预检通过：前置完整 · 顺序合法 · 无重名</div>""");
        }

        if (_state.Errors.Count > 0)
        {
            builder.Append("""<div class="box warn">以下插件目录有问题，已被跳过：<br>""");
            foreach (var error in _state.Errors)
            {
                builder.Append($"· {Escape(error)}<br>");
            }

            builder.Append("</div>");
        }

        builder.Append($"""
            <div class="meta">
              启动器：<code>{Escape(BuildInfo.Stamp)}</code><br>
              插件目录：<code>{Escape(_state.PluginsDir)}</code><br>
              装配档案：<code>{Escape(_state.ProfilePath)}</code>（LoadOrder 字段 = 手动排序）<br>
              已启用 {_state.Profile.EnabledPlugins.Count} / {_state.Catalog.Count} 个 · 装配顺序见列表序号
            </div>
            <div class="meta">
              启动时自动打开浏览器：<b>{(_state.Settings.OpenBrowserOnStart ? "开" : "关")}</b>
              · <a href="#" onclick="return post('/toggle-open', '已切换')">{(_state.Settings.OpenBrowserOnStart ? "关掉它" : "打开它")}</a>
              （关掉后本页仍可随时在浏览器打开；想只对本次强制打开，启动时加 <code>--open</code>）
            </div>
            """);

        if (_state.Log.Count > 0)
        {
            builder.Append("""<div class="log">""");
            foreach (var line in _state.Log.TakeLast(8))
            {
                builder.Append($"· {Escape(line)}<br>");
            }

            builder.Append("</div>");
        }

        builder.Append("""
            <div class="dbg">自用 debug：<a href="/debug">信息窗</a> · <a href="/api/state">/api/state</a></div>
            </div>
            <script>
            // 状态变更统一走 POST（配套服务端加固：GET 写端点已全部 405）。
            // 提交后整页重载 —— 页面是服务端渲染，重载即最新状态，零前端状态可失步。
            function post(path, okMsg) {
              fetch(path, { method: 'POST' })
                .then((r) => {
                  if (r.ok) { location.reload(); }
                  else { alert('操作失败 HTTP ' + r.status); }
                })
                .catch((e) => alert('操作失败：' + e.message));
              return false;
            }
            </script>
            </body>
            </html>
            """);

        return builder.ToString();
    }

    /// <summary>debug 信息窗：把启动器的内部状态一屏摊开，实测时省得翻日志。</summary>
    private string RenderDebug()
    {
        var issues = RunPreflight();
        var builder = new StringBuilder();

        builder.Append("""
            <!doctype html>
            <html lang="zh-CN">
            <head>
            <meta charset="utf-8">
            <meta http-equiv="refresh" content="3">
            <title>启动器 · debug 信息窗</title>
            <style>
              :root { color-scheme: dark; }
              body { margin: 0; padding: 24px; background: #101216; color: #c8d0dc;
                     font: 13px/1.8 Consolas, "JetBrains Mono", monospace; }
              h1 { font: 600 15px "Segoe UI", sans-serif; color: #e6e8ec; margin: 0 0 4px; }
              .sub { color: #5c6570; font-size: 11px; margin-bottom: 18px; }
              table { border-collapse: collapse; margin-bottom: 18px; }
              td { border: 1px solid #242a33; padding: 5px 12px; }
              td.k { color: #6f7787; }
              .ok { color: #7ee2a8; } .bad { color: #ff8f9d; } .warn { color: #e8c07a; }
              pre { background: #16191f; border: 1px solid #242a33; border-radius: 8px; padding: 12px 14px;
                    overflow-x: auto; font-size: 12px; line-height: 1.7; color: #9aa4b4; }
              a { color: #6ea8fe; text-decoration: none; }
            </style>
            </head>
            <body>
            <h1>启动器 debug 信息窗</h1>
            <div class="sub">每 3 秒自动刷新 · <a href="/">← 返回管理页</a> · <a href="/api/state">JSON</a></div>
            """);

        builder.Append("<table>");
        AddRow(builder, "启动器版本", Escape(BuildInfo.Stamp));
        AddRow(builder, "宿主进程", IsRunning ? $"<span class='ok'>运行中 PID {_state.GetRunningHost()!.Id}</span>" : "<span class='warn'>未运行</span>");
        // v3.5 审查：AddRow 的 value 通道是「已渲染 HTML」（调用方有意塞 <span>），
        // 所以纯数据项必须在调用处各自转义。
        AddRow(builder, "宿主 URL", _state.HostUrl is null ? "（未知）" : Escape(_state.HostUrl));
        AddRow(builder, "插件目录", Escape(_state.PluginsDir));
        AddRow(builder, "档案路径", Escape(_state.ProfilePath));
        AddRow(builder, "扫描插件数", _state.Catalog.Count.ToString());
        AddRow(builder, "目录错误数", _state.Errors.Count == 0 ? "<span class='ok'>0</span>" : $"<span class='bad'>{_state.Errors.Count}</span>");
        AddRow(builder, "已启用", _state.Profile.EnabledPlugins.Count.ToString());
        AddRow(builder, "预检问题", issues.Count == 0 ? "<span class='ok'>0</span>" : $"<span class='warn'>{issues.Count}</span>");
        builder.Append("</table>");

        builder.Append("<pre>");
        foreach (var id in EnabledInLoadOrder())
        {
            // v3.5 审查：插件 id 来自外部清单、无字符白名单，进 HTML 前必须转义（同源存储型 XSS）。
            builder.Append($"装配序: {Escape(id)}\n");
        }

        builder.Append("</pre>");

        builder.Append("<h1>宿主输出（尾部）</h1><pre>");
        foreach (var line in _state.HostOutput.Snapshot().TakeLast(40))
        {
            builder.Append(Escape(line) + "\n");
        }

        builder.Append("</pre>");

        builder.Append("<h1>最近日志</h1><pre>");
        foreach (var line in _state.Log.TakeLast(20))
        {
            builder.Append(Escape(line) + "\n");
        }

        builder.Append("</pre>");

        if (issues.Count > 0)
        {
            builder.Append("<h1>预检明细</h1><pre>");
            foreach (var issue in issues)
            {
                builder.Append($"[{issue.Kind}] {Escape(issue.Message)}\n");
            }

            builder.Append("</pre>");
        }

        builder.Append("</body></html>");
        return builder.ToString();

        void AddRow(StringBuilder b, string k, string v)
            => b.Append($"<tr><td class='k'>{Escape(k)}</td><td>{v}</td></tr>");
    }

    private string BuildStateJson()
    {
        var issues = RunPreflight();

        // v3.6 审查修复：改用 JsonSerializer —— 原先手工拼 JSON 且用 HTML 转义器
        // （只替 & < > "，不转反斜杠），Windows 路径里的 `\` 产出非法转义，
        // /api/state 直接变成格式错误的 JSON，任何消费者解析即失败。
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            running = IsRunning,
            bundleBusy = _state.BundleBusy,
            bundleProgress = _state.BundleProgress,
            bundleError = _state.BundleError,
            hostPid = IsRunning ? _state.GetRunningHost()!.Id : (int?)null,
            hostUrl = _state.HostUrl,
            loadOrder = EnabledInLoadOrder().ToArray(),
            issues = issues.Select(i => new { kind = i.Kind, plugin = i.PluginId, message = i.Message }).ToArray(),
        });
    }

    /// <summary>
    /// 浏览器发起的跨站 fetch 必带 Origin 头。与 WebUiServer 同一规则：
    /// Origin 为空（curl / 桌面壳 / 验证程序）或与自身同源才放行。
    /// </summary>
    private static bool IsAllowedOrigin(HttpListenerRequest request)
    {
        var origin = request.Headers["Origin"];
        if (string.IsNullOrEmpty(origin))
        {
            return true;
        }

        var port = request.LocalEndPoint?.Port ?? request.Url?.Port ?? 80;
        return origin.Equals($"http://localhost:{port}", StringComparison.OrdinalIgnoreCase)
            || origin.Equals($"http://127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 防 DNS rebinding（与 WebUiServer 同款）：只认指向本机的 Host 头。
    /// rebind 攻击里 Host 仍是恶意域名 —— CORS 与 Origin 都挡不住"同源"伪装，Host 一眼识破。
    /// </summary>
    private static bool IsAllowedHost(HttpListenerRequest request)
    {
        var host = request.Headers["Host"];
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        var port = request.LocalEndPoint?.Port ?? request.Url?.Port ?? 80;
        return host.Equals($"localhost:{port}", StringComparison.OrdinalIgnoreCase)
            || host.Equals($"127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase)
            || host.Equals($"[::1]:{port}", StringComparison.OrdinalIgnoreCase);
    }

    private static string Escape(string value)
        => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static void WriteHtml(HttpListenerContext context, string html)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes);
        context.Response.Close();
    }

    private static void WriteJson(HttpListenerContext context, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes);
        context.Response.Close();
    }

    private static void WriteBytes(HttpListenerContext context, string contentType, byte[] bytes)
    {
        context.Response.StatusCode = 200;
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes);
        context.Response.Close();
    }

    /// <summary>读内嵌的 assets/icon.png。读一次缓存；缺失返回 null。</summary>
    private static byte[]? _embeddedIcon;

    private static byte[]? LoadEmbeddedIcon()
    {
        if (_embeddedIcon is not null)
        {
            return _embeddedIcon;
        }

        try
        {
            using var stream = typeof(LauncherServer).Assembly
                .GetManifestResourceStream("AgentFramework.Launcher.assets.icon.png");
            if (stream is null)
            {
                return null;
            }

            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            _embeddedIcon = ms.ToArray();
            return _embeddedIcon;
        }
        catch
        {
            return null;
        }
    }

    private static void TryRedirect(HttpListenerContext context, string location)
    {
        try
        {
            context.Response.StatusCode = 302;
            context.Response.RedirectLocation = location;
            context.Response.Close();
        }
        catch
        {
            // 连接已断开时忽略
        }
    }
}

/// <summary>
/// 固定容量的日志环（F6）：超过容量丢最旧的。
/// 线程安全 —— 宿主输出回调与 debug 窗读取并发。
/// </summary>
public sealed class FixedSizeLog
{
    private readonly object _gate = new();
    private readonly Queue<string> _lines;
    private readonly int _capacity;

    public FixedSizeLog(int capacity)
    {
        _capacity = capacity;
        _lines = new Queue<string>(capacity);
    }

    public void Add(string line)
    {
        lock (_gate)
        {
            _lines.Enqueue(line);
            while (_lines.Count > _capacity)
            {
                _lines.Dequeue();
            }
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
        {
            return [.. _lines];
        }
    }
}
