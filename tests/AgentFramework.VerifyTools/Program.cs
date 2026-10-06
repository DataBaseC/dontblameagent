using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using AgentFramework.Agent;
using AgentFramework.Contracts;
using AgentFramework.Data;
using AgentFramework.Kernel;
using AgentFramework.Tools;
using static AgentFramework.Harness.Suite;

// ═══════════════════════════════════════════════════════════
//  工具集垂直切片验证
//  文件（含路径逃逸防护）/ 命令（超时）/ 搜索（降级链）/ 抓取（SSRF 防护）
// ═══════════════════════════════════════════════════════════


var root = Path.Combine(Path.GetTempPath(), "af-tools-verify", Guid.NewGuid().ToString("N")[..8]);
var workspace = Path.Combine(root, "workspace");
Directory.CreateDirectory(workspace);
Console.WriteLine("═══ 工具集垂直切片验证 ═══");
Console.WriteLine($"工作区：{workspace}");

var options = new ToolkitOptions { WorkspaceRoot = workspace };

// 沙箱按调用选 shell：断言跟着解析出的 shell 走，不能按 OS 猜（本机没有 Git Bash 时回落 cmd）。
var resolvedShell = ShellResolver.Resolve();
var posix = resolvedShell.IsPosix;

async ValueTask<ToolResult> Call(ITool tool, params (string Key, string Value)[] args)
{
    var dict = args.ToDictionary(a => a.Key, a => (string?)a.Value);
    return await tool.InvokeAsync(new ToolInvocation(tool.Name, dict));
}

// ── 1. 文件工具 ────────────────────────────────────────────
Console.WriteLine("\n── 1. 文件工具 ──");

var write = new WriteFileTool(options);
var read = new ReadFileTool(options);
var list = new ListDirTool(options);

var writeResult = await Call(write, ("path", "notes/todo.md"), ("content", "# 待办\n- 写框架"));
Check("write_file 写入成功", writeResult.Success, writeResult.Output);

var readResult = await Call(read, ("path", "notes/todo.md"));
Check("read_file 读回内容一致", readResult.Success && readResult.Output.Contains("写框架"), readResult.Output);

var listResult = await Call(list, ("path", "notes"));
Check("list_dir 列出条目", listResult.Success && listResult.Output.Contains("todo.md"), listResult.Output);

// ── 1c. edit_file 失败要给「下一步」（v3.28）───────────────────
{
    var wsService = new WorkspaceService(options);
    var editTool = AgentFramework.Tools.FileOps.CoreFileOps.CreateAll(wsService).First(t => t.Name == "edit_file");

    var missingFile = await Call(editTool, ("path", "nope/never.md"), ("old_string", "a"), ("new_string", "b"));
    Check("★ edit_file 文件不存在时指出路（list_dir / write_file）",
        !missingFile.Success && missingFile.Error!.Contains("write_file"), missingFile.Error);

    await Call(write, ("path", "dup.txt"), ("content", "alpha\nbeta\nUNIQUE_MARK\nbeta\nUNIQUE_MARK\n"));

    var multi = await Call(editTool, ("path", "dup.txt"), ("old_string", "UNIQUE_MARK"), ("new_string", "X"));
    Check("★ edit_file 多处匹配时列出每一处行号（模型一眼选对）",
        !multi.Success && multi.Error!.Contains("第 3 / 5 行"), multi.Error);

    var notFound = await Call(editTool, ("path", "dup.txt"), ("old_string", "ZZZ-NOT-EXIST"), ("new_string", "y"));
    Check("★ edit_file 找不到原文时给两条自救",
        !notFound.Success && notFound.Error!.Contains("自救"), notFound.Error);

    var replaceAll = await Call(editTool,
        ("path", "dup.txt"), ("old_string", "UNIQUE_MARK"), ("new_string", "X"), ("replace_all", "true"));
    Check("replace_all=true 时允许批量替换（不再要求唯一）",
        replaceAll.Success && replaceAll.Output.Contains("已替换 2 处"),
        replaceAll.Success ? FirstLine(replaceAll.Output) : replaceAll.Error);
}

