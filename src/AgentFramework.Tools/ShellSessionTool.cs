using System.Globalization;
using System.Text;
using AgentFramework.Contracts;

namespace AgentFramework.Tools;

/// <summary>
/// 在<b>常驻</b> shell 会话里执行命令（对齐 dsh 的 persistent shell · Claude Code 的 Bash 工具）。
///
/// <para>
/// <c>run_command</c> 每次都是全新进程 —— <c>cd</c> / <c>export</c> / <c>source venv/bin/activate</c>
/// 跨调用全部丢失。这个工具把 shell 保活，这三样跨调用保留，长任务里少一堆重复的环境设置。
/// </para>
///
/// <para>
/// 与 <c>run_command</c> 的分工：
/// <list type="bullet">
///   <item>要<b>延续状态</b>（切目录、设变量、激活环境）→ <c>shell</c>；</item>
///   <item>要<b>干净的一次执行</b>（互不污染、stderr 单列）→ <c>run_command</c>。</item>
/// </list>
/// 两者都属危险操作，都过审批链，都受进程级护栏。
/// </para>
///
/// <para>
/// 三个可选参数是照 Claude Code Bash 工具补齐的（<b>它有的，我们也得有</b>）：
/// <list type="bullet">
///   <item><c>description</c> —— 一句话说明这条命令在干什么，进报告，便于审计与追溯；</item>
///   <item><c>timeout</c> —— 覆盖默认超时，但被夹在运维设的<b>硬上限</b>内（模型管不了上限）；</item>
///   <item><c>run_in_background</c> —— 扔后台跑，立刻返回作业 id，用 <c>job</c> 工具收结果。</item>
/// </list>
/// 后台走的是与 <c>job</c> 工具<b>同一份作业池</b> —— 两个入口、一个作业表，
/// 否则「shell 起的后台」在 job 列表里看不见，模型会以为它丢了。
/// </para>
/// </summary>
public sealed class ShellSessionTool(ToolkitOptions options, JobManager? jobs = null) : IToolWithRisk, ITool, IToolWithSchema, IDisposable
{
    private readonly object _gate = new();
    private PersistentShell? _shell;

    public string Name => "shell";

    public ToolRisk Risk => ToolRisk.Execute;

    public string Description =>
        "在【常驻】shell 会话里执行一条命令：工作目录（cd）、环境变量（export）、已激活的虚拟环境跨调用保留。" +
        "可选参数：timeout（秒，覆盖默认超时，上限由运维设定）、description（一句话说明命令在干什么）、" +
        "run_in_background=true（扔后台跑，返回作业 id，用 job 工具查询/取输出/终结）。" +
        "与 run_command 的区别：run_command 每次都是全新进程，不保留这些。仅支持 POSIX shell（bash / sh）；命令超时会重建会话。属危险操作。";

    public string ParametersJsonSchema =>
        """
        {"type":"object","properties":{
        "command":{"type":"string","description":"要执行的完整命令"},
        "description":{"type":"string","description":"这条命令在干什么（5-10 字，便于审计与追溯）"},
        "timeout":{"type":"integer","description":"超时秒数（省略用默认值；超过运维上限会被压到上限）"},
        "run_in_background":{"type":"boolean","description":"true=扔后台跑，立即返回作业 id（用 job 工具查状态/输出/终结）"}},
        "required":["command"]}
        """;

    public async ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        if (!invocation.Arguments.TryGetValue("command", out var command) || string.IsNullOrWhiteSpace(command))
        {
            return ToolResult.Fail("缺少参数 command");
        }

        var description = Arg(invocation, "description");

        // 后台：与 job 工具同一份作业池，立即返回作业 id，不阻塞回合。
        if (IsTrue(invocation, "run_in_background"))
        {
            return StartBackground(command, description);
        }

        var shell = options.EffectiveShell;
        if (!shell.IsPosix)
        {
            // 不静默降级成「一次性」——那会让模型以为状态保留了，实际没有。
            return ToolResult.Fail(
                $"持久 shell 仅支持 POSIX shell（bash / sh）；当前 shell 是 {shell.Id}。" +
                "改用 run_command，或在配置里把 shell 设为 bash（Windows 可装 Git Bash）。");
        }

        var root = Path.GetFullPath(options.EffectiveRoot);
        var tempDirectory = Path.Combine(root, ".agent-sandbox", "tmp");

        PersistentShell session;
        lock (_gate)
        {
            _shell ??= PersistentShell.Create(shell, root, tempDirectory)
                ?? throw new InvalidOperationException("shell 不是 POSIX，无法建持久会话");

            session = _shell;
        }

