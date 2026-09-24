using System.Diagnostics;
using AgentFramework.Contracts;
using AgentFramework.Host;
using AgentFramework.Sandbox;

// ═══════════════════════════════════════════════════════════
//  命令沙箱 垂直切片验证
//    档位解析 → 工作目录/临时目录钉死 → 超时连子孙一起终结
//    → 输出封顶 → 取消 → 后端可注册 → run_command 真的走沙箱
//  不需要 API key / 联网 / 真实模型。
// ═══════════════════════════════════════════════════════════

var passes = 0;
var failures = 0;
var skips = 0;

void Check(string name, bool ok, string? detail = null)
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

void Skip(string name, string why)
{
    skips++;
    Console.WriteLine($"  [SKIP] {name}  ({why})");
}

var isWindows = OperatingSystem.IsWindows();
var platform = isWindows ? "Windows" : "非 Windows";
var timeoutCmd = isWindows ? "exit /b 3" : "exit 3";
var workDirCmd = isWindows ? "cd" : "pwd";
var tempDirCmd = isWindows ? "echo %TEMP%" : "echo $TMPDIR";
var floodCmd = isWindows ? "for /l %i in (1,1,3000) do @echo line-%i" : "seq 1 3000";
var (busyCmd, busyProcName) = isWindows
    ? ("start /b ping -n 60 127.0.0.1 > nul & ping -n 60 127.0.0.1 > nul", "PING")
    : ("sleep 60 & sleep 60", "sleep");

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

var autoName = registry.Resolve(null).Name;
Check($"★ auto 档在 {platform} 上解析为 {(isWindows ? "job" : "process")}",
    autoName == (isWindows ? "job" : "process"),
    autoName);

var unknown = registry.Resolve("no-such-backend");
Check("★ 档位名不认识时回落到可用档，且回落原因可读",
    unknown.Name == (isWindows ? "job" : "process") && registry.ResolveNote is not null,
    registry.ResolveNote);

if (!isWindows)
{
    var job = registry.Resolve("job");
    Check("★ 非 Windows 上请求 job 会回落，并说明原因",
        job.Name == "process" && registry.ResolveNote!.Contains("job"),
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

Console.WriteLine($"\n结果：{passes} 通过 / {failures} 失败" + (skips > 0 ? $" / {skips} 跳过" : string.Empty));

try
{
    Directory.Delete(root, recursive: true);
}
catch (Exception)
{
    // 临时目录偶尔删不掉（子进程刚退出还占着句柄），不影响结论
}

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
