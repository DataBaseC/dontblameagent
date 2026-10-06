using System.Globalization;
using System.Text;
using AgentFramework.Contracts;

namespace AgentFramework.Tools;

/// <summary>
/// 执行 shell 命令。
///
/// <para>
/// 这是最危险的一个工具 —— 因此它<b>故意不做任何"聪明"的安全分析</b>
/// （命令白名单/黑名单永远能被绕过），而是把关交给两层：
/// <b>审批</b>（<c>ToolPreExecuteEvent</c>，让宿主或插件决定放不放行）
/// 与<b>沙箱</b>（把跑起来之后能造成的破坏降下来）。
/// </para>
///
/// <para>
/// v3.9 起命令不再直接 <c>Process.Start</c>，而是交给 <see cref="ISandboxRegistry"/>
/// 解析出的后端执行。档位由配置决定（默认 <c>auto</c>：Windows 用 job，其他平台用 process）。
/// 于是"换一种沙箱"是加一个插件，而不是改这个文件。
/// </para>
///
/// <para>
/// v3.28 起把「让模型调得顺」当成本职：<b>退出码非零不再算工具失败</b>
/// （编译失败 / 测试失败 / grep 无匹配都可能是非零，那是命令的结论，不是工具坏了）——
/// 从前它被包成失败结果，主循环给正文加 <c>ERROR:</c> 前缀，模型看到的像"被拒绝"；
/// 另补 <c>timeout</c>（长构建自救）、<c>description</c>（审计）与一行「怎么自救」提示。
/// 报告约定与自救文案见 <see cref="CommandHints"/>（与 <c>shell</c> / <c>job</c> 共用）。
/// </para>
/// </summary>
public sealed class RunCommandTool(ToolkitOptions options, ISandboxRegistry sandbox) : IToolWithRisk, ITool, IToolWithSchema
{
    /// <summary>
    /// 沙箱降级备注的稳定前缀：护栏回落 / 配额未生效 / 作业创建失败等。
    /// </summary>
    private const string NoteDegradePrefix = "sandbox-degrade:";

    /// <summary>
    /// 沙箱启动失败备注的稳定前缀：命令进程根本没起来。
    /// </summary>
    private const string NoteStartFailPrefix = "sandbox-start-fail:";

    public string Name => "run_command";

    public ToolRisk Risk => ToolRisk.Execute;

    public string Description =>
        "在工作区目录下执行一条 shell 命令，返回 stdout / stderr / 退出码。属危险操作。" +
        "【每次都是新进程】cd / export / 虚拟环境跨调用不保留 —— 要保留这些请用 shell 工具。" +
        "长任务别干等：加 timeout 参数，或改用 shell / job 把它放后台跑。" +
        "退出码非零仍会如实返回输出（编译失败、测试失败、grep 无匹配都可能是非零），" +
        "请读输出判断，不要当成工具故障。";

    public string ParametersJsonSchema =>
        """
        {"type":"object","properties":{
        "command":{"type":"string","description":"要执行的完整命令"},
        "shell":{"type":"string","description":"可选：本次用哪个 shell（auto/bash/sh/pwsh/powershell/cmd）。不传则用会话默认。"},
        "timeout":{"type":"integer","description":"可选：本次超时秒数（省略用默认值；超过运维上限会被压到上限）。长构建/长测试用得上。"},
        "description":{"type":"string","description":"可选：这条命令在干什么（5-15 字，进报告便于审计与追溯）"}},
        "required":["command"]}
        """;

    public async ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        if (!invocation.Arguments.TryGetValue("command", out var command) || string.IsNullOrWhiteSpace(command))
        {
            return ToolResult.Fail("缺少参数 command");
        }

        var root = Path.GetFullPath(options.EffectiveRoot);
        var backend = sandbox.Resolve(options.SandboxName);

        // 按调用选 shell（模型参数，v3.22）：只允许白名单里的**关键字** ——
        // ShellResolver 会把认不出的字符串当显式路径直接执行，不设白名单就等于给模型任意执行入口。
        Contracts.ShellSpec shell;
        if (invocation.Arguments.TryGetValue("shell", out var requestedShell) && !string.IsNullOrWhiteSpace(requestedShell))
        {
            var candidate = requestedShell.Trim();
            if (!options.AllowedCommandShells.Contains(candidate))
            {
                return ToolResult.Fail(
                    $"不支持的 shell「{candidate}」；只允许：{string.Join(" / ", options.AllowedCommandShells.Order())}");
            }

            shell = Contracts.ShellResolver.Resolve(candidate);
        }
        else
        {
            shell = options.EffectiveShell;   // 会话级默认，不在这里重猜
        }

