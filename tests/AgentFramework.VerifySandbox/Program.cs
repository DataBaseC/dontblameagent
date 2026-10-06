using System.Diagnostics;
using AgentFramework.Contracts;
using AgentFramework.Host;
using AgentFramework.Sandbox;
using AgentFramework.Tools;
using static AgentFramework.Harness.Suite;

// ═══════════════════════════════════════════════════════════
//  命令沙箱 垂直切片验证
//    档位解析 → 工作目录/临时目录钉死 → 超时连子孙一起终结
//    → 输出封顶 → 取消 → 后端可注册 → run_command 真的走沙箱
//  不需要 API key / 联网 / 真实模型。
// ═══════════════════════════════════════════════════════════


var isWindows = OperatingSystem.IsWindows();
var platform = isWindows ? "Windows" : "非 Windows";

// shell 会话级一次决定（优先 POSIX）：测试命令必须跟着选中的 shell 走，不能再按 OS 猜。
var shell = ShellResolver.Resolve();
var posix = shell.IsPosix;
Console.WriteLine($"shell：{shell.Id} ({shell.FileName})  {shell.Note}");

var timeoutCmd = posix ? "exit 3" : "exit /b 3";
var workDirCmd = posix ? "pwd" : "cd";
var tempDirCmd = posix ? "echo $TMPDIR" : "echo %TEMP%";
var floodCmd = posix ? "seq 1 3000" : "for /l %i in (1,1,3000) do @echo line-%i";
var (busyCmd, busyProcName) = posix
    ? ("sleep 60 & sleep 60", "sleep")
    : ("start /b ping -n 60 127.0.0.1 > nul & ping -n 60 127.0.0.1 > nul", "PING");

var root = Path.Combine(Path.GetTempPath(), "af-sandbox-verify", Guid.NewGuid().ToString("N")[..8]);
var workspace = Path.Combine(root, "workspace");
var tempDir = Path.Combine(workspace, ".agent-sandbox", "tmp");
Directory.CreateDirectory(workspace);

Console.WriteLine("═══ 命令沙箱验证 ═══");
Console.WriteLine($"平台：{platform}    工作目录：{root}");

var registry = new SandboxRegistry();

// ═══ 1. 档位解析 ═══
Console.WriteLine("\n── 1. 档位解析 ──");
Check("★ 注册表内置 off / process 两档",
    registry.Names.Contains("off") && registry.Names.Contains("process"),
    string.Join(",", registry.Names));
Check(isWindows ? "Windows 上额外提供 job 档（内核限额）" : "非 Windows 上不注册 job 档（名字不在名单里）",
    registry.Names.Contains("job") == isWindows,
    string.Join(",", registry.Names));

// auto 默认档：装了 bwrap 优先 bwrap，其次 Windows job，最后 process（与 SandboxRegistry.Auto 一致）。
var expectedAuto = registry.Names.Contains("bwrap") ? "bwrap" : (isWindows ? "job" : "process");

var autoName = registry.Resolve(null).Name;
Check($"★ auto 档在 {platform} 上解析为 {expectedAuto}",
    autoName == expectedAuto,
    autoName);

var unknown = registry.Resolve("no-such-backend");
Check("★ 档位名不认识时回落到可用档，且回落原因可读",
    unknown.Name == expectedAuto && registry.ResolveNote is not null,
    registry.ResolveNote);

if (!isWindows)
{
    var job = registry.Resolve("job");
    Check("★ 非 Windows 上请求 job 会回落，并说明原因",
        job.Name == expectedAuto && registry.ResolveNote!.Contains("job"),
        registry.ResolveNote);
}
else
{
    Check("Windows 上请求 job 直接生效",
        registry.Resolve("job").Name == "job");
}

Check("off 档自述里说清了「未启用沙箱」",
    registry.Resolve("off").Describe().Contains("未启用沙箱"));

// ═══ 2. 进程护栏 ═══
Console.WriteLine("\n── 2. process 档（默认护栏）──");
var processBackend = registry.Resolve("process");
Console.WriteLine($"  · {processBackend.Describe()}");

async Task<SandboxOutcome> RunAsync(string command, SandboxLimits? limits = null, CancellationToken ct = default)
{
    Directory.CreateDirectory(tempDir);
    return await processBackend.RunAsync(
        new SandboxRequest(command, workspace, tempDir, limits ?? new SandboxLimits { TimeoutSeconds = 15 }),
        ct);
}

