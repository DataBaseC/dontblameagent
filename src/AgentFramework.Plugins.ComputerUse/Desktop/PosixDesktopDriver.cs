using System.Diagnostics;
using System.Runtime.Versioning;

namespace AgentFramework.Plugins.ComputerUse.Desktop;

/// <summary>POSIX 系的两个口味（两者走系统命令的方式不同，共用探测与执行壳）。</summary>
public enum PosixFlavor
{
    MacOS,
    Linux,
}

/// <summary>
/// macOS / Linux 桌面驱动：把系统命令当作后端。
///
/// <para>
/// <b>为什么不上 P/Invoke</b>：macOS 要 CGEvent、Linux 要 XTest —— 两条完全不同的原生栈，
/// 而系统自带的 <c>osascript</c> / <c>screencapture</c> / <c>xdotool</c> / <c>import</c>
/// 已经把这层封装好了。缺什么就如实报什么能力不可用，比塞两套原生绑定更划算。
/// </para>
///
/// <list type="bullet">
///   <item><b>macOS</b>：截屏 <c>screencapture</c>；键盘 <c>osascript</c>（System Events）；
///     鼠标优先 <c>cliclick</c>（AppleScript 没有指针移动的原语）。</item>
///   <item><b>Linux（X11）</b>：输入 <c>xdotool</c>；截屏 <c>import</c>（ImageMagick）/
///     <c>gnome-screenshot</c> / <c>scrot</c> 三选一。</item>
/// </list>
///
/// <para>
/// <b>边界</b>：Linux 走 X11（Wayland 下 xdotool 多数失效，如实拒绝）；命令以 argv 传参、不经 shell，
/// 环境只放行图形会话必需的那几个变量。
/// </para>
/// </summary>
public sealed class PosixDesktopDriver : IDesktopDriver
{
    private readonly PosixFlavor _flavor;
    private readonly string? _cliclick;
    private readonly string? _xdotool;
    private readonly string? _captureTool;

    public PosixDesktopDriver(PosixFlavor flavor)
    {
        _flavor = flavor;
        _cliclick = PathLookup.Find("cliclick");
        _xdotool = PathLookup.Find("xdotool");

        _captureTool = flavor == PosixFlavor.MacOS
            ? PathLookup.Find("screencapture")
            : PathLookup.Find("import") ?? PathLookup.Find("gnome-screenshot") ?? PathLookup.Find("scrot");
    }

    public string Name => _flavor == PosixFlavor.MacOS ? "macos" : "linux-x11";

    /// <summary>任一能力可用即算装上（缺的能力由工具在调用时如实报错，而不是整个驱动消失）。</summary>
    public bool IsAvailable => _captureTool is not null || PointerReady;

    private bool PointerReady => _flavor == PosixFlavor.MacOS ? _cliclick is not null : _xdotool is not null;

    public string Describe()
    {
        var parts = new List<string>();
        parts.Add(_captureTool is null ? "截屏缺失" : "截屏 ✓");
        parts.Add(PointerReady ? "指针 ✓" : "指针缺失");
        parts.Add(_flavor == PosixFlavor.MacOS
            ? (PathLookup.Find("osascript") is null ? "键盘缺失" : "键盘 ✓")
            : (_xdotool is null ? "键盘缺失" : "键盘 ✓"));

        var hint = _flavor == PosixFlavor.MacOS
            ? "macOS 需授予「屏幕录制 + 辅助功能」权限；指针操作依赖 cliclick（brew install cliclick）"
            : "Linux 需 X11 会话；输入依赖 xdotool，截屏依赖 import（ImageMagick）/ gnome-screenshot / scrot";

        return $"POSIX 桌面操作（{Name}）：{string.Join(" · ", parts)}。{hint}";
    }

    public DriverCapabilities Capabilities => new(
        Screenshot: _captureTool is not null,
        Pointer: PointerReady,
        Keyboard: _flavor == PosixFlavor.MacOS ? PathLookup.Find("osascript") is not null : _xdotool is not null,
        Windows: false,
        Note: Describe());

