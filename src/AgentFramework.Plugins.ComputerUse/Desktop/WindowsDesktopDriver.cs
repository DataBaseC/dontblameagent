using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace AgentFramework.Plugins.ComputerUse.Desktop;

/// <summary>
/// Windows 桌面驱动：<c>user32</c>（输入 / 窗口）+ GDI+（截屏）。
///
/// <para>
/// 注入统一走 <c>SendInput</c> 而不是老的 <c>mouse_event</c> / <c>keybd_event</c>：
/// 前者是同一个输入队列、能与真实硬件事件交织，且能干净地送 Unicode（文本输入不依赖键盘布局）。
/// </para>
///
/// <para>
/// <b>边界（如实说明）</b>：
/// <list type="bullet">
///   <item>截屏只覆盖<b>主屏</b>（多屏拼接不做 —— 那会让坐标语义变复杂）；</item>
///   <item>坐标为截图像素。若宿主进程非 DPI 感知，Windows 会把两者一起虚拟化，坐标仍然一致；</item>
///   <item><b>UAC 提升过的窗口点不进、打不字</b>（Windows 的 UIPI 限制），如实报错而不是假装成功。</item>
/// </list>
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDesktopDriver : IDesktopDriver
{
    public string Name => "windows";

    public bool IsAvailable => OperatingSystem.IsWindows();

    public string Describe() =>
        "Windows 桌面操作：主屏截屏、鼠标移动/点击/拖拽/滚轮、Unicode 文本与组合键注入、窗口枚举与聚焦、启动应用/打开链接。";

    public DriverCapabilities Capabilities => new(
        Screenshot: true, Pointer: true, Keyboard: true, Windows: true,
        Note: "截屏为主屏像素，坐标即截图像素；管理员（UAC 提升）窗口不可注入。");

    public ScreenSize GetScreenSize()
    {
        var w = GetSystemMetrics(SM_CXSCREEN);
        var h = GetSystemMetrics(SM_CYSCREEN);
        if (w <= 0 || h <= 0)
        {
            throw new DesktopException("取屏幕尺寸失败");
        }

        return new ScreenSize(w, h);
    }

    public byte[] CaptureScreenshot(ScreenRegion? region)
    {
        var screen = GetScreenSize();
        var r = region ?? new ScreenRegion(0, 0, screen.Width, screen.Height);
        if (r.Width <= 0 || r.Height <= 0)
        {
            throw new DesktopException("截屏区域尺寸非法");
        }

        try
        {
            using var bitmap = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.CopyFromScreen(r.X, r.Y, 0, 0, new Size(r.Width, r.Height), CopyPixelOperation.SourceCopy);
            }

            using var ms = new MemoryStream();
            bitmap.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        catch (Exception ex) when (ex is not DesktopException)
        {
            throw new DesktopException($"截屏失败：{ex.Message}（无图形会话 / 权限不足？）");
        }
    }

    public void MovePointer(int x, int y)
    {
        if (!SetCursorPos(x, y))
        {
            throw new DesktopException($"移动指针失败（Win32 {Marshal.GetLastWin32Error()}）");
        }
    }

    public void Click(int x, int y, MouseButton button, int count)
    {
        MovePointer(x, y);

        var (down, up) = button switch
        {
            MouseButton.Right => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
            MouseButton.Middle => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
            _ => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP),
        };

        for (var i = 0; i < Math.Max(1, count); i++)
        {
            SendMouse(down, 0);
            SendMouse(up, 0);
            Thread.Sleep(30);
        }
    }

    public void Drag(int fromX, int fromY, int toX, int toY, MouseButton button, int durationMs)
    {
        var down = button == MouseButton.Right ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_LEFTDOWN;
        var up = button == MouseButton.Right ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_LEFTUP;

        MovePointer(fromX, fromY);
        Thread.Sleep(60);
        SendMouse(down, 0);

        // 分步移动：很多拖拽目标靠中间的 mousemove 判定「拖到了」，一步到位会被当成瞬移。
        const int steps = 12;
        var stepDelay = Math.Max(1, durationMs / steps);
        for (var i = 1; i <= steps; i++)
        {
            SetCursorPos(fromX + (toX - fromX) * i / steps, fromY + (toY - fromY) * i / steps);
            Thread.Sleep(stepDelay);
        }

        SendMouse(up, 0);
    }

    public void Scroll(int x, int y, int deltaX, int deltaY)
    {
        MovePointer(x, y);
        if (deltaY != 0)
        {
            SendMouse(MOUSEEVENTF_WHEEL, deltaY * WHEEL_DELTA);
        }

        if (deltaX != 0)
        {
            SendMouse(MOUSEEVENTF_HWHEEL, deltaX * WHEEL_DELTA);
        }
    }

    public void TypeText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        // 逐 UTF-16 单元送 KEYEVENTF_UNICODE：与键盘布局无关，中文/emoji 都能进。
        foreach (var ch in text)
        {
            SendKeyboard(0, ch, KEYEVENTF_UNICODE);
            SendKeyboard(0, ch, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP);
        }
    }

    public void PressKey(IReadOnlyList<string> keys)
    {
        if (keys.Count == 0)
        {
            throw new DesktopException("组合键为空");
        }

        // 修饰键先按、最后松（顺序反了会变成「按 ctrl 时 c 已松开」的怪状态）。
        var modifiers = new List<ushort>();
        var mains = new List<ushort>();
        foreach (var key in keys)
        {
            var vk = ToVirtualKey(key) ?? throw new DesktopException($"无法映射到 Windows 虚拟键：{key}");
            if (KeyNames.IsModifier(key))
            {
                modifiers.Add(vk);
            }
            else
            {
                mains.Add(vk);
            }
        }

        foreach (var vk in modifiers)
        {
            SendKeyboard(vk, 0, 0);
        }

        foreach (var vk in mains)
        {
            SendKeyboard(vk, 0, 0);
            SendKeyboard(vk, 0, KEYEVENTF_KEYUP);
        }

        for (var i = modifiers.Count - 1; i >= 0; i--)
        {
            SendKeyboard(modifiers[i], 0, KEYEVENTF_KEYUP);
        }
    }

    public IReadOnlyList<WindowInfo> ListWindows()
    {
        var foreground = GetForegroundWindow();
        var list = new List<WindowInfo>();

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd))
            {
                return true;
            }

            var len = GetWindowTextLength(hWnd);
            if (len <= 0)
            {
                return true;
            }

            var sb = new StringBuilder(len + 1);
            GetWindowText(hWnd, sb, sb.Capacity);

            if (!GetWindowRect(hWnd, out var rect))
            {
                return true;
            }

            list.Add(new WindowInfo(
                sb.ToString(),
                hWnd.ToInt64(),
                rect.Left,
                rect.Top,
                rect.Right - rect.Left,
                rect.Bottom - rect.Top,
                hWnd == foreground));
            return true;
        }, IntPtr.Zero);

        return list;
    }

    public bool FocusWindow(string target)
    {
        var handle = ResolveHandle(target);
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        // 还原 + 置前；Windows 会因「前台锁定」拒绝 SetForegroundWindow，如实返回 false。
        ShowWindow(handle, SW_RESTORE);
        var ok = SetForegroundWindow(handle);
        if (!ok)
        {
            // 一次常见的补救：先 attach 到当前前台线程再置前。
            var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            var targetThread = GetWindowThreadProcessId(handle, out _);
            if (foregroundThread != targetThread)
            {
                if (AttachThreadInput(foregroundThread, targetThread, true))
                {
                    ok = SetForegroundWindow(handle);
                    AttachThreadInput(foregroundThread, targetThread, false);
                }
            }
        }

        return ok;
    }

    public void OpenApp(string app)
    {
        if (string.IsNullOrWhiteSpace(app))
        {
            throw new DesktopException("应用名不能为空");
        }

        try
        {
            Process.Start(new ProcessStartInfo(app) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            throw new DesktopException($"启动应用失败：{ex.Message}");
        }
    }

    public void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new DesktopException("URL 不能为空");
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            throw new DesktopException($"打开链接失败：{ex.Message}");
        }
    }

    // ── 内部 ───────────────────────────────────────────────

    private IntPtr ResolveHandle(string target)
    {
        // 支持句柄（十进制 / 0x 十六进制）
        if (target.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && long.TryParse(target[2..], System.Globalization.NumberStyles.HexNumber, null, out var hex))
        {
            return new IntPtr(hex);
        }

        if (long.TryParse(target, out var dec))
        {
            return new IntPtr(dec);
        }

        // 否则按标题子串匹配（首个命中）。
        var found = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd))
            {
                return true;
            }

            var len = GetWindowTextLength(hWnd);
            if (len <= 0)
            {
                return true;
            }

            var sb = new StringBuilder(len + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            if (sb.ToString().Contains(target, StringComparison.OrdinalIgnoreCase))
            {
                found = hWnd;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }

    private static void SendMouse(uint flags, int data)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags, mouseData = (uint)data } },
        };
        SendInputChecked([input]);
    }

    private static void SendKeyboard(ushort vk, int scan, uint flags)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = (ushort)scan, dwFlags = flags } },
        };
        SendInputChecked([input]);
    }

    private static void SendInputChecked(INPUT[] inputs)
    {
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            throw new DesktopException($"注入输入失败（Win32 {Marshal.GetLastWin32Error()}）—— 目标窗口可能以管理员权限运行");
        }
    }

    private static ushort? ToVirtualKey(string key) => key switch
    {
        "Ctrl" => VK_CONTROL,
        "Alt" => VK_MENU,
        "Shift" => VK_SHIFT,
        "Win" => VK_LWIN,
        "Return" => VK_RETURN,
        "Escape" => VK_ESCAPE,
        "Space" => VK_SPACE,
        "Tab" => VK_TAB,
        "Backspace" => VK_BACK,
        "Delete" => VK_DELETE,
        "Insert" => VK_INSERT,
        "Home" => VK_HOME,
        "End" => VK_END,
        "PageUp" => VK_PRIOR,
        "PageDown" => VK_NEXT,
        "ArrowUp" => VK_UP,
        "ArrowDown" => VK_DOWN,
        "ArrowLeft" => VK_LEFT,
        "ArrowRight" => VK_RIGHT,
        "CapsLock" => VK_CAPITAL,
        "PrintScreen" => VK_SNAPSHOT,
        _ when key.Length == 1 => char.ToUpperInvariant(key[0]),
        _ when key.Length is 2 or 3 && key[0] == 'F'
            && int.TryParse(key[1..], out var n) && n is >= 1 and <= 24
            => (ushort)(VK_F1 + n - 1),
        _ => null,
    };

    // ── P/Invoke ───────────────────────────────────────────

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_HWHEEL = 0x1000;
    private const int WHEEL_DELTA = 120;

    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    private const int SW_RESTORE = 9;

    private const ushort VK_BACK = 0x08;
    private const ushort VK_TAB = 0x09;
    private const ushort VK_RETURN = 0x0D;
    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_MENU = 0x12;
    private const ushort VK_CAPITAL = 0x14;
    private const ushort VK_ESCAPE = 0x1B;
    private const ushort VK_SPACE = 0x20;
    private const ushort VK_PRIOR = 0x21;
    private const ushort VK_NEXT = 0x22;
    private const ushort VK_END = 0x23;
    private const ushort VK_HOME = 0x24;
    private const ushort VK_LEFT = 0x25;
    private const ushort VK_UP = 0x26;
    private const ushort VK_RIGHT = 0x27;
    private const ushort VK_DOWN = 0x28;
    private const ushort VK_SNAPSHOT = 0x2C;
    private const ushort VK_INSERT = 0x2D;
    private const ushort VK_DELETE = 0x2E;
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_F1 = 0x70;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
}
