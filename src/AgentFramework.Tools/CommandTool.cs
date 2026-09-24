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
/// </summary>
public sealed class RunCommandTool(ToolkitOptions options, ISandboxRegistry sandbox) : ITool, IToolWithSchema
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

    public string Description => "在工作区目录下执行一条 shell 命令并返回标准输出与退出码。属危险操作。";

    public string ParametersJsonSchema =>
        """{"type":"object","properties":{"command":{"type":"string","description":"要执行的完整命令"}},"required":["command"]}""";

    public async ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        if (!invocation.Arguments.TryGetValue("command", out var command) || string.IsNullOrWhiteSpace(command))
        {
            return ToolResult.Fail("缺少参数 command");
        }

        var root = Path.GetFullPath(options.EffectiveRoot);
        var backend = sandbox.Resolve(options.SandboxName);

        var limits = new SandboxLimits
        {
            TimeoutSeconds = options.CommandTimeoutSeconds,
            MaxOutputChars = options.MaxCommandOutputChars,
            MaxMemoryBytes = options.CommandMaxMemoryBytes,
            MaxProcesses = options.CommandMaxProcesses,
            MaxCpuSeconds = options.CommandMaxCpuSeconds,
        };

        // 临时目录钉在工作区内：命令随手写的临时文件也落在同一个可审计的地方
        // （见 ProcessRunner 里对 TMP/TEMP/TMPDIR 的重定向）。
        var tempDirectory = Path.Combine(root, ".agent-sandbox", "tmp");

        var outcome = await backend.RunAsync(
            new SandboxRequest(command, root, tempDirectory, limits),
            ct).ConfigureAwait(false);

        if (outcome.Cancelled)
        {
            return ToolResult.Fail("命令已被取消");
        }

        if (outcome.TimedOut)
        {
            return ToolResult.Fail($"命令超时（{limits.TimeoutSeconds}s），已连同子孙进程终止（沙箱 {backend.Name}）");
        }

        var report = new StringBuilder();
        report.Append("exit=").Append(outcome.ExitCode).Append('\n');
        report.Append("sandbox=").Append(backend.Name);

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

        // L2：命令回显是长任务里的另一个大头（构建日志、测试输出尤其）
        var text = options.ShrinkResult("run_command", report.ToString());
        return outcome.ExitCode == 0 ? ToolResult.Ok(text) : ToolResult.Fail(text);
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

    private string Truncate(string value)
        => value.Length <= options.MaxCommandOutputChars
            ? value
            : string.Concat(value.AsSpan(0, options.MaxCommandOutputChars), "\n...[输出已截断]");
}