var echo = await RunAsync(isWindows ? "echo hello-sandbox" : "echo hello-sandbox");
Check("命令正常跑通且输出被收回",
    echo.ExitCode == 0 && echo.StdOut.Contains("hello-sandbox"),
    $"exit={echo.ExitCode}");

var failing = await RunAsync(timeoutCmd);
Check("非零退出码如实传递（不当成异常）",
    failing.ExitCode == 3,
    $"exit={failing.ExitCode}");

var pwd = await RunAsync(workDirCmd);
Check("★ 工作目录钉在工作区（不是宿主当前目录）",
    pwd.StdOut.Contains(Path.GetFileName(workspace), StringComparison.OrdinalIgnoreCase),
    pwd.StdOut.Trim());

var temp = await RunAsync(tempDirCmd);
Check("★ 临时目录被重定向进工作区（临时文件不再散落到系统目录）",
    temp.StdOut.Contains(".agent-sandbox", StringComparison.OrdinalIgnoreCase),
    temp.StdOut.Trim());

var flooded = await RunAsync(floodCmd, new SandboxLimits { TimeoutSeconds = 30, MaxOutputChars = 500 });
Check("★ 输出过程中封顶（不是先全收进内存再截）",
    flooded.OutputTruncated && flooded.StdOut.Length < 4000,
    $"{flooded.StdOut.Length} 字符，truncated={flooded.OutputTruncated}");

// ── 超时：必须连子孙一起终结 ──
// 三步采样才站得住脚：起之前 → 跑起来时（证明命令真起了进程，否则「杀干净」是假阳性）
// → 超时终结之后（证明连子孙一起清掉了）。
var before = CountProcesses(busyProcName);
var busyTask = RunAsync(busyCmd, new SandboxLimits { TimeoutSeconds = 3 });
await Task.Delay(1000);
var during = CountProcesses(busyProcName);
var timedOut = await busyTask;
await Task.Delay(1000);
var after = CountProcesses(busyProcName);

Check($"★ 命令确实起了子孙进程（对照组有效：{busyProcName} {before} → {during}）",
    during > before,
    $"before={before} during={during}");

Check("★ 超时被识别",
    timedOut.TimedOut && !timedOut.Cancelled,
    $"timedOut={timedOut.TimedOut}");

Check($"★ 超时后子孙进程一并终结（{busyProcName} 计数回落到 {after}）",
    after <= before,
    $"before={before} during={during} after={after}");

var stderrCase = await RunAsync("echo oops 1>&2");
Check("stderr 与 stdout 分开收回（不混成一条流）",
    stderrCase.StdErr.Contains("oops") && !stderrCase.StdOut.Contains("oops"),
    $"stdout={stderrCase.StdOut.Trim()} stderr={stderrCase.StdErr.Trim()}");

// ── 取消：与超时区分开 ──
using var cancelSource = new CancellationTokenSource();
cancelSource.CancelAfter(300);
var cancelled = await RunAsync(isWindows ? "ping -n 30 127.0.0.1" : "sleep 30", null, cancelSource.Token);
Check("★ 用户取消与超时是两件事（结果里分开报）",
    cancelled.Cancelled && !cancelled.TimedOut,
    $"cancelled={cancelled.Cancelled} timedOut={cancelled.TimedOut}");

// ═══ 3. off 档对照 ═══
Console.WriteLine("\n── 3. off 档（对照：不设护栏）──");
var offBackend = registry.Resolve("off");
var offTemp = await offBackend.RunAsync(
    new SandboxRequest(tempDirCmd, workspace, tempDir, new SandboxLimits { TimeoutSeconds = 15 }),
    CancellationToken.None);
Check("off 档确实不重定向临时目录（差异是真的）",
    !offTemp.StdOut.Contains(".agent-sandbox", StringComparison.OrdinalIgnoreCase),
    offTemp.StdOut.Trim());

// ═══ 4. 后端可注册（换沙箱 = 加插件）═══
Console.WriteLine("\n── 4. 后端可注册（插件扩展点）──");
var fake = new FakeBackend("fake-sandbox");
var registration = registry.Register(fake);
Check("★ 第三方可以注册新后端，配置里写它的名字即可用",
    registry.Resolve("fake-sandbox").Name == "fake-sandbox");

var duplicate = false;
try
{
    registry.Register(new FakeBackend("fake-sandbox"));
}
catch (InvalidOperationException)
{
    duplicate = true;
}

Check("重名注册被拒（明确失败优于静默顶替）", duplicate);