        var timeout = ResolveTimeout(invocation);
        var outcome = await session.RunAsync(command, timeout, ct).ConfigureAwait(false);

        var report = new StringBuilder();
        report.Append("exit=").Append(outcome.ExitCode).Append('\n');
        report.Append("shell=").Append(shell.Id).Append(" persistent#").Append(session.Generation);
        report.Append(CommandHints.TimeoutSuffix(timeout, options.CommandTimeoutSeconds));
        if (outcome.SessionRestarted)
        {
            report.Append("（会话已重建）");
        }

        report.Append('\n');

        // 当前工作目录：让模型看得见 cd 的效果，不必再跑一条 pwd 去确认。
        if (outcome.WorkingDirectory is { Length: > 0 } cwd)
        {
            report.Append("cwd=").Append(cwd).Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(description))
        {
            report.Append("desc=").Append(description).Append('\n');
        }

        if (outcome.TimedOut)
        {
            return ToolResult.Fail(
                report + $"命令超时（{timeout}s），持久 shell 已被终结并重建（下一条命令是新会话）。"
                + CommandHints.TimeoutRescue(persistentShell: true));
        }

        report.Append("--- output ---\n").Append(outcome.Output);
        if (outcome.OutputTruncated)
        {
            report.Append("\n...[输出超过上限，执行过程中已截断]");
        }

        // 与 run_command 同一约定（v3.28）：非零退出**不是工具失败** ——
        //   退出码是命令的结论（编译失败 / 测试不过 / grep 无匹配都可能是非零）。
        //   从前这里返回 Fail，主循环给正文加 "ERROR:" 前缀，模型以为工具坏了。
        //   只有**可识别**的失败模式才补一行自救提示。
        if (outcome.ExitCode != 0 && CommandHints.SelfRescue(outcome.ExitCode) is { } hint)
        {
            report.Append("\n[hint] ").Append(hint);
        }

        // 与 run_command 同一策略：长命令输出走 L2 引用化。
        var text = options.ShrinkResult("shell", report.ToString());
        return ToolResult.Ok(text);
    }

    /// <summary>把命令投进后台作业池（<c>job</c> 工具看到的是同一张表）。</summary>
    private ToolResult StartBackground(string command, string? description)
    {
        if (jobs is null)
        {
            return ToolResult.Fail("后台作业未接线（宿主未提供作业管理器）；请改用 run_command，或用 job 工具显式起后台。");
        }

        var root = Path.GetFullPath(options.EffectiveRoot);
        var tempDirectory = Path.Combine(root, ".agent-sandbox", "tmp");
        var shell = options.EffectiveShell;

        var id = jobs.Start(command, shell, root, tempDirectory, out var error);
        if (id is null)
        {
            return ToolResult.Fail(error ?? "启动作业失败");
        }

        var report = new StringBuilder();
        report.Append("已启动后台作业 ").Append(id).Append('\n');
        report.Append("command: ").Append(command).Append('\n');
        if (!string.IsNullOrWhiteSpace(description))
        {
            report.Append("desc: ").Append(description).Append('\n');
        }

        report.Append("shell=").Append(shell.Id).Append('\n');
        report.Append($"用 job(action=status, id={id}) 查状态，job(action=output, id={id}) 看输出，job(action=kill, id={id}) 终结。");
        return ToolResult.Ok(report.ToString());
    }

    /// <summary>
    /// 模型可传 <c>timeout</c> 覆盖默认值，但永远被夹在 <c>[1, CommandMaxTimeoutSeconds]</c> 内 ——
    /// 上限是运维的事（对齐 Claude Code：默认 2 分钟、模型最多要 10 分钟）。
    /// </summary>
    private int ResolveTimeout(ToolInvocation invocation)
    {
        var requested = options.CommandTimeoutSeconds;
        if (invocation.Arguments.TryGetValue("timeout", out var raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            requested = parsed;
        }

        var max = Math.Max(1, options.CommandMaxTimeoutSeconds);
        return Math.Clamp(requested, 1, max);
    }

    private static string? Arg(ToolInvocation invocation, string name)
        => invocation.Arguments.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    private static bool IsTrue(ToolInvocation invocation, string name)
        => invocation.Arguments.TryGetValue(name, out var value)
           && bool.TryParse(value, out var parsed)
           && parsed;

    public void Dispose()
    {
        lock (_gate)
        {
            _shell?.Dispose();
            _shell = null;
        }
    }
}