// ── 1d. 文件族失败也要给「下一步」（v3.30）────────────────────
{
    var fileOps = AgentFramework.Tools.FileOps.CoreFileOps
        .CreateAll(new WorkspaceService(options))
        .ToDictionary(t => t.Name);

    var missingRead = await Call(read, ("path", "no/such/file.md"));
    Check("★ read_file 文件不存在时给出找法（find_files / list_dir）",
        !missingRead.Success && missingRead.Error!.Contains("find_files"), missingRead.Error);

    var tooLong = await Call(write, ("path", "big.txt"), ("content", new string('x', options.MaxWriteChars + 1)));
    Check("★ write_file 内容过长时给出分段写的出路",
        !tooLong.Success && tooLong.Error!.Contains("edit_file"), tooLong.Error);

    var badRegex = await Call(fileOps["grep_files"], ("pattern", "(["), ("regex", "true"));
    Check("★ grep_files 正则无效时提示「去掉 regex」",
        !badRegex.Success && badRegex.Error!.Contains("regex=true"), badRegex.Error);

    var badFind = await Call(fileOps["find_files"], ("pattern", "*.md"), ("path", "no-such-dir"));
    Check("★ find_files 起点不存在时给出路",
        !badFind.Success && badFind.Error!.Contains("list_dir"), badFind.Error);

    var badMove = await Call(fileOps["move_path"], ("from", "ghost.md"), ("to", "x.md"));
    Check("★ move_path 源不存在时给出路",
        !badMove.Success && badMove.Error!.Contains("find_files"), badMove.Error);

    var badDelete = await Call(fileOps["delete_path"], ("path", "ghost-dir"));
    Check("★ delete_path 路径不存在时给出路",
        !badDelete.Success && badDelete.Error!.Contains("list_dir"), badDelete.Error);

    var badList = await Call(list, ("path", "no-such-dir"));
    Check("★ list_dir 目录不存在时给出路",
        !badList.Success && badList.Error!.Contains("list_dir"), badList.Error);

    var badReadLines = await Call(fileOps["read_lines"], ("path", "no/such/file.md"));
    Check("★ read_lines 文件不存在时给出路",
        !badReadLines.Success && badReadLines.Error!.Contains("find_files"), badReadLines.Error);
}

Console.WriteLine("\n── 1b. 路径边界：读放行 / 写拒绝 ──");
// v3.6：读与写的边界**刻意不同** —— 读默认放行到整台机器，写永远只落在工作区内。
// 所以「读越界」不再是错误：那正是「读得到、写不出去」想要的形状。
var outsideProbe = Path.Combine(root, "outside-probe.txt");
await File.WriteAllTextAsync(outsideProbe, "工作区外的内容");
var escapeRead = await Call(read, ("path", "../outside-probe.txt"));
Check("★ 读可以越出工作区（读得到）",
    escapeRead.Success && escapeRead.Output.Contains("工作区外的内容"),
    escapeRead.Error ?? escapeRead.Output);

var escapeWrite = await Call(write, ("path", "../escaped.txt"), ("content", "x"));
Check("写越界路径被拒", !escapeWrite.Success && escapeWrite.Error!.Contains("越出工作区"), escapeWrite.Error);
Check("越界文件确实没被创建", !File.Exists(Path.Combine(root, "escaped.txt")));

// v3.5 审查 P2：**符号链接绕过** —— 只比字符串前缀的话，工作区里一个指向外部的链接
// 就能把读写引到区外（这是「前缀比较」这条防线唯一的结构性漏洞）。
var outsideDir = Path.Combine(root, "outside");
Directory.CreateDirectory(outsideDir);
await File.WriteAllTextAsync(Path.Combine(outsideDir, "secret.txt"), "外部机密");

var linkPath = Path.Combine(workspace, "peek");
var linkSupported = true;
try
{
    Directory.CreateSymbolicLink(linkPath, outsideDir);
}
catch (Exception)
{
    linkSupported = false;
}

if (linkSupported)
{
    // 读：现在允许穿链读到区外（读放行）。
    var linkRead = await Call(read, ("path", "peek/secret.txt"));
    Check("★ 经符号链接读区外文件被放行（读不受限）",
        linkRead.Success && linkRead.Output.Contains("外部机密"), linkRead.Error ?? linkRead.Output);

    var linkWrite = await Call(write, ("path", "peek/planted.txt"), ("content", "x"));
    Check("★ 经符号链接写区外文件被拒", !linkWrite.Success && linkWrite.Error!.Contains("越出工作区"), linkWrite.Error);
    Check("区外文件确实没被写入", !File.Exists(Path.Combine(outsideDir, "planted.txt")));

    // 反向确认：区内正常文件仍可读（修复不能误伤合法路径）
    await Call(write, ("path", "normal.txt"), ("content", "正常内容"));
    var normalRead = await Call(read, ("path", "normal.txt"));
    Check("修复未误伤区内正常路径", normalRead.Success);

    // ★ 写路径真实落点（写 TOCTOU 修复）：写必须落在**穿透链接后的真实路径**上，
    //   而不是原始 candidate —— 否则校验到落盘之间 junction 被换就能写到区外。
    //   断言看结果里回显的「真实落点」：区内链接写入时，落点必须是目标目录而不是链接路径。
    var realTargetDir = Path.Combine(workspace, "real-target");
    Directory.CreateDirectory(realTargetDir);
    var aliasDir = Path.Combine(workspace, "alias");
    Directory.CreateSymbolicLink(aliasDir, realTargetDir);

    var viaAlias = await Call(write, ("path", "alias/landed.txt"), ("content", "落点内容"));
    Check("★ 经区内符号链接写入成功", viaAlias.Success, viaAlias.Error ?? viaAlias.Output);
    Check("★ 写落点是解析穿透后的真实路径（不是链接路径）",
        viaAlias.Output.Contains("真实落点")
        && viaAlias.Output.Contains("real-target")
        && File.Exists(Path.Combine(realTargetDir, "landed.txt"))
        && File.ReadAllText(Path.Combine(realTargetDir, "landed.txt")) == "落点内容",
        viaAlias.Output);
}
else
{
    Console.WriteLine("  [SKIP] 本平台不支持创建符号链接，跳过符号链接穿透测试");
}