registration.Dispose();
Check("★ 撤销注册后名字立刻消失（插件卸载即撤）",
    registry.Resolve("fake-sandbox").Name != "fake-sandbox",
    registry.Resolve("fake-sandbox").Name);

// ═══ 4b. 容器后端（Docker / Podman）═══
Console.WriteLine("\n── 4b. 容器后端（Docker / Podman）──");

// 参数拼装就是安全属性所在（禁网 / 只读根 / 只挂工作区 / 配额）——
// 抽成纯函数正是为了在这里断言，而不必依赖本机装没装 docker。
var containerArgs = ContainerSandboxBackend.BuildWrapper(
    new ContainerSandboxOptions { Image = "example/img:1", Network = ContainerNetwork.None, ReadOnlyRoot = true },
    runtime: "docker",
    containerName: "af-sbx-test",
    workDir: workspace,
    limits: new SandboxLimits { MaxMemoryBytes = 512L * 1024 * 1024 });

Check("★ 容器命令默认禁网（--network none）", ArgValue(containerArgs, "--network") == "none");
Check("★ 容器根文件系统只读、/tmp 留可写临时区",
    containerArgs.Contains("--read-only") && ArgValue(containerArgs, "--tmpfs") == "/tmp");
Check("★ 只有工作区被挂载（同一路径进出，配合只读根）",
    ArgValue(containerArgs, "-v") == $"{workspace}:{workspace}");
Check("容器以工作目录启动（-w）", ArgValue(containerArgs, "-w") == workspace);
Check("★ 内存上限传给容器", ArgValue(containerArgs, "--memory") == (512L * 1024 * 1024).ToString());
Check("容器记名（超时/取消时可按名强制清理）", ArgValue(containerArgs, "--name") == "af-sbx-test");
Check("镜像与容器内 shell 排在末尾（先镜像、后命令）",
    containerArgs[^2] == "example/img:1" && containerArgs[^1] == "/bin/sh");

var bridgeArgs = ContainerSandboxBackend.BuildWrapper(
    new ContainerSandboxOptions { Image = "i", Network = ContainerNetwork.Bridge },
    "docker", "n", workspace, new SandboxLimits());
Check("网络策略可切到 bridge（装依赖的出网诉求有出口）", ArgValue(bridgeArgs, "--network") == "bridge");

// 没配镜像 = 档不可用（回落，而不是静默跑一条进不了容器的命令）。
var runtimeFound = new ContainerSandboxBackend(new ContainerSandboxOptions()).RuntimeFound;
var noImageBackend = new ContainerSandboxBackend(new ContainerSandboxOptions());
// 无运行时 → 原因说「运行时」；有运行时 → 原因说「镜像」；都不该是「不可用」这种含糊话。
Check("★ 档不可用时说清原因（缺运行时 / 缺镜像，不含糊）",
    !noImageBackend.IsAvailable
    && (noImageBackend.UnavailableReason?.Contains(runtimeFound ? "镜像" : "运行时") ?? false),
    noImageBackend.UnavailableReason);

// 注册表：只在装了运行时（docker / podman）时才把 container 放进名单。
Check("★ container 只在装了 docker / podman 时进名单",
    registry.Names.Contains("container") == runtimeFound,
    $"names={string.Join(",", registry.Names)}");

if (runtimeFound)
{
    var containerFallback = registry.Resolve("container");
    Check("★ 有运行时但没配镜像时请求 container 会回落，且原因可读",
        containerFallback.Name == expectedAuto && (registry.ResolveNote?.Contains("镜像") ?? false),
        registry.ResolveNote);
}
else
{
    var containerFallback = registry.Resolve("container");
    Check("★ 没装容器运行时时请求 container 回落，说明「没有这个后端」",
        containerFallback.Name == expectedAuto && (registry.ResolveNote?.Contains("container") ?? false),
        registry.ResolveNote);
}

// 真跑：需本机有运行时 + 显式给镜像（不自动拉镜像，避免测试偷偷下载几个 G）。
var containerImage = Environment.GetEnvironmentVariable("AF_TEST_CONTAINER_IMAGE");
if (runtimeFound && !string.IsNullOrWhiteSpace(containerImage))
{
    var containerBackend = new ContainerSandboxBackend(
        new ContainerSandboxOptions { Image = containerImage, Network = ContainerNetwork.None });
    var containerOutcome = await containerBackend.RunAsync(
        new SandboxRequest("echo hi-from-container", workspace, null, new SandboxLimits { TimeoutSeconds = 120 }),
        CancellationToken.None);
    Check("★ 容器档真跑：命令在容器里执行并收回输出",
        containerOutcome.ExitCode == 0 && containerOutcome.StdOut.Contains("hi-from-container"),
        $"exit={containerOutcome.ExitCode}");
}
else
{
    Skip("容器档真跑（需 docker/podman + AF_TEST_CONTAINER_IMAGE）",
        runtimeFound ? "未设 AF_TEST_CONTAINER_IMAGE（避免自动拉镜像）" : "本机没有容器运行时");
}

