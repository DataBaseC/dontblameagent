using System.Text.Json;
using AgentFramework.Contracts;

namespace AgentFramework.Host;

/// <summary>
/// 内置路由 · <b>会话域</b>（P5）。
///
/// <para>
/// 这些端点从前挤在 <see cref="WebUiServer"/> 的主 <c>switch</c> 里。
/// 搬出来的理由不是「文件太长」，而是**改动的影响面**：
/// 主分发一改，所有端点的验收都要重走一遍；而按域拆开之后，
/// 会话域的改动只碰会话域这一个文件。
/// </para>
/// <para>
/// 判断标准（评审给的）：**以后加一个审批策略端点，不应该再打开主分发文件。**
/// </para>
/// <para>
/// 这一域在 P1 之后语义变了：<c>/api/sessions/switch</c> 不再是「重建宿主」，
/// 而是「换宿主的当前会话指针」。
/// </para>
/// </summary>
public sealed partial class WebUiServer
{
    /// <summary>注册会话域的内置端点。</summary>
    private void RegisterSessionRoutes()
    {
        // 首页
        Map(new DelegateRoute("GET", "/", (request, _) =>
        {
            request.Text(WebUiPage.Html, "text/html; charset=utf-8");
            return ValueTask.CompletedTask;
        }));

        // 状态总览：UI 每隔几秒来拉一次，拼的是宿主各面的当前状态
        Map(new DelegateRoute("GET", "/api/status", (request, _) =>
        {
            request.Json(new
            {
                sessionId = _host.SessionId,
                offlineDemo = _host.UsingOfflineDemo,
                summarization = _host.SummarizationEnabled,
                // HCI：页面加载/重连时同步“回合是否在跑”——
                // 否则刷新页面后停止按钮状态丢失，只剩会话列表的 busy 点。
                turnRunning = _host.IsBusy,
                projectDir = _host.Session?.ProjectDir,
                tools = _host.ToolNames,
                plugins = _host.LoadedPlugins.Select(p => new { p.Id, p.Version }).ToList(),
                skipped = _host.SkippedPlugins,
                failed = _host.FailedPlugins,
                logPath = _host.SessionLogPath,
                rephrase = new
                {
                    available = _host.RephraserAvailable,
                    enabled = _host.RephraseSettings.Enabled,
                    autoBeforeSend = _host.RephraseSettings.AutoBeforeSend,
                    model = _host.RephraseSettings.Model,
                },
                context = ContextStatus(),
                mode = ModeStatus(),
                usage = UsageStatus(),
            });

            return ValueTask.CompletedTask;
        }));

        // 会话列表
        Map(new DelegateRoute("GET", "/api/sessions", (request, _) =>
        {
            request.Json(new { sessions = ListSessions(), current = _host.SessionId });
            return ValueTask.CompletedTask;
        }));

        // 切换会话
        Map(new DelegateRoute("POST", "/api/sessions/switch", async (request, _) =>
        {
            var body = await request.ReadBodyAsync().ConfigureAwait(false);
            if (body is null)
            {
                request.Json(new { ok = false, error = "请求体不是合法 JSON" }, 400);
                return;
            }

            var id = ReadString(body.Value, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                request.Json(new { ok = false, error = "缺少 id" }, 400);
                return;
            }

            if (!IsValidSessionId(id))
            {
                request.Json(new { ok = false, error = "会话 id 非法" }, 400);
                return;
            }

            var (switched, switchError) = await SwitchSessionAsync(id).ConfigureAwait(false);
            if (!switched)
            {
                request.Json(new { ok = false, error = switchError }, 409);
                return;
            }

            request.Json(new { ok = true, sessionId = _host.SessionId });
        }));

        // 删除会话
        Map(new DelegateRoute("POST", "/api/sessions/delete", async (request, _) =>
        {
            var body = await request.ReadBodyAsync().ConfigureAwait(false);
            var id = body is null ? null : ReadString(body.Value, "id");

            if (string.IsNullOrWhiteSpace(id))
            {
                request.Json(new { ok = false, error = "缺少 id" }, 400);
                return;
            }

            // ★ 会话 id 直接参与拼路径，必须白名单化（P1-S2）：
            //   否则 id = "..\\..\\文档" 能让 delete 删掉任意 *.jsonl。
            if (!IsValidSessionId(id))
            {
                request.Json(new { ok = false, error = "会话 id 非法" }, 400);
                return;
            }

            if (string.Equals(id, _host.SessionId, StringComparison.Ordinal))
            {
                request.Json(new { ok = false, error = "不能删除当前正在使用的会话" }, 400);
                return;
            }

            var file = Path.Combine(_baseOptions.SessionsDir, id + ".jsonl");
            if (!File.Exists(file))
            {
                request.Json(new { ok = false, error = "会话不存在" }, 404);
                return;
            }

            // ★ B3 修复：删的不只是文件 —— 若会话在本进程里已打开（runtime 在会话表里），
            //   必须同步移除并释放它，否则同 id 重建会拿到旧 runtime，
            //   内存事件表把已删除的历史“复活”（磁盘上却是新空文件）。
            //   CloseSessionAsync 内部处理：等回合（≤5s，跑着则 409）→
            //   移除 + Dispose runtime → 清派生物（快照缓存 + 索引）。
            //   从未打开过的会话没有 runtime 可关，直接删文件即可。
            if (_host.OpenSessionIds.Contains(id, StringComparer.Ordinal))
            {
                var closed = await _host.CloseSessionAsync(id).ConfigureAwait(false);
                if (!closed)
                {
                    request.Json(new { ok = false, error = "该会话有回合正在运行，请稍候再删" }, 409);
                    return;
                }
            }

            File.Delete(file);
            await _host.InvalidateDerivedDataAsync(id).ConfigureAwait(false);

            request.Json(new { ok = true });
        }));

        // 历史（当前会话的原始事件流）
        Map(new DelegateRoute("GET", "/api/history", (request, _) =>
        {
            var allEvents = _host.Events();
            request.RawJson(JsonSerializer.Serialize(
                new { events = allEvents, lastSeq = allEvents.Count == 0 ? 0 : allEvents[^1].Seq },
                WebUiJson.Options));
            return ValueTask.CompletedTask;
        }));

        // ── F3 会话分叉：从某事件序号前复制出独立会话（SessionForker）──
        Map(new DelegateRoute("POST", "/api/sessions/fork", async (request, _) =>
        {
            var body = await request.ReadBodyAsync().ConfigureAwait(false);
            var id = body is null ? null : ReadString(body.Value, "id");
            long fromSeq = 0;
            if (body is not null && body.Value.TryGetProperty("fromSeq", out var seqNode)
                && seqNode.ValueKind == System.Text.Json.JsonValueKind.Number)
            {
                fromSeq = seqNode.GetInt64();
            }

            if (string.IsNullOrWhiteSpace(id) || fromSeq <= 0)
            {
                request.Json(new { ok = false, error = "缺少 id 或 fromSeq" }, 400);
                return;
            }

            if (!IsValidSessionId(id))
            {
                request.Json(new { ok = false, error = "会话 id 非法" }, 400);
                return;
            }

            try
            {
                var newId = _host.ForkSession(id, fromSeq);
                request.Json(new { ok = true, sessionId = newId });
            }
            catch (Exception ex)
            {
                request.Json(new { ok = false, error = ex.Message }, 400);
            }
        }));

        // HCI：创建会话时选定工作模式（模式属会话，创建后钉住 —— 顶栏不再有全局切换器）。
        // 插件注册的自定义档位（酒馆/宠物/…）经同一入口进来，对前端零特殊。
        Map(new DelegateRoute("POST", "/api/sessions/new", async (request, _) =>
        {
            var body = await request.ReadBodyAsync().ConfigureAwait(false);
            var mode = body is null ? null : ReadString(body.Value, "mode") ?? "work";
            var projectDir = body is null ? null : ReadString(body.Value, "projectDir");

            if (!AgentModes.TryResolve(mode, out var profile))
            {
                request.Json(new { ok = false, error = $"未知模式：{mode}" }, 400);
                return;
            }

            // 项目目录（可选）：多项目记忆隔离的锚点。填了就建目录；空 = 宿主工作区。
            if (!string.IsNullOrWhiteSpace(projectDir))
            {
                try
                {
                    projectDir = Path.GetFullPath(projectDir);
                }
                catch (Exception ex)
                {
                    request.Json(new { ok = false, error = $"项目目录非法：{ex.Message}" }, 400);
                    return;
                }

                if (!Path.IsPathRooted(projectDir))
                {
                    request.Json(new { ok = false, error = "项目目录必须是绝对路径" }, 400);
                    return;
                }

                Directory.CreateDirectory(projectDir);
            }
            else
            {
                projectDir = null;
            }

            var now = DateTime.UtcNow;
            var sessionId = $"s-{now:yyyyMMdd-HHmmss}";
            var ordinal = 1;
            while (_host.OpenSessionIds.Contains(sessionId, StringComparer.Ordinal)
                   || File.Exists(Path.Combine(_baseOptions.SessionsDir, sessionId + ".jsonl")))
            {
                sessionId = $"s-{now:yyyyMMdd-HHmmss}-{ordinal++}";
            }

            _host.OpenSession(sessionId, relayToUi: true, modeId: mode, projectDir: projectDir);

            var (switched, switchError) = await SwitchSessionAsync(sessionId).ConfigureAwait(false);
            if (!switched)
            {
                request.Json(new { ok = false, error = switchError ?? "会话已创建，但切换失败" }, 409);
                return;
            }

            Broadcast(JsonSerializer.SerializeToElement(
                new { type = "session-switched", sessionId },
                WebUiJson.Options));

            request.Json(new { ok = true, sessionId, mode = ModeStatus(), projectDir = _host.Session?.ProjectDir });
        }));

        // 插件列表（HCI：agent 工具的"已装内容"一览；有独立面板的插件给出入口）
        Map(new DelegateRoute("GET", "/api/plugins", (request, _) =>
        {
            var toolCount = new System.Collections.Concurrent.ConcurrentDictionary<string, int>(StringComparer.Ordinal);
            foreach (var kv in _host.ToolSources)
            {
                toolCount.AddOrUpdate(kv.Value, 1, (_, n) => n + 1);
            }

            request.Json(new
            {
                ok = true,
                plugins = _host.LoadedPlugins.Select(p => new
                {
                    id = p.Id,
                    version = p.Version,
                    tools = toolCount.TryGetValue(p.Id, out var n) ? n : 0,
                    hasPanel = FindPanelPath(p.Id) is not null,
                }),
                skipped = _host.SkippedPlugins,
                failed = _host.FailedPlugins,
            });
            return ValueTask.CompletedTask;
        }));

        // 插件独立面板：插件目录下的 panel.html，iframe 沙箱承载（dsh 式：插件自带一小块 UI）。
        // 面板与插件同一信任域 —— 插件代码本就跑在宿主进程里。
        Map(new DelegateRoute("GET", "/plugin-panel", (request, _) =>
        {
            var id = request.Query["id"];
            var panel = IsValidSessionId(id ?? "") ? FindPanelPath(id!) : null;
            if (panel is null)
            {
                request.Json(new { ok = false, error = "插件不存在或未提供面板" }, 404);
                return ValueTask.CompletedTask;
            }

            request.Text(File.ReadAllText(panel), "text/html; charset=utf-8");
            return ValueTask.CompletedTask;
        }));

        // ── F4 会话重命名：标题存 titles.json（用户元数据，不进事件流）──
        Map(new DelegateRoute("POST", "/api/sessions/rename", async (request, _) =>
        {
            var body = await request.ReadBodyAsync().ConfigureAwait(false);
            var id = body is null ? null : ReadString(body.Value, "id");
            var title = body is null ? null : ReadString(body.Value, "title");

            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
            {
                request.Json(new { ok = false, error = "缺少 id 或 title" }, 400);
                return;
            }

            if (!IsValidSessionId(id))
            {
                request.Json(new { ok = false, error = "会话 id 非法" }, 400);
                return;
            }

            _host.RenameSession(id, title);
            request.Json(new { ok = true });
        }));

        // ── F8 导出会话为 Markdown（下载）──
        Map(new DelegateRoute("GET", "/api/sessions/export", (request, _) =>
        {
            var id = request.Query["id"];
            if (string.IsNullOrWhiteSpace(id) || !IsValidSessionId(id))
            {
                request.Json(new { ok = false, error = "会话 id 非法" }, 400);
                return ValueTask.CompletedTask;
            }

            try
            {
                var md = _host.ExportSessionMarkdown(id);
                request.Http.Response.StatusCode = 200;
                request.Http.Response.ContentType = "text/markdown; charset=utf-8";
                request.Http.Response.Headers.Add("Content-Disposition",
                    $"attachment; filename=\"{id}.md\"");
                var bytes = System.Text.Encoding.UTF8.GetBytes(md);
                request.Http.Response.ContentLength64 = bytes.Length;
                request.Http.Response.OutputStream.Write(bytes);
                request.Http.Response.Close();
            }
            catch (Exception ex)
            {
                request.Json(new { ok = false, error = ex.Message }, 404);
            }

            return ValueTask.CompletedTask;
        }));

        // ── debug 信息窗（v3.2 自用诊断，不伪装成"产品功能"）────────
        // 一屏摊开实测时最常问的几件事：现在哪个会话、内存事件表与磁盘是否一致、
        // 水位/压缩状态、放行集内容。JSON 版给脚本拉取。
        Map(new DelegateRoute("GET", "/debug", (request, _) =>
        {
            request.Text(RenderDebugPage(), "text/html; charset=utf-8");
            return ValueTask.CompletedTask;
        }));

        Map(new DelegateRoute("GET", "/api/debug", (request, _) =>
        {
            request.RawJson(BuildDebugJson());
            return ValueTask.CompletedTask;
        }));
    }

