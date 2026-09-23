using AgentFramework.Contracts;

namespace AgentFramework.Host;

/// <summary>
/// 内置路由 · <b>转述域</b>（P5）：输入转述（澄清模式）的读取、设置与手动运行。
///
/// <para>
/// 这是 DESIGN 4.14 那个「实验性」功能的操作面 —— 默认关，开了才会用到这些端点。
/// 手动转述（<c>/api/rephrase/run</c>）是**同步**的：用户点了按钮就在等结果，
/// 没什么可后台跑的，所以这里不加 202。
/// </para>
/// </summary>
public sealed partial class WebUiServer
{
    /// <summary>注册转述域的内置端点。</summary>
    private void RegisterRephraseRoutes()
    {
        // 读设置（含默认提示词，前端拿来当占位符）
        Map(new DelegateRoute("GET", "/api/rephrase", (request, _) =>
        {
            var settings = _host.RephraseSettings;

            request.Json(new
            {
                ok = true,
                available = _host.RephraserAvailable,
                enabled = settings.Enabled,
                autoBeforeSend = settings.AutoBeforeSend,
                model = settings.Model,
                systemPrompt = settings.SystemPrompt,
                defaultPrompt = RephraseOptions.DefaultSystemPrompt,
                extraInstructions = settings.ExtraInstructions,
                temperature = settings.Temperature,
                timeoutMs = settings.TimeoutMs,
                minChars = settings.MinChars,
                maxInputChars = settings.MaxInputChars,
            });

            return ValueTask.CompletedTask;
        }));

        // 改设置
        Map(new DelegateRoute("POST", "/api/rephrase/settings", async (request, _) =>
        {
            var body = await request.ReadBodyAsync().ConfigureAwait(false);
            if (body is null)
            {
                request.Json(new { ok = false, error = "请求体不是合法 JSON" }, 400);
                return;
            }

            var settings = _host.RephraseSettings;

            // 只覆盖请求里**出现过的**字段 —— 前端可以只改一个开关
            if (TryReadBool(body.Value, "enabled", out var enabled))
            {
                settings.Enabled = enabled;
            }

            if (TryReadBool(body.Value, "autoBeforeSend", out var auto))
            {
                settings.AutoBeforeSend = auto;
            }

            var model = ReadString(body.Value, "model");
            if (!string.IsNullOrWhiteSpace(model))
            {
                settings.Model = model.Trim();
            }

            var prompt = ReadString(body.Value, "systemPrompt");
            if (!string.IsNullOrWhiteSpace(prompt))
            {
                settings.SystemPrompt = prompt;
            }

            var extra = ReadString(body.Value, "extraInstructions");
            if (extra is not null)
            {
                settings.ExtraInstructions = extra;
            }

            if (TryReadInt(body.Value, "timeoutMs", out var timeout) && timeout > 0)
            {
                settings.TimeoutMs = timeout;
            }

            if (TryReadInt(body.Value, "minChars", out var minChars) && minChars >= 0)
            {
                settings.MinChars = minChars;
            }

            _host.SaveRephraseSettings();
            request.Json(new { ok = true });
        }));

        // 手动跑一次转述（结果**只回填输入框**，此刻尚未发送，不进模型上下文）
        Map(new DelegateRoute("POST", "/api/rephrase/run", async (request, _) =>
        {
            var body = await request.ReadBodyAsync().ConfigureAwait(false);
            var text = body is null ? null : ReadString(body.Value, "text");

            if (string.IsNullOrWhiteSpace(text))
            {
                request.Json(new { ok = false, error = "text 不能为空" }, 400);
                return;
            }

            var result = await _host.RephraseAsync(text).ConfigureAwait(false);

            request.Json(new
            {
                ok = true,
                rephrased = result.Rephrased,
                text = result.Text,
                original = result.Original,
                model = result.Model,
                elapsedMs = result.ElapsedMs,
                error = result.Error,
                skipReason = result.SkipReason,
            });
        }));
    }
}
