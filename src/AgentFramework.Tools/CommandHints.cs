namespace AgentFramework.Tools;

/// <summary>
/// 命令类工具（<c>run_command</c> / <c>shell</c> / <c>job</c>）共用的<b>报告约定</b>与<b>自救提示</b>。
///
/// <para>
/// 为什么抽出来：同一套「退出码 → 怎么自救」的映射若各写一份，改一处漏一处，
/// 模型在不同工具里收到的提示就会不一致 —— 而这正是「调得不顺」的来源。
/// </para>
///
/// <para><b>约定</b>（v3.28）：</para>
/// <list type="number">
///   <item>退出码非零<b>不是工具失败</b> —— 那是命令跑完后的结论（编译失败 / 测试不过 / grep 无匹配都可能是非零）；</item>
///   <item>只有「命令根本没起来」（exit=-1 且零输出）、超时、取消才算工具失败；</item>
///   <item>失败时<b>必须</b>给「下一步怎么走」—— 否则模型只会原样重试到天荒地老；</item>
///   <item>报告头行够用就好：非默认值才回显（正常路径不塞长文本）。</item>
/// </list>
/// </summary>
internal static class CommandHints
{
    /// <summary>
    /// 非零退出时的一行自救提示 —— 只对<b>可识别</b>的失败模式说话；
    /// 认不出的退出码返回 null（不加噪音，模型自己会读 stderr）。
    /// </summary>
    public static string? SelfRescue(int exitCode) => exitCode switch
    {
        127 => "命令不存在（127）。检查拼写，或先确认它装在哪（POSIX: which / Windows: where）。",
        126 => "命令不可执行（126）。检查文件权限（POSIX 可 chmod +x）。",
        9009 => "命令不存在（Windows 9009，等价于 POSIX 127）。检查拼写，或先 where 一下。",
        -1 => "命令未能启动。确认可执行文件存在、路径正确、shell 可用。",
        _ => null,
    };

    /// <summary>
    /// 超时时的自救三步 —— 超时是「真没跑完」，也是最容易被模型原样重试的一种失败。
    /// </summary>
    /// <param name="persistentShell">
    /// true = 调用方本身就是常驻 shell（那就别再劝它「改用 shell」，直接说 run_in_background）。
    /// </param>
    public static string TimeoutRescue(bool persistentShell)
        => "自救：① 加 timeout 参数（上限由运维设定）；"
         + (persistentShell
             ? "② 加 run_in_background=true 放后台跑；"
             : "② 长任务改用 shell 的 run_in_background 或 job 工具放后台跑；")
         + "③ 先缩小范围（只跑失败的用例 / 只构建目标工程）。";

    /// <summary>
    /// 非默认超时才回显 —— 正常路径上这一行必须够短（与沙箱降级备注同一纪律）。
    /// </summary>
    public static string TimeoutSuffix(int actualSeconds, int defaultSeconds)
        => actualSeconds == defaultSeconds ? string.Empty : $" timeout={actualSeconds}s";
}