    /// <summary>debug 数据快照（HTML 窗与 JSON 端点共用，保证两处数字永远一致）。</summary>
    private static (string SessionId, int MemEvents, int DiskEvents, bool MemSynced, object Context, IReadOnlyList<string> Allowed, IReadOnlyList<string> Recent) DebugSnapshot(AgentHost host)
    {
        var memEvents = host.Session.Events;

        // 性能修复（安全审查 P1-3）：原先每 3 秒的全量反序列化只为数一个数。
        // 一致性检查降级为「行数近似」：先看文件行数（换行符数+1，纯字节扫描），
        // 与内存事件数相等才坐实一致；不等时才付全量读的代价做精确比对（也仍然便宜）。
        // 注：JSONL 每行一条事件，行数即事件数的上界 —— 足以回答「是否失步」。
        var diskCount = CountLinesFast(host.Session.LogPath);
        var memSynced = diskCount == memEvents.Count;

        var context = new
        {
            tokens = host.ContextStatusTokens(),
            waterLevel = host.ContextStatusWaterLevel(),
            keptTurns = host.ContextStatusKeptTurns(),
            compactions = host.ContextStatusCompactions(),
        };

        // F7：最近 8 条事件的时间线 —— 实测事件顺序问题不用翻 JSONL
        var recent = memEvents.TakeLast(8)
            .Select(e => $"#{e.Seq} [{e.Timestamp:HH:mm:ss}] {e.GetType().Name.Replace("Event", "")}")
            .ToList();

        return (host.SessionId, memEvents.Count, diskCount, memSynced, context, host.Session.AllowedTools, recent);
    }

