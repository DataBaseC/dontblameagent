namespace AgentFramework.Host;

/// <summary>
/// 对话界面（单文件 HTML，无构建步骤、无前端依赖）。
///
/// 为什么不做成前端工程：这个界面的复杂度撑不起一套构建链，
/// 而单文件 HTML 让「编码 AI 生成 + 人直接改」都最省事 ——
/// 也符合既定方针：UI 用 Web 技术承载，但别把简单事情复杂化。
/// </summary>
internal static class WebUiPage
{
    /// <summary>
    /// 把插件贡献的样式表与脚本挂进主页面。
    ///
    /// <para>
    /// 拼接点在 <c>&lt;/head&gt;</c> 与 <c>&lt;/body&gt;</c> 之前 —— 样式先进头、
    /// 脚本后进尾，于是插件的 CSS 天然压过内置样式（同权重后者胜），
    /// 脚本也天然晚于页面自身的初始化，能安全地读 DOM。
    /// </para>
    ///
    /// <para>
    /// 注入顺序按插件 id 排序（见 <see cref="PluginUi.Collect"/>）：
    /// 顺序稳定才谈得上「谁覆盖谁」可预期 —— 抖动的顺序会让同一套插件每次刷新长不一样。
    /// </para>
    /// </summary>
    public static string WithPluginUi(string html, IReadOnlyList<PluginUiContribution> contributions)
    {
        if (contributions.Count == 0)
        {
            return html;
        }

        var head = new System.Text.StringBuilder();
        var body = new System.Text.StringBuilder();

        foreach (var contribution in contributions)
        {
            var id = Uri.EscapeDataString(contribution.Id);

            foreach (var style in contribution.Styles)
            {
                head.Append("<link rel=\"stylesheet\" href=\"/plugin-ui?id=").Append(id)
                    .Append("&file=").Append(Uri.EscapeDataString(style)).Append("\">\n");
            }

            foreach (var script in contribution.Scripts)
            {
                body.Append("<script src=\"/plugin-ui?id=").Append(id)
                    .Append("&file=").Append(Uri.EscapeDataString(script)).Append("\"></script>\n");
            }
        }

        var withHead = head.Length == 0
            ? html
            : html.Replace("</head>", head.ToString() + "</head>", StringComparison.Ordinal);

        return body.Length == 0
            ? withHead
            : withHead.Replace("</body>", body.ToString() + "</body>", StringComparison.Ordinal);
    }

    public static readonly string Html = WebAssets.Load("index.html");
}
