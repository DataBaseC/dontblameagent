using System.Runtime.InteropServices;

namespace AgentFramework.Plugins.ComputerUse.Desktop;

/// <summary>
/// 按平台挑驱动。
///
/// <para>
/// 挑不出来时给 <see cref="UnsupportedDesktopDriver"/> 而不是抛异常：插件仍要能装上、
/// 能列在插件页里 —— 只是它的工具有话说（「本平台不支持」），而不是整个插件加载失败。
/// </para>
/// </summary>
public static class DesktopDriverFactory
{
    public static IDesktopDriver Create()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsDesktopDriver();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new PosixDesktopDriver(PosixFlavor.MacOS);
        }

        if (OperatingSystem.IsLinux())
        {
            return new PosixDesktopDriver(PosixFlavor.Linux);
        }

        return new UnsupportedDesktopDriver($"不支持的平台：{RuntimeInformation.OSDescription}");
    }
}

/// <summary>
/// 兜底驱动：本平台做不了电脑操作时，一切如实报告不可用。
///
/// <para>
/// 存在的意义是<b>不让模型自欺</b>：如果没有这一档，模型会看到一堆工具、逐个调用、
/// 逐个失败、反复重试 —— 而结果里一句「X 平台不支持电脑操作」能立刻把它导到别的路子上。
/// </para>
/// </summary>
public sealed class UnsupportedDesktopDriver(string reason) : IDesktopDriver
{
    public string Name => "unsupported";

    public bool IsAvailable => false;

    public string Describe() => $"电脑操作不可用：{reason}";

    public DriverCapabilities Capabilities => new(false, false, false, false, reason);

    public ScreenSize GetScreenSize() => throw new DesktopException(reason);

    public byte[] CaptureScreenshot(ScreenRegion? region) => throw new DesktopException(reason);

    public void MovePointer(int x, int y) => throw new DesktopException(reason);

    public void Click(int x, int y, MouseButton button, int count) => throw new DesktopException(reason);

    public void Drag(int fromX, int fromY, int toX, int toY, MouseButton button, int durationMs)
        => throw new DesktopException(reason);

    public void Scroll(int x, int y, int deltaX, int deltaY) => throw new DesktopException(reason);

    public void TypeText(string text) => throw new DesktopException(reason);

    public void PressKey(IReadOnlyList<string> keys) => throw new DesktopException(reason);

    public IReadOnlyList<WindowInfo> ListWindows() => throw new DesktopException(reason);

    public bool FocusWindow(string target) => throw new DesktopException(reason);

    public void OpenApp(string app) => throw new DesktopException(reason);

    public void OpenUrl(string url) => throw new DesktopException(reason);
}