// ── 2. 命令工具 ────────────────────────────────────────────
Console.WriteLine("\n── 2. 命令工具 ──");

var fastOptions = new ToolkitOptions { WorkspaceRoot = workspace, CommandTimeoutSeconds = 20 };
// 沙箱：v3.9 起 run_command 不再自己 Process.Start，而是交给沙箱后端。
// 这里用默认档（auto），与生产路径一致 —— 本工程验的是工具语义，不是沙箱本身。
var run = new RunCommandTool(fastOptions, new AgentFramework.Sandbox.SandboxRegistry());

var echoResult = await Call(run, ("command", "echo hello-from-command"));
Check("命令执行成功且拿到输出", echoResult.Success && echoResult.Output.Contains("hello-from-command"), FirstLine(echoResult.Output));

var slowOptions = new ToolkitOptions { WorkspaceRoot = workspace, CommandTimeoutSeconds = 1 };
var runSlow = new RunCommandTool(slowOptions, new AgentFramework.Sandbox.SandboxRegistry());
var slowCommand = OperatingSystem.IsWindows() ? "ping -n 6 127.0.0.1" : "sleep 6";
var timeoutResult = await Call(runSlow, ("command", slowCommand));
Check("超时命令被终止", !timeoutResult.Success && timeoutResult.Error!.Contains("超时"), timeoutResult.Error);

// ── 2b. run_command 调得顺不顺（v3.28）────────────────────────
// 核心修正：退出码非零 = 命令**跑完了**的结论（编译失败 / 测试失败 / grep 无匹配都可能是非零），
// 不是工具失败。从前它走 ToolResult.Fail，主循环把正文写成 "ERROR: …"，
// 模型据此以为工具坏了或被拒，而不是去看输出里说了什么。
// 命令按解析出的 shell 选（不能在 Windows 上无条件用 cmd 语法：
// Windows + Git Bash（CI 形态）下 ShellResolver 解析到 bash，`exit /b 3` 会被当成非法参数退出码 2）。
var nonZero = await Call(run, ("command", posix ? "exit 3" : "exit /b 3"));
Check("★ 非零退出仍算「跑完了」（不伪装成工具失败）",
    nonZero.Success && nonZero.Output.Contains("exit=3"), FirstLine(nonZero.Output));

var missingCommand = await Call(run, ("command", "no-such-command-xyz-123"));
if (posix)
{
    // POSIX：命令不存在 → 127 → CommandHints 给一行 [hint] 自救。
    Check("★ 命令不存在时给一行自救提示（不是让模型干猜）",
        missingCommand.Output.Contains("[hint]"), FirstLine(missingCommand.Output));
}
else
{
    // cmd：沙箱包装把「命令不存在」表达为 exit=1 + stderr 如实带「不是内部或外部命令」——
    // 报告没被伪装成成功，模型能读到真实结论（CommandHints 对认不出的退出码不加噪音是设计）。
    Check("★ 命令不存在时给一行自救提示（不是让模型干猜）",
        missingCommand.Output.Contains("exit=") && missingCommand.Output.Contains("stderr"),
        FirstLine(missingCommand.Output));
}

Check("★ 超时提示告诉模型怎么自救（不再是死路）",
    timeoutResult.Error!.Contains("timeout 参数") && timeoutResult.Error.Contains("run_in_background"),
    timeoutResult.Error);

var boundedOptions = new ToolkitOptions
{
    WorkspaceRoot = workspace,
    CommandTimeoutSeconds = 30,
    CommandMaxTimeoutSeconds = 600,
};
var bounded = new RunCommandTool(boundedOptions, new AgentFramework.Sandbox.SandboxRegistry());

var clampedTimeout = await Call(bounded, ("command", "echo t"), ("timeout", "99999"));
Check("★ timeout 超过运维上限被压到上限（模型管不了上限）",
    clampedTimeout.Output.Contains("timeout=600s"), FirstLine(clampedTimeout.Output));

var explicitTimeout = await Call(bounded, ("command", "echo t"), ("timeout", "45"));
Check("timeout 参数生效并如实回显", explicitTimeout.Output.Contains("timeout=45s"), FirstLine(explicitTimeout.Output));

var described = await Call(bounded, ("command", "echo d"), ("description", "冒烟一条"));
Check("description 进报告（审计与追溯）",
    described.Output.Contains("# 冒烟一条"), FirstLine(described.Output));

