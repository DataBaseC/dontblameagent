using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AgentFramework.Launcher;

// ═══════════════════════════════════════════════════════════
//  插件启动器
//  用法：dotnet run --project src/AgentFramework.Launcher -- --plugins <插件目录>
// ═══════════════════════════════════════════════════════════

try
{
    Console.OutputEncoding = Encoding.UTF8;
}
catch
{
    // 忽略
}

string? GetArg(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

bool HasFlag(string name) => Array.IndexOf(args, name) >= 0;

var cwd = Directory.GetCurrentDirectory();

// 单文件发布形态（Assembly.Location 为空是官方判据）。开发态不是这个形态，不涉及内嵌载荷。
var isSingleFile = false;
#pragma warning disable IL3000
// 单文件发布下 Location 恒为空串 —— 正是官方推荐的判定方式。
isSingleFile = string.IsNullOrEmpty(Assembly.GetEntryAssembly()?.Location);
#pragma warning restore IL3000

// 后台解压状态（单文件形态才相关）：页面立刻可用，解压进度/结果实时可见，
// 没就绪时点「启动宿主」会得到明确提示而不是「找不到宿主程序」。
// 为什么后台：杀软实时扫描会把「自解压出 500+ 个 dll/exe」拖到几十秒甚至几分钟，
// 同步解压会让窗口迟迟起不来 —— 那正是「启动器没把主程序带起来」的根源。
var exeSideHostExe = Path.Combine(AppContext.BaseDirectory, "host", "AgentFramework.Host.exe");
// 绿色形态：exe 旁已带 host\（pack-launcher.cmd 同时产出），零解压、秒开。
var greenMode = isSingleFile && File.Exists(exeSideHostExe);
var bundleBusy = isSingleFile && !greenMode;
var bundleProgress = "";
string? bundleError = null;
if (bundleBusy)
{
    Console.WriteLine("首次运行：正在后台解压内置的宿主程序……（页面立即可用，解压完才能启动宿主）");
    _ = Task.Run(() =>
    {
        try
        {
            bundleError = EnsureBundledHost(message => bundleProgress = message);
        }
        catch (Exception ex)
        {
            bundleError = "解压内置宿主失败：" + ex.Message;
        }
        finally
        {
            bundleBusy = false;
        }
    });
}

var pluginsDir = Path.GetFullPath(GetArg("--plugins") ?? Path.Combine(cwd, "plugins"));

// 显式指定优先；cwd 下没有 plugins 时退回随包示例插件（解压到用户数据目录的 host\plugins）。
if (!HasFlag("--plugins") && isSingleFile)
{
    var bundledPlugins = greenMode
        ? Path.Combine(AppContext.BaseDirectory, "host", "plugins")
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentFramework", "host", "plugins");
    if (!Directory.Exists(pluginsDir) && Directory.Exists(bundledPlugins))
    {
        pluginsDir = bundledPlugins;
    }
}

var profilePath = Path.GetFullPath(GetArg("--profile") ?? Path.Combine(cwd, "profiles", "default.json"));
var port = int.TryParse(GetArg("--port"), out var parsedPort) ? parsedPort : PickFreePort();

// 1) 扫描（只读清单，不加载程序集，零风险）
var catalog = PluginCatalog.Scan(pluginsDir);
var profile = PluginProfile.Load(profilePath);

// 首次运行：默认全选（像游戏启动器初次打开那样，让用户看到"能装什么"）
if (!File.Exists(profilePath) && catalog.Plugins.Count > 0)
{
    profile.EnabledPlugins = [.. catalog.Plugins.Select(p => p.Id)];
}

var state = new LauncherState
{
    PluginsDir = pluginsDir,
    ProfilePath = profilePath,
    Profile = profile,
    Catalog = catalog.Plugins,
    Errors = catalog.Errors,
};

// 桥接：后台解压任务（可能此刻仍在跑）把进度/结果同步进 state，页面 /api/state 才能看见。
if (bundleBusy)
{
    state.BundleBusy = true;
    _ = Task.Run(async () =>
    {
        while (bundleBusy)
        {
            state.BundleProgress = bundleProgress;
            await Task.Delay(300).ConfigureAwait(false);
        }

        state.BundleProgress = bundleProgress;
        state.BundleBusy = false;
        if (bundleError is not null)
        {
            state.BundleError = bundleError;
            state.Log.Add(bundleError);
            state.Log.Add("建议：改用「绿色文件夹」分发（Launcher.exe + host + desktop 文件夹一起拷，双击即用，零解压）；或把本程序加入杀软白名单后重开。");
        }
    });
}
else if (bundleError is not null)
{
    state.BundleError = bundleError;
    state.Log.Add(bundleError);
}

// 本体 dll 的位置（开发态）。打包形态的自包含 exe 在「启动宿主」点击时才解析 ——
// 后台解压可能尚未完成，启动时刻它还不存在，启动时解析才能拿到刚就绪的它。
var hostDll = FindHostDll();

// v2：启动器真正持有宿主进程 —— 停止按钮、退出清理、debug 窗里的 PID 都靠它。
// HostUrl 取约定端口（--web 默认 8090），实测时"打开工作区"一键直达。
state.Launcher = options =>
{
    if (bundleBusy)
    {
        return $"内置宿主还在解压中，请稍候几秒再点（{bundleProgress}）";
    }

    var hostExe = isSingleFile ? FindHostExe() : null;

    if (hostExe is null && hostDll is null)
    {
        return bundleError is not null
            ? bundleError
            : "找不到宿主程序 —— 先编译本体（build-host.cmd），或用 --host <路径> 指定";
    }

    var argsList = string.Join(' ', options.Select(o => o.Contains(' ') ? $"\"{o}\"" : o));

    // 自包含本体 exe（打包形态）直接启动 —— 目标机无需装 .NET；开发态退回 dotnet <dll>。
    var startInfo = hostExe is not null
        ? new ProcessStartInfo(hostExe, $"{argsList} --web --no-open")
        : new ProcessStartInfo("dotnet", $"{Quote(hostDll!)} {argsList} --web --no-open");
    startInfo.WorkingDirectory = cwd;
    startInfo.UseShellExecute = false;
    startInfo.RedirectStandardOutput = true;
    startInfo.RedirectStandardError = true;

    var process = Process.Start(startInfo);

    if (process is not null)
    {
        // F6：宿主输出进环形缓冲，/debug 信息窗显示尾部 —— 实测时不用切控制台
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) state.HostOutput.Add(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) state.HostOutput.Add("[err] " + e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    if (process is null)
    {
        return "宿主进程启动失败（Process.Start 返回 null）";
    }

    lock (state.HostGate)
    {
        state.HostProcess = process;
        state.HostUrl = "http://localhost:8090/";
    }

    // 原生窗口优先：桌面壳可用就直接开窗（不跳浏览器）；没有桌面壳才退回浏览器。
    TryOpenDesktopShell(cwd);

    _ = process.WaitForExitAsync().ContinueWith(_ =>
    {
        state.Log.Add($"宿主进程已退出（code={process.ExitCode}）");
        lock (state.HostGate)
        {
            if (ReferenceEquals(state.HostProcess, process))
            {
                state.HostProcess = null;
                state.HostUrl = null;
            }
        }
    });

    return $"已启动宿主：{Path.GetFileName(startInfo.FileName)} {argsList}（PID {process.Id}）";
};

static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;

var server = new LauncherServer(state, port);

Console.WriteLine("═══ Agent 启动器 ═══");
Console.WriteLine($"插件目录：{pluginsDir}");
Console.WriteLine($"装配档案：{profilePath}");
Console.WriteLine($"发现插件：{catalog.Plugins.Count} 个" + (catalog.Errors.Count > 0 ? $"（{catalog.Errors.Count} 个有问题的被跳过）" : ""));
Console.WriteLine($"已勾选  ：{profile.EnabledPlugins.Count} 个");
Console.WriteLine();
Console.WriteLine($"请用浏览器打开：{server.Url}");
Console.WriteLine("（Ctrl+C 退出）");
Console.WriteLine();

if (!HasFlag("--no-open"))
{
    TryOpenBrowser(server.Url);
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    await server.RunAsync(cts.Token);
}
finally
{
    server.Shutdown();   // v2：顺手 Kill 宿主进程，不留孤儿 dotnet
}

return 0;

static void TryOpenBrowser(string url)
{
    try
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
    catch
    {
        // 打不开就让用户自己点链接
    }
}

/// <summary>
/// 找本体（开发态）。这是「启动器与本体分别编译」的全部代价 —— 用一次查找换来两边的编译互不牵连。
/// </summary>
string? FindHostDll()
{
    var explicitPath = GetArg("--host") ?? Environment.GetEnvironmentVariable("AGENT_HOST_DLL");

    if (!string.IsNullOrWhiteSpace(explicitPath))
    {
        var full = Path.GetFullPath(explicitPath);
        return File.Exists(full) ? full : null;
    }

    var baseDir = AppContext.BaseDirectory;
    var configuration = baseDir.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}")
        ? "Release"
        : "Debug";

    var candidates = new List<string>
    {
        // 1) 编译启动器时顺手拷过来的那一份
        Path.Combine(baseDir, "host", "AgentFramework.Host.dll"),
        // 2) 跟启动器并排（老布局）
        Path.Combine(baseDir, "AgentFramework.Host.dll"),
    };

    // 3) 源码树里本体自己的输出目录 —— 各编各的时走这条
    var dir = new DirectoryInfo(baseDir);
    for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
    {
        candidates.Add(Path.Combine(
            dir.FullName, "AgentFramework.Host", "bin", configuration, "net10.0", "AgentFramework.Host.dll"));
    }

    return candidates.FirstOrDefault(File.Exists);
}

/// <summary>打包形态的自包含本体 exe（用户数据目录 host\AgentFramework.Host.exe，由内嵌载荷解出）。
/// --host 指到 .exe 时也认。</summary>
string? FindHostExe()
{
    var explicitPath = GetArg("--host");
    if (!string.IsNullOrWhiteSpace(explicitPath) && explicitPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
    {
        var full = Path.GetFullPath(explicitPath);
        return File.Exists(full) ? full : null;
    }

    var dataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentFramework");
    // 绿色形态优先 exe 旁（可能比数据目录里上次解压的更新）；纯单文件形态优先数据目录。
    var exeSide = Path.Combine(AppContext.BaseDirectory, "host", "AgentFramework.Host.exe");
    var inData = Path.Combine(dataDir, "host", "AgentFramework.Host.exe");
    var candidates = greenMode ? new[] { exeSide, inData } : new[] { inData, exeSide };
    return candidates.FirstOrDefault(File.Exists);
}

/// <summary>
/// 拉起桌面壳窗口（--url 形态，宿主归启动器管）。找不到桌面壳就什么都不做 ——
/// 页面照旧可以在浏览器里打开。窗口 6 秒内非零退出 → 回退浏览器（入口永不落空）。
/// </summary>
void TryOpenDesktopShell(string cwd)
{
    try
    {
        // 桌面壳自带运行时：绿色形态优先 exe 旁，纯单文件形态优先用户数据目录（兼容旧布局）。
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentFramework");
        var dataDesktop = Path.Combine(dataDir, "host", "desktop", "AgentFramework.Desktop.exe");
        var dataDesktopLegacy = Path.Combine(dataDir, "host", "AgentFramework.Desktop.exe");
        var exeSideDesktop = Path.Combine(AppContext.BaseDirectory, "host", "desktop", "AgentFramework.Desktop.exe");
        var exeSideDesktopLegacy = Path.Combine(AppContext.BaseDirectory, "host", "AgentFramework.Desktop.exe");
        var candidates = greenMode
            ? new[] { exeSideDesktop, dataDesktop, exeSideDesktopLegacy }
            : new[] { dataDesktop, exeSideDesktop, dataDesktopLegacy, exeSideDesktopLegacy };
        var desktopExe = candidates.FirstOrDefault(File.Exists);

        if (desktopExe is null)
        {
            return;
        }

        var window = Process.Start(new ProcessStartInfo(desktopExe, "--url http://localhost:8090/")
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
        });

        if (window is null)
        {
            TryOpenBrowser("http://localhost:8090/");
            return;
        }

        // 窗口起不来（比如系统缺 WebView2 Runtime）→ 回退浏览器，别让用户没有入口。
        _ = Task.Run(async () =>
        {
            try
            {
                // WaitForExitAsync 不收 TimeSpan：用「限时等退出」的手写等价物
                var exited = await Task.Run(() => window.WaitForExit(6000)).ConfigureAwait(false);
                if (exited && window.ExitCode != 0)
                {
                    TryOpenBrowser("http://localhost:8090/");
                }
            }
            catch
            {
                // 等待失败就算了
            }
        });
    }
    catch
    {
        // 窗口打不开就算了：浏览器入口还在
    }
}

/// <summary>
/// 单文件打包形态：本体发布包以 zip 资源内嵌在启动器里，首次运行（或包更新后）
/// 后台解压到用户数据目录（%LOCALAPPDATA%\AgentFramework\host）。开发态没有这个资源。
/// 返回 null = 就绪；返回字符串 = 失败原因（页面会显示，绝不静默）。
/// 不解到 exe 旁：exe 所在目录可能只读（Program Files / 下载目录 / 网盘），
/// 且杀软对「exe 旁边冒出一堆 dll」格外敏感 —— 用户数据目录既可写又少惹麻烦。
/// </summary>
string? EnsureBundledHost(Action<string>? progress = null)
{
    const string resourceName = "AgentFramework.Launcher.Payload.host-bundle.zip";
    var entry = Assembly.GetEntryAssembly();
    if (entry is null)
    {
        return null;
    }

    using var resource = entry.GetManifestResourceStream(resourceName);
    if (resource is null)
    {
        return null; // 开发态 / 未打包
    }

    // 用整包哈希当版本号：内容变了就重新解压，没变就跳过（幂等）。
    var hash = Convert.ToHexString(SHA256.HashData(resource));

    var baseDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentFramework");
    var hostDir = Path.Combine(baseDir, "host");
    var marker = Path.Combine(hostDir, ".bundle-version");

    if (File.Exists(marker))
    {
        try
        {
            if (File.ReadAllText(marker).Trim() == hash)
            {
                return null; // 已就绪
            }
        }
        catch
        {
            // 标记读不出来就当没有，走重新解压
        }
    }

    // ★ 先建目录再枚举：首跑时 baseDir 还不存在，EnumerateDirectories 会抛
    //   DirectoryNotFoundException —— 后台任务静默死掉，解压永远完不成（实测踩坑）。
    Directory.CreateDirectory(baseDir);

    progress?.Invoke($"正在解压到 {hostDir} ……");

    // 上次解压失败的残目录顺手清掉
    foreach (var stale in Directory.EnumerateDirectories(baseDir, "host.new-*"))
    {
        try { Directory.Delete(stale, recursive: true); } catch { }
    }

    var tempDir = Path.Combine(baseDir, "host.new-" + Guid.NewGuid().ToString("N")[..8]);
    try
    {
        resource.Position = 0;
        using var archive = new ZipArchive(resource, ZipArchiveMode.Read, leaveOpen: true);

        var total = archive.Entries.Count;
        var done = 0;
        foreach (var item in archive.Entries)
        {
            var targetPath = Path.GetFullPath(Path.Combine(tempDir, item.FullName));
            if (!targetPath.StartsWith(tempDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue; // 拒绝 zip 路径穿越
            }

            if (item.FullName.EndsWith("/") || item.FullName.EndsWith("\\"))
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            // 杀软实时扫描常让「刚写出的 dll/exe」短暂被锁 —— 重试几次再放弃。
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    item.ExtractToFile(targetPath, overwrite: true);
                    break;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(120 * attempt);
                }
                catch (UnauthorizedAccessException) when (attempt < 4)
                {
                    Thread.Sleep(120 * attempt);
                }
            }

            done++;
            if (done % 25 == 0)
            {
                var line = $"解压中… {done}/{total}";
                progress?.Invoke(line);
            }
        }

        File.WriteAllText(Path.Combine(tempDir, ".bundle-version"), hash);

        if (Directory.Exists(hostDir))
        {
            Directory.Delete(hostDir, recursive: true);
        }

        Directory.Move(tempDir, hostDir);
        progress?.Invoke($"完成：{hostDir}");
        return null;
    }
    catch (Exception ex)
    {
        var message = $"解压内置宿主失败：{ex.Message}（目标目录：{baseDir}）";
        try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        return message;
    }
}

static int PickFreePort()
{
    var probe = new TcpListener(IPAddress.Loopback, 0);
    probe.Start();
    var port = ((IPEndPoint)probe.LocalEndpoint).Port;
    probe.Stop();
    return port;
}