static string? ArgValue(IReadOnlyList<string> args, string flag)
{
    for (var i = 0; i + 1 < args.Count; i++)
    {
        if (args[i] == flag)
        {
            return args[i + 1];
        }
    }

    return null;
}

// ═══ 5. 真跑：run_command 走沙箱 ═══
Console.WriteLine("\n── 5. run_command 真的走沙箱 ──");
var options = new HostOptions
{
    WorkspaceRoot = workspace,
    SessionsDir = Path.Combine(root, "sessions"),
    SessionId = "default",
    Sandbox = "process",
    ApprovalPolicy = static _ => ApprovalDecision.Allow,
};

await using var host = await AgentHost.CreateAsync(options);
var info = host.SandboxInfo;
Check("★ 宿主诊断面报出当前沙箱档位",
    info.Name == "process" && info.Description.Length > 0,
    $"{info.Name}：{info.Description[..Math.Min(40, info.Description.Length)]}…");

var result = await host.Plugins.InvokeToolAsync(
    "run_command",
    new Dictionary<string, string?> { ["command"] = "echo via-sandbox" });

Check("★ run_command 结果里标明经过哪一档沙箱",
    result.Success && result.Output.Contains("sandbox=process"),
    result.Output.Split('\n').FirstOrDefault(l => l.StartsWith("sandbox=")));

Check("★ 命令的输出照常返回（沙箱不改变命令语义）",
    result.Output.Contains("via-sandbox"));

var unknownHost = await AgentHost.CreateAsync(new HostOptions
{
    WorkspaceRoot = workspace,
    SessionsDir = Path.Combine(root, "sessions2"),
    SessionId = "default",
    Sandbox = "docker-not-installed",
    ApprovalPolicy = static _ => ApprovalDecision.Allow,
});

Check("★ 宿主层面：档位名不认识也回落，且说明可见",
    unknownHost.SandboxInfo.Note is not null
    && unknownHost.SandboxInfo.Note.Contains("docker-not-installed"),
    unknownHost.SandboxInfo.Note);

if (isWindows)
{
    Skip("内存上限触发（Job Object）", "需要真实 Windows + 大内存分配，本轮不自动跑");
}
else
{
    Skip("内存上限触发（Job Object）", "非 Windows 平台没有 Job Object");
}

// ── job 档在本平台的可运行性 ──
Console.WriteLine("\n── 6. job 档（内核限额）──");
if (isWindows)
{
    var jobBackend = registry.Resolve("job");
    var jobOutcome = await jobBackend.RunAsync(
        new SandboxRequest("echo job-ok", workspace, tempDir, new SandboxLimits { TimeoutSeconds = 15 }),
        CancellationToken.None);
    Check("★ job 档下命令照常跑通（限额不误伤正常命令）",
        jobOutcome.ExitCode == 0 && jobOutcome.StdOut.Contains("job-ok"),
        $"exit={jobOutcome.ExitCode}");
}
else
{
    Skip("job 档实跑（Job Object 是 Windows 专有）", "本平台不可用，已由回落逻辑覆盖");
}

try
{
    Directory.Delete(root, recursive: true);
}
catch (Exception)
{
    // 临时目录偶尔删不掉（子进程刚退出还占着句柄），不影响结论
}

// ── 9. 持久 shell（常驻会话：cwd / env 跨调用保留）──
Console.WriteLine("\n── 9. 持久 shell（常驻会话）──");

