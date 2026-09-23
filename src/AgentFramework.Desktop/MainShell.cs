using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AgentFramework.Desktop;

/// <summary>
/// 主窗口：一个填满窗口的 WebView2，指向本机的界面服务。
///
/// 这里刻意不做任何「界面逻辑」—— 菜单、对话框、布局全在 HTML 里。
/// 窗口只负责提供一个容器、一条状态栏、以及把外链丢给系统浏览器。
/// </summary>
internal sealed class MainShell : Form
{
    private readonly WebView2 _view = new() { Dock = DockStyle.Fill };
    private readonly string _url;

    public MainShell(string url)
    {
        _url = url;

        Text = "Agent";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 600);
        Size = new Size(1320, 860);

        Controls.Add(_view);
        Controls.Add(BuildStatusBar());

        Load += async (_, _) => await AttachViewAsync();
    }

    private StatusStrip BuildStatusBar()
    {
        var strip = new StatusStrip { Dock = DockStyle.Bottom, SizingGrip = false };

        strip.Items.Add(new ToolStripStatusLabel($"服务：{_url}")
        {
            Spring = true,
            TextAlign = ContentAlignment.MiddleLeft,
        });

        strip.Items.Add(new ToolStripStatusLabel("数据目录：" + Application.StartupPath));

        return strip;
    }

    private async Task AttachViewAsync()
    {
        try
        {
            // 用户数据放 exe 同级，保持绿色版
            var userData = Path.Combine(Application.StartupPath, "webview-data");
            Directory.CreateDirectory(userData);

            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
            await _view.EnsureCoreWebView2Async(environment);

            var settings = _view.CoreWebView2.Settings;
            settings.AreDevToolsEnabled = true;      // 调试界面用
            settings.IsStatusBarEnabled = false;     // 状态栏我们自己有
            settings.AreDefaultContextMenusEnabled = true;

            // 外链走系统浏览器 —— 别把这个窗口变成浏览器
            _view.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                OpenExternally(e.Uri);
            };

            _view.Source = new Uri(_url);
        }
        catch (Exception ex)
        {
            // 最常见的原因是系统缺 WebView2 Runtime —— 给出可操作的指引，而不是一句报错
            var hint = ex.Message.Contains("WebView2", StringComparison.OrdinalIgnoreCase)
                ? "\n\n看起来系统缺少 Edge WebView2 运行时。\n"
                  + "请安装「Microsoft Edge WebView2 Evergreen Runtime」后重试。\n"
                  + "（Win10/11 一般自带，精简版系统可能需要手动装）"
                : "";

            MessageBox.Show(
                $"界面初始化失败：\n\n{ex.Message}{hint}",
                "Agent",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private static void OpenExternally(string uri)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch
        {
            // 打不开就算了，不打扰用户
        }
    }
}