var defaultTimeout = await Call(bounded, ("command", "echo t"));
Check("默认超时不回显（正常路径不塞噪音）",
    !defaultTimeout.Output.Contains("timeout="), FirstLine(defaultTimeout.Output));

// ★ 降级备注用稳定前缀（sandbox-degrade: / sandbox-start-fail:），
//   不再依赖中文魔法子串（"失败"/"退化"/"未能启动"）—— 文案一改旧筛选就静默失效。
var degradeHostOptions = new ToolkitOptions { WorkspaceRoot = workspace, SandboxName = "no-such-sandbox" };
var runDegrade = new RunCommandTool(degradeHostOptions, new AgentFramework.Sandbox.SandboxRegistry());
var degradeResult = await Call(runDegrade, ("command", "echo ok"));
Check("★ 沙箱回落备注带稳定前缀 sandbox-degrade:",
    degradeResult.Success && degradeResult.Output.Contains("sandbox-degrade:"),
    FirstLine(degradeResult.Output));

// 未加前缀的动态备注：包装层补前缀分类。文案故意不含任何旧魔法子串 ——
// 若筛选仍靠 Contains("失败") 等，这条会静默丢备注，断言当场抓住。
var startFailBackend = new NotesProbeBackend(
    "probe-start-fail",
    exitCode: -1,
    stdOut: string.Empty,
    notes: ["动态启动备注（文案可变）"]);
var startFailResult = await Call(
    new RunCommandTool(options, new ScriptedSandboxRegistry(startFailBackend)),
    ("command", "echo x"));
// exit=-1 时工具走 Fail，报告在 Error 里
var startFailText = startFailResult.Error ?? startFailResult.Output;
Check("★ 启动失败备注被标为 sandbox-start-fail:（不靠文案子串）",
    startFailText.Contains("sandbox-start-fail:") && startFailText.Contains("动态启动备注"),
    startFailText);

var degradeBackend = new NotesProbeBackend(
    "probe-degrade",
    exitCode: 0,
    stdOut: "ok",
    notes:
    [
        "探针后端自述",
        "动态降级备注（文案可变）",
        "sandbox-degrade:已带前缀的备注",
    ]);
var degradeProbeResult = await Call(
    new RunCommandTool(options, new ScriptedSandboxRegistry(degradeBackend)),
    ("command", "echo x"));
Check("★ 动态降级备注被标为 sandbox-degrade:，已带前缀的保留",
    degradeProbeResult.Success
    && degradeProbeResult.Output.Contains("sandbox-degrade:动态降级备注")
    && degradeProbeResult.Output.Contains("sandbox-degrade:已带前缀的备注"),
    degradeProbeResult.Output);
Check("★ 后端自述不当成降级（正常路径备注够短）",
    !degradeProbeResult.Output.Contains("sandbox-degrade:探针后端自述"),
    degradeProbeResult.Output);

// ── 2b. 常驻 shell：完整 bash 工具（description / timeout / 后台）──
Console.WriteLine("\n── 2b. 常驻 shell（完整 bash 工具）──");

var persistentShell = ShellResolver.Resolve();
if (!persistentShell.IsPosix)
{
    Console.WriteLine($"  [SKIP] 常驻 shell 需要 POSIX shell（当前解析到 {persistentShell.Id}）");
}
else
{
    var shellJobs = new JobManager();
    var shellTool = new ShellSessionTool(
        new ToolkitOptions { WorkspaceRoot = workspace, CommandTimeoutSeconds = 20, CommandMaxTimeoutSeconds = 60 },
        shellJobs);
    try
    {
        var shellRun = await Call(shellTool, ("command", "echo shell-ok"), ("description", "冒烟测试"));
        Check("★ shell 工具跑通并回报当前目录（cwd 可见）",
            shellRun.Success && shellRun.Output.Contains("shell-ok") && shellRun.Output.Contains("cwd="),
            FirstLine(shellRun.Output));
        Check("description 进报告（审计可见）",
            shellRun.Output.Contains("desc=冒烟测试"),
            FirstLine(shellRun.Output));

        // ★ v3.28：与 run_command 同一约定 —— 非零退出**不是工具失败**，
        //   它只是命令跑完后的结论（从前这里返回 Fail，主循环加 "ERROR:" 前缀误导模型）。
        var shellNonZero = await Call(shellTool, ("command", "false"));
        Check("★ shell 非零退出仍算「跑完了」（不再伪装成工具失败）",
            shellNonZero.Success && shellNonZero.Output.Contains("exit=1"), FirstLine(shellNonZero.Output));

        var shellMissing = await Call(shellTool, ("command", "no-such-cmd-xyz-9"));
        Check("★ shell 命令不存在时给 [hint] 自救",
            shellMissing.Output.Contains("[hint]"), FirstLine(shellMissing.Output));

        var clamped = await Call(shellTool, ("command", "echo t"), ("timeout", "99999"));
        Check("timeout 超过运维硬上限被夹住（模型越不过上限）", clamped.Success, FirstLine(clamped.Output));

        var background = await Call(shellTool, ("command", "echo bg-done"), ("run_in_background", "true"));
        Check("★ run_in_background 立即返回作业 id",
            background.Success && background.Output.Contains("job-"),
            FirstLine(background.Output));

        // 同一份作业池：shell 起的后台，job 工具看得见（否则模型会以为它丢了）。
        var shellJobList = await Call(
            new JobTool(new ToolkitOptions { WorkspaceRoot = workspace }, shellJobs),
            ("action", "list"));
        Check("★ shell 起的后台在 job 工具里可见（两个入口、一个作业池）",
            shellJobList.Output.Contains("job-"),
            FirstLine(shellJobList.Output));

        // ★ v3.28：id 打错时给「下一步」，不是只回一句「作业不存在」让模型瞎猜
        var jobMissing = await Call(
            new JobTool(new ToolkitOptions { WorkspaceRoot = workspace }, shellJobs),
            ("action", "status"), ("id", "job-nope"));
        Check("★ job id 打错时给出路（用 action=list 看现有作业）",
            !jobMissing.Success && jobMissing.Error!.Contains("action=list"), jobMissing.Error);
    }
    finally
    {
        shellTool.Dispose();
        shellJobs.Dispose();
    }
}

