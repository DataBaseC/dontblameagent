namespace AgentFramework.Plugins.ComputerUse.Desktop;

/// <summary>
/// 驱动抛出的操作失败（权限不足 / 后端不可用 / 参数非法）。
///
/// <para>
/// 用异常而不是错误码：驱动实现里一处 <c>throw</c> 比每条路径都 return 错误码干净得多，
/// 而工具层用<b>一个</b> catch 统一转成 <c>ToolResult.Fail</c> —— 于是「不炸回合」的约定
/// 只需要在一处保证。
/// </para>
/// </summary>
public sealed class DesktopException(string message) : Exception(message);

/// <summary>鼠标键。</summary>
public enum MouseButton
{
    Left,
    Right,
    Middle,
}

/// <summary>屏幕尺寸（像素）。</summary>
public readonly record struct ScreenSize(int Width, int Height);

/// <summary>屏幕区域（截屏用；坐标为像素）。</summary>
public readonly record struct ScreenRegion(int X, int Y, int Width, int Height);

/// <summary>一个可见窗口。</summary>
public sealed record WindowInfo(string Title, long Handle, int X, int Y, int Width, int Height, bool IsForeground);

/// <summary>
/// 驱动的能力自述。
///
/// <para>
/// 为什么要按能力而不是按平台判断：同一个平台上装没装 <c>xdotool</c>、
/// 有没有辅助功能权限，决定了「能做什么」。工具据此说清「这条命令为什么不行」，
/// 而不是让模型反复重试一个注定失败的调用。
/// </para>
/// </summary>
public sealed record DriverCapabilities(
    bool Screenshot,
    bool Pointer,
    bool Keyboard,
    bool Windows,
    string Note);

/// <summary>
/// 桌面驱动：把「操作用户的电脑」做成<b>可替换的后端</b>（学 dsh：sandbox/subprocess 本身是 provider）。
///
/// <para>
/// 平台实现各自独立（Windows 走 user32/gdi32 P/Invoke；macOS / Linux 走系统命令），
/// 找不到依赖时退化成 <see cref="UnsupportedDesktopDriver"/> —— 一律如实报告不可用，
/// 绝不用「近似模拟」糊弄（那会让模型以为点到了，其实没点）。
/// </para>
///
/// <para>
/// <b>实现约定</b>：失败一律 <c>throw DesktopException</c>，不返回静默失败。
/// </para>
/// </summary>
public interface IDesktopDriver
{
    /// <summary>稳定 id：<c>windows</c> / <c>macos</c> / <c>linux-x11</c> / <c>unsupported</c>。</summary>
    string Name { get; }

    bool IsAvailable { get; }

    /// <summary>一句话说明这一档到底能做什么、缺什么（进工具结果与诊断）。</summary>
    string Describe();

    DriverCapabilities Capabilities { get; }

    ScreenSize GetScreenSize();

    /// <summary>截屏返回 PNG 字节；<paramref name="region"/> 为空表示整屏。</summary>
    byte[] CaptureScreenshot(ScreenRegion? region);

    void MovePointer(int x, int y);

    void Click(int x, int y, MouseButton button, int count);

    void Drag(int fromX, int fromY, int toX, int toY, MouseButton button, int durationMs);

    void Scroll(int x, int y, int deltaX, int deltaY);

    /// <summary>输入任意 Unicode 文本。</summary>
    void TypeText(string text);

    /// <summary>按下并释放一组键（组合键，如 <c>["ctrl","shift","t"]</c>）。</summary>
    void PressKey(IReadOnlyList<string> keys);

    IReadOnlyList<WindowInfo> ListWindows();

    /// <summary>按标题子串或句柄（十进制/<c>0x…</c>）聚焦窗口；找不到返回 false。</summary>
    bool FocusWindow(string target);

    void OpenApp(string app);

    void OpenUrl(string url);
}
