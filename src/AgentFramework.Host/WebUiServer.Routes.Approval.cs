using System.Text.Json;

namespace AgentFramework.Host;

/// <summary>
/// 内置路由 · <b>审批档位域</b>（任务 5）。
///
/// <para>
/// 「总有一档在生效 + 随时能收紧」：读取当前档位与可选档位表；写回即切档（活配置）。
/// 切档走 <see cref="AgentHost.SetApprovalTier"/>，策略委托随之重建 —— 下一次工具调用立即生效。
/// </para>
/// </summary>
public sealed partial class WebUiServer
{
    /// <summary>注册审批档位域的内置端点。</summary>
    private void RegisterApprovalRoutes()
    {
        Map(new DelegateRoute("GET", "/api/approval", (request, _) =>
        {
            request.Json(ApprovalPayload());
            return ValueTask.CompletedTask;
        }));

        Map(new DelegateRoute("POST", "/api/approval", async (request, _) =>
        {
            var body = await request.ReadBodyAsync().ConfigureAwait(false);
            var tierRaw = body is null ? null : ReadString(body.Value, "tier");
            if (string.IsNullOrWhiteSpace(tierRaw))
            {
                request.Json(new { ok = false, error = "缺少 tier（ask / plan / build / yolo）" }, 400);
                return;
            }

            _host.SetApprovalTier(ApprovalTiers.Parse(tierRaw));
            request.Json(ApprovalPayload());
        }));
    }

    private object ApprovalPayload()
    {
        var tier = _host.Options.ApprovalTier;

        return new
        {
            ok = true,
            tier = ApprovalTiers.Id(tier),
            displayName = ApprovalTiers.DisplayName(tier),
            // Yolo 档：界面据此常驻横幅（有明显风险提示 + 一键收回）
            yolo = tier == ApprovalTier.Yolo,
            tiers = new[]
            {
                new { id = "ask", name = "逐项确认", desc = "每次敏感操作都问（默认）" },
                new { id = "plan", name = "先计划后执行", desc = "写 / 执行类批准一次，本回合同类放行" },
                new { id = "build", name = "常规放行", desc = "常规读写与执行自动放行，仅高危再问" },
                new { id = "yolo", name = "全盘托管", desc = "全部自动放行（有横幅 + 一键收回）" },
            },
        };
    }
}
