namespace AgentFramework.Harness;

/// <summary>
/// 20 个 <c>Verify*</c> 套件共用的最小断言 harness —— 消除每个文件顶部复制粘贴的
/// <c>passes / failures / skips</c> 计数与 <c>Check / Skip</c> 样板（此前 19/20 份逐字节相同）。
///
/// 输出格式与各套件原有实现**逐字节一致**（<c>[PASS]</c> / <c>[FAIL]</c> / <c>[SKIP]</c> 前缀 + 两空格缩进），
/// 所以抽取前后各套件的通过数、失败数、退出码都不变。
///
/// 用法：在套件顶部写 <c>using static AgentFramework.Harness.Suite;</c>，
/// 之后 <c>Check(...)</c> / <c>Skip(...)</c> 与 <c>passes</c> / <c>failures</c> / <c>skips</c> 直接可用，
/// 调用点无需任何改动。
/// </summary>
public static class Suite
{
    /// <summary>累计通过条数（对应原套件的局部 <c>passes</c>）。</summary>
    public static int passes;

    /// <summary>累计失败条数（对应原套件的局部 <c>failures</c>）。</summary>
    public static int failures;

    /// <summary>累计跳过条数（对应原套件的局部 <c>skips</c>）。</summary>
    public static int skips;

    /// <summary>断言：<paramref name="ok"/> 为真记通过，否则记失败；输出与原实现逐字节一致。</summary>
    public static void Check(string name, bool ok, string? detail = null)
    {
        var suffix = detail is null ? "" : $"  ({detail})";
        if (ok)
        {
            passes++;
            Console.WriteLine($"  [PASS] {name}{suffix}");
        }
        else
        {
            failures++;
            Console.WriteLine($"  [FAIL] {name}{suffix}");
        }
    }

    /// <summary>跳过（平台/依赖不满足时）：输出与原实现逐字节一致。</summary>
    public static void Skip(string name, string why)
    {
        skips++;
        Console.WriteLine($"  [SKIP] {name}  ({why})");
    }

    /// <summary>套件退出码：全通过 0，有失败 1（与各套件 <c>return failures == 0 ? 0 : 1</c> 一致）。</summary>
    public static int ExitCode => failures == 0 ? 0 : 1;
}