    public ScreenSize GetScreenSize()
    {
        if (_flavor == PosixFlavor.MacOS)
        {
            // screencapture -g 输出形如「Current screen: {0, 0, 1512, 982}」
            var output = Run("screencapture", "-g");
            var numbers = ExtractInts(output);
            if (numbers.Count >= 4)
            {
                return new ScreenSize(numbers[2], numbers[3]);
            }

            return new ScreenSize(0, 0);
        }

        var size = Run(_xdotool ?? throw new DesktopException("未安装 xdotool"),
            "getdisplaygeometry");
        var ints = ExtractInts(size);
        if (ints.Count >= 2)
        {
            return new ScreenSize(ints[0], ints[1]);
        }

        throw new DesktopException("无法取屏幕尺寸");
    }

    public byte[] CaptureScreenshot(ScreenRegion? region)
    {
        var tool = _captureTool ?? throw new DesktopException("未找到截屏工具");
        var temp = Path.Combine(Path.GetTempPath(), $"af-cu-{Guid.NewGuid():N}.png");

        try
        {
            if (_flavor == PosixFlavor.MacOS)
            {
                var args = new List<string> { "-x", "-t", "png" };
                if (region is { } r)
                {
                    args.Add("-R");
                    args.Add($"{r.X},{r.Y},{r.Width},{r.Height}");
                }

                args.Add(temp);
                Run("screencapture", [.. args]);
            }
            else if (Path.GetFileName(tool) == "import")
            {
                var args = new List<string> { "-window", "root" };
                if (region is { } r)
                {
                    args.Add("-crop");
                    args.Add($"{r.Width}x{r.Height}+{r.X}+{r.Y}");
                }

                args.Add(temp);
                Run(tool, [.. args]);
            }
            else if (Path.GetFileName(tool) == "scrot")
            {
                var args = new List<string>();
                if (region is { } r)
                {
                    args.Add("-a");
                    args.Add($"{r.X},{r.Y},{r.Width},{r.Height}");
                }

                args.Add(temp);
                Run(tool, [.. args]);
            }
            else
            {
                // gnome-screenshot 不支持区域裁剪
                Run(tool, "-f", temp);
            }

            return File.ReadAllBytes(temp);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    public void MovePointer(int x, int y)
    {
        if (_flavor == PosixFlavor.MacOS)
        {
            Run(Cliclick(), "m:" + $"{x},{y}");
        }
        else
        {
            Run(Xdotool(), "mousemove", x.ToString(), y.ToString());
        }
    }

    public void Click(int x, int y, MouseButton button, int count)
    {
        MovePointer(x, y);
        if (_flavor == PosixFlavor.MacOS)
        {
            var verb = button == MouseButton.Right ? "rc:" : "c:";
            for (var i = 0; i < Math.Max(1, count); i++)
            {
                Run(Cliclick(), verb + $"{x},{y}");
            }
        }
        else
        {
            var btn = button switch { MouseButton.Right => "3", MouseButton.Middle => "2", _ => "1" };
            Run(Xdotool(), "click", "--repeat", Math.Max(1, count).ToString(), "--delay", "30", btn);
        }
    }

    public void Drag(int fromX, int fromY, int toX, int toY, MouseButton button, int durationMs)
    {
        if (_flavor == PosixFlavor.MacOS)
        {
            Run(Cliclick(), $"dd:{fromX},{fromY}", $"dm:{toX},{toY}", $"du:{toX},{toY}");
        }
        else
        {
            var btn = button == MouseButton.Right ? "3" : "1";
            Run(Xdotool(), "mousemove", fromX.ToString(), fromY.ToString(),
                "mousedown", btn, "mousemove", toX.ToString(), toY.ToString(), "mouseup", btn);
        }
    }

    public void Scroll(int x, int y, int deltaX, int deltaY)
    {
        if (_flavor == PosixFlavor.MacOS)
        {
            throw new DesktopException("macOS 暂不支持滚轮注入（cliclick / System Events 无可靠原语）");
        }

        MovePointer(x, y);
        // X11：4/5 = 纵向上/下，6/7 = 横向左/右
        var repeat = Math.Max(1, Math.Abs(deltaY));
        var (btnY, dirsY) = deltaY > 0 ? ("4", repeat) : ("5", repeat);
        if (deltaY != 0)
        {
            Run(Xdotool(), "click", "--repeat", dirsY.ToString(), btnY);
        }

        if (deltaX != 0)
        {
            var repeatX = Math.Max(1, Math.Abs(deltaX));
            Run(Xdotool(), "click", "--repeat", repeatX.ToString(), deltaX > 0 ? "6" : "7");
        }
    }

    public void TypeText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (_flavor == PosixFlavor.MacOS)
        {
            // 用 argv 传文本，避免 AppleScript 字符串转义地狱。
            Run("osascript",
                "-e", "on run argv",
                "-e", "tell application \"System Events\" to keystroke (item 1 of argv)",
                "-e", "end run",
                text);
        }
        else
        {
            Run(Xdotool(), "type", "--clearmodifiers", "--", text);
        }
    }

    public void PressKey(IReadOnlyList<string> keys)
    {
        if (keys.Count == 0)
        {
            throw new DesktopException("组合键为空");
        }

        if (_flavor == PosixFlavor.MacOS)
        {
            var modifiers = keys.Where(KeyNames.IsModifier).Select(ToAppleModifier).ToList();
            var main = keys.LastOrDefault(k => !KeyNames.IsModifier(k))
                ?? throw new DesktopException("组合键里没有主键（只有修饰键）");

            var script = "on run argv\n"
                + "tell application \"System Events\" to keystroke (item 1 of argv)"
                + (modifiers.Count > 0 ? " using {" + string.Join(", ", modifiers) + "}" : "")
                + "\nend run";
            Run("osascript", "-e", script, main);
        }
        else
        {
            var xdo = string.Join("+", keys.Select(ToXdotoolKey));
            Run(Xdotool(), "key", "--clearmodifiers", xdo);
        }
    }

    public IReadOnlyList<WindowInfo> ListWindows()
    {
        if (_flavor == PosixFlavor.MacOS)
        {
            var output = Run("osascript", "-e",
                "tell application \"System Events\" to get name of every process whose visible is true");
            return output.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(name => new WindowInfo(name, 0, 0, 0, 0, 0, false))
                .ToList();
        }

        var wmctrl = PathLookup.Find("wmctrl") ?? throw new DesktopException("未安装 wmctrl，无法枚举窗口");
        var lines = Run(wmctrl, "-lG").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<WindowInfo>();
        foreach (var line in lines)
        {
            // 格式：<id> <desktop> <x> <y> <w> <h> <host> <title...>
            var cols = line.Split(' ', 8, StringSplitOptions.RemoveEmptyEntries);
            if (cols.Length < 8)
            {
                continue;
            }

            result.Add(new WindowInfo(
                cols[7],
                Convert.ToInt64(cols[0], 16),
                ParseInt(cols[2]), ParseInt(cols[3]), ParseInt(cols[4]), ParseInt(cols[5]),
                IsForeground: false));
        }

        return result;
    }

    public bool FocusWindow(string target)
    {
        if (_flavor == PosixFlavor.MacOS)
        {
            var all = ListWindows();
            var hit = all.FirstOrDefault(w => w.Title.Contains(target, StringComparison.OrdinalIgnoreCase));
            if (hit is null)
            {
                return false;
            }

            // 通过 activate 把进程带到前台
            Run("osascript", "-e",
                $"tell application \"System Events\" to set frontmost of process \"{hit.Title}\" to true");
            return true;
        }

        var wmctrl = PathLookup.Find("wmctrl") ?? throw new DesktopException("未安装 wmctrl，无法聚焦窗口");
        var windows = ListWindows();
        var match = windows.FirstOrDefault(w => w.Title.Contains(target, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return false;
        }

        Run(wmctrl, "-i", "-a", "0x" + match.Handle.ToString("x"));
        return true;
    }

    public void OpenApp(string app)
    {
        if (string.IsNullOrWhiteSpace(app))
        {
            throw new DesktopException("应用名不能为空");
        }

        if (_flavor == PosixFlavor.MacOS)
        {
            Run("open", "-a", app);
        }
        else
        {
            // 不经 shell：交给 .NET 的 shell-execute 通道（Linux 上落到 xdg-open / 直接在 PATH 上找）。
            try
            {
                Process.Start(new ProcessStartInfo(app) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                throw new DesktopException($"启动应用失败：{ex.Message}");
            }
        }
    }

    public void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new DesktopException("URL 不能为空");
        }

        if (_flavor == PosixFlavor.MacOS)
        {
            Run("open", url);
        }
        else
        {
            Run("xdg-open", url);
        }
    }

    // ── 内部 ───────────────────────────────────────────────

    private string Cliclick() => _cliclick ?? throw new DesktopException(
        "未安装 cliclick（macOS 指针操作需要它：brew install cliclick）");

    private string Xdotool() => _xdotool ?? throw new DesktopException(
        "未安装 xdotool（Linux 输入注入需要它：apt install xdotool）");

    private static string ToAppleModifier(string key) => key switch
    {
        "Ctrl" => "control down",
        "Alt" => "option down",
        "Shift" => "shift down",
        "Win" => "command down",
        _ => "command down",
    };

    private static string ToXdotoolKey(string key) => key switch
    {
        "Ctrl" => "ctrl",
        "Alt" => "alt",
        "Shift" => "shift",
        "Win" => "super",
        "Return" => "Return",
        "Escape" => "Escape",
        "Space" => "space",
        "Tab" => "Tab",
        "Backspace" => "BackSpace",
        "Delete" => "Delete",
        "Insert" => "Insert",
        "Home" => "Home",
        "End" => "End",
        "PageUp" => "Prior",
        "PageDown" => "Next",
        "ArrowUp" => "Up",
        "ArrowDown" => "Down",
        "ArrowLeft" => "Left",
        "ArrowRight" => "Right",
        "CapsLock" => "Caps_Lock",
        "PrintScreen" => "Print",
        _ => key,
    };

    /// <summary>跑一条外部命令，返回 stdout；非零退出抛 <see cref="DesktopException"/>。</summary>
    private static string Run(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = file,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            // 只放行图形会话必需的变量 —— 宿主环境里的模型 / 搜索密钥不该进这些子进程。
            psi.Environment.Clear();
            foreach (var name in new[]
                     {
                         "PATH", "HOME", "USER", "LOGNAME", "LANG", "LC_ALL", "TMPDIR",
                         "DISPLAY", "XAUTHORITY", "WAYLAND_DISPLAY", "DBUS_SESSION_BUS_ADDRESS",
                     })
            {
                var v = Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrEmpty(v))
                {
                    psi.Environment[name] = v;
                }
            }

            using var process = Process.Start(psi) ?? throw new DesktopException($"无法启动 {file}");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(30_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
                throw new DesktopException($"{Path.GetFileName(file)} 超时（30s）");
            }

            if (process.ExitCode != 0)
            {
                throw new DesktopException(
                    $"{Path.GetFileName(file)} 失败（exit={process.ExitCode}）：{stderr.Trim()}");
            }

            return stdout;
        }
        catch (DesktopException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new DesktopException($"调用 {Path.GetFileName(file)} 失败：{ex.Message}");
        }
    }

    private static List<int> ExtractInts(string text)
    {
        var result = new List<int>();
        var current = new System.Text.StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsDigit(ch) || (ch == '-' && current.Length == 0))
            {
                current.Append(ch);
            }
            else if (current.Length > 0)
            {
                if (int.TryParse(current.ToString(), out var n))
                {
                    result.Add(n);
                }

                current.Clear();
            }
        }

        if (current.Length > 0 && int.TryParse(current.ToString(), out var last))
        {
            result.Add(last);
        }

        return result;
    }

    private static int ParseInt(string s) => int.TryParse(s, out var n) ? n : 0;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // 临时文件删不掉不影响结论
        }
    }
}