// ── 3. 联网搜索 ────────────────────────────────────────────
Console.WriteLine("\n── 3. 联网搜索（provider seam + 降级链）──");

var fakeProvider = new FakeSearchProvider("fake",
[
    new SearchHit("https://example.com/a", "第一篇文章", "这是摘要 A", "2026-01-01"),
    new SearchHit("https://example.com/b", "第二篇文章", "这是摘要 B", null),
    new SearchHit("https://example.com/c", "第三篇文章", null, null),
]);

var searchTool = new WebSearchTool(options, fakeProvider);
var searchResult = await Call(searchTool, ("query", "测试关键词"));
Check("搜索返回 markdown 链接", searchResult.Success && searchResult.Output.Contains("[第一篇文章](https://example.com/a)"), FirstLine(searchResult.Output));
Check("搜索结果含摘要与日期", searchResult.Output.Contains("这是摘要 A") && searchResult.Output.Contains("2026-01-01"));
Check("搜索末尾要求引用来源", searchResult.Output.Contains("引用上述 URL"));

var limitedOptions = new ToolkitOptions { WorkspaceRoot = workspace, SearchMaxResults = 2 };
var limitedSearch = new WebSearchTool(limitedOptions, fakeProvider);
var limitedResult = await Call(limitedSearch, ("query", "q"));
Check("SearchMaxResults 生效（只返回 2 条）",
    limitedResult.Output.Split("- [").Length - 1 == 2,
    $"{limitedResult.Output.Split("- [").Length - 1} 条");

var failing = new FakeSearchProvider("broken", []);
failing.ThrowOnSearch = true;
var fallback = new FallbackSearchProvider(failing, fakeProvider);
var fallbackTool = new WebSearchTool(options, fallback);
var fallbackResult = await Call(fallbackTool, ("query", "q"));
Check("首个后端失败时降级到下一个", fallbackResult.Success && fallbackResult.Output.Contains("第一篇文章"));
Check("降级过程有记录（不静默失败）", fallback.AttemptLog.Count >= 2, string.Join(" | ", fallback.AttemptLog));

// ── 4. 网页抓取 ────────────────────────────────────────────
Console.WriteLine("\n── 4. 网页抓取（SSRF 防护）──");

const string sampleHtml = """
<html><head><title>测试页</title><style>p{color:red}</style></head>
<body><h1>标题一</h1><p>正文段落 &amp; 实体</p><script>alert('bad')</script></body></html>
""";

var (listener, port) = StartLocalServer(sampleHtml);
var localUrl = $"http://127.0.0.1:{port}/page";

try
{
    // 4a：默认拒绝私有/回环
    var strictOptions = new ToolkitOptions { WorkspaceRoot = workspace };
    var strictFetch = new WebFetchTool(strictOptions);
    var blocked = await Call(strictFetch, ("url", localUrl));
    Check("默认拒绝访问本地地址（SSRF 防护）",
        !blocked.Success && blocked.Error!.Contains("SSRF"), blocked.Error);

    // 4b：放开后能抓，并做 HTML → 文本
    var looseOptions = new ToolkitOptions { WorkspaceRoot = workspace, AllowPrivateNetworks = true };
    var looseFetch = new WebFetchTool(looseOptions);
    var fetched = await Call(looseFetch, ("url", localUrl));
    Check("放开私有网段后可抓取", fetched.Success, FirstLine(fetched.Output));
    Check("HTML 已转纯文本（含标题与正文）",
        fetched.Output.Contains("标题一") && fetched.Output.Contains("正文段落 & 实体"));
    Check("脚本与样式已剔除",
        !fetched.Output.Contains("alert") && !fetched.Output.Contains("color:red"));

    // 4c：非 http(s) 被拒
    var badScheme = await Call(looseFetch, ("url", "file:///etc/passwd"));
    Check("非 http(s) 协议被拒", !badScheme.Success && badScheme.Error!.Contains("只支持 http"), badScheme.Error);

    // 4d：地址归一化（P1-S3）—— 这几种写法从前会落进 IPv6 分支被直接放行，
    //     而操作系统连它们时访问的其实是回环 / 内网地址。
    //     注意：SSRF 校验现在位于 SocketsHttpHandler.ConnectCallback（连接时），
    //     下列断言同时也是对「连接时校验真的生效」的钉子 —— 先检后连的 TOCTOU 路径已不存在。
    foreach (var target in new[]
             {
                 "http://[::ffff:127.0.0.1]/",
                 "http://[::ffff:a00:1]/",
                 "http://[fc00::1]/",
                 "http://[::]/",              // IPv6 未指定地址：等价本机任意地址，必须拒
                 "http://[::ffff:0.0.0.0]/",  // IPv4-mapped 未指定地址，归一后同样拒
             })
    {
        var probe = await Call(strictFetch, ("url", target));
        Check($"归一化后仍拒绝 {target}", !probe.Success && probe.Error!.Contains("SSRF"), probe.Error);
    }

    // 4e：域名形式（localhost）也走连接时校验 —— 不存在「DNS 检完再连」的窗口
    var byName = await Call(strictFetch, ("url", "http://localhost/"));
    Check("★ 域名解析到回环时连接被拒（连接时 SSRF 校验）",
        !byName.Success && byName.Error!.Contains("SSRF"), byName.Error);
}
finally
{
    listener.Stop();
}

// ── 5. 工具集接进主循环 ────────────────────────────────────
Console.WriteLine("\n── 5. 工具集 + Agent 主干端到端 ──");

var tools = new List<ITool> { write, read, list, run };
var scripted = new ScriptedLlmClient(
    "scripted",
    new ScriptedTurn(
        [],
        [new ToolCallRequest("c1", "write_file", """{"path":"from-agent.md","content":"由 agent 写入"}""")],
        "tool_calls"),
    new ScriptedTurn(["已写入文件。"], null, "stop"));

var host = new PluginHost(new PluginHostOptions { DataRoot = Path.Combine(root, "plugin-data") });
var logPath = Path.Combine(root, "agent.jsonl");
AgentRunResult runResult;
List<SessionEvent> events;

using (var log = JsonlEventLog.Open(logPath))
{
    var sink = new JsonlSink(log, host);
    var runner = new AgentRunner(scripted, () => tools, sink, new AgentOptions { SessionId = "s-tools" });
    runResult = await runner.RunAsync("把内容写进 from-agent.md");
    events = JsonlEventLog.Read(logPath).ToList();
}

Check("主循环完成", runResult.Completed);
Check("工具 schema 真的下发给模型了",
    scripted.ReceivedRequests[0].Tools.Any(t => t.Name == "write_file" && t.ParametersJsonSchema.Contains("content")),
    string.Join(",", scripted.ReceivedRequests[0].Tools.Select(t => t.Name)));
Check("工具真的写进了文件", File.Exists(Path.Combine(workspace, "from-agent.md")));
Check("文件内容正确", File.ReadAllText(Path.Combine(workspace, "from-agent.md")) == "由 agent 写入");
Check("事件链完整（user→用量→tool-requested→tool-completed→用量→assistant）",
    events.Count == 6 && events[2] is ToolCallRequestedEvent && events[3] is ToolCallCompletedEvent,
    string.Join(" → ", events.Select(e => e.GetType().Name.Replace("Event", ""))));

// 核心不变式：凡是进过模型上下文的内容，都要能从事件流原样重建
var rebuilt = SessionContextBuilder.Build(events);
Check("从事件流重建出模型上下文（模型可见即已记录）",
    rebuilt.Count == 4
    && rebuilt[0].Role == LlmRole.User
    && rebuilt[1].Role == LlmRole.Assistant
    && rebuilt[1].ToolCalls is { Count: 1 } rebuiltCalls
    && rebuiltCalls[0].ToolName == "write_file"
    && rebuilt[2].Role == LlmRole.Tool
    && rebuilt[2].ToolCallId == "c1"
    && rebuilt[3].Role == LlmRole.Assistant,
    string.Join(" → ", rebuilt.Select(m => m.ToolCalls is { Count: > 0 } ? $"{m.Role}(+tool_call)" : m.Role)));

Check("重建的工具参数与原始一致",
    rebuilt[1].ToolCalls![0].ArgumentsJson.Contains("from-agent.md"),
    rebuilt[1].ToolCalls![0].ArgumentsJson);

// ── 会话级工作区沙箱（多项目，本批新增）────────────────────
Console.WriteLine("\n── 会话级工作区沙箱 ──");

var projDir = Path.Combine(root, "session-project");
Directory.CreateDirectory(projDir);
File.WriteAllText(Path.Combine(projDir, "inner.txt"), "项目内的文件");
File.WriteAllText(Path.Combine(root, "host-only.txt"), "宿主工作区的文件");

var sessionToolkit = new ToolkitOptions
{
    WorkspaceRoot = root,                                  // 宿主级根
    WorkspaceRootResolver = () => projDir,                 // 会话钉住的项目目录
};

var sessionRead = await new ReadFileTool(sessionToolkit).InvokeAsync(
    new ToolInvocation("read_file", new Dictionary<string, string?> { ["path"] = "inner.txt" }));
Check("★ 会话钉了项目目录后，文件工具锚定项目目录", sessionRead.Success && sessionRead.Output!.Contains("项目内的文件"));

var sessionEscape = await new ReadFileTool(sessionToolkit).InvokeAsync(
    new ToolInvocation("read_file", new Dictionary<string, string?> { ["path"] = "../host-only.txt" }));
Check("★ 会话钉了项目目录后，读仍不受限（读得到宿主目录的文件）",
    sessionEscape.Success && sessionEscape.Output!.Contains("宿主工作区的文件"), sessionEscape.Error ?? sessionEscape.Output);

// 写才是被项目目录圈住的那一半 —— 同一个 ../host-only.txt，写进去必须被拒。
var sessionWriteEscape = await new WriteFileTool(sessionToolkit).InvokeAsync(
    new ToolInvocation("write_file", new Dictionary<string, string?>
    {
        ["path"] = "../host-only.txt",
        ["content"] = "不该写进去",
    }));
Check("★ 项目目录外的写被拒（写只落在会话项目目录内）",
    !sessionWriteEscape.Success && sessionWriteEscape.Error!.Contains("越出工作区"), sessionWriteEscape.Error);
Check("★ 项目目录外的文件确实没被改写",
    File.ReadAllText(Path.Combine(root, "host-only.txt")) == "宿主工作区的文件");

// ── run_command 按调用选 shell（v3.22）：白名单只管关键字，拒绝任意路径 ──
Console.WriteLine("\n── run_command 按调用选 shell ──");
{
    var shellOpts = new ToolkitOptions { WorkspaceRoot = workspace };
    var shellRunner = new RunCommandTool(shellOpts, new AgentFramework.Sandbox.SandboxRegistry());

    Check("schema 暴露 shell 参数（模型才知道能选）", shellRunner.ParametersJsonSchema.Contains("\"shell\""));

    var rejected = await shellRunner.InvokeAsync(new ToolInvocation("run_command", new Dictionary<string, string?>
    {
        ["command"] = "echo hi",
        ["shell"] = "/usr/bin/env",   // 任意路径 = 任意程序执行入口，必须拒
    }));
    Check("★ 拒绝白名单外的 shell（不给任意可执行入口）",
        !rejected.Success && rejected.Error!.Contains("只允许"), rejected.Error ?? "");

    var okShell = await shellRunner.InvokeAsync(new ToolInvocation("run_command", new Dictionary<string, string?>
    {
        ["command"] = "echo shell-ok",
        ["shell"] = "sh",
    }));
    Check("白名单内的 sh 可执行", okShell.Success && okShell.Output.Contains("shell-ok"), okShell.Output.Trim());
    Check("报告里如实标出实际 shell",
        posix
            ? okShell.Output.Contains("shell=sh")
            : okShell.Output.Contains("shell=cmd") && okShell.Output.Contains("回落"),
        okShell.Output.Split('\n')[1]);
}

// ── 原子写（v3.22）：崩溃不留半截文件，也不留临时文件 ──
Console.WriteLine("\n── 原子写 ──");
{
    var atomicOptions = new ToolkitOptions { WorkspaceRoot = workspace };
    var atomicWrite = new WriteFileTool(atomicOptions);

    Check("write_file 成功",
        (await atomicWrite.InvokeAsync(new ToolInvocation("write_file", new Dictionary<string, string?>
        {
            ["path"] = "atomic/note.txt",
            ["content"] = "第一版",
        }))).Success);

    var target = Path.Combine(workspace, "atomic", "note.txt");
    Check("内容落盘", File.Exists(target) && File.ReadAllText(target) == "第一版");

    await atomicWrite.InvokeAsync(new ToolInvocation("write_file", new Dictionary<string, string?>
    {
        ["path"] = "atomic/note.txt",
        ["content"] = "第二版",
    }));
    Check("覆盖后内容更新", File.ReadAllText(target) == "第二版");
    Check("★ 没有残留 .tmp 临时文件",
        Directory.GetFiles(Path.Combine(workspace, "atomic")).All(f => !Path.GetFileName(f).Contains(".tmp-")),
        string.Join(",", Directory.GetFiles(Path.Combine(workspace, "atomic")).Select(Path.GetFileName)));
}

Console.WriteLine($"\n═══ 结果：{passes} 通过 / {failures} 失败 ═══");
return failures == 0 ? 0 : 1;

// ═══════════════════════════ 辅助 ═══════════════════════════

static string FirstLine(string text)
{
    var line = text.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "";
    return line.Length > 80 ? line[..80] : line;
}

/// <summary>起一个只回一份 HTML 的极简 HTTP 服务器（用于验证抓取链路）。</summary>
static (TcpListener Listener, int Port) StartLocalServer(string html)
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;

    _ = Task.Run(async () =>
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync();
            }
            catch
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    using (client)
                    using (var stream = client.GetStream())
                    {
                        var buffer = new byte[8192];
                        _ = await stream.ReadAsync(buffer);

                        var body = Encoding.UTF8.GetBytes(html);
                        var head = Encoding.ASCII.GetBytes(
                            "HTTP/1.1 200 OK\r\n"
                            + "Content-Type: text/html; charset=utf-8\r\n"
                            + $"Content-Length: {body.Length}\r\n"
                            + "Connection: close\r\n\r\n");

                        await stream.WriteAsync(head);
                        await stream.WriteAsync(body);
                        await stream.FlushAsync();
                    }
                }
                catch
                {
                    // 测试服务器，忽略
                }
            });
        }
    });

    return (listener, port);
}

