using System.Text.Json;
using AgentFramework.Contracts;

namespace AgentFramework.Host;

/// <summary>
/// 内置路由 · <b>模型域</b>（P5）：探测端点、增删端点、切换当前模型。
///
/// <para>
/// 这一域有个共同点：凡是**改动**的端点都要广播 <c>models-changed</c> ——
/// 模型是全局状态，一个标签页换了，别的标签页的状态栏也得跟着变。
/// </para>
/// <para>
/// 换模型**不重启**（DESIGN 4.18）：换的是一根引用，装配原样不动。
/// </para>
/// </summary>
public sealed partial class WebUiServer
{
    /// <summary>注册模型域的内置端点。</summary>
    private void RegisterModelRoutes()
    {
        // 当前模型清单（含每个端点的可用状态）
        Map(new DelegateRoute("GET", "/api/models", (request, _) =>
        {
            request.Json(ModelsStatus());
            return ValueTask.CompletedTask;
        }));

        // 探测端点：连一下，看能不能用、有哪些模型
        Map(new DelegateRoute("POST", "/api/models/probe", async (request, _) =>
        {
            var body = await request.ReadBodyAsync().ConfigureAwait(false);
            if (body is null)
            {
                request.Json(new { ok = false, error = "缺少请求体" }, 400);
                return;
            }

            var probe = await ProbeModelsAsync(body.Value, CancellationToken.None).ConfigureAwait(false);

            request.Json(new
            {
                ok = probe.Ok,
                kind = probe.Kind,
                message = probe.Message,
                models = probe.Models,
            });
        }));

        // 保存一个端点
        Map(new DelegateRoute("POST", "/api/models/provider", async (request, _) =>
        {
            var body = await request.ReadBodyAsync().ConfigureAwait(false);
            if (body is null)
            {
                request.Json(new { ok = false, error = "缺少请求体" }, 400);
                return;
            }

            var (saved, error) = SaveProvider(body.Value);
            if (!saved)
            {
                request.Json(new { ok = false, error }, 400);
                return;
            }

            Broadcast(JsonSerializer.SerializeToElement(new { type = "models-changed" }, WebUiJson.Options));
            request.Json(new { ok = true, models = ModelsStatus() });
        }));

        // 换当前模型
        Map(new DelegateRoute("POST", "/api/models/active", async (request, _) =>
        {
            var body = await request.ReadBodyAsync().ConfigureAwait(false);
            var providerId = body is null ? null : ReadString(body.Value, "providerId");
            var modelId = body is null ? null : ReadString(body.Value, "modelId");

            if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(modelId))
            {
                request.Json(new { ok = false, error = "缺少 providerId 或 modelId" }, 400);
                return;
            }

            _host.SwitchModel(providerId.Trim(), modelId.Trim());

            Broadcast(JsonSerializer.SerializeToElement(new { type = "models-changed" }, WebUiJson.Options));
            request.Json(new { ok = true, models = ModelsStatus() });
        }));

        // 删掉一个端点
        Map(new DelegateRoute("POST", "/api/models/remove", async (request, _) =>
        {
            var body = await request.ReadBodyAsync().ConfigureAwait(false);
            var providerId = body is null ? null : ReadString(body.Value, "providerId");
            var settings = _host.ModelSettings;

            if (settings is null || _host.Models is null)
            {
                request.Json(new { ok = false, error = "这次启动没有启用模型配置" }, 400);
                return;
            }

            if (string.IsNullOrWhiteSpace(providerId)
                || settings.FindProvider(providerId.Trim()) is null)
            {
                request.Json(new { ok = false, error = "端点不存在" }, 404);
                return;
            }

            var id = providerId.Trim();
            settings.Providers.RemoveAll(p => string.Equals(p.Id, id, StringComparison.Ordinal));

            // 删掉的正好是正在用的那个 → 顺位到剩下的第一个，别让当前模型悬空
            if (settings.Active is not null
                && string.Equals(settings.Active.ProviderId, id, StringComparison.Ordinal))
            {
                var first = settings.Providers.FirstOrDefault();
                settings.Active = first is null
                    ? null
                    : new ActiveModelRef
                    {
                        ProviderId = first.Id,
                        ModelId = first.Models.FirstOrDefault()?.Id ?? string.Empty,
                    };
            }

            _host.ApplyModelSettings(settings);

            Broadcast(JsonSerializer.SerializeToElement(new { type = "models-changed" }, WebUiJson.Options));
            request.Json(new { ok = true, models = ModelsStatus() });
        }));
    }
}
