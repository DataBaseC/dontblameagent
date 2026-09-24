using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AgentFramework.Contracts;

namespace AgentFramework.Sandbox;

/// <summary>
/// Job Object 的原生封装（Windows 专有）。
///
/// <para>
/// 为什么 Windows 上选 Job Object：它是<b>系统自带、零依赖</b>的进程级配额机制，
/// 而 agent 跑命令真正需要的正是这几条 —— 内存上限、CPU 时间上限、活动进程数上限，
/// 以及「宿主一退出，作业里的东西全部陪葬」。用别的路（容器 / 低完整性级别 / 防火墙）
/// 要么要求用户装运行环境，要么会把普通程序跑坏。
/// </para>
///
/// <para>
/// 挂载时机很要紧：<b>必须在进程刚起来的那一刻就把它塞进作业</b>。
/// 晚一步，命令可能已经起好了自己的子进程，那些就漏在作业之外 —— 于是「限制住了」
/// 只是错觉。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class NativeJob
{
    private const int JobObjectExtendedLimitInformation = 9;

    private const uint LimitJobTime = 0x00000004;
    private const uint LimitActiveProcess = 0x00000008;
    private const uint LimitJobMemory = 0x00000200;
    private const uint LimitKillOnJobClose = 0x00002000;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr job, int infoClass, IntPtr info, uint infoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>
    /// 建一个带配额的作业。失败返回 <see cref="IntPtr.Zero"/>（原因写进 <paramref name="error"/>）——
    /// 调用方据此退化为便携护栏，而不是让命令干脆跑不了。
    /// </summary>
    public static IntPtr Create(SandboxLimits limits, out string? error)
    {
        error = null;
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            error = $"CreateJobObject 失败（Win32 {Marshal.GetLastWin32Error()}）";
            return IntPtr.Zero;
        }

        var info = new ExtendedLimitInformation();
        var flags = LimitKillOnJobClose;   // 宿主一死，作业里的东西全部陪葬

        if (limits.MaxMemoryBytes > 0)
        {
            flags |= LimitJobMemory;
            info.JobMemoryLimit = (nuint)limits.MaxMemoryBytes;
        }

        if (limits.MaxProcesses > 0)
        {
            flags |= LimitActiveProcess;
            info.BasicLimitInformation.ActiveProcessLimit = (uint)limits.MaxProcesses;
        }

        if (limits.MaxCpuSeconds > 0)
        {
            // 单位是 100 纳秒（与 FILETIME 同源），故 ×10^7
            flags |= LimitJobTime;
            info.BasicLimitInformation.PerJobUserTimeLimit = limits.MaxCpuSeconds * 10_000_000L;
        }

        info.BasicLimitInformation.LimitFlags = flags;

        var size = Marshal.SizeOf<ExtendedLimitInformation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, fDeleteOld: false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)size))
            {
                error = $"SetInformationJobObject 失败（Win32 {Marshal.GetLastWin32Error()}）";
                CloseHandle(job);
                return IntPtr.Zero;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return job;
    }

    public static bool Assign(IntPtr job, Process process)
        => AssignProcessToJobObject(job, process.Handle);

    public static void Terminate(IntPtr job)
    {
        try
        {
            TerminateJobObject(job, 1);
        }
        catch (Exception)
        {
            // 作业可能已经被关掉
        }
    }

    public static void Close(IntPtr job)
    {
        if (job == IntPtr.Zero)
        {
            return;
        }

        // KILL_ON_JOB_CLOSE：关句柄这一步本身就会把残留子孙清干净 —— 这正是
        // 「命令已退出、却留了个后台进程偷偷在跑」的解药。
        CloseHandle(job);
    }
}

/// <summary>
/// 内核限额档（Windows）。
///
/// <para>
/// 它在进程护栏之上再加三条<b>只有内核能做的事</b>：内存上限、CPU 时间上限、
/// 宿主退出时连同子孙一并终结。管的是「跑疯的构建别把机器拖死」。
/// </para>
///
/// <para>
/// 建作业失败（某些嵌套作业环境、权限受限等）时<b>自动退化</b>为进程护栏并在结果里注明 ——
/// 沙箱的降级必须可见，否则用户会以为自己在受保护。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsJobSandboxBackend : ISandboxBackend
{
    public string Name => "job";

    public bool IsAvailable => OperatingSystem.IsWindows();

    public string Describe() =>
        "内核限额（Job Object）：在进程护栏之上加内存上限、CPU 时间上限、活动进程数上限，且宿主退出时连同子孙一并终结。";

    public async Task<SandboxOutcome> RunAsync(SandboxRequest request, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            return await new PortableSandboxBackend().RunAsync(request, ct).ConfigureAwait(false);
        }

        var notes = new List<string> { Describe() };
        var job = NativeJob.Create(request.Limits, out var error);

        if (job == IntPtr.Zero)
        {
            notes.Add($"作业创建失败，已退化为进程护栏：{error}");
            return await ProcessRunner.RunAsync(
                new ProcessRunner.RunOptions(
                    request.Command,
                    request.WorkingDirectory,
                    request.TempDirectory,
                    request.Limits,
                    notes,
                    OnStarted: null,
                    OnTerminate: null),
                ct).ConfigureAwait(false);
        }

        try
        {
            var outcome = await ProcessRunner.RunAsync(
                new ProcessRunner.RunOptions(
                    request.Command,
                    request.WorkingDirectory,
                    request.TempDirectory,
                    request.Limits,
                    notes,
                    OnStarted: process =>
                    {
                        if (!NativeJob.Assign(job, process))
                        {
                            throw new InvalidOperationException(
                                $"AssignProcessToJobObject 失败（Win32 {Marshal.GetLastWin32Error()}）—— 配额未生效");
                        }
                    },
                    OnTerminate: () => NativeJob.Terminate(job)),
                ct).ConfigureAwait(false);

            return outcome;
        }
        finally
        {
            // 关句柄 = 连带清掉残留子孙（KILL_ON_JOB_CLOSE）
            NativeJob.Close(job);
        }
    }
}
