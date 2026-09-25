using System.Text.Json;
using AgentFramework.Contracts;

namespace AgentFramework.Host;

/// <summary>
/// 内置路由 · <b>对话域</b>（P5）：把消息发出去、把审批收回来、切工作模式。
///
/// <para>
/// 这三件事是界面的主循环，从前它们夹在模型配置与转述设置之间，
/// 挤在主 switch 里 —— 每次动其中一个都要在几百行里找位置。
/// </para>
/// <para>
/// 搬出来之后主分发只剩「HTTP 管道 → 路由表 → 少量尚未搬走的端点 → 404」。
/// </para>
/// </summary>
public sealed partial class WebUiServer
{
    /// <summary>
    /// 进行中/排队中的回合（HCI 优化：停止按钮）。
    /// key = turnId，value = (归属会话, 取消源)。
    /// 会话闸保证同一会话同时只有一个回合在跑，但用户可能连续发送多个排队回合 ——
    /// 「停止」按会话取消全部（正在跑 + 排队），这才是用户的心智模型。
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string SessionId, CancellationTokenSource Cts)> _turnCts = new();

    /// <summary>注册对话域的内置端点。</summary>
    private void RegisterChatRoutes()
    {
        // 发一条消息。回合在后台跑，结果通过 SSE 推给页面 —— 所以这里是 202 而不是 200。
        Map(new DelegateRoute("POST", "/api/send", async (request, ct) =>
        {
            var body = await request.ReadBodyAsync().ConfigureAwait(false);
            var text = body is null ? null : ReadString(body.Value, "text");

            if (string.IsNullOrWhiteSpace(text))
            {
                request.Json(new { ok = false, error = "text 不能为空" }, 400);
                return;
            }

            // ★ 把「回合归属」在 POST 这一刻就定下来（P1）：
            //   不只是捕获宿主（宿主现在根本不换），还要捕获**会话** ——
            //   主人完全可能在这一轮跑着的时候切去看别的会话，
            //   那时「当前会话」已经不是他按下发送的那一个了。
            var host = _host;
            var session = _host.Session;

            // HCI：回合注册 + 可取消。turnId 随 202 返回（诊断用），
            // 取消源在回合结束时必须清掉 —— 否则泄漏 CTS 且下一次 stop 会误伤。
            var turnId = Guid.NewGuid().ToString("N")[..8];
            var cts = new CancellationTokenSource();
            _turnCts[turnId] = (session.SessionId, cts);

            _ = Task.Run(async () =>
            {
                try
                {
                    await host.SendAsync(session, text, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 主动停止不是错误：给一条温和的系统帧而不是红色报错
                    Broadcast(JsonSerializer.SerializeToElement(
                        new { type = "sys", sessionId = session.SessionId, message = "⏹ 回合已停止（已产生的部分结果仍在，工具中断会在下轮自动修复）" },
                        WebUiJson.Options));
                }
                catch (Exception ex)
                {
                    Broadcast(JsonSerializer.SerializeToElement(
                        new { type = "error", sessionId = session.SessionId, message = ex.Message },
                        WebUiJson.Options));
                }
                finally
                {
                    _turnCts.TryRemove(turnId, out _);
                    cts.Dispose();

                    // 回合生命周期帧：所有页面据此恢复发送按钮 / 清忙标记。
                    // 之前前端只能猜（202 后盲等事件），现在有明确的终点信号。
                    //
                    // ★ 带上 turnId：极快的回合（工具一失败就收尾）会在 202 响应**之前**
                    //   就把这一帧推出去，前端随后收到 202 会盲目把按钮又置成「停止」——
                    //   回合明明结束了，按钮却卡住，非要手动再点一下。带上 id，
                    //   前端就能识别「这个回合已经结束了」，不再覆盖终点状态。
                    Broadcast(JsonSerializer.SerializeToElement(
                        new { type = "turn-ended", sessionId = session.SessionId, turnId },
                        WebUiJson.Options));
                }
            });

            request.Json(new { ok = true, accepted = true, turnId }, 202);
        }));

        // HCI：停止当前会话的回合（正在跑的 + 排队中的）。Esc 快捷键同款。
        Map(new DelegateRoute("POST", "/api/stop", (request, _) =>
        {
            var sessionId = _host.SessionId;
            var stopped = 0;

            foreach (var kv in _turnCts)
            {
                if (string.Equals(kv.Value.SessionId, sessionId, StringComparison.Ordinal))
                {
                    try
                    {
                        kv.Value.Cts.Cancel();
                        stopped++;
                    }
                    catch (ObjectDisposedException)
                    {
                        // 回合刚好自己结束：不算失败
                    }
                }
            }

            request.Json(stopped > 0
                ? new { ok = true, stopped }
                : new { ok = false, error = "当前会话没有正在运行的回合" },
                stopped > 0 ? 200 : 409);
            return ValueTask.CompletedTask;
        }));

        // 审批回执。P6 之后可以带上「本会话允许此工具」。
        Map(new DelegateRoute("POST", "/api/approve", async (request, ct) =>
        {
            var body = await request.ReadBodyAsync().ConfigureAwait(false);
            if (body is null)
            {
                request.Json(new { ok = false, error = "请求体不是合法 JSON" }, 400);
                return;
            }

            var id = ReadString(body.Value, "id") ?? string.Empty;
            var allow = body.Value.TryGetProperty("allow", out var allowNode)
                && allowNode.ValueKind == JsonValueKind.True;

            // ★ P6：「本会话允许此工具」—— 只有点了允许才谈得上记住。
            var remember = allow
                && body.Value.TryGetProperty("remember", out var rememberNode)
                && rememberNode.ValueKind == JsonValueKind.True;

            TaskCompletionSource<ApprovalAnswer>? completion = null;
            lock (_gate)
            {
                if (!string.IsNullOrEmpty(id))
                {
                    _pendingApprovals.Remove(id, out completion);
                }
            }

            if (completion is null)
            {
                request.Json(new { ok = false, error = "审批请求不存在或已超时" }, 404);
                return;
            }

            completion.TrySetResult(new ApprovalAnswer(allow, remember));
            request.Json(new { ok = true, allow, remember });
        }));

        // 切工作模式。模式是**全局**状态，切了要广播给所有标签页。
        Map(new DelegateRoute("POST", "/api/mode", async (request, ct) =>
        {
            var body = await request.ReadBodyAsync().ConfigureAwait(false);
            var name = body is null ? null : ReadString(body.Value, "mode");

            if (string.IsNullOrWhiteSpace(name))
            {
                request.Json(new { ok = false, error = "缺少 mode" }, 400);
                return;
            }

            var mode = name.Trim().ToLowerInvariant() switch
            {
                "chat" => AgentMode.Chat,
                "design" => AgentMode.Design,
                _ => AgentMode.Work,
            };

            // 切换不需要重启：工具照常注册，换的只是「这一轮暴露什么」
            _host.SetMode(mode);

            Broadcast(JsonSerializer.SerializeToElement(
                new { type = "mode-changed", mode = mode switch { AgentMode.Chat => "chat", AgentMode.Design => "design", _ => "work" } },
                WebUiJson.Options));

            request.Json(new { ok = true, mode = ModeStatus() });
        }));

        // ── 模式目录（新建会话选择器的数据源；含插件注册的自定义档位）──
        Map(new DelegateRoute("GET", "/api/modes", (request, _) =>
        {
            request.Json(new
            {
                ok = true,
                modes = AgentModes.Available.Select(p => new
                {
                    id = p.CustomId ?? AgentModes.IdOf(p.Mode),
                    name = p.Name,
                    summary = p.SystemPromptSuffix is null ? null : p.SystemPromptSuffix.Split('。')[0] + "。",
                    exposesTools = p.ExposesTools,
                    custom = p.CustomId is not null,
                }),
            });
            return ValueTask.CompletedTask;
        }));

        // ── G2 技能系统：列表与启停（回合边界生效）──────────────
        Map(new DelegateRoute("GET", "/api/skills", (request, ct) =>
        {
            request.Json(new
            {
                ok = true,
                skills = _host.Skills.Select(s => new
                {
                    id = s.Id,
                    name = s.Name,
                    description = s.Description,
                    tools = s.Tools,
                    enabled = _host.EnabledSkills.Contains(s.Name),
                    // 创意工坊元数据（旧清单无这些字段 → null/空，前端降级展示）
                    source = s.Source,
                    version = s.Version,
                    author = s.Author,
                    tags = s.Tags,
                }),
                enabledSkills = _host.EnabledSkills,
            });
            return ValueTask.CompletedTask;
        }));

        Map(new DelegateRoute("POST", "/api/skills/toggle", async (request, ct) =>
        {
            var body = await request.ReadBodyAsync().ConfigureAwait(false);
            var name = body is null ? null : ReadString(body.Value, "name");
            var enabled = body is not null
                && body.Value.TryGetProperty("enabled", out var en)
                && en.ValueKind == System.Text.Json.JsonValueKind.True;

            if (string.IsNullOrWhiteSpace(name) || _host.Skills.All(x => x.Name != name))
            {
                request.Json(new { ok = false, error = "技能不存在" }, 404);
                return;
            }

            _host.SetSkillEnabled(name, enabled);
            request.Json(new { ok = true, name, enabled });
        }));

        // ── 工具包（可见性的最小单位）────────────────────────────
        // 「装了什么」与「这一轮给模型看什么」解耦的界面出口：
        // 一排开关，按这段活的需要开合，省的是每轮的 schema token，也是挑错的机会。
        Map(new DelegateRoute("GET", "/api/toolsets", (request, _) =>
        {
            request.Json(new
            {
                ok = true,
                toolsets = _host.Toolsets.Select(v => new
                {
                    id = v.Id,
                    name = v.Name,
                    description = v.Description,
                    tools = v.Tools,
                    enabled = v.Enabled,
                    locked = v.Protected,
                    eager = v.Eager,
                    source = v.Source,
                }),
            });
            return ValueTask.CompletedTask;
        }));

        Map(new DelegateRoute("POST", "/api/toolsets/toggle", async (request, ct) =>
        {
            var body = await request.ReadBodyAsync().ConfigureAwait(false);
            var id = body is null ? null : ReadString(body.Value, "id");
            var enabled = body is not null && TryReadBool(body.Value, "enabled", out var flag) && flag;

            if (string.IsNullOrWhiteSpace(id) || _host.Toolsets.All(x => x.Id != id))
            {
                request.Json(new { ok = false, error = "工具包不存在" }, 404);
                return;
            }

            // 保留包会被 SetToolsetEnabled 拒掉 —— 这里如实回原因，不假装成功
            if (!_host.SetToolsetEnabled(id, enabled))
            {
                request.Json(new { ok = false, error = "这是保留包，不能关闭" }, 400);
                return;
            }

            request.Json(new { ok = true, id, enabled });
        }));

        // ── 记忆管理面板（降级/升级/合并/清扫）──────────────
        Map(new DelegateRoute("GET", "/api/memory/list", async (request, ct) =>
        {
            if (_host.Memory.Kind == "none")
            {
                request.Json(new { ok = false, error = "记忆未启用" }, 400);
                return;
            }

            // 列表按当前会话的项目作用域取；必须 await —— .Result 会把同步上下文卡死。
            request.Json(new { ok = true, memory = await MemoryListPayloadAsync(_host, ct).ConfigureAwait(false) });
        }));

        Map(new DelegateRoute("POST", "/api/memory/action", async (request, ct) =>
        {
            var body = await request.ReadBodyAsync().ConfigureAwait(false);
            var action = body is null ? null : ReadString(body.Value, "action");
            var scope = body is null ? null : ReadString(body.Value, "scope");
            var id = body is null ? null : ReadString(body.Value, "id");

            if (_host.Memory.Kind == "none")
            {
                request.Json(new { ok = false, error = "记忆未启用" }, 400);
                return;
            }

            if (string.IsNullOrWhiteSpace(scope) || string.IsNullOrWhiteSpace(id))
            {
                request.Json(new { ok = false, error = "缺少 scope 或 id" }, 400);
                return;
            }

            // v3.5 审查 P2：项目 scope 必须**等于本会话的项目作用域** ——
            // 原实现只查 "project:" 前缀，于是前端传 project:../../x 就能把记忆写到工作区之外。
            // 面板本来就只该操作「全局」与「本项目」两本账，所以这里精确比对。
            var sessionProjectScope = MemoryScope.ProjectFor(_host.Session?.ProjectDir ?? _host.Options.WorkspaceRoot);
            if (scope != MemoryScope.Global
                && !string.Equals(scope, sessionProjectScope, StringComparison.Ordinal))
            {
                request.Json(new { ok = false, error = "scope 必须是 global 或本会话的项目作用域" }, 400);
                return;
            }

            switch (action)
            {
                case "pin":
                {
                    var result = await _host.Memory.RecordHitAsync(scope, id, delta: 0, markImportant: true, ct).ConfigureAwait(false);
                    request.Json(result is null
                        ? new { ok = false, error = "记忆不在主视图（先恢复再置顶）" }
                        : new { ok = true, action = "pin" });
                    break;
                }

                case "archive":
                {
                    var result = await _host.Memory.ArchiveAsync(scope, id, "user", ct).ConfigureAwait(false);
                    request.Json(result is null
                        ? new { ok = false, error = "记忆不在主视图（可能已归档）" }
                        : new { ok = true, action = "archive" });
                    break;
                }

                case "restore":
                {
                    var result = await _host.Memory.RestoreAsync(scope, id, "user", ct).ConfigureAwait(false);
                    request.Json(result is null
                        ? new { ok = false, error = "记忆不在归档层" }
                        : new { ok = true, action = "restore" });
                    break;
                }

                case "forget":
                {
                    var result = await _host.Memory.RetractAsync(scope, id, "user", ct).ConfigureAwait(false);
                    request.Json(result is null
                        ? new { ok = false, error = "记忆不存在或已撤销过" }
                        : new { ok = true, action = "forget" });
                    break;
                }

                default:
                    request.Json(new { ok = false, error = "未知 action（pin/archive/restore/forget）" }, 400);
                    break;
            }
        }));

        Map(new DelegateRoute("POST", "/api/memory/merge", async (request, ct) =>
        {
            var body = await request.ReadBodyAsync().ConfigureAwait(false);
            var scope = body is null ? null : ReadString(body.Value, "scope");
            var text = body is null ? null : ReadString(body.Value, "text");
            var ids = new List<string>();

            if (body is not null && body.Value.TryGetProperty("ids", out var idsEl)
                && idsEl.ValueKind == JsonValueKind.Array)
            {
                ids.AddRange(idsEl.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString()!));
            }

            if (_host.Memory.Kind == "none")
            {
                request.Json(new { ok = false, error = "记忆未启用" }, 400);
                return;
            }

            // v3.5 审查 P2：与 /api/memory/action 同款 —— 项目 scope 必须等于本会话的项目作用域。
            var mergeProjectScope = MemoryScope.ProjectFor(_host.Session?.ProjectDir ?? _host.Options.WorkspaceRoot);
            if (string.IsNullOrWhiteSpace(scope)
                || (scope != MemoryScope.Global
                    && !string.Equals(scope, mergeProjectScope, StringComparison.Ordinal))
                || ids.Count < 2 || string.IsNullOrWhiteSpace(text))
            {
                request.Json(new { ok = false, error = "合并需要 scope + ≥2 个 ids + 合并后的正文" }, 400);
                return;
            }

            try
            {
                var merged = await _host.Memory.MergeAsync(scope, ids, text!, null, "user", ct).ConfigureAwait(false);
                request.Json(new { ok = true, merged = new { id = merged.Id, score = merged.Score } });
            }
            catch (ArgumentException ex)
            {
                request.Json(new { ok = false, error = ex.Message }, 400);
            }
        }));

        Map(new DelegateRoute("POST", "/api/memory/sweep", async (request, ct) =>
        {
            var body = await request.ReadBodyAsync().ConfigureAwait(false);
            var policy = SweepPolicy.Default;
            var dryRun = true;

            if (body is not null)
            {
                var halfLife = policy.HalfLifeDays;
                var minHeat = policy.MinEffectiveScore;

                // 可选：前端想调半衰期/阈值就传；不传则用默认（30 天半衰期、阈值 0.5）。
                if (body.Value.TryGetProperty("halfLifeDays", out var h) && h.ValueKind == JsonValueKind.Number)
                {
                    halfLife = Math.Max(0.1, h.GetDouble());
                }

                if (body.Value.TryGetProperty("minEffectiveScore", out var s) && s.ValueKind == JsonValueKind.Number)
                {
                    minHeat = Math.Max(0, s.GetDouble());
                }

                policy = new SweepPolicy { HalfLifeDays = halfLife, MinEffectiveScore = minHeat };

                if (body.Value.TryGetProperty("dryRun", out var dr) && dr.ValueKind == JsonValueKind.False)
                {
                    dryRun = false;
                }
            }

            if (_host.Memory.Kind == "none")
            {
                request.Json(new { ok = false, error = "记忆未启用" }, 400);
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var results = new List<object>();
            var total = 0;

            // 巩固范围：全局 + 当前会话的项目作用域（多项目下不动别的项目的账）
            var projectScope = MemoryScope.ProjectFor(_host.Session?.ProjectDir ?? _host.Options.WorkspaceRoot);
            foreach (var scope in (string[]) [MemoryScope.Global, projectScope])
            {
                // 预览与执行共用存储层同一份判据（按使用频率衰减），杜绝口径漂移。
                var count = dryRun
                    ? (await _host.Memory.PreviewSweepAsync(scope, now, policy, ct).ConfigureAwait(false)).Count
                    : await _host.Memory.SweepAsync(scope, now, policy, "system", ct).ConfigureAwait(false);
                results.Add(new { scope, count });
                total += count;
            }

            request.Json(new { ok = true, dryRun, total, results });
        }));

        // ── 创意工坊：导入技能（M0 地基）──────────────────────
        // 白名单型技能是纯声明包（工具白名单 + 提示词），导入永远不引入代码 ——
        // 这是工坊能开放分享的安全前提。导入 = 拷贝到 {workspace}/workshop/{name}/ + 重扫。
        Map(new DelegateRoute("POST", "/api/skills/import", async (request, ct) =>
        {
            var body = await request.ReadBodyAsync().ConfigureAwait(false);
            var sourcePath = body is null ? null : ReadString(body.Value, "path");

            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                request.Json(new { ok = false, error = "缺少 path（技能目录，须含 skill.json）" }, 400);
                return;
            }

            var manifestPath = Path.Combine(sourcePath, "skill.json");
            if (!Directory.Exists(sourcePath) || !File.Exists(manifestPath))
            {
                request.Json(new { ok = false, error = "目录不存在或缺少 skill.json" }, 400);
                return;
            }

            var skill = SkillLoader.TryReadManifest(manifestPath);
            if (skill is null)
            {
                request.Json(new { ok = false, error = "skill.json 解析失败或缺少 name" }, 400);
                return;
            }

            // 目录名用清单 name 的安全形式（与会话 id 同一白名单）—— 工坊条目的目录身份
            var dirName = skill.Name.Trim();
            if (!IsValidSessionId(dirName))
            {
                request.Json(new { ok = false, error = $"技能名「{dirName}」含不安全字符（只允许字母数字 - _ ，≤64 字符）" }, 400);
                return;
            }

            // 工具白名单必须引用已注册工具 —— 工坊包无法凭空造出新工具（也不会有代码可跑）
            var registered = _host.ToolNames.ToHashSet(StringComparer.Ordinal);
            var unknown = skill.Tools.Where(t => !registered.Contains(t, StringComparer.Ordinal)).ToList();
            if (unknown.Count > 0)
            {
                request.Json(new { ok = false, error = $"工具白名单引用了未注册工具：{string.Join(", ", unknown)}" }, 400);
                return;
            }

            // 内置优先规则：与 skills/ 自写技能重名时，导入会被忽略 —— 明确拒绝而不是静默无效
            if (_host.Skills.Any(s => s.Name == skill.Name && s.Source == "builtin"))
            {
                request.Json(new { ok = false, error = $"与内置技能「{skill.Name}」重名：工坊不覆盖本地创作，请先移除内置版或给导入包改名" }, 409);
                return;
            }

            // v3.5 审查 P2：与**已导入的** workshop 技能重名同样要拒绝。
            // 原实现会 Directory.Delete 后整体替换，而 enabled 集合按名保留 ——
            // 新包的工具白名单/提示词下一轮就静默生效，用户毫无察觉，与「导入不覆盖」的安全观相悖。
            if (_host.Skills.Any(s => s.Name == skill.Name && s.Source == "workshop"))
            {
                request.Json(new { ok = false, error = $"工作坊已存在同名技能「{skill.Name}」：请先删除它，或给导入包改名（不静默覆盖）" }, 409);
                return;
            }

            var targetDir = Path.Combine(_host.WorkspaceRoot, SkillLoader.WorkshopDirName, dirName);
            try
            {
                CopyDirectory(sourcePath, targetDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // v3.5 审查：500 不回传 ex.Message（可能含绝对路径）—— 详情进宿主日志，对外统一话术。
                Console.Error.WriteLine($"[skills/import] 拷贝失败：{ex}");
                request.Json(new { ok = false, error = "拷贝失败（详情见宿主日志）" }, 500);
                return;
            }

            _host.ReloadSkills();

            var imported = _host.Skills.FirstOrDefault(s => s.Name == skill.Name);
            request.Json(new { ok = true, imported = imported is null ? null : new
            {
                name = imported.Name,
                source = imported.Source,
                tools = imported.Tools,
            } });
        }));
    }

    /// <summary>
    /// 记忆面板的数据载荷：全局层 + **当前会话的项目作用域**（多项目隔离）。
    /// 项目作用域 id 内嵌项目目录 —— 面板看到的永远是"这个会话所在项目"的账。
    /// </summary>
    private static async Task<object> MemoryListPayloadAsync(AgentHost host, CancellationToken ct = default)
    {
        var projectScope = MemoryScope.ProjectFor(host.Session?.ProjectDir ?? host.Options.WorkspaceRoot);
        var projectDir = host.Session?.ProjectDir ?? host.Options.WorkspaceRoot;

        // 全部 await —— 用 .Result 是 sync-over-async，在 ASP/同步上下文上会直接卡死。
        var active = await host.Memory.LoadAsync(projectScope, 500, ct).ConfigureAwait(false);
        var archived = await host.Memory.LoadArchivedAsync(projectScope, 500, ct).ConfigureAwait(false);
        var globalActive = await host.Memory.LoadAsync(MemoryScope.Global, 500, ct).ConfigureAwait(false);
        var globalArchived = await host.Memory.LoadArchivedAsync(MemoryScope.Global, 500, ct).ConfigureAwait(false);

        static object MemItem(MemoryEntry e) => new
        {
            id = e.Id,
            text = e.Text,
            score = e.Score,
            // 降档判据是「使用频率」：把次数与最近使用时间也带上，面板才讲得清
            // 「这条为什么该睡 / 为什么该留」。
            useCount = e.UseCount,
            lastUsedAt = e.LastUsedAt,
            important = e.IsImportant,
            slot = e.Slot,
            created = e.CreatedAt,
            tags = e.Tags,
        };

        return new
        {
            projectDir,
            scope = projectScope,
            project = new
            {
                active = active.Select(MemItem),
                archived = archived.Select(MemItem),
            },
            global = new
            {
                active = globalActive.Select(MemItem),
                // v3.5 审查 P2：全局层也要有归档层 —— 原先恒为空数组，
                // 于是被清扫归档的全局记忆既进不了索引卡、又不在面板里，成了 UI 孤儿（无法恢复）。
                archived = globalArchived.Select(MemItem),
            },
        };
    }

    /// <summary>递归拷贝目录（工坊导入用；目标存在则整体替换 —— 同名导入视为更新）。</summary>
    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        sourceDir = Path.GetFullPath(sourceDir);
        targetDir = Path.GetFullPath(targetDir);

        // 目标必须是 workspace/workshop 下新建的路径，但禁止源/目标嵌套（自拷贝死循环）
        if (targetDir.StartsWith(sourceDir + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || sourceDir.StartsWith(targetDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new IOException("源目录与目标目录嵌套");
        }

        if (Directory.Exists(targetDir))
        {
            Directory.Delete(targetDir, recursive: true);
        }

        Directory.CreateDirectory(targetDir);
        // v3.5 审查 P2：不用 SearchOption.AllDirectories —— 它会**跟随符号链接**，
        // 一个精心构造的分享包（目录链接指向盘根）就能把整盘拷进来，或撞上环状链接空转。
        foreach (var file in EnumerateFilesSafe(sourceDir))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            var target = Path.Combine(targetDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    /// <summary>
    /// 递归枚举文件，**不跟随符号链接**（v3.5 审查 P2）：
    /// <c>SearchOption.AllDirectories</c> 会钻进目录联接 / 符号链接 ——
    /// 一个精心构造的分享包就能把整盘拷进来，或者撞上环状链接空转。
    /// </summary>
    private static IEnumerable<string> EnumerateFilesSafe(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            yield return file;
        }

        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            // LinkTarget 非空 = 符号链接 / 目录联接 → 不跟随
            if (new DirectoryInfo(sub).LinkTarget is not null)
            {
                continue;
            }

            foreach (var nested in EnumerateFilesSafe(sub))
            {
                yield return nested;
            }
        }
    }
}