    /// <summary>纯字节扫描数行（每行一条 JSONL 事件）；文件不存在返回 0。</summary>
    private static int CountLinesFast(string path)
    {
        if (!File.Exists(path))
        {
            return 0;
        }

        var lines = 0;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = new byte[65_536];
        int read;
        var tailIsNewline = true;   // 空文件 0 行；末尾无换行也计最后一行
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == (byte)'\n')
                {
                    lines++;
                    tailIsNewline = true;
                }
                else
                {
                    tailIsNewline = false;
                }
            }
        }

        return tailIsNewline && lines == 0 ? 0 : lines + (tailIsNewline ? 0 : 1);
    }

    private string BuildDebugJson()
    {
        var snap = DebugSnapshot(_host);
        return JsonSerializer.Serialize(new
        {
            session = snap.SessionId,
            memoryEvents = snap.MemEvents,
            diskEvents = snap.DiskEvents,
            memorySynced = snap.MemSynced,
            context = snap.Context,
            allowedTools = snap.Allowed,
            recentEvents = snap.Recent,
        }, WebUiJson.Options);
    }

    private string RenderDebugPage()
    {
        var snap = DebugSnapshot(_host);
        var syncColor = snap.MemSynced ? "#52ffa8" : "#ff5370";
        var syncText = snap.MemSynced ? "一致" : "不一致（内存与磁盘事件数不同，可能正在写或已失步）";
        var contextJson = JsonSerializer.Serialize(snap.Context, WebUiJson.Options);
        var allowedText = snap.Allowed.Count == 0 ? "（空）" : string.Join(", ", snap.Allowed);

        // XSS 加固（安全审查 P0-3）：会话 id 虽有白名单，但放行集/上下文/最近事件
        // 都可能携带模型或网页投毒的内容（工具名、事件摘要）。全部过 Escape 再进 HTML ——
        // /debug 与审批 UI 同源，这里的脚本能直接 POST /api/approve 放行危险工具。
        var safeSession = WebUiEscape(snap.SessionId);
        var safeContext = WebUiEscape(contextJson);
        var safeAllowed = WebUiEscape(allowedText);
        var safeSyncText = WebUiEscape(syncText);
        var safeRecent = string.Join("<br>", snap.Recent.Select(WebUiEscape));

        return $$"""
            <!doctype html>
            <html lang="zh-CN">
            <head><meta charset="utf-8"><meta http-equiv="refresh" content="3">
            <title>WebUI · debug 信息窗</title>
            <style>
              :root { color-scheme: dark; }
              body { margin:0; padding:22px; background:#101216; color:#c8d0dc; font:13px/1.8 Consolas, monospace; }
              h1 { font:600 15px "Segoe UI", sans-serif; margin:0 0 4px; }
              .sub { color:#5c6570; font-size:11px; margin-bottom:16px; }
              table { border-collapse:collapse; margin-bottom:16px; }
              td { border:1px solid #242a33; padding:5px 12px; }
              td.k { color:#6f7787; }
              a { color:#6ea8fe; text-decoration:none; }
            </style></head>
            <body>
            <h1>WebUI debug 信息窗</h1>
            <div class="sub">每 3 秒自动刷新 · <a href="/">← 返回对话</a> · <a href="/api/debug">JSON</a></div>
            <table>
              <tr><td class="k">当前会话</td><td>{{safeSession}}</td></tr>
              <tr><td class="k">内存事件表 / 磁盘事件数</td><td>{{snap.MemEvents}} / {{snap.DiskEvents}}</td></tr>
              <tr><td class="k">内存与磁盘一致性</td><td style="color:{{syncColor}}">{{safeSyncText}}</td></tr>
              <tr><td class="k">上下文状态</td><td>{{safeContext}}</td></tr>
              <tr><td class="k">本会话放行集（P6）</td><td>{{safeAllowed}}</td></tr>
              <tr><td class="k">最近事件（8 条）</td><td>{{safeRecent}}</td></tr>
            </table>
            </body></html>
            """;
    }

    /// <summary>HTML 转义（与 LauncherServer 同款；两个服务各自持有，避免跨层引用）。</summary>
    private static string WebUiEscape(string value)
        => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace("\"", "&quot;").Replace("'", "&#39;");
}