// ═══════════════════════════ 测试替身 ═══════════════════════════

/// <summary>固定后端的沙箱注册表：用于给 run_command 注入带各类备注的 outcome。</summary>
internal sealed class ScriptedSandboxRegistry(ISandboxBackend backend) : ISandboxRegistry
{
    public IReadOnlyCollection<string> Names => [backend.Name];

    public string? ResolveNote => null;

    public ISandboxBackend Resolve(string? name) => backend;

    public IDisposable Register(ISandboxBackend backendToRegister) => throw new NotSupportedException();
}

/// <summary>返回预设 Notes 的假沙箱后端：钉住 CommandTool 对备注的稳定前缀分类。</summary>
internal sealed class NotesProbeBackend(
    string name,
    int exitCode,
    string stdOut,
    IReadOnlyList<string> notes) : ISandboxBackend
{
    public string Name => name;

    public bool IsAvailable => true;

    public string Describe() => "探针后端自述";

    public Task<SandboxOutcome> RunAsync(SandboxRequest request, CancellationToken ct)
        => Task.FromResult(new SandboxOutcome(exitCode, stdOut, string.Empty, false, false, false, notes));
}

internal sealed class FakeSearchProvider(string name, IReadOnlyList<SearchHit> hits) : ISearchProvider
{
    public string Name => name;

