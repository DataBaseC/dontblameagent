namespace AgentFramework.Contracts;

/// <summary>
/// 沙箱档位。
///
/// <para>
/// 为什么要有档位而不是"开/关"：沙箱的强度与代价是正相关的，
/// 而不同场景要的东西不一样 —— 跑一次 <c>git status</c> 和跑一个来路不明的构建脚本，
/// 该给的护栏不是一回事。
/// </para>
/// </summary>
public enum SandboxMode
{
    /// <summary>
    /// 不设护栏（保持最原始的行为）。留这一档是为了向后兼容与排查问题 ——
    /// 出事时能一键退回"没有沙箱的世界"，才好判断到底是不是沙箱的锅。
    /// </summary>
    Off = 0,

    /// <summary>
    /// <b>进程护栏（跨平台，默认）</b>：工作目录钉在工作区、临时目录重定向进工作区、
    /// 超时连子孙进程一起终结、输出过程中封顶。
    /// 这一档不限制资源用量（跨平台限制不住），管的是"别乱落地、别留孤儿"。
    /// </summary>
    Process = 1,

    /// <summary>
    /// <b>内核限额（Windows）</b>：在 Process 之上再加 Job Object ——
    /// 内存上限、CPU 时间上限、活动进程数上限、宿主退出时连同子孙一并终结。
    /// 管的是"跑疯的东西别把机器拖死"。
    /// </summary>
    Job = 2,
}

/// <summary>
/// 一次沙箱执行的上限。与其它预算一样：<b>这些参数不进模型可见的工具契约</b> ——
/// 模型只管给命令，护栏是运维的事。
/// </summary>
public sealed class SandboxLimits
{
    public int TimeoutSeconds { get; set; } = 30;

    public int MaxOutputChars { get; set; } = 50_000;

    /// <summary>整个作业的内存上限（0 = 不限）。仅在 Job 档位生效。</summary>
    public long MaxMemoryBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// 作业内活动进程数上限（0 = 不限）。仅在 Job 档位生效。
    ///
    /// <para>
    /// <b>注意别设成 1</b>：<c>cmd.exe /c foo</c> 本身就是「cmd + foo」两个进程，
    /// 设 1 会让命令连启动的机会都没有。默认不限重压（fork 炸弹）这事交给审批。
    /// </para>
    /// </summary>
    public int MaxProcesses { get; set; }

    /// <summary>CPU 时间上限（秒，0 = 不限）。仅在 Job 档位生效。</summary>
    public int MaxCpuSeconds { get; set; }
}

/// <summary>一次沙箱执行的请求。</summary>
public sealed record SandboxRequest(
    string Command,
    string WorkingDirectory,
    string? TempDirectory,
    SandboxLimits Limits);

/// <summary>一次沙箱执行的结果。</summary>
public sealed record SandboxOutcome(
    int ExitCode,
    string StdOut,
    string StdErr,
    bool TimedOut,
    bool Cancelled,
    bool OutputTruncated,
    IReadOnlyList<string> Notes);

/// <summary>
/// 沙箱后端：把"跑一条命令"这件事做成可替换的。
///
/// <para>
/// 为什么是接口而不是写死在 <c>run_command</c> 里：dsh 把 sandbox 本身做成插件，
/// 我们也照这个来 —— 宿主内置两档（<c>process</c> / <c>job</c>），
/// 插件可以再注册新后端（容器、远程执行、带审计的包装……）而不必改宿主一行。
/// </para>
///
/// <para>
/// 实现约定：<b>不抛异常</b>，失败一律体现在 <see cref="SandboxOutcome"/> 里 ——
/// 命令跑不起来是常态（路径错、权限不足），不该炸掉整个回合。
/// </para>
/// </summary>
public interface ISandboxBackend
{
    string Name { get; }

    /// <summary>本平台能不能用（如 job 后端只在 Windows 可用）。</summary>
    bool IsAvailable { get; }

    /// <summary>一句话说明这一档到底管住了什么（进诊断面与工具结果）。</summary>
    string Describe();

    Task<SandboxOutcome> RunAsync(SandboxRequest request, CancellationToken ct);
}

/// <summary>
/// 沙箱后端注册表。<b>宿主提供，插件注册</b> ——
/// 于是"换个沙箱"是加一个插件，而不是改宿主配置里的枚举。
/// </summary>
public interface ISandboxRegistry
{
    /// <summary>已注册的后端名（按名排序）。</summary>
    IReadOnlyCollection<string> Names { get; }

    /// <summary>
    /// 按名取后端。<paramref name="name"/> 为 null / 空 / <c>auto</c> 时按平台挑默认；
    /// 名字不认识时同样回落默认（配置写错不该让命令直接跑不了），并可在
    /// <see cref="ResolveNote"/> 里读到原因。
    /// </summary>
    ISandboxBackend Resolve(string? name);

    /// <summary>上一次 <see cref="Resolve"/> 的说明（回落原因 / 档位释义）。</summary>
    string? ResolveNote { get; }

    /// <summary>
    /// 注册后端（重名即拒）。返回值撤销注册 ——
    /// 插件用 <c>ctx.Effect(() =&gt; registry.Register(...))</c> 挂上，卸载时自动摘掉。
    /// </summary>
    IDisposable Register(ISandboxBackend backend);
}
