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

    public const string Html = """
<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Agent 对话</title>
<link rel="icon" href="data:image/svg+xml,<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'><text y='.9em' font-size='88'>🦞</text></svg>">
<style>
  /* ── 设计令牌 ─────────────────────────────────────────────
     一套变量管全局：改主题色/明暗只动这里。颜色梯度按表面高度走。 */
  :root {
    color-scheme: dark;
    --bg0: #0d0f13;  --bg1: #101216;  --bg2: #14171d;  --bg3: #1a1e26;
    --surface: #161a21;  --surface-2: #1c2028;
    --border: #232732;  --border2: #2f3542;
    --text: #e6e8ec;  --dim: #8b93a1;  --faint: #5f6775;
    --accent: #2f6bff;  --accent-2: #7aa2f7;  --accent-dim: #1d3a7a;
    --ok: #7ee2a8;  --warn: #e8c07a;  --bad: #ff8f8f;
    --radius: 12px;  --radius-s: 8px;
    --mono: ui-monospace, "Cascadia Mono", "JetBrains Mono", Consolas, monospace;
    --anim: .18s cubic-bezier(.2, .7, .3, 1);
  }
  * { box-sizing: border-box; }
  html, body { height: 100%; margin: 0; }
  body { background: var(--bg1); color: var(--text); overflow: hidden;
         font-family: -apple-system, "Segoe UI", "Microsoft YaHei", "PingFang SC", "Noto Sans SC", system-ui, sans-serif;
         font-size: 14px; -webkit-font-smoothing: antialiased; }
  ::selection { background: rgba(47, 107, 255, .35); }
  ::-webkit-scrollbar { width: 10px; height: 10px; }
  ::-webkit-scrollbar-thumb { background: #262c37; border-radius: 6px; border: 2px solid var(--bg1); }
  ::-webkit-scrollbar-thumb:hover { background: #323a48; }
  ::-webkit-scrollbar-track { background: transparent; }
  :focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; border-radius: 4px; }

  .app { display: flex; height: 100%; }

  /* ── 侧栏：会话列表 ─────────────────────────────────── */
  aside { width: 236px; flex: 0 0 auto; background: var(--bg2); border-right: 1px solid var(--border);
          display: flex; flex-direction: column; }
  .side-head { padding: 13px 14px; display: flex; align-items: center; justify-content: space-between;
               border-bottom: 1px solid var(--border); font-size: 12px; color: var(--dim);
               letter-spacing: .04em; }
  .side-head button { width: 26px; height: 26px; padding: 0; font-size: 15px; line-height: 1;
                      border-radius: 6px; background: var(--bg3); color: #aab3c2; border: 1px solid var(--border2);
                      transition: border-color var(--anim), color var(--anim), transform var(--anim); }
  .side-head button:hover { border-color: var(--accent); color: #dce3f0; }
  .side-head button:active { transform: scale(.92); }
  #session-list { flex: 1; overflow-y: auto; padding: 8px; }
  .session { padding: 9px 11px; border-radius: var(--radius-s); cursor: pointer; margin-bottom: 4px;
             transition: background var(--anim); animation: fadeIn var(--anim) ease-out; }
  .session:hover { background: var(--bg3); }
  .session.current { background: #1d2430; box-shadow: inset 2px 0 0 var(--accent); }
  .session-name { font-size: 13px; color: #d6dbe4; overflow: hidden; text-overflow: ellipsis;
                  white-space: nowrap; }
  .session-ops { display:flex; gap:8px; margin-top:4px; }
  .session-ops a { font-size:10.5px; color:#565f89; cursor:pointer; text-decoration:none;
                   transition: color var(--anim); }
  .session-ops a:hover { color:var(--accent-2); }
  .session-meta { font-size: 11px; color: var(--faint); margin-top: 3px; }
  /* 后台会话的"进行中"呼吸点 —— 一眼看出谁在干活 */
  .busy-dot { display: inline-block; animation: pulse 1.2s ease-in-out infinite; }

  main { flex: 1; display: flex; flex-direction: column; min-width: 0; }

  /* ── 顶栏：状态 pills ──────────────────────────────── */
  header { padding: 11px 20px; border-bottom: 1px solid var(--border); background: var(--bg2);
           display: flex; align-items: center; gap: 10px; flex-wrap: wrap; font-size: 12px; }
  header b { color: var(--text); font-size: 13px; margin-right: 4px; letter-spacing: .02em; }
  .pill { padding: 3px 10px; border-radius: 99px; border: 1px solid var(--border2); color: var(--dim);
          transition: border-color var(--anim), color var(--anim), background var(--anim); }
  .pill.on { border-color: #2f6b48; color: var(--ok); }
  .pill.warn { border-color: #6b4a1f; color: var(--warn); }
  .pill.conn-off { border-color: #6b3a3a; color: var(--bad); }

  /* ── 消息流 ────────────────────────────────────────── */
  #stream { flex: 1; overflow-y: auto; padding: 22px 20px 8px; scroll-behavior: auto; }
  .wrap { max-width: 860px; margin: 0 auto; display: flex; flex-direction: column; gap: 12px; }
  .row { display: flex; animation: rise var(--anim) ease-out both; }
  .row.user { justify-content: flex-end; }
  .bubble { max-width: 78%; padding: 11px 15px; border-radius: var(--radius); font-size: 14px;
            line-height: 1.65; white-space: pre-wrap; word-break: break-word; }
  .user .bubble { background: var(--accent); color: #fff; border-bottom-right-radius: 4px;
                  box-shadow: 0 1px 6px rgba(47, 107, 255, .25); }
  .assistant .bubble { background: var(--surface); border: 1px solid #2a2f39; border-bottom-left-radius: 4px; }
  /* 流式光标：正在生成的回复末尾一枚呼吸方块 —— "agent 正在写"的直觉信号 */
  .bubble.streaming::after { content: '▍'; margin-left: 2px; color: var(--accent-2);
                             animation: blink 1s steps(2, start) infinite; }

  /* 等首字的思考点 —— 回合已受理、模型还没开口的那一小段 */
  .dots { display: inline-flex; gap: 5px; align-items: center; padding: 14px 16px; }
  .dots span { width: 6px; height: 6px; border-radius: 50%; background: var(--faint);
               animation: bounce 1.2s ease-in-out infinite; }
  .dots span:nth-child(2) { animation-delay: .15s; }
  .dots span:nth-child(3) { animation-delay: .3s; }

  /* ── 工具卡片：agent 工作过程的可见性核心 ─────────── */
  .tool { max-width: 82%; font-size: 12.5px; background: var(--surface); border: 1px solid #2a2f39;
          border-radius: 10px; overflow: hidden; align-self: flex-start;
          animation: rise var(--anim) ease-out both; transition: border-color var(--anim); }
  .tool:hover { border-color: var(--border2); }
  .tool .head { padding: 8px 13px; display: flex; align-items: center; gap: 9px;
                color: #9aa4b4; cursor: pointer; user-select: none; }
  .tool .chev { color: var(--faint); font-size: 10px; transition: transform var(--anim); }
  .tool.open .chev { transform: rotate(90deg); }
  .tool .name { color: var(--text); font-weight: 600; }
  .tool .meta { color: #7c8595; margin-left: auto; font-family: var(--mono); font-size: 11px; }
  .tool .meta.ok { color: var(--ok); }
  .tool .meta.bad { color: var(--bad); }
  .tool .body { display: none; padding: 2px 13px 12px; color: var(--dim); white-space: pre-wrap;
                word-break: break-word; font-family: var(--mono); font-size: 12px; line-height: 1.6;
                border-top: 1px dashed #232b36; margin-top: 6px; padding-top: 10px; }
  .tool.open .body { display: block; animation: fadeIn var(--anim) ease-out; }

  .sys { text-align: center; font-size: 12px; color: var(--faint); animation: fadeIn var(--anim) ease-out; }

  /* ── 审批卡：唯一需要用户立刻注意的元素，允许它亮一点 ── */
  .approval { max-width: 82%; background: #2a2213; border: 1px solid #6b4a1f; border-radius: 10px;
              padding: 12px 15px; align-self: flex-start; animation: rise var(--anim) ease-out both;
              box-shadow: 0 2px 18px rgba(232, 192, 122, .08); }
  .approval .title { color: var(--warn); font-size: 13px; font-weight: 600; margin-bottom: 7px; }
  .approval .detail { color: #b9a980; font-size: 12px; font-family: var(--mono);
                      white-space: pre-wrap; word-break: break-word; margin-bottom: 11px; }
  .approval .acts { display: flex; gap: 8px; align-items: center; }
  .approval button { font-size: 12px; padding: 6px 16px; }
  .approval button.deny { background: #3a2a2a; border: 1px solid #6b3a3a; color: var(--bad); }
  .approval .state { font-size: 12px; color: var(--dim); }
  .approval .remember { font-size: 12px; color: #b9a980; display: flex; align-items: center;
                        gap: 5px; user-select: none; cursor: pointer; }
  .approval.done { opacity: .55; }

  /* 思考块 —— 默认折叠（学 Claude）：想看就点开，不想看它不占视线 */
  .thinking { max-width: 82%; align-self: flex-start; background: #151a22; border: 1px solid #232b38;
              border-radius: 10px; font-size: 12.5px; color: var(--dim); overflow: hidden;
              animation: rise var(--anim) ease-out both; }
  .thinking summary { padding: 8px 13px; cursor: pointer; color: #7f8b9c; user-select: none;
                      list-style: none; transition: color var(--anim); }
  .thinking summary::-webkit-details-marker { display: none; }
  .thinking summary::before { content: '▸ '; color: var(--faint); }
  .thinking[open] summary::before { content: '▾ '; }
  .thinking summary:hover { color: #aeb9c9; }
  .thinking.live summary { color: #9fb4d8; }
  .thinking.live summary::after { content: '…'; animation: blink 1s steps(2, start) infinite; }
  .thinking .thinking-body { padding: 0 13px 11px; white-space: pre-wrap; word-break: break-word;
                             max-height: 260px; overflow-y: auto; line-height: 1.6; }

  /* 每轮用量小字 —— 贴在回复下面，不抢视线 */
  .usage { align-self: flex-start; font-size: 11px; color: var(--faint); padding: 0 4px;
           font-family: var(--mono); animation: fadeIn var(--anim) ease-out; }
  .usage b { color: var(--dim); font-weight: 600; }

  /* 空会话引导：新会话不是一片死黑，而是行动提示 */
  .empty { text-align: center; margin-top: 18vh; color: var(--faint); font-size: 13px;
           line-height: 2.1; white-space: pre-wrap; animation: fadeIn .3s ease-out; }
  .empty b { color: var(--dim); font-size: 15px; display: block; margin-bottom: 6px; }

  /* ── 底部：阶段指示 + 输入区 ───────────────────────── */
  footer { border-top: 1px solid var(--border); background: var(--bg2); padding: 13px 20px 16px; }
  #phase { max-width: 860px; margin: 0 auto 9px; display: flex; align-items: center; gap: 8px;
           font-size: 12px; color: var(--accent-2); font-family: var(--mono);
           animation: fadeIn var(--anim) ease-out; }
  #phase[hidden] { display: none; }
  .spin { width: 11px; height: 11px; flex: 0 0 auto; border-radius: 50%;
          border: 2px solid var(--border2); border-top-color: var(--accent-2);
          animation: spin .8s linear infinite; }
  .composer { max-width: 860px; margin: 0 auto; display: flex; gap: 10px; align-items: flex-end; }
  textarea { flex: 1; resize: none; min-height: 46px; max-height: 160px; padding: 12px 14px;
             border-radius: 10px; border: 1px solid var(--border2); background: var(--bg1); color: var(--text);
             font-family: inherit; font-size: 14px; line-height: 1.5; outline: none;
             transition: border-color var(--anim), box-shadow var(--anim); }
  textarea:focus { border-color: var(--accent); box-shadow: 0 0 0 3px rgba(47, 107, 255, .15); }
  button { padding: 12px 22px; border-radius: 10px; border: 0; background: var(--accent); color: #fff;
           font-size: 14px; font-weight: 600; cursor: pointer;
           transition: filter var(--anim), transform var(--anim), background var(--anim), border-color var(--anim); }
  button:hover:not(:disabled) { filter: brightness(1.12); }
  button:active:not(:disabled) { transform: translateY(1px); }
  button:disabled { opacity: .45; cursor: default; }
  button.ghost { background: var(--bg3); border: 1px solid var(--border2); color: #aab3c2; font-weight: 500; }
  button.ghost:hover:not(:disabled) { border-color: var(--accent); color: #dce3f0; filter: none; }
  /* HCI：回合进行中，发送键变停止键（同一个位置，不用找第二个按钮） */
  button.stop { background: #3a1e1e; border: 1px solid #6b3a3a; color: var(--bad); }
  button.stop:hover { border-color: #ff6b6b; filter: none; }
  button.stop:not(:disabled) { animation: stopPulse 2s ease-in-out infinite; }
  /* HCI：助手气泡的复制按钮 —— 悬停浮现，不打扰阅读 */
  .assistant { position: relative; }
  .assistant .copy-btn { position: absolute; right: 6px; top: -14px; padding: 2px 10px; font-size: 11px;
                         border-radius: 6px; background: var(--bg3); border: 1px solid var(--border2);
                         color: var(--dim); opacity: 0; transition: opacity var(--anim), color var(--anim), border-color var(--anim);
                         cursor: pointer; font-weight: 500; }
  .row.assistant:hover .copy-btn { opacity: 1; }
  .assistant .copy-btn:hover { color: #dce3f0; border-color: var(--accent); }
  /* HCI：模式三段选择器（替代三态循环 —— 直接点目标，不用背顺序） */
  .mode-group { display: inline-flex; gap: 0; border-radius: 99px; border: 1px solid var(--border2); overflow: hidden; }
  .mode-btn { padding: 3px 12px; font-size: 12px; color: var(--dim); cursor: pointer; user-select: none;
              border-right: 1px solid var(--border2); transition: color var(--anim), background var(--anim); }
  .mode-btn:last-child { border-right: 0; }
  .mode-btn.on { background: #16281e; color: var(--ok); cursor: default; font-weight: 600; }
  .mode-btn:not(.on):hover { color: #dce3f0; background: var(--bg3); }
  /* HCI：上下文水位 —— 预算快满时提前知道（压缩会打断缓存，也是该换会话的信号） */
  #pill-ctx { min-width: 86px; text-align: center; }
  #pill-ctx .bar { display: block; height: 3px; border-radius: 2px; background: var(--border); margin-top: 3px; overflow: hidden; }
  #pill-ctx .bar i { display: block; height: 100%; background: var(--accent); width: 0; transition: width .3s; }
  #pill-ctx.hot { border-color: #6b4a1f; color: var(--warn); }
  #pill-ctx.hot .bar i { background: var(--warn); }

  /* 转述设置面板 —— 平时收起，点头部的「转述」pill 展开 */
  .settings { max-width: 860px; margin: 10px auto 0; border: 1px solid var(--border); border-radius: 10px;
              padding: 12px 14px; background: #12161c; animation: fadeIn var(--anim) ease-out; }
  .settings .row { display: flex; align-items: center; gap: 14px; flex-wrap: wrap;
                   font-size: 12px; color: var(--dim); margin-bottom: 9px; }
  .settings label { display: flex; align-items: center; gap: 6px; cursor: pointer; }
  .settings select { background: var(--bg1); color: var(--text); border: 1px solid var(--border2);
                     border-radius: 6px; padding: 4px 8px; font-size: 12px; }
  .settings textarea { width: 100%; min-height: 108px; font-size: 12px; line-height: 1.5;
                       font-family: var(--mono); }
  .settings button.small { padding: 6px 13px; font-size: 12px; }
  .settings .state { color: var(--ok); }
  .pill.clickable { cursor: pointer; }
  .pill.clickable:hover { border-color: var(--accent); color: #dce3f0; }
  .hint a { color: var(--accent-2); }
  .hint { max-width: 860px; margin: 8px auto 0; font-size: 11.5px; color: var(--faint); min-height: 14px;
          transition: color var(--anim); }
.md-row { display: flex; align-items: center; gap: 8px; padding: 3px 0; }
.md-row .md-title { flex: 1; font-size: 12px; color: #bdbdc9; }
#md-list { max-height: 132px; overflow: auto; margin: 6px 0; }
#md-editor { border-top: 1px solid #33333f; padding-top: 6px; }
#md-editor input { background: #1c1c24; border: 1px solid #34343f; color: #e6e6ee; border-radius: 5px; padding: 2px 6px; }
.md-provider { color: #7a7a8a; font-size: 11px; }

  /* ── 技能与创意工坊面板 ───────────────────────────── */
  .skill-row { display: flex; align-items: center; gap: 10px; padding: 8px 2px;
               border-bottom: 1px dashed var(--border); font-size: 12.5px; }
  .skill-row:last-child { border-bottom: 0; }
  .skill-name { color: var(--text); font-weight: 600; white-space: nowrap; }
  .skill-desc { color: var(--dim); flex: 1; min-width: 0; overflow: hidden;
                text-overflow: ellipsis; white-space: nowrap; }
  .skill-tools { color: var(--faint); font-family: var(--mono); font-size: 11px; white-space: nowrap; }
  .skill-src { font-size: 10.5px; padding: 1px 8px; border-radius: 5px; border: 1px solid var(--border2);
               color: var(--dim); white-space: nowrap; }
  .skill-src.workshop { color: var(--accent-2); border-color: var(--accent-dim); }
  .skill-src.on { color: var(--ok); border-color: #2f6b48; }
  .skill-row button { padding: 3px 12px; font-size: 11.5px; border-radius: 6px; }
  #ws-path { flex: 1; background: var(--bg1); border: 1px solid var(--border2); color: var(--text);
             border-radius: 6px; padding: 6px 10px; font-size: 12px; font-family: var(--mono); outline: none; }
  #ws-path:focus { border-color: var(--accent); }

  /* ── 插件列表 + 独立面板 + 新会话模式选择 ────────── */
  .plugin-row { display: flex; align-items: center; gap: 10px; padding: 8px 2px;
                border-bottom: 1px dashed var(--border); font-size: 12.5px; }
  .plugin-row:last-child { border-bottom: 0; }
  .plugin-id { color: var(--text); font-weight: 600; font-family: var(--mono); }
  .plugin-meta { color: var(--faint); font-size: 11px; }
  .plugin-row .spacer { flex: 1; }
  .plugin-row button { padding: 3px 12px; font-size: 11.5px; border-radius: 6px; }
  .modal-mask { position: fixed; inset: 0; background: rgba(6, 8, 11, .72); display: flex;
                align-items: center; justify-content: center; z-index: 50; backdrop-filter: blur(3px); }
  .modal-mask[hidden] { display: none; }
  .modal { width: min(560px, 92vw); background: var(--bg2); border: 1px solid var(--border2);
           border-radius: 14px; padding: 20px 22px; box-shadow: 0 18px 60px rgba(0, 0, 0, .5);
           animation: rise var(--anim) ease-out both; }
  .modal-title { font-size: 15px; font-weight: 700; color: var(--text); margin-bottom: 4px; }
  .modal-sub { font-size: 12px; color: var(--faint); margin-bottom: 14px; }
  .mode-card { padding: 12px 14px; border: 1px solid var(--border2); border-radius: 10px;
               cursor: pointer; margin-bottom: 9px; transition: border-color var(--anim), background var(--anim); }
  .mode-card:hover { border-color: var(--accent); background: var(--bg3); }
  .mode-card .mc-name { font-size: 13.5px; font-weight: 700; color: var(--text); }
  .mode-card .mc-badge { font-size: 10.5px; padding: 1px 8px; border-radius: 5px; margin-left: 8px;
                         border: 1px solid var(--accent-dim); color: var(--accent-2); }
  .mode-card .mc-desc { font-size: 12px; color: var(--dim); margin-top: 4px; line-height: 1.6; }
  .modal-acts { display: flex; justify-content: flex-end; margin-top: 6px; }
  /* ── 记忆管理面板 ─────────────────────────────── */
  .mem-row { display: flex; align-items: center; gap: 9px; padding: 7px 2px;
             border-bottom: 1px dashed var(--border); font-size: 12.5px; }
  .mem-row:last-child { border-bottom: 0; }
  .mem-row input[type="checkbox"] { flex: 0 0 auto; }
  .mem-text { flex: 1; min-width: 0; color: #d6dbe4; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .mem-score { font-family: var(--mono); font-size: 11px; color: var(--warn); white-space: nowrap; }
  .mem-scope { font-size: 10.5px; padding: 1px 7px; border-radius: 5px; border: 1px solid var(--border2); color: var(--dim); }
  .mem-scope.project { color: var(--accent-2); border-color: var(--accent-dim); }
  .mem-archived .mem-text { color: var(--faint); text-decoration: line-through 1px rgba(95, 103, 117, .5); }
  .mem-row button { padding: 2px 9px; font-size: 11px; border-radius: 5px; }
  .mem-section-title { font-size: 11px; color: var(--faint); letter-spacing: .06em; margin: 10px 0 4px; }

  /* ── 目录浏览（「新建会话」挑项目目录）───────────── */
  .dir-list { max-height: 320px; overflow-y: auto; border: 1px solid var(--border2);
              border-radius: 10px; padding: 4px; margin-bottom: 10px; background: var(--bg1); }
  .dir-row { display: flex; align-items: center; gap: 9px; padding: 7px 10px; border-radius: 7px;
             cursor: pointer; font-size: 12.5px; color: var(--text); }
  .dir-row:hover { background: var(--bg3); }
  .dir-row .ico { flex: 0 0 auto; }
  .dir-row .nm { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .dir-row.hidden-dir .nm { color: var(--faint); }
  .dir-empty { padding: 12px 10px; color: var(--faint); font-size: 12px; }
  .dir-cur { font-family: var(--mono); font-size: 11.5px; color: var(--accent-2);
             word-break: break-all; margin-bottom: 8px; }

  /* ── 动画关键帧 ─────────────────────────────────────── */
  @keyframes rise { from { opacity: 0; transform: translateY(5px); } to { opacity: 1; transform: none; } }
  @keyframes fadeIn { from { opacity: 0; } to { opacity: 1; } }
  @keyframes blink { 50% { opacity: 0; } }
  @keyframes pulse { 0%, 100% { opacity: 1; } 50% { opacity: .25; } }
  @keyframes spin { to { transform: rotate(360deg); } }
  @keyframes bounce { 0%, 60%, 100% { transform: none; } 30% { transform: translateY(-5px); } }
  @keyframes stopPulse { 0%, 100% { box-shadow: 0 0 0 0 rgba(255, 107, 107, .25); }
                         50% { box-shadow: 0 0 0 5px rgba(255, 107, 107, 0); } }
  /* 尊重系统"减少动态效果"设置 —— 动画是调味，不是门槛 */
  @media (prefers-reduced-motion: reduce) {
    *, *::before, *::after { animation: none !important; transition: none !important; }
  }
</style>
</head>
<body>
<div class="app">
  <aside>
    <div class="side-head">
      <span>会话</span>
      <button id="new-session" title="新建会话">＋</button>
    </div>
    <div id="session-list"></div>
  </aside>

  <main>
    <header>
      <b>Agent 对话</b>
      <span id="pill-conn" class="pill" title="与服务的实时连接状态">…</span>
      <span id="pill-model" class="pill clickable" title="点击打开 / 收起模型设置">模型</span>
      <span id="pill-tools" class="pill"></span>
      <span id="pill-plugins" class="pill clickable" title="插件：已装内容与独立面板入口">插件</span>
      <span id="pill-skills" class="pill clickable" title="技能与创意工坊：启用工具白名单包 / 导入分享的技能">技能</span>
      <span id="pill-toolsets" class="pill clickable" title="工具包：按这段活的需要开合 —— 装得多不等于负担重，收起来才省">包</span>
      <span id="pill-memory" class="pill clickable" title="记忆管理：热度 / 置顶 / 归档 / 合并 / 清扫降级">记忆</span>
      <span id="pill-ctx" class="pill" title="上下文水位：超 80% 会触发压缩（打断前缀缓存）">ctx</span>
      <span id="pill-rephrase" class="pill clickable" title="点击打开 / 收起转述设置">转述</span>
      <span id="pill-usage" class="pill" title="本次会话累计用量">用量</span>
      <span id="pill-session" class="pill"></span>
    </header>
    <div id="stream"><div class="wrap" id="wrap"></div></div>
    <footer>
      <!-- 阶段指示：agent 干活的每一步（思考 / 调用工具 / 生成）都在这行可见 -->
      <div id="phase" hidden><span class="spin"></span><span id="phase-text"></span></div>
      <div class="composer">
        <textarea id="input" rows="1" placeholder="说点什么…（Enter 发送 / Shift+Enter 换行）"></textarea>
        <button id="optimize" class="ghost"
                title="让转述模型把这段话改得更清楚，再交给主模型（结果回填输入框，可撤销）">✨ 优化</button>
        <button id="send">发送</button>
      </div>

      <div class="settings" id="settings" hidden>
        <div class="row">
          <label><input type="checkbox" id="rp-enabled"> 启用转述</label>
          <label><input type="checkbox" id="rp-auto"> 发送前自动澄清</label>
          <label>转述模型
            <select id="rp-model"></select>
          </label>
          <button id="rp-save" class="ghost small">保存</button>
          <button id="rp-reset" class="ghost small">恢复默认提示词</button>
          <span id="rp-state" class="state"></span>
        </div>
        <textarea id="rp-prompt" spellcheck="false" placeholder="转述模型使用的系统提示词"></textarea>
      </div>

      <div class="settings" id="model-panel" hidden>
        <div class="row">
          <b>当前模型</b>
          <select id="md-active"></select>
          <button id="md-refresh" class="ghost small">刷新</button>
          <button id="md-new" class="ghost small">＋ 端点</button>
          <span id="md-state" class="state"></span>
        </div>

        <div id="md-list"></div>

        <div id="md-editor" hidden>
          <div class="row">
            <label>id <input id="md-id" size="12" placeholder="deepseek"></label>
            <label>名称 <input id="md-name" size="12" placeholder="DeepSeek"></label>
            <label>地址 <input id="md-url" size="30" placeholder="https://api.deepseek.com/v1"></label>
            <label>密钥 <input id="md-key" type="password" size="18" placeholder="留空 = 不修改"></label>
          </div>
          <div class="row">
            <label>思考风格
              <select id="md-style">
                <option value="none">不注入（none）</option>
                <option value="openai">OpenAI（reasoning_effort）</option>
                <option value="qwen">Qwen（enable_thinking）</option>
              </select>
            </label>
            <label>默认思考强度
              <select id="md-effort">
                <option value="">端点默认</option>
                <option value="off">off（关闭）</option>
                <option value="low">low（低）</option>
                <option value="medium">medium（中）</option>
                <option value="high">high（高）</option>
              </select>
            </label>
          </div>
          <div class="row">
            <button id="md-test" class="ghost small">测试连接</button>
            <button id="md-save" class="ghost small">保存</button>
            <button id="md-cancel" class="ghost small">取消</button>
            <span id="md-editor-state" class="state"></span>
          </div>
          <textarea id="md-models" rows="3" spellcheck="false"
                    placeholder="模型列表，一行一个（点「测试连接」会自动填充）"></textarea>
        </div>
      </div>

      <!-- 技能与创意工坊：白名单型技能 = 工具子集 + 提示词，纯声明包、可安全导入分享 -->
      <div class="settings" id="skills-panel" hidden>
        <div class="row">
          <b>技能 · 创意工坊</b>
          <span style="color:var(--faint)">启用后改变本轮可见工具与提示词；纯声明包，导入不引入代码</span>
        </div>
        <div id="skills-list"></div>
        <div class="row" style="margin-top:10px; margin-bottom:0">
          <input id="ws-path" placeholder="粘贴技能目录路径（含 skill.json）导入到工坊…">
          <button id="ws-import" class="ghost small">导入工坊</button>
          <span id="ws-state" class="state"></span>
        </div>
      </div>

      <!-- 工具包：可见性的最小单位 —— 「装了什么」与「这一轮给模型看什么」分开 -->
      <div class="settings" id="toolsets-panel" hidden>
        <div class="row">
          <b>工具包</b>
          <span style="color:var(--faint)">用不上的收起来：省每轮的 schema token，也少让模型在几十个工具里挑错。core 与 工具包管理 不可关</span>
        </div>
        <div id="toolsets-list"></div>
      </div>

      <!-- 插件面板：dsh 式 —— 插件自带一小块 UI，从这里点入 -->
      <div class="settings" id="plugins-panel" hidden>
        <div class="row">
          <b>插件</b>
          <span style="color:var(--faint)">工作模式、宠物、界面美化…都是插件；有独立面板的可直接点入</span>
        </div>
        <div id="plugins-list"></div>
        <div id="plugin-frame-wrap" hidden>
          <div class="row" style="justify-content: space-between">
            <b id="plugin-frame-title"></b>
            <button id="plugin-frame-close" class="ghost small">关闭面板</button>
          </div>
          <iframe id="plugin-frame" sandbox="allow-scripts" style="width:100%; height:420px; border:1px solid var(--border); border-radius:8px; background:var(--bg1)"></iframe>
        </div>
      </div>

      <!-- 记忆管理：三层（索引卡 ← 主视图 ← 归档层），降级/升级/合并全部事件化可回滚 -->
      <div class="settings" id="memory-panel" hidden>
        <div class="row">
          <b>记忆管理</b>
          <span style="color:var(--faint)">热度来自被引用（recall 命中 +1）；置顶常驻索引卡；归档移出主检索，原文永不删</span>
        </div>
        <div class="row" style="margin-bottom:6px">
          <button id="mem-sweep-preview" class="ghost small">清扫预览</button>
          <button id="mem-sweep-run" class="ghost small">执行降级（30 天未用且热度≤1）</button>
          <span id="mem-sweep-state" class="state"></span>
        </div>
        <div id="memory-list"></div>
        <div class="row" style="margin-top:10px; margin-bottom:0" id="mem-merge-bar" hidden>
          <span style="color:var(--warn)">已选 <b id="mem-merge-count">0</b> 条</span>
          <input id="mem-merge-text" placeholder="合并后的正文（写清楚这组记忆共同的结论）…">
          <button id="mem-merge-go" class="ghost small">合并</button>
          <span id="mem-merge-state" class="state"></span>
        </div>
      </div>

      <!-- 新建会话：选择工作模式（创建后钉住） -->
      <div id="mode-pick" class="modal-mask" hidden>
        <div class="modal">
          <div class="modal-title">新会话 · 选择工作模式</div>
          <div class="modal-sub">模式决定暴露哪些工具与记忆层级 —— 创建后固定，换模式就再开一个新会话</div>
          <div id="mode-cards"></div>
          <div class="row" style="margin-top:6px; margin-bottom:0">
            <span style="font-size:12px; color:var(--dim); flex:0 0 auto">项目目录</span>
            <input id="np-project-dir" placeholder="留空 = 宿主默认工作区；点右侧「浏览…」挑一个文件夹">
            <button id="np-browse" class="ghost small" type="button">浏览…</button>
          </div>
          <div class="modal-sub" style="margin:6px 0 0">
            这个目录只圈「写」：会话的文件写入与项目记忆锚定在这里；「读」不受限，可读硬盘任意目录。
          </div>
          <div class="modal-acts"><button id="mode-cancel" class="ghost small">取消</button></div>
        </div>
      </div>

      <!-- 目录浏览：挑项目目录（可进任意位置、可新建文件夹） -->
      <div id="dir-pick" class="modal-mask" hidden>
        <div class="modal">
          <div class="modal-title">选择项目目录</div>
          <div class="modal-sub" id="dp-current">…</div>
          <div class="dir-cur" id="dp-path"></div>
          <div class="row" style="margin-bottom:8px">
            <button id="dp-up" class="ghost small" type="button">↑ 上一级</button>
            <button id="dp-home" class="ghost small" type="button">🏠 主目录</button>
            <button id="dp-new" class="ghost small" type="button">＋ 新建文件夹</button>
            <span id="dp-state" class="state"></span>
          </div>
          <div id="dp-list" class="dir-list"></div>
          <div class="modal-acts">
            <button id="dp-cancel" class="ghost small" type="button">取消</button>
            <button id="dp-choose" class="ghost small" type="button">就用这个目录</button>
          </div>
        </div>
      </div>

      <div class="hint" id="hint"></div>
    </footer>
  </main>
</div>

<script>
const wrap = document.getElementById('wrap');
const input = document.getElementById('input');
const sendBtn = document.getElementById('send');
const hint = document.getElementById('hint');
const scroller = document.getElementById('stream');
const sessionList = document.getElementById('session-list');
const optimizeBtn = document.getElementById('optimize');
const settingsPanel = document.getElementById('settings');
const rpEnabled = document.getElementById('rp-enabled');
const rpAuto = document.getElementById('rp-auto');
const rpModel = document.getElementById('rp-model');
const rpPrompt = document.getElementById('rp-prompt');
const rpSave = document.getElementById('rp-save');
const rpReset = document.getElementById('rp-reset');
const rpState = document.getElementById('rp-state');
const rephrasePill = document.getElementById('pill-rephrase');
const skillsPanel = document.getElementById('skills-panel');
const modelPill = document.getElementById('pill-model');
const modelPanel = document.getElementById('model-panel');
const mdState = document.getElementById('md-state');
const mdActive = document.getElementById('md-active');
const mdStyle = document.getElementById('md-style');
const mdEffort = document.getElementById('md-effort');
const mdList = document.getElementById('md-list');
const mdEditor = document.getElementById('md-editor');
const mdEditorState = document.getElementById('md-editor-state');
const mdId = document.getElementById('md-id');
const mdName = document.getElementById('md-name');
const mdUrl = document.getElementById('md-url');
const mdKey = document.getElementById('md-key');
const mdModels = document.getElementById('md-models');

let modelInfo = null;
let editingProviderId = null;

// 端点 id / 模型 id 都可能是任意字符串，拼成一个 option value 要能原样拆回来
function mdKey2(providerId, modelId) {
  return encodeURIComponent(providerId) + '|' + encodeURIComponent(modelId);
}

function mdSplit(value) {
  const at = value.indexOf('|');
  return at < 0
    ? ['', '']
    : [decodeURIComponent(value.slice(0, at)), decodeURIComponent(value.slice(at + 1))];
}

let currentAssistant = null;
let lastOriginal = '';
let rephraseInfo = { available: false, enabled: false, autoBeforeSend: false };
const toolCards = new Map();
let sessionMode = 'work';
let sessionModeName = '工作模式';

// B2：当前会话 id（loadStatus 时同步）+ 后台有回合在跑的会话集合。
// 分流规则见 stream.onmessage：非当前会话的增量帧不渲染，只标记“进行中”。
let currentSessionId = null;
let lastSeq = 0;   // F3：最后一条事件的序号（分叉按钮的默认分叉点）
const busySessions = new Set();

// ③ 竞态防线：已经发出过「回合结束」信号的 turnId（只留最近 32 个）。
// 回合跑得极快时（例如工具一失败就收尾），turn-ended 会**先于** /api/send 的 202 响应到达；
// 前端若在 202 回来之后无条件把按钮置成「停止」，回合明明已经结束、按钮却再也回不来，
// 非得手动点一下。所以先记下已结束的回合，202 回来时若发现它已经结束，就不再覆盖终点状态。
const endedTurns = [];

function markSessionBusy(sessionId) {
  if (busySessions.has(sessionId)) return;
  busySessions.add(sessionId);
  const item = document.querySelector('#session-list [data-id="' + CSS.escape(sessionId) + '"]');
  if (item && !item.querySelector('.busy-dot')) {
    const dot = document.createElement('span');
    dot.className = 'busy-dot';
    dot.textContent = '●';
    dot.style.color = '#6ea8fe';
    dot.title = '这个会话有回合正在运行';
    item.appendChild(dot);
  }
}

function clearSessionBusy(sessionId) {
  if (!busySessions.delete(sessionId)) return;
  const item = document.querySelector('#session-list [data-id="' + CSS.escape(sessionId) + '"]');
  if (item) {
    const dot = item.querySelector('.busy-dot');
    if (dot) dot.remove();
  }
}

function toBottom() { scroller.scrollTop = scroller.scrollHeight; }

// ── 阶段指示 + 思考点：让"agent 正在干活"每一步都被看见 ──
const phaseEl = document.getElementById('phase');
const phaseText = document.getElementById('phase-text');
let dotsEl = null;

function setPhase(text) { phaseEl.hidden = false; phaseText.textContent = text; }
function hidePhase() { phaseEl.hidden = true; }

function showDots() {
  if (dotsEl && dotsEl.isConnected) return;
  dotsEl = document.createElement('div');
  dotsEl.className = 'row assistant';
  const bubble = document.createElement('div');
  bubble.className = 'bubble dots';
  bubble.innerHTML = '<span></span><span></span><span></span>';
  dotsEl.appendChild(bubble);
  wrap.appendChild(dotsEl);
  toBottom();
}

function hideDots() {
  if (dotsEl && dotsEl.isConnected) dotsEl.remove();
  dotsEl = null;
}

// 空会话引导：新会话不是一片死黑，而是行动提示（打字前先知道快捷键与预期）
function maybeEmptyState() {
  if (wrap.children.length > 0) return;
  const el = document.createElement('div');
  el.className = 'empty';
  el.textContent = '🦞 开始你的第一轮对话\nEnter 发送 · Shift+Enter 换行 · Esc 停止回合\n长任务会在这里逐步展示每次工具调用与用量';
  wrap.appendChild(el);
}

function clearMessages() {
  wrap.innerHTML = '';
  currentAssistant = null;
  toolCards.clear();
  dotsEl = null;
}

function addBubble(role, text) {
  const emptyHint = wrap.querySelector('.empty');
  if (emptyHint) emptyHint.remove();

  const row = document.createElement('div');
  row.className = 'row ' + role;
  const bubble = document.createElement('div');
  bubble.className = 'bubble';
  bubble.textContent = text || '';
  row.appendChild(bubble);

  // HCI：助手回复一键复制 —— 流式结束后点一下就能拿走全文
  if (role === 'assistant') {
    const copy = document.createElement('button');
    copy.className = 'copy-btn';
    copy.textContent = '复制';
    copy.title = '复制本条回复全文';
    copy.onclick = async (ev) => {
      ev.stopPropagation();
      try {
        await navigator.clipboard.writeText(bubble.innerText);
        copy.textContent = '已复制 ✓';
      } catch {
        // 剪贴板 API 被拒（非安全上下文等）：退回选区复制
        const range = document.createRange();
        range.selectNodeContents(bubble);
        const sel = getSelection();
        sel.removeAllRanges();
        sel.addRange(range);
        copy.textContent = '按 Ctrl+C';
      }
      setTimeout(() => { copy.textContent = '复制'; }, 1400);
    };
    row.appendChild(copy);
  }

  wrap.appendChild(row);
  toBottom();
  return bubble;
}

function addToolCard(evt) {
  const card = document.createElement('div');
  card.className = 'tool';
  card.dataset.tool = evt.toolName;
  const head = document.createElement('div');
  head.className = 'head';
  const chev = document.createElement('span');
  chev.className = 'chev';
  chev.textContent = '▸';
  const name = document.createElement('span');
  name.className = 'name';
  name.textContent = '🔧 ' + evt.toolName;
  const meta = document.createElement('span');
  meta.className = 'meta';
  meta.textContent = '执行中…';
  head.appendChild(chev);
  head.appendChild(name);
  head.appendChild(meta);
  const body = document.createElement('div');
  body.className = 'body';
  body.textContent = '参数：' + JSON.stringify(evt.arguments || {});
  head.onclick = () => card.classList.toggle('open');
  card.appendChild(head);
  card.appendChild(body);
  wrap.appendChild(card);
  setPhase('调用 ' + evt.toolName + ' …');
  toBottom();
  return card;
}

function addApprovalCard(a) {
  const card = document.createElement('div');
  card.className = 'approval';

  const title = document.createElement('div');
  title.className = 'title';
  title.textContent = '⚠ 需要你确认：' + a.toolName;

  const detail = document.createElement('div');
  detail.className = 'detail';
  detail.textContent = JSON.stringify(a.arguments || {}, null, 2);

  const acts = document.createElement('div');
  acts.className = 'acts';
  const allow = document.createElement('button');
  allow.textContent = '允许';
  const deny = document.createElement('button');
  deny.className = 'deny';
  deny.textContent = '拒绝';

  // ★ P6「本会话记住」：勾上之后，这个工具在本会话里不再弹卡。
  //   只作用于**本会话**、重启即失效 —— 不做「永久允许」那种危险承诺。
  const rememberBox = document.createElement('label');
  rememberBox.className = 'remember';
  const remember = document.createElement('input');
  remember.type = 'checkbox';
  rememberBox.appendChild(remember);
  rememberBox.appendChild(document.createTextNode('本会话允许此工具'));

  const state = document.createElement('span');
  state.className = 'state';

  async function decide(ok) {
    allow.disabled = true;
    deny.disabled = true;
    remember.disabled = true;
    state.textContent = '已' + (ok ? '允许' : '拒绝') + '，处理中…';
    try {
      await fetch('/api/approve', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ id: a.id, allow: ok, remember: ok && remember.checked })
      });
    } catch (err) {
      state.textContent = '提交失败：' + err.message;
    }
    card.classList.add('done');
  }

  allow.onclick = () => decide(true);
  deny.onclick = () => decide(false);

  acts.appendChild(allow);
  acts.appendChild(deny);
  if (a.canRemember) acts.appendChild(rememberBox);
  acts.appendChild(state);
  card.appendChild(title);
  card.appendChild(detail);
  card.appendChild(acts);
  wrap.appendChild(card);
  toBottom();
}

function renderEvent(e) {
  if (!e || !e.type) return;
  switch (e.type) {
    case 'approval':
      // 等用户批的时候「正在思考…」是撒谎 —— 先收口；批准后模型再想会另开新块。
      sealReasoning();
      addApprovalCard(e);
      break;
    case 'user-message':
      addBubble('user', e.text);
      break;
    case 'assistant-message':
      // B2：assistant 落地 = 本轮结束（该帧已通过 onmessage 的归属分流），
      // 清掉这个会话的“进行中”标记。事件自带 SessionId，不依赖外层帧变量。
      // 思考块也在这里收口：若模型直接给正文（没有 delta 流），
      // 不收的话它会永远停在「正在思考…」。历史回放同样走这里 —— 历史里的
      // 思考块不该是 live 态。
      sealReasoning();
      clearSessionBusy(e.sessionId || '');
      hideDots();
      if (currentAssistant) {
        currentAssistant.textContent = e.text;
        currentAssistant.classList.remove('streaming');
        currentAssistant = null;
      } else {
        addBubble('assistant', e.text);
      }
      break;
    case 'tool-call-requested':
      // 思考段结束的信号：模型已经想完，开始动手了。
      sealReasoning();
      hideDots();
      toolCards.set(e.callId, addToolCard(e));
      break;
    case 'tool-call-completed': {
      const card = toolCards.get(e.callId);
      if (card) {
        const meta = card.querySelector('.meta');
        meta.textContent = e.success ? '✓ 完成' : '✗ 失败';
        meta.className = 'meta ' + (e.success ? 'ok' : 'bad');
        card.querySelector('.body').textContent +=
          '\n\n结果：' + (e.success ? (e.output || '(空)') : (e.error || '失败'));
        card.classList.add('open');
        setPhase((card.dataset.tool || '工具') + (e.success ? ' ✓ 完成' : ' ✗ 失败'));
      }
      // B2：非当前会话的轮次到不了这里（onmessage 已分流），
      // 当前会话这里也不需要再做什么 —— 忙标记统一在 assistant-message 清。
      break;
    }
    case 'session-created':
      addBubble('sys', '会话已创建：' + (e.title || e.sessionId));
      break;
    case 'model-usage':
      // 「这一轮花了多少」是事后信息 —— 贴在回复下面，不占状态栏
      sealReasoning();
      addUsageLine(e);
      loadStatus();   // 状态栏跟着刷新（一轮一次，不算频繁）
      break;
    case 'reasoning':
      // 历史回放时的思考内容：直接落成折叠块
      if (!currentReasoning || !currentReasoning.el.isConnected) {
        currentReasoning = addThinkingBlock();
      }
      currentReasoning.body.textContent = e.text || '';
      break;
  }
}

// ── 可观测：思考过程 / 每轮用量 ───────────────────────────
// 思考与正文是两条通道：思考进折叠块，正文直出。
// 默认折叠是学 Claude —— 想看的人点开，不想看的人不被刷屏。

let currentReasoning = null;

function fmtNum(value) {
  if (value == null) return '?';
  if (value < 1000) return String(value);
  if (value < 1000000) return (value / 1000).toFixed(1) + 'k';
  return (value / 1000000).toFixed(2) + 'M';
}

function addThinkingBlock() {
  const el = document.createElement('details');
  el.className = 'thinking live';
  const summary = document.createElement('summary');
  summary.textContent = '正在思考…';
  const body = document.createElement('div');
  body.className = 'thinking-body';
  el.appendChild(summary);
  el.appendChild(body);
  wrap.appendChild(el);
  toBottom();
  return { el, summary, body };
}

function onReasoning(text) {
  // 自愈：清空对话后旧引用会指向已移除的节点
  if (!currentReasoning || !currentReasoning.el.isConnected) {
    currentReasoning = addThinkingBlock();
  }
  currentReasoning.body.textContent += text;
  setPhase('思考中…');
  toBottom();
}

// 正文一到就把思考块收口 —— 它已经「想完了」
function sealReasoning() {
  if (!currentReasoning) return;
  if (currentReasoning.el.isConnected) {
    currentReasoning.el.classList.remove('live');
    const chars = currentReasoning.body.textContent.length;
    currentReasoning.summary.textContent = '思考过程（' + chars + ' 字，点击展开）';
  }
  currentReasoning = null;
}

// 每轮用量：贴在回复下面。缺哪个就不显示哪个 —— 绝不拿 0 冒充「不知道」。
function addUsageLine(e) {
  const parts = [];
  if (e.inputTokens != null) parts.push('<b>in</b> ' + fmtNum(e.inputTokens));
  if (e.outputTokens != null) parts.push('<b>out</b> ' + fmtNum(e.outputTokens));
  if (e.cachedTokens != null) parts.push('<b>cache</b> ' + fmtNum(e.cachedTokens));
  if (e.reasoningTokens != null) parts.push('<b>think</b> ' + fmtNum(e.reasoningTokens));
  if (e.elapsedMs) parts.push((e.elapsedMs / 1000).toFixed(1) + 's');
  if (e.firstTokenMs != null) parts.push('首字 ' + e.firstTokenMs + 'ms');
  if (!parts.length) parts.push('用量未知（端点未回报）');

  const el = document.createElement('div');
  el.className = 'usage';
  el.innerHTML = parts.join(' · ');
  if (e.model) el.title = '服务端点：' + e.model;
  wrap.appendChild(el);
  toBottom();
}

function onDelta(text) {
  sealReasoning();
  hideDots();
  if (!currentAssistant) {
    currentAssistant = addBubble('assistant', '');
  }
  currentAssistant.textContent += text;
  currentAssistant.classList.add('streaming');
  setPhase('生成回复…');
  toBottom();
}

function autoGrow() {
  input.style.height = 'auto';
  input.style.height = Math.min(input.scrollHeight, 160) + 'px';
}

// ── 工作模式：属会话，创建时选定（HCI）──────────────────
// 模式不再是宿主全局开关 —— 会话在创建时钉住模式，列表里看徽标，
// 想换模式就开新会话。选择器在「＋ 新建会话」的弹层里（见 newSession / showModePick）。

// ── 输入转述（澄清模式）───────────────────────────────────
// 手动为主：点「✨ 优化」→ 结果回填输入框，可撤销。
// 自动为可选：设置里打开后，发送前先澄清一次（这一步在服务端做）。
async function optimize() {
  const text = input.value.trim();
  if (!text) return;

  optimizeBtn.disabled = true;
  hint.textContent = '正在优化描述…';

  try {
    const response = await fetch('/api/rephrase/run', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ text })
    });
    const data = await response.json();

    if (data.rephrased) {
      lastOriginal = text;
      input.value = data.text;
      autoGrow();
      hint.textContent = '';
      hint.append('已优化 · ' + (data.model || '模型') + ' · ' + data.elapsedMs + 'ms · ');
      const undo = document.createElement('a');
      undo.textContent = '撤销';
      undo.href = '#';
      undo.onclick = (event) => {
        event.preventDefault();
        input.value = lastOriginal;
        autoGrow();
        hint.textContent = '已撤销';
      };
      hint.appendChild(undo);
    } else if (data.skipReason) {
      hint.textContent = '未优化：' + data.skipReason;
    } else {
      hint.textContent = '优化失败：' + (data.error || '未知原因');
    }
  } catch (err) {
    hint.textContent = '优化失败：' + err.message;
  } finally {
    optimizeBtn.disabled = false;
    input.focus();
  }
}

// 转述目标下拉：端点直选（老配置）+ 「端点:模型」精确定位（多端点），一律来自模型管理。
function renderRephraseModelOptions() {
  if (!rpModel) return;
  const previous = rpModel.value;
  rpModel.innerHTML = '';

  const legacy = [
    ['local', 'local（本地 · 便宜）'],
    ['cloud', 'cloud（联网 · 贵）'],
  ];
  legacy.forEach(([value, label]) => {
    const option = document.createElement('option');
    option.value = value;
    option.textContent = label;
    rpModel.appendChild(option);
  });

  if (modelInfo && modelInfo.providers) {
    modelInfo.providers.forEach((p) => {
      const name = p.name || p.id;
      if (p.id !== 'local' && p.id !== 'cloud') {
        const option = document.createElement('option');
        option.value = p.id;
        option.textContent = name + '（整个端点，按端点默认模型）';
        rpModel.appendChild(option);
      }
      p.models.forEach((m) => {
        const option = document.createElement('option');
        option.value = p.id + ':' + m.id;
        option.textContent = name + ' / ' + m.id + (m.reasoning ? ' · 推理' : '');
        rpModel.appendChild(option);
      });
    });
  }

  if (previous) rpModel.value = previous;
}

function paintRephrase() {
  const available = rephraseInfo.available;
  const enabled = rephraseInfo.enabled;

  rephrasePill.textContent = '转述 '
    + (available ? (enabled ? (rephraseInfo.autoBeforeSend ? '自动' : '手动') : '关') : '不可用');
  rephrasePill.className = 'pill clickable ' + (available ? (enabled ? 'on' : '') : 'warn');
  rephrasePill.title = available
    ? '点击打开 / 收起转述设置'
    : '没有可用端点：转述需要一个已配置的模型（local / cloud）';

  if (!available) {
    rpEnabled.disabled = true;
    rpAuto.disabled = true;
    optimizeBtn.disabled = true;
    optimizeBtn.title = '没有可用端点：转述需要一个已配置的模型（local / cloud）';
  }
}

async function loadRephrase() {
  try {
    const data = await (await fetch('/api/rephrase')).json();
    rephraseInfo = data;
    rpEnabled.checked = data.enabled;
    rpAuto.checked = data.autoBeforeSend;
    rpModel.value = data.model;
    rpPrompt.value = data.systemPrompt;
    rpPrompt.dataset.defaultPrompt = data.defaultPrompt;
    renderRephraseModelOptions();
    rpModel.value = data.model;
    paintRephrase();
  } catch (err) {
    hint.textContent = '转述设置加载失败：' + err.message;
  }
}

async function saveRephrase(payload) {
  try {
    const response = await fetch('/api/rephrase/settings', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    });
    if (!response.ok) {
      rpState.textContent = '保存失败 HTTP ' + response.status;
      return;
    }
    rpState.textContent = '已保存';
    setTimeout(() => { rpState.textContent = ''; }, 1600);
    await loadRephrase();
  } catch (err) {
    rpState.textContent = '保存失败：' + err.message;
  }
}

async function send() {
  const text = input.value.trim();
  if (!text) return;
  input.value = '';
  input.style.height = 'auto';
  hint.textContent = '';
  try {
    const response = await fetch('/api/send', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ text })
    });
    if (!response.ok) {
      hint.textContent = '发送失败：HTTP ' + response.status;
    } else {
      // ③：回合可能在这条 202 响应回来**之前**就已经跑完（turn-ended 先到）。
      // 那种情况下再置「运行中」，就是把已经收束的按钮又按回「停止」——
      // 回合早没了，却要手动点一下才复位。所以只对「还没结束的回合」置运行中。
      const data = await response.json().catch(() => ({}));
      if (data && data.turnId && endedTurns.includes(data.turnId)) {
        setTurnRunning(false);
      } else {
        setTurnRunning(true);
      }
    }
  } catch (err) {
    hint.textContent = '发送失败：' + err.message;
  } finally {
    if (!turnRunning) input.focus();
  }
}

function renderSessions(sessions, current) {
  sessionList.innerHTML = '';
  sessions.forEach((s) => {
    const item = document.createElement('div');
    item.className = 'session' + (s.isCurrent ? ' current' : '');
    item.dataset.id = s.id;
    const name = document.createElement('div');
    name.className = 'session-name';
    name.textContent = s.preview;
    const meta = document.createElement('div');
    meta.className = 'session-meta';
    const projName = s.projectDir ? s.projectDir.split(/[\\/]/).filter(Boolean).pop() : null;
    meta.textContent = s.id + ' · ' + s.messageCount + ' 条消息 · ' + (modeCatalog[s.mode] || s.mode || 'work')
      + (projName ? ' · 📁' + projName : '');
    meta.title = '工作模式：' + (modeCatalog[s.mode] || s.mode || 'work')
      + (s.projectDir ? '\n项目目录：' + s.projectDir : '\n项目目录：宿主默认工作区');
    item.appendChild(name);
    item.appendChild(meta);
    // F3/F4/F8：分叉 · 重命名 · 导出（当前会话才有分叉——从最后一条事件分叉）
    const ops = document.createElement('div');
    ops.className = 'session-ops';
    if (s.isCurrent && lastSeq > 0) {
      const forkBtn = document.createElement('a');
      forkBtn.textContent = '分叉';
      forkBtn.title = '从当前会话最后一条事件分叉出新会话';
      forkBtn.onclick = (ev) => { ev.stopPropagation(); forkSession(s.id); };
      ops.appendChild(forkBtn);
    }
    const renBtn = document.createElement('a');
    renBtn.textContent = '重命名';
    renBtn.onclick = (ev) => { ev.stopPropagation(); renameSession(s.id); };
    ops.appendChild(renBtn);
    const expBtn = document.createElement('a');
    expBtn.textContent = '导出';
    expBtn.href = '/api/sessions/export?id=' + encodeURIComponent(s.id);
    ops.appendChild(expBtn);
    item.appendChild(ops);
    // 重绘后恢复“后台回合进行中”标记（busySessions 是持久集合）
    if (busySessions.has(s.id)) {
      const dot = document.createElement('span');
      dot.className = 'busy-dot';
      dot.textContent = ' ●';
      dot.style.color = '#6ea8fe';
      dot.title = '这个会话有回合正在运行';
      item.appendChild(dot);
    }
    item.title = '点击切换到会话 ' + s.id;
    item.onclick = () => switchSession(s.id);
    sessionList.appendChild(item);
  });
  if (current) {
    document.getElementById('pill-session').textContent = '会话 ' + current;
  }
}

let modeCatalog = {};
async function ensureModeCatalog() {
  if (Object.keys(modeCatalog).length) return;
  try {
    const data = await (await fetch('/api/modes')).json();
    data.modes.forEach((m) => { modeCatalog[m.id] = m.name; });
  } catch { /* 徽标降级显示 id，不阻断 */ }
}

async function loadSessions() {
  await ensureModeCatalog();
  try {
    const data = await (await fetch('/api/sessions')).json();
    renderSessions(data.sessions, data.current);
  } catch (err) {
    hint.textContent = '会话列表加载失败：' + err.message;
  }
}

async function switchSession(id) {
  hint.textContent = '正在切换到 ' + id + ' …';
  try {
    const response = await fetch('/api/sessions/switch', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ id })
    });
    if (!response.ok) {
      hint.textContent = '切换失败：HTTP ' + response.status;
      return;
    }
    hint.textContent = '';
    await afterSessionChange();
  } catch (err) {
    hint.textContent = '切换失败：' + err.message;
  }
}

async function newSession() {
  // HCI：模式在创建会话时选定（不再有顶栏切换器）—— 弹出模式选择卡
  await showModePick();
}

// ── 新建会话：模式选择弹层 ───────────────────────────
async function showModePick() {
  const mask = document.getElementById('mode-pick');
  const cards = document.getElementById('mode-cards');
  cards.innerHTML = '<div class="mc-desc">加载模式目录…</div>';
  mask.hidden = false;

  try {
    const data = await (await fetch('/api/modes')).json();
    cards.innerHTML = '';
    data.modes.forEach((m) => {
      const card = document.createElement('div');
      card.className = 'mode-card';
      const name = document.createElement('div');
      name.className = 'mc-name';
      name.textContent = m.name;
      if (m.custom) {
        const badge = document.createElement('span');
        badge.className = 'mc-badge';
        badge.textContent = '插件';
        name.appendChild(badge);
      }
      const desc = document.createElement('div');
      desc.className = 'mc-desc';
      desc.textContent = (m.summary || '')
        + (m.exposesTools ? '（全工具）' : '（不挂工具）');
      card.appendChild(name);
      card.appendChild(desc);
      card.onclick = () => createSession(m.id);
      cards.appendChild(card);
    });
  } catch (err) {
    cards.innerHTML = '<div class="mc-desc">模式目录加载失败：' + err.message + '</div>';
  }
}

async function createSession(mode) {
  document.getElementById('mode-pick').hidden = true;
  const projectDir = document.getElementById('np-project-dir').value.trim();
  try {
    const response = await fetch('/api/sessions/new', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ mode, projectDir: projectDir || null })
    });
    const data = await response.json();
    if (!data.ok) {
      hint.textContent = '创建失败：' + (data.error || response.status);
      return;
    }
    hint.textContent = '新会话已就绪（模式：' + data.mode.name + '）';
    await loadSessions();
    await switchSession(data.sessionId);
  } catch (err) {
    hint.textContent = '创建失败：' + err.message;
  }
}

async function forkSession(id) {
  try {
    const response = await fetch('/api/sessions/fork', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ id, fromSeq: lastSeq })
    });
    const data = await response.json();
    if (!data.ok) { hint.textContent = '分叉失败：' + (data.error || response.status); return; }
    hint.textContent = '已分叉出新会话 ' + data.sessionId;
    await switchSession(data.sessionId);
  } catch (err) { hint.textContent = '分叉失败：' + err.message; }
}

async function renameSession(id) {
  const title = prompt('新标题（留空取消）：');
  if (!title) return;
  try {
    const response = await fetch('/api/sessions/rename', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ id, title })
    });
    const data = await response.json();
    if (!data.ok) { hint.textContent = '重命名失败：' + (data.error || response.status); return; }
    await loadSessions();
  } catch (err) { hint.textContent = '重命名失败：' + err.message; }
}

async function afterSessionChange() {
  clearMessages();
  await loadSessions();
  await loadStatus();
  await loadHistory();
}
async function loadStatus() {
  try {
    const status = await (await fetch('/api/status')).json();
    const model = document.getElementById('pill-model');
    model.textContent = status.offlineDemo
      ? '离线演示（未配端点）'
      : (status.summarization ? '模型已接入 · 本地摘要开启' : '模型已接入');
    model.className = 'pill ' + (status.offlineDemo ? 'warn' : 'on');

    document.getElementById('pill-tools').textContent = '工具 ' + status.tools.length + ' 个';
    document.getElementById('pill-tools').title = status.tools.join(', ');

    const plugins = document.getElementById('pill-plugins');
    plugins.textContent = '插件 ' + status.plugins.length + ' 个'
      + (status.skipped.length ? ' / 未装 ' + status.skipped.length : '');
    plugins.title = status.plugins.map(p => p.id + '@' + p.version).join(', ');

    const sessionPill = document.getElementById('pill-session');
    sessionPill.textContent = '会话 ' + status.sessionId;
    // B2：记住“当前会话”——增量帧按归属分流就靠它比对。
    currentSessionId = status.sessionId;

    // 用量 pill：常驻一眼可见 —— 这是「钱花在哪」唯一的常驻入口
    const usagePill = document.getElementById('pill-usage');
    const usage = status.usage;
    if (usage && usage.calls > 0) {
      const hit = usage.cacheHitRate != null
        ? ' · 缓存 ' + Math.round(usage.cacheHitRate * 100) + '%'
        : '';
      usagePill.textContent = '↑' + fmtNum(usage.inputTokens) + ' ↓' + fmtNum(usage.outputTokens) + hit;
      usagePill.className = 'pill ' + (usage.cacheHitRate >= 0.5 ? 'on' : '');
      usagePill.title = '本次会话累计：' + usage.calls + ' 次调用 · 输入 ' + usage.inputTokens
        + ' · 输出 ' + usage.outputTokens + ' · 缓存命中 ' + usage.cachedTokens
        + (usage.unknownCalls ? '（其中 ' + usage.unknownCalls + ' 次端点未回报用量）' : '');
    } else {
      usagePill.textContent = '尚无调用';
    }

    // HCI：模式随会话走 —— 记下当前会话模式，会话列表徽标与状态提示用
    sessionMode = status.mode && status.mode.current ? status.mode.current : 'work';
    sessionModeName = status.mode ? status.mode.name : '工作模式';

    // HCI：页面刷新/重连后同步回合状态（否则停止按钮状态丢失）
    setTurnRunning(!!status.turnRunning);

    // HCI：上下文水位常驻 —— 快满时提前知道（压缩会打断缓存，也是该开新会话/派子 Agent 的信号）
    const ctxPill = document.getElementById('pill-ctx');
    if (status.context && status.context.budget && isFinite(status.context.waterLevel)) {
      const pct = Math.min(100, Math.round(status.context.waterLevel * 100));
      ctxPill.innerHTML = 'ctx ' + pct + '%<span class="bar"><i style="width:' + pct + '%"></i></span>';
      ctxPill.className = 'pill' + (status.context.waterLevel >= 0.8 ? ' hot' : '');
      ctxPill.title = '上下文约 ' + fmtNum(status.context.tokens) + ' / ' + fmtNum(status.context.budget)
        + ' tokens · 保留 ' + status.context.keptTurns + ' 轮 · 已压缩 ' + status.context.compactions + ' 次'
        + (status.context.waterLevel >= 0.8 ? '\n已超 80% —— 下一轮会触发压缩（历史折叠为占位符，可用 search_history 捞回）' : '');
    } else {
      ctxPill.textContent = 'ctx —';
      ctxPill.title = '上下文水位（当前模式未启用治理或无历史）';
    }

    if (status.failed && status.failed.length) {
      hint.textContent = '有插件加载失败：' + status.failed.join('；');
    }
  } catch (err) {
    hint.textContent = '状态获取失败：' + err.message;
  }
}

async function loadHistory() {
  try {
    const history = await (await fetch('/api/history')).json();
    lastSeq = history.lastSeq || 0;   // F3：分叉按钮用
    history.events.forEach(renderEvent);
    sealReasoning();   // 历史回放结束后不允许残留「正在思考…」的 live 块
    if (history.events.length) {
      addBubble('sys', '已恢复 ' + history.events.length + ' 条历史事件');
    }
    maybeEmptyState();   // 新/空会话给出行动提示，而不是一片死黑
  } catch (err) {
    hint.textContent = '历史加载失败：' + err.message;
  }
}

sendBtn.onclick = () => (turnRunning ? stopTurn() : send());
optimizeBtn.onclick = optimize;
document.getElementById('new-session').onclick = newSession;

rephrasePill.onclick = () => { settingsPanel.hidden = !settingsPanel.hidden; };
// ── 工具包（可见性的最小单位）─────────────────────────
// 「装了什么」与「这一轮给模型看什么」分开：装得多不等于负担重，收起来才省。
const toolsetsPanel = document.getElementById('toolsets-panel');
document.getElementById('pill-toolsets').onclick = () => {
  toolsetsPanel.hidden = !toolsetsPanel.hidden;
  if (!toolsetsPanel.hidden) loadToolsets();
};

async function loadToolsets() {
  const list = document.getElementById('toolsets-list');
  try {
    const data = await (await fetch('/api/toolsets')).json();
    list.innerHTML = '';

    const off = data.toolsets.filter((t) => !t.enabled).length;
    const head = document.createElement('div');
    head.className = 'skill-desc';
    head.style.padding = '2px 0 6px';
    head.textContent = '共 ' + data.toolsets.length + ' 个包，' + off + ' 个已收起';
    list.appendChild(head);

    data.toolsets.forEach((t) => {
      const row = document.createElement('div');
      row.className = 'skill-row';

      const state = document.createElement('span');
      state.className = 'skill-src' + (t.enabled ? ' on' : '');
      state.textContent = t.enabled ? '● 开' : '○ 关';

      const name = document.createElement('span');
      name.className = 'skill-name';
      name.textContent = t.name + '（' + t.id + '）';

      const desc = document.createElement('span');
      desc.className = 'skill-desc';
      desc.textContent = t.description || '（无描述）';
      desc.title = t.description || '';

      const tools = document.createElement('span');
      tools.className = 'skill-tools';
      tools.textContent = '⚙ ' + t.tools.length;
      tools.title = t.tools.length ? t.tools.join(', ') : '（空包）';

      const btn = document.createElement('button');
      btn.className = 'ghost';
      if (t.locked) {
        btn.textContent = '不可关';
        btn.disabled = true;
        btn.title = '保留包：关掉会把能力关成残废，或让开关本身再也开不回来';
      } else {
        btn.textContent = t.enabled ? '收起' : '打开';
        btn.onclick = async () => {
          const res = await (await fetch('/api/toolsets/toggle', {
            method: 'POST', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ id: t.id, enabled: !t.enabled })
          })).json();
          if (!res.ok) { hint.textContent = res.error || '开关失败'; }
          else { hint.textContent = (t.enabled ? '已收起「' + t.name + '」' : '已打开「' + t.name + '」') + '（下一轮生效）'; }
          loadToolsets();
        };
      }

      row.appendChild(state);
      row.appendChild(name);
      row.appendChild(desc);
      row.appendChild(tools);
      row.appendChild(btn);
      list.appendChild(row);
    });
  } catch (err) {
    list.innerHTML = '<div class="skill-desc">工具包列表加载失败：' + err.message + '</div>';
  }
}

document.getElementById('pill-skills').onclick = () => {
  skillsPanel.hidden = !skillsPanel.hidden;
  if (!skillsPanel.hidden) loadSkills();
};

// ── 技能与创意工坊 ───────────────────────────────────
async function loadSkills() {
  const list = document.getElementById('skills-list');
  try {
    const data = await (await fetch('/api/skills')).json();
    list.innerHTML = '';
    if (!data.skills.length) {
      list.innerHTML = '<div class="skill-desc" style="padding:6px 0">还没有技能 —— 在工作区 skills/ 放一个含 skill.json 的目录，或从下面导入别人的。</div>';
      return;
    }
    data.skills.forEach((s) => {
      const row = document.createElement('div');
      row.className = 'skill-row';

      const src = document.createElement('span');
      src.className = 'skill-src' + (s.enabled ? ' on' : '') + (s.source === 'workshop' ? ' workshop' : '');
      src.textContent = (s.enabled ? '● ' : '○ ') + (s.source === 'workshop' ? '工坊' : '内置');
      src.title = (s.author ? '作者 ' + s.author : '作者未知') + (s.version ? ' · v' + s.version : '');

      const name = document.createElement('span');
      name.className = 'skill-name';
      name.textContent = s.name;

      const desc = document.createElement('span');
      desc.className = 'skill-desc';
      desc.textContent = s.description || '（无描述）';
      desc.title = s.description || '';

      const tools = document.createElement('span');
      tools.className = 'skill-tools';
      tools.textContent = s.tools.length ? '⚙ ' + s.tools.length : '纯提示词';
      tools.title = '工具白名单：' + (s.tools.length ? s.tools.join(', ') : '不收窄（仅提示词）');

      const btn = document.createElement('button');
      btn.className = 'ghost';
      btn.textContent = s.enabled ? '停用' : '启用';
      btn.onclick = async () => {
        await fetch('/api/skills/toggle', {
          method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ name: s.name, enabled: !s.enabled })
        });
        loadSkills();
        hint.textContent = s.enabled ? '已停用「' + s.name + '」（回合边界生效）' : '已启用「' + s.name + '」（回合边界生效）';
      };

      row.appendChild(src);
      row.appendChild(name);
      row.appendChild(desc);
      row.appendChild(tools);
      row.appendChild(btn);
      list.appendChild(row);
    });
  } catch (err) {
    list.innerHTML = '<div class="skill-desc">技能列表加载失败：' + err.message + '</div>';
  }
}

// ── 记忆管理面板（三层：索引卡 ← 主视图 ← 归档层）──────
const memoryPanel = document.getElementById('memory-panel');
document.getElementById('pill-memory').onclick = () => {
  memoryPanel.hidden = !memoryPanel.hidden;
  if (!memoryPanel.hidden) { memMergeIds.clear(); loadMemory(); }
};

const memMergeIds = new Set();

function memState(id, text, color) {
  const el = document.getElementById(id);
  el.textContent = text;
  el.style.color = color || 'var(--ok)';
}

async function loadMemory() {
  const list = document.getElementById('memory-list');
  try {
    const data = await (await fetch('/api/memory/list')).json();
    if (!data.ok) { list.innerHTML = '<div class="mem-row">' + data.error + '</div>'; return; }
    list.innerHTML = '';

    const renderScope = (scopeLabel, scopePayload, isProject) => {
      // v3.5 审查 P1-3：这个 scope 是「本分组对应的记忆作用域 id」——
      // 全局层恒为 'global'，项目层用后端算好的 data.scope（形如 project:{目录}）。
      // 之前这里漏了声明，loadMemory() 渲染第一行就抛 ReferenceError，整个面板不可用。
      const scope = isProject ? data.scope : 'global';
      const title = document.createElement('div');
      title.className = 'mem-section-title';
      title.textContent = scopeLabel + '（活跃 ' + scopePayload.active.length + ' · 归档 ' + scopePayload.archived.length + '）';
      list.appendChild(title);

      const makeRow = (m, archived) => {
        const row = document.createElement('div');
        row.className = 'mem-row' + (archived ? ' mem-archived' : '');

        const pick = document.createElement('input');
        pick.type = 'checkbox';
        pick.title = '勾选两条以上可合并';
        pick.disabled = archived;
        pick.onchange = () => {
          if (pick.checked) memMergeIds.add(scope + ':' + m.id); else memMergeIds.delete(scope + ':' + m.id);
          document.getElementById('mem-merge-count').textContent = memMergeIds.size;
          document.getElementById('mem-merge-bar').hidden = memMergeIds.size === 0;
        };

        const scopeTag = document.createElement('span');
        scopeTag.className = 'mem-scope' + (isProject ? ' project' : '');
        scopeTag.textContent = scope;

        const text = document.createElement('span');
        text.className = 'mem-text';
        text.textContent = m.text;
        text.title = m.text + '\n（' + m.created + '）';

        const score = document.createElement('span');
        score.className = 'mem-score';
        score.textContent = (m.important ? '📌' : '') + '🔥' + m.score;

        const spacer = document.createElement('span');
        spacer.style.flex = '1';

        row.appendChild(pick);
        row.appendChild(scopeTag);
        row.appendChild(text);
        row.appendChild(score);
        row.appendChild(spacer);

        const act = async (action) => {
          const resp = await fetch('/api/memory/action', {
            method: 'POST', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ action, scope, id: m.id })
          });
          const r = await resp.json();
          if (!r.ok) hint.textContent = r.error || ('操作失败 ' + resp.status);
          loadMemory();
        };

        if (!archived) {
          const pin = document.createElement('button');
          pin.className = 'ghost';
          pin.textContent = m.important ? '取消置顶' : '置顶';
          pin.onclick = () => act('pin');
          row.appendChild(pin);

          const arc = document.createElement('button');
          arc.className = 'ghost';
          arc.textContent = '归档';
          arc.title = '移出主检索（原文保留，可恢复）';
          arc.onclick = () => act('archive');
          row.appendChild(arc);
        } else {
          const res = document.createElement('button');
          res.className = 'ghost';
          res.textContent = '恢复';
          res.onclick = () => act('restore');
          row.appendChild(res);
        }

        const forget = document.createElement('button');
        forget.className = 'ghost';
        forget.textContent = '撤销';
        forget.title = '追加 retract 事件，历史一个字不改';
        forget.onclick = () => { if (confirm('撤销这条记忆？（可从日志追溯，但视图里消失）')) act('forget'); };
        row.appendChild(forget);

        list.appendChild(row);
      };

      scopePayload.active.forEach((m) => makeRow(m, false));
      scopePayload.archived.forEach((m) => makeRow(m, true));
    };

    renderScope('全局记忆', data.memory.global, false);
    renderScope('项目记忆', data.memory.project, true);
  } catch (err) {
    list.innerHTML = '<div class="mem-row">记忆加载失败：' + err.message + '</div>';
  }
}

document.getElementById('mem-sweep-preview').onclick = () => memSweep(true);
document.getElementById('mem-sweep-run').onclick = () => {
  if (confirm('把 30 天未写且热度≤1 的记忆全部归档？（事件化，可随时恢复）')) memSweep(false);
};
async function memSweep(dryRun) {
  memState('mem-sweep-state', dryRun ? '统计中…' : '执行中…');
  try {
    const resp = await fetch('/api/memory/sweep', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ days: 30, maxScore: 1, dryRun })
    });
    const data = await resp.json();
    if (!data.ok) { memState('mem-sweep-state', data.error || '失败', 'var(--bad)'); return; }
    memState('mem-sweep-state', dryRun
      ? '可归档 ' + data.total + ' 条（点「执行降级」生效）'
      : '已归档 ' + data.total + ' 条');
    if (!dryRun) loadMemory();
  } catch (err) {
    memState('mem-sweep-state', '失败：' + err.message, 'var(--bad)');
  }
}

document.getElementById('mem-merge-go').onclick = async () => {
  const text = document.getElementById('mem-merge-text').value.trim();
  const state = document.getElementById('mem-merge-state');
  if (!text) { state.textContent = '先写合并后的正文'; state.style.color = 'var(--warn)'; return; }

  // 同 scope 才能合并：按前缀分组，取数量最多的一组
  const byScope = {};
  memMergeIds.forEach((key) => {
    // scope 里可能含 ':'（project:{绝对目录}），所以按**最后一个**冒号切分；
    // 用 split(':') 会把项目作用域切成 'project' 而丢掉目录，合并就落到别的账上了。
    const cut = key.lastIndexOf(':');
    const scope = key.slice(0, cut);
    const id = key.slice(cut + 1);
    (byScope[scope] = byScope[scope] || []).push(id);
  });
  const scope = Object.keys(byScope).sort((a, b) => byScope[b].length - byScope[a].length)[0];
  const ids = byScope[scope];

  try {
    const resp = await fetch('/api/memory/merge', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ scope, ids, text })
    });
    const data = await resp.json();
    if (!data.ok) { state.textContent = data.error || '合并失败'; state.style.color = 'var(--bad)'; return; }
    state.textContent = '已合并为 ' + data.merged.id + '（热度 ' + data.merged.score + '）';
    memMergeIds.clear();
    document.getElementById('mem-merge-bar').hidden = true;
    document.getElementById('mem-merge-text').value = '';
    loadMemory();
  } catch (err) {
    state.textContent = '合并失败：' + err.message;
    state.style.color = 'var(--bad)';
  }
};

document.getElementById('ws-import').onclick = async () => {
  const pathInput = document.getElementById('ws-path');
  const state = document.getElementById('ws-state');
  const path = pathInput.value.trim();
  if (!path) { state.textContent = '先填技能目录路径'; state.style.color = 'var(--warn)'; return; }
  state.textContent = '导入中…';
  try {
    const response = await fetch('/api/skills/import', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ path })
    });
    const data = await response.json();
    if (data.ok) {
      state.textContent = '已导入「' + data.imported.name + '」（在 workshop/ 中，启用见上表）';
      pathInput.value = '';
      loadSkills();
    } else {
      state.textContent = data.error || ('导入失败 HTTP ' + response.status);
      state.style.color = 'var(--bad)';
    }
  } catch (err) {
    state.textContent = '导入失败：' + err.message;
    state.style.color = 'var(--bad)';
  }
};
modelPill.onclick = () => {
  modelPanel.hidden = !modelPanel.hidden;
  if (!modelPanel.hidden) loadModels();
};
// HCI：模式卡片的点击在 showModePick 里逐卡挂 —— 容器不再需要全局 onclick
document.getElementById('mode-cancel').onclick = () => { document.getElementById('mode-pick').hidden = true; };

// ── 项目目录：目录浏览弹窗（「浏览…」）─────────────────────
// 浏览器出于安全拿不到本地绝对路径（showDirectoryPicker 只给句柄），
// 而会话的项目目录必须落成一个真实绝对路径 —— 所以目录列表由本地服务给
// （GET /api/fs/dirs）。浏览本身不设限（用户要的就是「自己挑任意位置」），
// 但「新建文件夹」只收单层目录名（见后端 /api/fs/mkdir）。
const dirPick = document.getElementById('dir-pick');
const dpList = document.getElementById('dp-list');
const dpState = document.getElementById('dp-state');
const dpCurrent = document.getElementById('dp-current');
const dpPathEl = document.getElementById('dp-path');
let dpPath = null;     // 当前浏览到的绝对路径（null = 还在「选一个起点」）
let dpParent = null;   // 上一级（到根为止为 null）
let dpHome = null;     // 主目录快捷入口

async function dirBrowse(path) {
  dpState.style.color = 'var(--dim)';
  dpState.textContent = '读取中…';
  try {
    const url = '/api/fs/dirs' + (path ? '?path=' + encodeURIComponent(path) : '');
    const response = await fetch(url);
    const data = await response.json();

    if (!data.ok) {
      dpState.style.color = 'var(--bad)';
      dpState.textContent = data.error || '读不了这个目录';
      return;
    }

    dpState.textContent = '';
    if (data.home) dpHome = data.home;
    dpPath = data.path || null;
    dpParent = data.parent || null;

    dpCurrent.textContent = dpPath ? '进入子文件夹，或直接点「就用这个目录」' : '从下面选一个起点';
    dpPathEl.textContent = dpPath || '';
    dpList.innerHTML = '';

    const addRow = (name, full, hidden) => {
      const row = document.createElement('div');
      row.className = 'dir-row' + (hidden ? ' hidden-dir' : '');
      const ico = document.createElement('span');
      ico.className = 'ico';
      ico.textContent = '📁';
      const nm = document.createElement('span');
      nm.className = 'nm';
      nm.textContent = name;
      const arrow = document.createElement('span');
      arrow.className = 'ico';
      arrow.textContent = '›';
      row.appendChild(ico);
      row.appendChild(nm);
      row.appendChild(arrow);
      row.onclick = () => dirBrowse(full);
      dpList.appendChild(row);
    };

    (data.entries || []).forEach((entry) => addRow(entry.name, entry.path, entry.hidden));
    (data.roots || []).forEach((rootEntry) => addRow(rootEntry.name, rootEntry.path, false));

    if (!dpList.children.length) {
      const empty = document.createElement('div');
      empty.className = 'dir-empty';
      empty.textContent = dpPath ? '（这个目录下没有子文件夹）' : '（没找到可用的起点，直接手输路径吧）';
      dpList.appendChild(empty);
    }
  } catch (err) {
    dpState.style.color = 'var(--bad)';
    dpState.textContent = '读取失败：' + err.message;
  }
}

document.getElementById('dp-up').onclick = () => { if (dpParent) dirBrowse(dpParent); };
document.getElementById('dp-home').onclick = () => { if (dpHome) dirBrowse(dpHome); };

document.getElementById('dp-new').onclick = async () => {
  if (!dpPath) { dpState.textContent = '先进入一个目录，再在里面新建'; return; }
  const name = prompt('新文件夹名：');
  if (!name) return;
  dpState.style.color = 'var(--dim)';
  dpState.textContent = '新建中…';
  try {
    const response = await fetch('/api/fs/mkdir', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ parent: dpPath, name })
    });
    const data = await response.json();
    if (!data.ok) {
      dpState.style.color = 'var(--bad)';
      dpState.textContent = '新建失败：' + (data.error || response.status);
      return;
    }
    await dirBrowse(dpPath);
  } catch (err) {
    dpState.style.color = 'var(--bad)';
    dpState.textContent = '新建失败：' + err.message;
  }
};

document.getElementById('dp-cancel').onclick = () => { dirPick.hidden = true; };

document.getElementById('dp-choose').onclick = () => {
  if (!dpPath) { dpState.textContent = '先进入一个目录，再点「就用这个目录」'; return; }
  document.getElementById('np-project-dir').value = dpPath;
  dirPick.hidden = true;
};

document.getElementById('np-browse').onclick = () => {
  dirPick.hidden = false;
  const current = document.getElementById('np-project-dir').value.trim();
  dirBrowse(current || null);
};
document.getElementById('mode-pick').onclick = (ev) => {
  if (ev.target.id === 'mode-pick') ev.target.hidden = true;   // 点遮罩关闭
};

// ── 插件列表与独立面板（dsh 式）─────────────────────
const pluginsPanel = document.getElementById('plugins-panel');
document.getElementById('pill-plugins').onclick = () => {
  pluginsPanel.hidden = !pluginsPanel.hidden;
  if (!pluginsPanel.hidden) loadPlugins();
};

async function loadPlugins() {
  const list = document.getElementById('plugins-list');
  try {
    const data = await (await fetch('/api/plugins')).json();
    list.innerHTML = '';
    if (!data.plugins.length) {
      list.innerHTML = '<div class="plugin-meta" style="padding:6px 0">还没有加载任何插件 —— 在启动器里启用一个插件再回来。</div>';
      return;
    }
    data.plugins.forEach((p) => {
      const row = document.createElement('div');
      row.className = 'plugin-row';
      const id = document.createElement('span');
      id.className = 'plugin-id';
      id.textContent = p.id;
      const ver = document.createElement('span');
      ver.className = 'plugin-meta';
      ver.textContent = 'v' + p.version + ' · ' + (p.tools > 0 ? p.tools + ' 个工具' : '无工具');
      const spacer = document.createElement('span');
      spacer.className = 'spacer';
      const openBtn = document.createElement('button');
      openBtn.className = 'ghost';
      if (p.hasPanel) {
        openBtn.textContent = '打开面板';
        openBtn.onclick = () => openPluginPanel(p.id);
      } else {
        openBtn.textContent = '无面板';
        openBtn.disabled = true;
        openBtn.title = '插件目录里没有 panel.html';
      }
      row.appendChild(id);
      row.appendChild(ver);
      row.appendChild(spacer);
      row.appendChild(openBtn);
      list.appendChild(row);
    });
  } catch (err) {
    list.innerHTML = '<div class="plugin-meta">插件列表加载失败：' + err.message + '</div>';
  }
}

function openPluginPanel(id) {
  const wrapEl = document.getElementById('plugin-frame-wrap');
  document.getElementById('plugin-frame-title').textContent = '面板 · ' + id;
  document.getElementById('plugin-frame').src = '/plugin-panel?id=' + encodeURIComponent(id);
  wrapEl.hidden = false;
}
document.getElementById('plugin-frame-close').onclick = () => {
  document.getElementById('plugin-frame-wrap').hidden = true;
  document.getElementById('plugin-frame').src = 'about:blank';
};

// ── 回合生命周期：发送键二态（发送 ⇆ 停止）─────────────────
// 之前 202 后按钮立即可点（连点会排队多个回合），且没有任何办法中断失控回合 ——
// 长任务跑偏时只能干看着或重启。现在：进行中=停止键（含 Esc），结束自动复原。
let turnRunning = false;

function setTurnRunning(running) {
  turnRunning = running;
  sendBtn.textContent = running ? '■ 停止' : '发送';
  sendBtn.classList.toggle('stop', running);
  sendBtn.disabled = false;
  input.placeholder = running
    ? '回合进行中…（Esc 或 ■ 停止 可中断）'
    : '说点什么…（Enter 发送 / Shift+Enter 换行）';
  if (running) { setPhase('思考中…'); showDots(); }
  else {
    hidePhase();
    hideDots();
    document.querySelectorAll('.bubble.streaming').forEach((b) => b.classList.remove('streaming'));
  }
}

async function stopTurn() {
  try {
    const response = await fetch('/api/stop', { method: 'POST' });
    if (!response.ok) {
      const data = await response.json().catch(() => ({}));
      hint.textContent = data.error || ('停止失败 HTTP ' + response.status);
      // 服务端说没有回合在跑 → 状态失步，强制复位
      setTurnRunning(false);
    }
  } catch (err) {
    hint.textContent = '停止失败：' + err.message;
  }
}

// ── 模型设置 ─────────────────────────────────────────────
// 「能不能用」由端点说了算：连上、拉到列表、选中 —— 三步都当场看得见结果。
// 界面里刻意不回显密钥：既然后端不给，这里也就没有可泄露的东西。

async function loadModels() {
  try {
    modelInfo = await (await fetch('/api/models')).json();
  } catch (err) {
    mdState.textContent = '读不到模型配置：' + err.message;
    return;
  }
  renderModels();
}

function renderModels() {
  mdList.innerHTML = '';
  mdActive.innerHTML = '';

  if (!modelInfo || !modelInfo.enabled) {
    mdState.textContent = '未启用（这次启动没有配置单路径）';
    return;
  }

  mdState.textContent = modelInfo.protector.available
    ? '密钥加密：' + modelInfo.protector.kind
    : '⚠ 无可用加密（' + modelInfo.protector.kind + '）';

  modelInfo.providers.forEach(p => {
    p.models.forEach(m => {
      const option = document.createElement('option');
      option.value = mdKey2(p.id, m.id);
      option.textContent = (p.name || p.id) + ' / ' + m.id + (m.reasoning ? ' · 推理' : '');
      mdActive.appendChild(option);
    });

    const row = document.createElement('div');
    row.className = 'md-row';

    const title = document.createElement('span');
    title.className = 'md-title';
    title.textContent = (p.name || p.id) + (p.local ? ' · 本地' : '')
      + ' · ' + p.models.length + ' 个模型';
    title.title = p.baseUrl + (p.hasKey ? ' · 已配密钥' : ' · 未配密钥');
    row.appendChild(title);

    const edit = document.createElement('button');
    edit.className = 'ghost small';
    edit.textContent = '编辑';
    edit.onclick = () => openEditor(p);
    row.appendChild(edit);

    const remove = document.createElement('button');
    remove.className = 'ghost small';
    remove.textContent = '删除';
    remove.onclick = () => removeProvider(p.id);
    row.appendChild(remove);

    mdList.appendChild(row);
  });

  if (!modelInfo.providers.length) {
    const empty = document.createElement('div');
    empty.className = 'md-provider';
    empty.textContent = '还没有端点 —— 点「＋ 端点」加一个（本地 Ollama 走同一条路）。';
    mdList.appendChild(empty);
  }

  mdEditor.hidden = true;
  editingProviderId = null;

  if (modelInfo.active) {
    mdActive.value = mdKey2(modelInfo.active.providerId, modelInfo.active.modelId);
  }

  renderRephraseModelOptions();
  if (rephraseInfo && rephraseInfo.model) rpModel.value = rephraseInfo.model;
}

function openEditor(provider) {
  mdEditor.hidden = false;
  editingProviderId = provider ? provider.id : null;

  mdId.value = provider ? provider.id : '';
  mdId.disabled = !!provider;
  mdName.value = provider ? (provider.name || '') : '';
  mdUrl.value = provider ? provider.baseUrl : '';
  mdKey.value = '';
  mdModels.value = provider ? provider.models.map(m => m.id).join('\n') : '';

  mdEditorState.textContent = provider
    ? (provider.hasKey ? '已配密钥（留空则不修改）' : '尚未配密钥')
    : '新端点：先「测试连接」把模型列表拉下来，再保存';
}

async function testEndpoint() {
  mdEditorState.textContent = '测试中…';

  let data;
  try {
    data = await (await fetch('/api/models/probe', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        providerId: editingProviderId,
        baseUrl: mdUrl.value.trim(),
        apiKey: mdKey.value.trim()
      })
    })).json();
  } catch (err) {
    mdEditorState.textContent = '✗ 请求失败：' + err.message;
    return;
  }

  mdEditorState.textContent = (data.ok ? '✓ ' : '✗ ') + data.message;

  // 只在空的时候自动填 —— 拉来的列表可以覆盖「没填」，但绝不覆盖主人自己敲的
  if (data.ok && data.models && data.models.length && !mdModels.value.trim()) {
    mdModels.value = data.models.join('\n');
  }
}

async function saveEndpoint() {
  const id = (mdId.value || '').trim();
  const url = (mdUrl.value || '').trim();

  if (!id || !url) {
    mdEditorState.textContent = 'id 和地址都要填';
    return;
  }

  mdEditorState.textContent = '保存中…';

  let data;
  try {
    data = await (await fetch('/api/models/provider', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        id,
        name: (mdName.value || '').trim(),
        baseUrl: url,
        apiKey: mdKey.value.trim(),
        reasoningStyle: mdStyle.value,
        reasoningEffort: mdEffort.value,
        models: mdModels.value.split('\n').map(s => s.trim()).filter(Boolean)
      })
    })).json();
  } catch (err) {
    mdEditorState.textContent = '✗ 请求失败：' + err.message;
    return;
  }

  if (!data.ok) {
    mdEditorState.textContent = '✗ ' + data.error;
    return;
  }

  modelInfo = data.models;
  renderModels();
  loadStatus();
}

async function removeProvider(providerId) {
  if (!confirm('删除端点 ' + providerId + '？')) return;

  const data = await (await fetch('/api/models/remove', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ providerId })
  })).json();

  if (!data.ok) {
    mdState.textContent = '✗ ' + data.error;
    return;
  }

  modelInfo = data.models;
  renderModels();
  loadStatus();
}

async function switchModel(value) {
  const pair = mdSplit(value);

  const data = await (await fetch('/api/models/active', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ providerId: pair[0], modelId: pair[1] })
  })).json();

  if (!data.ok) {
    mdState.textContent = '✗ ' + data.error;
    return;
  }

  // 换模型**不重启**：下一轮对话就是新模型在答
  modelInfo = data.models;
  renderModels();
  loadStatus();
}

mdActive.onchange = () => switchModel(mdActive.value);
document.getElementById('md-refresh').onclick = loadModels;
document.getElementById('md-new').onclick = () => openEditor(null);
document.getElementById('md-test').onclick = testEndpoint;
document.getElementById('md-save').onclick = saveEndpoint;
document.getElementById('md-cancel').onclick = () => { mdEditor.hidden = true; };

rpEnabled.onchange = () => saveRephrase({ enabled: rpEnabled.checked });
rpAuto.onchange = () => saveRephrase({ autoBeforeSend: rpAuto.checked });
rpModel.onchange = () => saveRephrase({ model: rpModel.value });
rpSave.onclick = () => saveRephrase({
  enabled: rpEnabled.checked,
  autoBeforeSend: rpAuto.checked,
  model: rpModel.value,
  systemPrompt: rpPrompt.value
});
rpReset.onclick = () => {
  rpPrompt.value = rpPrompt.dataset.defaultPrompt || '';
  saveRephrase({ systemPrompt: rpPrompt.value });
};

input.addEventListener('keydown', (event) => {
  if (event.key === 'Enter' && !event.shiftKey) {
    event.preventDefault();
    // HCI：回合进行中，空 Enter = 停止；带着字 Enter = 提示（别把用户正在写的话变成误停）
    if (turnRunning) {
      if (!input.value.trim()) stopTurn();
      else hint.textContent = '回合进行中 —— 按 ■ 停止 或 Esc 可中断，结束后再发送';
      return;
    }
    send();
  }
});
// HCI：Esc = 停止当前回合（不用去够按钮）
document.addEventListener('keydown', (event) => {
  if (event.key === 'Escape' && turnRunning) {
    event.preventDefault();
    stopTurn();
  }
});
input.addEventListener('input', autoGrow);

const stream = new EventSource('/api/stream');

// HCI：连接状态常驻可见 —— 之前断线只有一行 hint 小字，下一帧就被冲掉
function setConn(up) {
  const conn = document.getElementById('pill-conn');
  conn.textContent = up ? '● 已连接' : '○ 重连中';
  conn.className = 'pill' + (up ? '' : ' conn-off');
  if (up && hint.textContent === '连接已断开，正在重连…') hint.textContent = '';
}
stream.onopen = () => setConn(true);
stream.onmessage = (message) => {
  let data;
  try { data = JSON.parse(message.data); } catch { return; }

  // B2：回合相关帧（delta / reasoning / event）按归属会话分流 ——
  //   P1 之后“有回合在跑的会话”不必是当前会话（人可以切去别的会话看）。
  //   不分流的话，A 的打字机会打进 B 的气泡（currentAssistant 是全局状态）。
  //   兼容性：老帧不带 sessionId 时按当前会话处理（isCurrent = true）。
  const frameSession = data.sessionId || null;
  const isCurrent = frameSession === null || frameSession === currentSessionId;
  if (!isCurrent && (data.type === 'delta' || data.type === 'reasoning' || data.type === 'event')) {
    markSessionBusy(frameSession);
    return;
  }

  if (data.type === 'delta') onDelta(data.text);
  else if (data.type === 'reasoning') onReasoning(data.text);
  else if (data.type === 'event') { if (data.event && data.event.seq) lastSeq = data.event.seq; renderEvent(data.event); }
  else if (data.type === 'sys') {
    // 温和的系统消息（如“回合已停止”）—— 也可能是一轮的最后一条，一并收口思考块
    sealReasoning();
    const el = document.createElement('div');
    el.className = 'sys';
    el.textContent = data.message;
    wrap.appendChild(el);
    toBottom();
  }
  else if (data.type === 'turn-ended') {
    // 回合终点信号：恢复发送键；后台会话顺带清忙点。
    // 思考块在此收口 —— 一轮以工具调用/审批等待/报错结束而没有正文时，
    // 它只能在这里被收掉，否则永远显示「正在思考…」。
    sealReasoning();
    // ③：记下这个回合已经结束，供 send() 的 202 回调判断（见 endedTurns 注释）。
    if (data.turnId) {
      endedTurns.push(data.turnId);
      if (endedTurns.length > 32) endedTurns.shift();
    }
    if (data.sessionId) clearSessionBusy(data.sessionId);
    if (!data.sessionId || data.sessionId === currentSessionId) setTurnRunning(false);
  }
  else if (data.type === 'error') {
    sealReasoning();
    hint.textContent = '错误：' + data.message;
    // 行内且**持久** —— 业界的反面教材正是「错误闪现即消失」，事后无从追查
    const el = document.createElement('div');
    el.className = 'sys';
    el.style.color = '#ff8f8f';
    el.textContent = '⚠ ' + data.message;
    wrap.appendChild(el);
    toBottom();
  }
  else if (data.type === 'session-switched') afterSessionChange();
  else if (data.type === 'mode-changed') loadStatus();
  else if (data.type === 'models-changed') { loadStatus(); if (!modelPanel.hidden) loadModels(); }
};
stream.onerror = () => setConn(false);

(async () => {
  await loadStatus();
  await loadSessions();
  await loadRephrase();
  await loadHistory();
  input.focus();
})();
</script>
</body>
</html>
""";
}