    public bool ThrowOnSearch { get; set; }

    public ValueTask<IReadOnlyList<SearchHit>> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        if (ThrowOnSearch)
        {
            throw new HttpRequestException("模拟后端故障");
        }

        return ValueTask.FromResult<IReadOnlyList<SearchHit>>(hits.Take(maxResults).ToList());
    }
}

internal sealed class ScriptedLlmClient : ILlmClient
{
    private readonly Queue<ScriptedTurn> _turns = new();
    private readonly List<LlmRequest> _received = [];

    public ScriptedLlmClient(string name, params ScriptedTurn[] turns)
    {
        Name = name;
        foreach (var turn in turns)
        {
            _turns.Enqueue(turn);
        }
    }

    public string Name { get; }

    public IReadOnlyList<LlmRequest> ReceivedRequests => _received;

    public async IAsyncEnumerable<LlmStreamChunk> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        _received.Add(request);

        if (!_turns.TryDequeue(out var turn))
        {
            yield return new LlmStreamChunk.Completed("stop");
            yield break;
        }

        foreach (var piece in turn.TextPieces)
        {
            yield return new LlmStreamChunk.TextDelta(piece);
        }

        if (turn.ToolCalls is { Count: > 0 })
        {
            yield return new LlmStreamChunk.ToolCallsReady(turn.ToolCalls);
        }

        yield return new LlmStreamChunk.Completed(turn.FinishReason);
    }
}

internal sealed record ScriptedTurn(
    IReadOnlyList<string> TextPieces,
    IReadOnlyList<ToolCallRequest>? ToolCalls,
    string FinishReason);

internal sealed class JsonlSink(JsonlEventLog log, PluginHost host) : IAgentEventSink
{
    public ValueTask EmitAsync(SessionEvent sessionEvent, CancellationToken ct)
    {
        log.Append(sessionEvent);
        return ValueTask.CompletedTask;
    }

    public async ValueTask RequestApprovalAsync(ToolPreExecuteEvent toolPreExecuteEvent, CancellationToken ct)
        => await host.EmitAsync(toolPreExecuteEvent, ct).ConfigureAwait(false);
}
