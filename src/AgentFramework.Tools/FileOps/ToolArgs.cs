namespace AgentFramework.Tools.FileOps;

/// <summary>
/// 工具入参读取helpers。
///
/// <para>
/// 参数一律以字符串到达（见 <c>ToolInvocation</c> 的注释：值来自 JSON，字符串化后
/// 既能干净地写进事件日志，也能原样往返）。模型传 <c>true</c> / <c>1</c> / <c>"true"</c>
/// 都该被当成真 —— 这一层把这类琐碎差异收干净，工具里只写业务。
/// </para>
/// </summary>
internal static class ToolArgs
{
    public static string? Str(this IReadOnlyDictionary<string, string?> args, string key)
        => args.TryGetValue(key, out var value) ? value : null;

    public static string StrOr(this IReadOnlyDictionary<string, string?> args, string key, string fallback)
        => args.Str(key) is { Length: > 0 } value ? value : fallback;

    public static bool Bool(this IReadOnlyDictionary<string, string?> args, string key, bool fallback = false)
    {
        var raw = args.Str(key)?.Trim();
        return raw?.ToLowerInvariant() switch
        {
            null or "" => fallback,
            "true" or "1" or "yes" or "y" or "on" => true,
            "false" or "0" or "no" or "n" or "off" => false,
            _ => fallback,
        };
    }

    public static int Int(this IReadOnlyDictionary<string, string?> args, string key, int fallback)
        => int.TryParse(args.Str(key)?.Trim(), out var value) ? value : fallback;
}
