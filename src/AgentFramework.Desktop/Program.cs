using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using AgentFramework.Contracts;
using AgentFramework.Host;

namespace AgentFramework.Desktop;

/// <summary>
/// 桌面壳入口。
///
/// <b>分工</b>：内核与宿主完全不认识「窗口」—— 界面仍然是那个单文件 HTML。
/// 这个壳只做三件事：起宿主 → 起界面服务 → 把 WebView2 指过去。
///
/// <b>两种运行方式</b>：
///   · 不带参数（原行为）：自己内置起一个宿主（绿色版，数据目录在 exe 旁）；
///   · <c>--url http://localhost:8090/</c>：只当窗口用，连接一个已经跑着的宿主
///     （启动器拉起形态 —— 宿主归启动器管，窗口关了宿主照常跑）。
///
/// <b>为什么用 WebView2 而不是 WPF/XAML 界面</b>：
///   界面要求由编码 AI 生成。HTML/CSS/JS 的生成质量与可验证性远高于 XAML，
///   而且界面与内核解耦 —— 改界面不动内核，内核也不为界面背锅。
///
/// <b>依赖</b>：WebView2 需要系统里的 Edge WebView2 Runtime
///   （Win10/11 一般自带；Win7/8 及精简系统需装 Evergreen Runtime，首次启动会给出提示）。
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        try
        {
            var url = ResolveUrl(args);
            if (url is not null)
            {
                // 启动器拉起形态：只当窗口，宿主归启动器管
                using (var shell = new MainShell(url))
                {
                    Application.Run(shell);
                }

                return;
            }

            Run(ResolveRoot(args));
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"启动失败：\n\n{ex.Message}",
                "Agent",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    /// <summary>解析 --url 参数（启动器拉起形态）。没给就返回 null，退回内置模式。</summary>
    private static string? ResolveUrl(string[] args)
    {
        for (var i = 0; i + 1 < args.Length; i++)
        {
            if (string.Equals(args[i], "--url", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(args[i + 1]))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static void Run(string root)
    {
        // 第一个参数是数据目录 —— 默认为 exe 同级，保持「绿色版」
        var options = new HostOptions
        {
            WorkspaceRoot = Path.Combine(root, "agent-workspace"),
            SessionsDir = Path.Combine(root, "agent-sessions"),
            PluginsDir = Path.Combine(AppContext.BaseDirectory, "plugins"),
            SessionId = "desktop",
        };

        var host = AgentHost.CreateAsync(options).GetAwaiter().GetResult();
        var port = PickFreePort();
        var server = new WebUiServer(host, options, port);
        using var cts = new CancellationTokenSource();

        _ = server.RunAsync(cts.Token);

        using (var shell = new MainShell(server.Url))
        {
            Application.Run(shell);
        }

        cts.Cancel();
        server.Stop();
        host.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>数据根目录：命令行给了就用，否则取 exe 同级（绿色版）。</summary>
    private static string ResolveRoot(string[] args)
    {
        var root = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0])
            ? Path.GetFullPath(args[0])
            : AppContext.BaseDirectory;

        Directory.CreateDirectory(root);
        return root;
    }

    private static int PickFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
