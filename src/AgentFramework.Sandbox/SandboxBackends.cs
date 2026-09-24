using AgentFramework.Contracts;

namespace AgentFramework.Sandbox;

/// <summary>
/// 「不设护栏」后端。<b>保留它是有意的</b>：出事时能一键退回没有沙箱的世界，
/// 才好判断问题到底出在命令本身还是沙箱身上。
/// </summary>
public sealed class OffSandboxBackend : ISandboxBackend
{
    public string Name => "off";

    public bool IsAvailable => true;

    public string Describe() =>
        "未启用沙箱：命令在工作区目录下直接执行（临时目录不重定向；超时与取消仍会终结进程树，输出仍封顶）。";

    public Task<SandboxOutcome> RunAsync(SandboxRequest request, CancellationToken ct) =>
        ProcessRunner.RunAsync(
            new ProcessRunner.RunOptions(
                request.Command,
                request.WorkingDirectory,
                TempDirectory: null,          // 不重定向：就是要它按系统默认来
                request.Limits,
                [Describe()],
                OnStarted: null,
                OnTerminate: null),
            ct);
}

/// <summary>
/// 进程护栏（跨平台，默认档）。
///
/// <para>
/// 它管的是三件<b>不需要内核特权</b>的事，恰好也是 agent 跑命令最常见的三种翻车：
/// </para>
/// <list type="number">
///   <item>命令往系统临时目录乱写 —— 重定向进工作区，落地全部可审计；</item>
///   <item>命令超时/被叫停后留下孤儿进程继续跑 —— 连子孙一起终结；</item>
///   <item>构建日志刷屏 —— 输出过程中封顶。</item>
/// </list>
///
/// <para>
/// 它<b>不</b>限制内存与 CPU 用量：那需要内核级配额（见 <c>job</c> 档）。
/// 说清哪一档管得住什么，比笼统宣称「已沙箱化」有用得多。
/// </para>
/// </summary>
public sealed class PortableSandboxBackend : ISandboxBackend
{
    public string Name => "process";

    public bool IsAvailable => true;

    public string Describe() =>
        "进程护栏：工作目录钉在工作区、临时目录重定向进工作区、超时/取消连子孙进程一起终结、输出过程中封顶。不限制内存与 CPU。";

    public Task<SandboxOutcome> RunAsync(SandboxRequest request, CancellationToken ct) =>
        ProcessRunner.RunAsync(
            new ProcessRunner.RunOptions(
                request.Command,
                request.WorkingDirectory,
                request.TempDirectory,
                request.Limits,
                [Describe()],
                OnStarted: null,
                OnTerminate: null),
            ct);
}