        var timeoutSeconds = ResolveTimeout(invocation);

        var limits = new SandboxLimits
        {
            TimeoutSeconds = timeoutSeconds,
            MaxOutputChars = options.MaxCommandOutputChars,
            MaxMemoryBytes = options.CommandMaxMemoryBytes,
            MaxProcesses = options.CommandMaxProcesses,
            MaxCpuSeconds = options.CommandMaxCpuSeconds,
        };

        // 临时目录钉在工作区内：命令随手写的临时文件也落在同一个可审计的地方
        // （见 ProcessRunner 里对 TMP/TEMP/TMPDIR 的重定向）。
        var tempDirectory = Path.Combine(root, ".agent-sandbox", "tmp");

        var outcome = await backend.RunAsync(
            new SandboxRequest(command, root, tempDirectory, limits) { Shell = shell },
            ct).ConfigureAwait(false);

        if (outcome.Cancelled)
        {
            return ToolResult.Fail("命令已被取消");
        }

        if (outcome.TimedOut)
        {
            // 超时是**真没跑完**（区别于「跑完了但退出码非零」），因此仍是失败；
            // 但必须把「下一步怎么办」一并交出去，否则模型只会原样重试到天荒地老。
            return ToolResult.Fail(
                $"命令超时（{timeoutSeconds}s），已连同子孙进程终止（沙箱 {backend.Name} · shell {shell.Id}）。"
                + CommandHints.TimeoutRescue(persistentShell: false));
        }

        var report = new StringBuilder();
        report.Append("exit=").Append(outcome.ExitCode).Append('\n');
        report.Append("sandbox=").Append(backend.Name);
        report.Append(" shell=").Append(shell.Id);
        if (!shell.IsPosix)
        {
            report.Append("(非 POSIX)");
        }

        // 只在**非默认**时才回显超时：正常路径的这行必须够短（与降级备注同一纪律）。
        report.Append(CommandHints.TimeoutSuffix(timeoutSeconds, options.CommandTimeoutSeconds));

        // shell 选得不理想时说一句（正常路径不加长文 —— 与沙箱降级同一纪律）
        if (shell.Note.Contains("回落") || shell.Note.Contains("不可用") || shell.Note.Contains("显式"))
        {
            report.Append("（").Append(shell.Note).Append('）');
        }

        // 只在「发生了回落/降级」时才展开细节 —— 正常路径上这行必须够短。
        // 否则每跑一条命令都要在上下文里塞一段沙箱说明书。
        var resolveNote = sandbox.ResolveNote;
        var degraded = ClassifyDegradedNotes(outcome, backend.Describe());
        if (!string.IsNullOrWhiteSpace(resolveNote) || degraded.Count > 0)
        {
            report.Append("（");
            if (!string.IsNullOrWhiteSpace(resolveNote))
            {
                // 注册表回落本身也是降级 —— 打上同一套稳定前缀，便于下游统一解析
                report.Append(HasStableNotePrefix(resolveNote) ? resolveNote : NoteDegradePrefix + resolveNote);
            }

            if (degraded.Count > 0)
            {
                if (!string.IsNullOrWhiteSpace(resolveNote))
                {
                    report.Append('；');
                }

                report.Append(string.Join("；", degraded));
            }

            report.Append('）');
        }

        // 调用方自述（审计用）：有才写，没有就不占地方。
        if (invocation.Arguments.TryGetValue("description", out var description)
            && !string.IsNullOrWhiteSpace(description))
        {
            report.Append("  # ").Append(description.Trim());
        }

        report.Append("\n--- stdout ---\n").Append(Truncate(outcome.StdOut));

        var errText = outcome.StdErr;
        if (errText.Length > 0)
        {
            report.Append("\n--- stderr ---\n").Append(Truncate(errText));
        }

        if (outcome.OutputTruncated)
        {
            report.Append("\n...[输出超过上限，执行过程中已截断]");
        }

        // 非零退出：只对**可识别**的失败模式补一行「怎么自救」（见 CommandHints）。
        if (outcome.ExitCode != 0 && CommandHints.SelfRescue(outcome.ExitCode) is { } hint)
        {
            report.Append("\n[hint] ").Append(hint);
        }

        // L2：命令回显是长任务里的另一个大头（构建日志、测试输出尤其）
        var text = options.ShrinkResult("run_command", report.ToString());

