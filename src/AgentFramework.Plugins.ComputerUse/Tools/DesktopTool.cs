using System.Globalization;
using AgentFramework.Contracts;
using AgentFramework.Plugins.ComputerUse.Desktop;

namespace AgentFramework.Plugins.ComputerUse.Tools;

/// <summary>
/// 桌面工具的公共壳：持有驱动 + 参数解析 + <b>统一接住所有失败</b>。
///
/// <para>
/// 关键设计：<see cref="InvokeAsync"/> 是模板方法 —— 它在<b>最外层</b>把整段执行包进 try，
/// 于是「参数校验抛的错」和「驱动操作抛的错」都被同一处接住。
/// 若把 try 只放在驱动调用那一句（最初就是这么写的），参数校验一旦失败就会逃出去炸掉整个回合 ——
/// 这正是本工程验证里第一条抓到的真实缺陷。
/// </para>
///
/// <para>
/// 实现约定：子类只管写 <see cref="ExecuteAsync"/>，<b>可以放心 throw <see cref="DesktopException"/></b>。
/// </para>
/// </summary>
internal abstract class DesktopTool(IDesktopDriver driver) : IToolWithRisk, ITool, IToolWithSchema
{
    protected IDesktopDriver Driver { get; } = driver;

    public abstract string Name { get; }

    /// <summary>
    /// 默认「执行」档 —— 桌面操作会改变外部世界（默认档下每次都要问）。
    /// 只读的（截图 / 屏尺寸 / 列窗 / 等待）在子类覆写为 <see cref="ToolRisk.ReadOnly"/>。
    /// </summary>
    public virtual ToolRisk Risk => ToolRisk.Execute;

    public abstract string Description { get; }

    public abstract string ParametersJsonSchema { get; }

    public async ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        try
        {
            return await ExecuteAsync(invocation, ct).ConfigureAwait(false);
        }
        catch (DesktopException ex)
        {
            return ToolResult.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            // 驱动实现里任何意料之外的异常也不该炸回合 —— 它是「工具没干成」，不是「宿主崩了」。
            return ToolResult.Fail($"{Name} 执行失败：{ex.Message}");
        }
    }

    /// <summary>工具的执行体；可以放心 throw <see cref="DesktopException"/>。</summary>
    protected abstract ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken ct);

    // ── 参数解析（工具参数一律是字符串：JSON 值在宿主层已字符串化）──

    protected static string RequireString(ToolInvocation invocation, string name, string hint)
    {
        if (invocation.Arguments.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }

        throw new DesktopException($"缺少参数 {name}（{hint}）");
    }

    protected static string? OptionalString(ToolInvocation invocation, string name)
        => invocation.Arguments.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    protected static int RequireInt(ToolInvocation invocation, string name)
    {
        if (invocation.Arguments.TryGetValue(name, out var value)
            && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        throw new DesktopException($"缺少或非法的整数参数 {name}");
    }

    protected static int IntArg(ToolInvocation invocation, string name, int fallback)
    {
        if (invocation.Arguments.TryGetValue(name, out var value)
            && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return fallback;
    }

    protected static bool BoolArg(ToolInvocation invocation, string name, bool fallback)
        => invocation.Arguments.TryGetValue(name, out var value)
           && bool.TryParse(value, out var parsed)
            ? parsed
            : fallback;

    protected static MouseButton ParseButton(string? raw) => (raw ?? "left").Trim().ToLowerInvariant() switch
    {
        "left" or "l" => MouseButton.Left,
        "right" or "r" => MouseButton.Right,
        "middle" or "m" or "center" => MouseButton.Middle,
        _ => throw new DesktopException($"未知鼠标键：{raw}（可用：left / right / middle）"),
    };

    /// <summary>解析 <c>"x,y,w,h"</c> 区域串；非法格式直接报错（不静默取全屏）。</summary>
    protected static ScreenRegion? ParseRegion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var parts = raw.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4
            || !int.TryParse(parts[0], out var x)
            || !int.TryParse(parts[1], out var y)
            || !int.TryParse(parts[2], out var w)
            || !int.TryParse(parts[3], out var h))
        {
            throw new DesktopException($"区域格式非法：{raw}（应为 \"x,y,宽,高\"，如 \"0,0,400,300\"）");
        }

        if (w <= 0 || h <= 0)
        {
            throw new DesktopException("区域宽高必须为正");
        }

        return new ScreenRegion(x, y, w, h);
    }
}
