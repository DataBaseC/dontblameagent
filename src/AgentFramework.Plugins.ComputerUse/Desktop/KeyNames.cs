namespace AgentFramework.Plugins.ComputerUse.Desktop;

/// <summary>
/// 键名解析：把模型给的 <c>"ctrl+shift+t"</c> / <c>"Return"</c> / <c>"F5"</c> 统一成键序列。
///
/// <para>
/// 抽成纯静态类是刻意的：<b>键名归一化是最容易出错、也最容易测</b>的一块
/// （别名、大小写、组合分隔符、非法键），把它与平台注入分开，就能脱离桌面环境单测。
/// 平台驱动只负责把归一化后的键映射成自己的键码。
/// </para>
///
/// <para>
/// 别名表口径（对齐常见键名）：<c>enter/return</c>、<c>esc/escape</c>、<c>ctrl/control</c>、
/// <c>cmd/win/super/meta</c>（平台各自解释）、<c>arrowup/up</c>、<c>space/spacebar</c>、<c>del/delete</c> 等。
/// </para>
/// </summary>
public static class KeyNames
{
    /// <summary>别名 → 规范名（规范名用大小写敏感的标准写法，如 <c>Return</c> / <c>ArrowUp</c>）。</summary>
    private static readonly Dictionary<string, string> Alias = new(StringComparer.OrdinalIgnoreCase)
    {
        // 修饰键
        ["ctrl"] = "Ctrl", ["control"] = "Ctrl",
        ["alt"] = "Alt", ["option"] = "Alt", ["opt"] = "Alt",
        ["shift"] = "Shift",
        ["cmd"] = "Win", ["command"] = "Win", ["win"] = "Win", ["super"] = "Win", ["meta"] = "Win",
        ["fn"] = "Fn",

        // 编辑 / 导航
        ["enter"] = "Return", ["return"] = "Return",
        ["esc"] = "Escape", ["escape"] = "Escape",
        ["space"] = "Space", ["spacebar"] = "Space",
        ["tab"] = "Tab",
        ["backspace"] = "Backspace", ["back"] = "Backspace",
        ["del"] = "Delete", ["delete"] = "Delete",
        ["ins"] = "Insert", ["insert"] = "Insert",
        ["home"] = "Home", ["end"] = "End",
        ["pageup"] = "PageUp", ["pgup"] = "PageUp",
        ["pagedown"] = "PageDown", ["pgdn"] = "PageDown",
        ["up"] = "ArrowUp", ["arrowup"] = "ArrowUp",
        ["down"] = "ArrowDown", ["arrowdown"] = "ArrowDown",
        ["left"] = "ArrowLeft", ["arrowleft"] = "ArrowLeft",
        ["right"] = "ArrowRight", ["arrowright"] = "ArrowRight",
        ["capslock"] = "CapsLock",
        ["printscreen"] = "PrintScreen", ["prtsc"] = "PrintScreen",
    };

    /// <summary>功能键 <c>F1</c>–<c>F24</c>。</summary>
    private static readonly HashSet<string> FunctionKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "F1","F2","F3","F4","F5","F6","F7","F8","F9","F10","F11","F12",
        "F13","F14","F15","F16","F17","F18","F19","F20","F21","F22","F23","F24",
    };

    /// <summary>
    /// 拆分并归一化一个组合键串（<c>"ctrl+shift+T"</c> → <c>["Ctrl","Shift","T"]</c>）。
    /// 空串 / 不可识别的键会抛 <see cref="DesktopException"/> —— 静默丢弃键会让
    /// 「按了 ctrl+shift+t 却只按了 shift+t」这种错误极难排查。
    /// </summary>
    public static IReadOnlyList<string> Parse(string? chord)
    {
        var parts = (chord ?? string.Empty)
            .Split(['+', '-'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 0)
        {
            throw new DesktopException("键名不能为空（例：ctrl+c / Return / F5）");
        }

        var keys = new List<string>(parts.Length);
        foreach (var raw in parts)
        {
            keys.Add(Normalize(raw));
        }

        return keys;
    }

    /// <summary>把一个按键名归一化为规范名。单字符（字母/数字/符号）原样返回（保持大小写）。</summary>
    public static string Normalize(string key)
    {
        var k = key.Trim();
        if (k.Length == 0)
        {
            throw new DesktopException("键名不能为空");
        }

        if (Alias.TryGetValue(k, out var canonical))
        {
            return canonical;
        }

        if (FunctionKeys.Contains(k))
        {
            return k.ToUpperInvariant();
        }

        // 单字符（字母 / 数字 / 标点）：原样保留 —— 大小写对文本输入有意义（如 shift+a 的 'A'）。
        if (k.Length == 1)
        {
            return k;
        }

        throw new DesktopException($"无法识别的键名：{key}（可用：字母/数字/标点、Ctrl/Alt/Shift/Win、F1-F24、Return/Escape/Tab/Arrow 等）");
    }

    /// <summary>是不是修饰键（组合键注入时要「先按下修饰键、最后释放」）。</summary>
    public static bool IsModifier(string normalized)
        => normalized is "Ctrl" or "Alt" or "Shift" or "Win" or "Fn";
}