var posixShell = ShellResolver.Resolve();
if (!posixShell.IsPosix)
{
    Skip("持久 shell 实跑（需 POSIX shell）", $"本平台解析到 {posixShell.Id}（非 POSIX）");
}
else
{
    using var persistent = PersistentShell.Create(posixShell, workspace, tempDir);
    Check("★ POSIX 下能建持久 shell", persistent is not null);

    if (persistent is not null)
    {
        await persistent.RunAsync("cd /tmp && pwd", 15, CancellationToken.None);
        var second = await persistent.RunAsync("pwd", 15, CancellationToken.None);
        Check("★ cd 跨调用保留（第二次 pwd 仍在新目录）",
            second.ExitCode == 0 && second.Output.Trim().EndsWith("/tmp", StringComparison.Ordinal),
            second.Output.Trim());

        await persistent.RunAsync("export AF_PERSIST=xyz", 15, CancellationToken.None);
        var echoOut = await persistent.RunAsync("echo $AF_PERSIST", 15, CancellationToken.None);
        Check("★ export 跨调用保留（环境变量没丢）",
            echoOut.Output.Trim() == "xyz", echoOut.Output.Trim());

        var failExit = await persistent.RunAsync("false", 15, CancellationToken.None);
        Check("退出码如实传递", failExit.ExitCode == 1, failExit.ExitCode.ToString());

        var genBefore = persistent.Generation;
        var timeoutOut = await persistent.RunAsync("sleep 30", 2, CancellationToken.None);
        Check("★ 命令超时被终结（不把回合挂死）", timeoutOut.TimedOut, $"timedOut={timeoutOut.TimedOut}");

        var revived = await persistent.RunAsync("echo alive", 15, CancellationToken.None);
        Check("★ 超时后会话自动重建（下一条命令仍可用）",
            revived.ExitCode == 0 && revived.Output.Contains("alive", StringComparison.Ordinal),
            revived.Output.Trim());
        Check("重建后会话代号递增", persistent.Generation > genBefore, $"{genBefore} → {persistent.Generation}");
    }
}

// ── bwrap 隔离参数（纯函数断言：只读根 / 只挂工作区 / 可切换禁网）──
Console.WriteLine("\n── bwrap 隔离参数 ──");
{
    var bashShell = ShellResolver.Resolve("bash");
    var plain = BwrapSandboxBackend.BuildWrapper("/usr/bin/bwrap", bashShell, "/work", isolateNetwork: false).ToList();
    var netted = BwrapSandboxBackend.BuildWrapper("/usr/bin/bwrap", bashShell, "/work", isolateNetwork: true).ToList();

    Check("根文件系统只读挂载（--ro-bind / /）",
        plain.Contains("--ro-bind") && plain[plain.IndexOf("--ro-bind") + 1] == "/" && plain[plain.IndexOf("--ro-bind") + 2] == "/");
    Check("命名空间隔离齐全（user/pid/uts/ipc）",
        new[] { "--unshare-user", "--unshare-pid", "--unshare-uts", "--unshare-ipc" }.All(plain.Contains));
    Check("默认不隔离网络（不打断 npm install 之类）", !plain.Contains("--unshare-net"));
    Check("★ 显式禁网时才加 --unshare-net", netted.Contains("--unshare-net"));
    Check("工作区可写（--bind workdir workdir）",
        plain.Contains("--bind") && plain[plain.IndexOf("--bind") + 1] == "/work" && plain[plain.IndexOf("--bind") + 2] == "/work");
    Check("工作区挂载先于 chdir（进去之前先存在）", plain.IndexOf("--bind") < plain.IndexOf("--chdir"));
    Check("用 -- 与真正的 shell 分隔（参数不会被 bwrap 吞掉）",
        plain[^2] == "--" && plain[^1] == bashShell.FileName);

    var bwrap = new BwrapSandboxBackend(isolateNetwork: true);
    Check("Describe 如实说明网络是否隔离", bwrap.Describe().Contains("网络隔离") && bwrap.IsolateNetwork);
    Check("默认档 Describe 说明不隔离网络", !new BwrapSandboxBackend().Describe().Contains("网络隔离"));
}

Console.WriteLine($"\n结果：{passes} 通过 / {failures} 失败" + (skips > 0 ? $" / {skips} 跳过" : string.Empty));

return failures == 0 ? 0 : 1;

static int CountProcesses(string name)
{
    try
    {
        return Process.GetProcesses().Count(p =>
            string.Equals(p.ProcessName, name, StringComparison.OrdinalIgnoreCase));
    }
    catch (Exception)
    {
        return -1;
    }
}

/// <summary>测试用的假后端：验证「换沙箱 = 加一个后端」这条缝真的通。</summary>
internal sealed class FakeBackend(string name) : ISandboxBackend
{
    public string Name { get; } = name;

    public bool IsAvailable => true;

    public string Describe() => "测试用假后端";

    public Task<SandboxOutcome> RunAsync(SandboxRequest request, CancellationToken ct)
        => Task.FromResult(new SandboxOutcome(0, "fake", string.Empty, false, false, false, [Describe()]));
}