        // ★ 只有「命令根本没起来」才算工具失败（ProcessRunner 的固定返回形状：exit=-1 且零输出）。
        //   其余一律算「命令跑完了」—— 退出码是命令的结论，如实交回输出与码即可。
        //   这不是措辞问题：从前它走 ToolResult.Fail，主循环会把正文写成 "ERROR: …"，
        //   模型据此以为工具坏了或被拒绝，而不是去看输出里到底说了什么。
        var startFailed = outcome.ExitCode == -1
            && outcome.StdOut.Length == 0
            && outcome.StdErr.Length == 0;

        return startFailed ? ToolResult.Fail(text) : ToolResult.Ok(text);
    }

    /// <summary>
    /// 本次调用的超时：模型可传 <c>timeout</c>，但**夹在运维设的硬上限内** ——
    /// 上限进配置、当前次取值进参数（与 <c>shell</c> 工具同一手法）。
    /// </summary>
    private int ResolveTimeout(ToolInvocation invocation)
    {
        var requested = invocation.Arguments.TryGetValue("timeout", out var raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : options.CommandTimeoutSeconds;

        var cap = Math.Max(1, options.CommandMaxTimeoutSeconds);
        return Math.Clamp(requested, 1, cap);
    }

    /// <summary>
    /// 把沙箱返回的备注整理成「带稳定前缀的降级备注」。
    ///
    /// <para>
    /// <b>ProcessRunner 的备注文案可变，筛选只看前缀</b> —— 不做中文魔法子串匹配
    /// （"失败"/"退化"/"未能启动" 这类文案一改，旧筛选就静默失效，降级信息直接消失）。
    /// 稳定前缀约定：
    ///   <c>sandbox-degrade:</c>（护栏回落/降级）与 <c>sandbox-start-fail:</c>（命令未能启动）。
    /// </para>
    /// <para>
    /// 若 ProcessRunner / 后端的 notes 不含前缀，就在本包装层补上前缀分类：
    ///   1. 已带前缀的备注原样保留；
    ///   2. 与后端 <see cref="ISandboxBackend.Describe"/> 相同的是档位自述，属信息性备注，不进降级摘要；
    ///   3. 其余动态备注一律打上稳定前缀。启动失败 vs其它降级用**结构信号**判断
    ///      （exit=-1 且无输出 = ProcessRunner 启动失败的固定返回形状），不匹配文案。
    /// </para>
    /// </summary>
    private static List<string> ClassifyDegradedNotes(SandboxOutcome outcome, string backendDescribe)
    {
        var result = new List<string>();

        // 结构信号：ProcessRunner 在命令未能启动时以 exit=-1 且零输出返回。
        // 用返回形状而不是备注文案分类 —— 文案可变，形状是接口的一部分。
        var startFailed = outcome.ExitCode == -1
            && outcome.StdOut.Length == 0
            && outcome.StdErr.Length == 0
            && !outcome.TimedOut
            && !outcome.Cancelled;

        foreach (var note in outcome.Notes)
        {
            if (HasStableNotePrefix(note))
            {
                result.Add(note);
                continue;
            }

            // 后端自述是档位说明，不是降级
            if (string.Equals(note, backendDescribe, StringComparison.Ordinal))
            {
                continue;
            }

            result.Add((startFailed ? NoteStartFailPrefix : NoteDegradePrefix) + note);
        }

        return result;
    }

    private static bool HasStableNotePrefix(string note)
        => note.StartsWith(NoteDegradePrefix, StringComparison.Ordinal)
        || note.StartsWith(NoteStartFailPrefix, StringComparison.Ordinal);

    /// <summary>
    /// 输出超限时保留 <b>头部 + 尾部</b>，而不是只留头部。
    ///
    /// <para>
    /// 结论常在尾部：测试失败的 summary、构建报错的最后一行、异常堆栈的末行 ——
    /// 只砍尾部就等于把最要紧的一段丢掉，模型于是只能重跑或加管道去捞。
    /// 头 60% 给上下文，尾 40% 给结论。
    /// </para>
    /// </summary>
    private string Truncate(string value)
    {
        var max = options.MaxCommandOutputChars;
        if (value.Length <= max)
        {
            return value;
        }

        var head = max * 3 / 5;
        var tail = max - head;
        var omitted = value.Length - head - tail;
        return value[..head] + $"\n...[中间省略 {omitted} 字符；已保留头尾]...\n" + value[^tail..];
    }
}
