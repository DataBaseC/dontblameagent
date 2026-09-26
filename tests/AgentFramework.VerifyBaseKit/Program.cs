using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AgentFramework.Contracts;
using AgentFramework.Host;

// ═══════════════════════════════════════════════════════════
//  基石插件垂直切片验证
//    工作区 seam → 编程工具包 → 写作工具包 → 控制台（UI 注入）→ 卸载
//  全程不需要 API key / 联网 / 真实模型：只碰插件、工具与 HTTP 端点。
// ═══════════════════════════════════════════════════════════

var passes = 0;
var failures = 0;

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

var root = Path.Combine(Path.GetTempPath(), "af-basekit-verify", Guid.NewGuid().ToString("N")[..8]);
var workspace = Path.Combine(root, "workspace");
var sessions = Path.Combine(root, "sessions");
var pluginsDir = Path.Combine(root, "plugins");
Directory.CreateDirectory(workspace);
Directory.CreateDirectory(sessions);

Console.WriteLine("═══ 基石插件验证 ═══");
Console.WriteLine($"工作目录：{root}");

// ── 0. 组装插件目录（dll 走 ProjectReference 产物，静态文件走 baseplugins/）──
(string Id, string Dll)[] binaries =
[
    ("writing-kit", "AgentFramework.Plugins.WritingKit.dll"),
    ("console-kit", "AgentFramework.Plugins.ConsoleKit.dll"),
];

foreach (var (id, dll) in binaries)
{
    var destination = Path.Combine(pluginsDir, id);
    Directory.CreateDirectory(destination);
    File.Copy(Path.Combine(AppContext.BaseDirectory, dll), Path.Combine(destination, dll), overwrite: true);

    var staticSource = Path.Combine(AppContext.BaseDirectory, "baseplugins", id);
    if (Directory.Exists(staticSource))
    {
        foreach (var file in Directory.GetFiles(staticSource))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }
    }
}

var options = new HostOptions
{
    WorkspaceRoot = workspace,
    SessionsDir = sessions,
    SessionId = "default",
    PluginsDir = pluginsDir,
    ApprovalPolicy = static _ => ApprovalDecision.Allow,
};

await using var host = await AgentHost.CreateAsync(options);
var kernel = host.Plugins;

static Dictionary<string, string?> Args(params (string Key, string Value)[] pairs)
    => pairs.ToDictionary(p => p.Key, p => (string?)p.Value);

// ═══ 1. seam：宿主把工作区交给插件 ═══
Console.WriteLine("\n── 1. 工作区 seam ──");
Check("★ 宿主向内核提供了 IWorkspaceService（插件唯一的文件正门）",
    kernel.ProvidedServiceTypes.Contains(typeof(IWorkspaceService)),
    string.Join(",", kernel.ProvidedServiceTypes.Select(t => t.Name)));

// ═══ 2. 领域插件装载 ═══
Console.WriteLine("\n── 2. 领域插件装载（A 通道 · 程序集插件）──");
var loaded = host.LoadedPlugins.Select(p => p.Id).ToList();
Check("★ writing-kit 装载成功", loaded.Contains("writing-kit"), string.Join(",", host.FailedPlugins));
Check("★ console-kit 装载成功", loaded.Contains("console-kit"), string.Join(",", host.FailedPlugins));

string[] coreFileTools =
[
    "edit_file", "grep_files", "find_files", "read_lines", "make_dir", "move_path", "delete_path",
];
string[] writingTools =
[
    "word_count", "paragraph_report", "repeat_words", "check_punctuation", "outline",
];

Check("★ core 收齐文件链（插件卸了也不断手）",
    coreFileTools.All(host.ToolNames.Contains),
    string.Join(",", coreFileTools.Where(t => !host.ToolNames.Contains(t))));
Check("writing-kit 注册了 5 个写作工具",
    writingTools.All(host.ToolNames.Contains),
    string.Join(",", writingTools.Where(t => !host.ToolNames.Contains(t))));
Check("工具来源正确：文件链=官方 core，写作=writing-kit",
    host.ToolSources.GetValueOrDefault("edit_file")?.Contains("official") == true
    && host.ToolSources.GetValueOrDefault("word_count") == "writing-kit");

// ═══ 3. 文件链 · 编辑 ═══
Console.WriteLine("\n── 3. edit_file（agent 从「整篇重写」升级为「局部改」）──");
var sample = Path.Combine(workspace, "sample.cs");
File.WriteAllText(sample, "line one\nvar a = 1;\nvar b = 2;\n");

var edit = await kernel.InvokeToolAsync("edit_file",
    Args(("path", "sample.cs"), ("old_string", "var a = 1;"), ("new_string", "var a = 42;")));
Check("★ 精确替换成功且写回文件",
    edit.Success && File.ReadAllText(sample).Contains("var a = 42;"),
    edit.Success ? edit.Output.Split('\n')[0] : edit.Error);

File.WriteAllText(sample, "x = 1;\nx = 1;\n");
var duplicate = await kernel.InvokeToolAsync("edit_file",
    Args(("path", "sample.cs"), ("old_string", "x = 1;"), ("new_string", "x = 2;")));
Check("★ 多处匹配时拒绝执行（明确失败优于静默猜错）",
    !duplicate.Success && duplicate.Error!.Contains("2 次"),
    duplicate.Error ?? duplicate.Output);

var replaceAll = await kernel.InvokeToolAsync("edit_file",
    Args(("path", "sample.cs"), ("old_string", "x = 1;"), ("new_string", "x = 2;"), ("replace_all", "true")));
Check("replace_all=true 时全部替换",
    replaceAll.Success && File.ReadAllText(sample) == "x = 2;\nx = 2;\n",
    replaceAll.Success ? "内容已核对" : replaceAll.Error);

File.WriteAllText(sample, "alpha\r\nbeta\r\n");
var crlf = await kernel.InvokeToolAsync("edit_file",
    Args(("path", "sample.cs"), ("old_string", "alpha\nbeta"), ("new_string", "alpha\nBETA")));
Check("★ 换行符宽容：用 LF 写的 old_string 能改 CRLF 文件，且不改动无关字节",
    crlf.Success && File.ReadAllText(sample) == "alpha\r\nBETA\r\n",
    crlf.Success ? "换行符保持 CRLF" : crlf.Error);

var outsideFile = Path.Combine(root, "outside.txt");
File.WriteAllText(outsideFile, "secret");
var escape = await kernel.InvokeToolAsync("edit_file",
    Args(("path", outsideFile), ("old_string", "secret"), ("new_string", "hacked")));
Check("★ 插件写操作越界被拒（写只能落在工作区内）",
    !escape.Success && escape.Error!.Contains("越出工作区") && File.ReadAllText(outsideFile) == "secret",
    escape.Error ?? escape.Output);

// ═══ 4. 文件链 · 检索与读 ═══
Console.WriteLine("\n── 4. grep / find / read_lines ──");
Directory.CreateDirectory(Path.Combine(workspace, "src"));
File.WriteAllText(Path.Combine(workspace, "src", "a.cs"), "class A\n{\n    void Go() { }\n}\n");
File.WriteAllText(Path.Combine(workspace, "src", "b.md"), "搜索目标：这里\n");

var grep = await kernel.InvokeToolAsync("grep_files", Args(("pattern", "void Go")));
Check("grep_files 命中并带「文件:行号」",
    grep.Success && grep.Output.Contains("src/a.cs:3:"),
    grep.Output.Split('\n')[0]);

var grepFiltered = await kernel.InvokeToolAsync("grep_files",
    Args(("pattern", "搜索目标"), ("include", "*.cs")));
Check("grep_files 的 include 过滤生效（*.cs 不该命中 .md）",
    grepFiltered.Success && !grepFiltered.Output.Contains("b.md"),
    grepFiltered.Output.Split('\n')[0]);

var grepRegex = await kernel.InvokeToolAsync("grep_files",
    Args(("pattern", "void \\w+\\(\\)"), ("regex", "true")));
Check("grep_files 支持正则（regex=true）",
    grepRegex.Success && grepRegex.Output.Contains("src/a.cs:3:"),
    grepRegex.Output.Split('\n')[0]);

File.WriteAllText(sample, "l1\nl2\nl3\nl4\n");
var readLines = await kernel.InvokeToolAsync("read_lines",
    Args(("path", "sample.cs"), ("start", "2"), ("end", "3")));
Check("read_lines 带行号且只给请求的范围",
    readLines.Success && readLines.Output.Contains("2 | l2") && readLines.Output.Contains("3 | l3")
    && !readLines.Output.Contains("l4"),
    readLines.Success ? readLines.Output.Split('\n')[1].Trim() : readLines.Error);

var find = await kernel.InvokeToolAsync("find_files", Args(("pattern", "*.cs")));
Check("find_files 按 glob 找文件名（*.cs 跨目录命中）",
    find.Success && find.Output.Contains("src/a.cs") && !find.Output.Contains("b.md"),
    find.Output.Split('\n')[0]);

// ═══ 5. 文件链 · 搬移 ═══
Console.WriteLine("\n── 5. 目录与搬移 ──");
var makeDir = await kernel.InvokeToolAsync("make_dir", Args(("path", "out/dir")));
Check("make_dir 建目录（含中间层级）",
    makeDir.Success && Directory.Exists(Path.Combine(workspace, "out", "dir")),
    makeDir.Success ? makeDir.Output : makeDir.Error);

File.WriteAllText(Path.Combine(workspace, "out", "dir", "x.txt"), "hi");
var move = await kernel.InvokeToolAsync("move_path",
    Args(("from", "out/dir/x.txt"), ("to", "out/y.txt")));
Check("move_path 移动文件",
    move.Success && File.Exists(Path.Combine(workspace, "out", "y.txt")),
    move.Success ? move.Output : move.Error);

var moveEscape = await kernel.InvokeToolAsync("move_path",
    Args(("from", "out/y.txt"), ("to", outsideFile)));
Check("★ move_path 目标越界被拒（起点与终点都过边界检查）",
    !moveEscape.Success && moveEscape.Error!.Contains("越出工作区"),
    moveEscape.Error ?? moveEscape.Output);

var deleteNonEmpty = await kernel.InvokeToolAsync("delete_path", Args(("path", "out")));
Check("★ delete_path 删非空目录默认拒绝（门槛是结构性的）",
    !deleteNonEmpty.Success && deleteNonEmpty.Error!.Contains("recursive"),
    deleteNonEmpty.Error ?? deleteNonEmpty.Output);

var deleteRecursive = await kernel.InvokeToolAsync("delete_path",
    Args(("path", "out"), ("recursive", "true")));
Check("delete_path recursive=true 才真删",
    deleteRecursive.Success && !Directory.Exists(Path.Combine(workspace, "out")),
    deleteRecursive.Success ? deleteRecursive.Output : deleteRecursive.Error);

var deleteRoot = await kernel.InvokeToolAsync("delete_path", Args(("path", ".")));
Check("★ delete_path 拒删工作区根",
    !deleteRoot.Success,
    deleteRoot.Error ?? deleteRoot.Output);

// ═══ 6. writing-kit ═══
Console.WriteLine("\n── 6. 写作工具包 ──");
var prose = "你好，世界。这是测试。\n他在写小说。小说里有他的影子。小说就是这样。\n";

var wordCount = await kernel.InvokeToolAsync("word_count", Args(("text", prose)));
Check("★ word_count 汉字数正确（8 + 19 = 27）",
    wordCount.Success && ExtractInt(wordCount.Output, "汉字数") == 27,
    ExtractInt(wordCount.Output, "汉字数")?.ToString() ?? wordCount.Error);

var paragraph = await kernel.InvokeToolAsync("paragraph_report", Args(("text", prose)));
Check("paragraph_report 报出段落数（2 段）",
    paragraph.Success && ExtractInt(paragraph.Output, "段落数") == 2,
    ExtractInt(paragraph.Output, "段落数")?.ToString() ?? paragraph.Error);

var repeat = await kernel.InvokeToolAsync("repeat_words", Args(("text", prose)));
Check("★ repeat_words 检出重复词「小说」",
    repeat.Success && repeat.Output.Contains("小说"),
    repeat.Output.Split('\n').Skip(1).FirstOrDefault());

var punctuation = await kernel.InvokeToolAsync("check_punctuation",
    Args(("text", "这是一句话,还在继续。第二句。。结束\n")));
Check("★ check_punctuation 检出中英标点混用",
    punctuation.Success && punctuation.Output.Contains("标点混用"),
    punctuation.Output.Split('\n').FirstOrDefault(l => l.Contains("混用")));
Check("★ check_punctuation 检出重复标点",
    punctuation.Success && punctuation.Output.Contains("重复标点"),
    punctuation.Output.Split('\n').FirstOrDefault(l => l.Contains("重复")));

var outline = await kernel.InvokeToolAsync("outline", Args(("text", prose)));
Check("outline 抽取骨架（含段数）",
    outline.Success && outline.Output.Contains("共 2 段"),
    outline.Output.Split('\n')[0]);

var writingFile = Path.Combine(workspace, "draft.md");
File.WriteAllText(writingFile, "第一段。\n第二段。\n");
var fromFile = await kernel.InvokeToolAsync("word_count", Args(("path", "draft.md")));
Check("写作工具同样支持「直接读工作区文件」（path 参数）",
    fromFile.Success && ExtractInt(fromFile.Output, "汉字数") == 6,
    ExtractInt(fromFile.Output, "汉字数")?.ToString() ?? fromFile.Error);

// ═══ 7. console-kit · UI 注入 ═══
Console.WriteLine("\n── 7. 控制台插件与 UI 注入 seam ──");
var ui = PluginUi.Describe(pluginsDir, "console-kit");
Check("★ 清单里的 ui 声明被宿主读到（styles/scripts）",
    ui is not null && ui.Styles.Contains("theme.css") && ui.Scripts.Contains("theme.js"),
    ui is null ? "Describe 返回 null" : $"{string.Join(",", ui.Styles)} | {string.Join(",", ui.Scripts)}");

Check("★ PluginUi 只服务清单里声明过的文件（未声明即拒）",
    PluginUi.ResolveFile(pluginsDir, "console-kit", "panel.html") is null,
    "panel.html 未在 ui 里声明，应取不到");

Check("★ PluginUi 拒绝带 .. 的路径",
    PluginUi.ResolveFile(pluginsDir, "console-kit", "../plugin.json") is null);

Check("PluginUi 能解析已声明且存在的文件",
    PluginUi.ResolveFile(pluginsDir, "console-kit", "theme.css") is not null);

var port = PickFreePort();
using var server = new WebUiServer(host, options, port);
using var serverCts = new CancellationTokenSource();
_ = server.RunAsync(serverCts.Token);
await Task.Delay(500);   // 等监听起来（与 VerifyWeb 同一手法）
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
var baseUrl = server.Url.TrimEnd('/');

var page = await http.GetStringAsync(baseUrl + "/");
Check("★ 首页被注入了插件样式（link）与脚本（script）",
    page.Contains("/plugin-ui?id=console-kit&file=theme.css") && page.Contains("theme.js"),
    $"页面长度 {page.Length}");

var css = await http.GetStringAsync(baseUrl + "/plugin-ui?id=console-kit&file=theme.css");
Check("★ /plugin-ui 端到端可取到样式",
    css.Contains("data-ck-theme"),
    $"{css.Length} 字符");

var notDeclared = await http.GetAsync(baseUrl + "/plugin-ui?id=console-kit&file=plugin.json");
Check("★ 未声明的界面文件返回 404（不是「插件目录随便读」）",
    notDeclared.StatusCode == HttpStatusCode.NotFound,
    notDeclared.StatusCode.ToString());

var panel = await http.GetStringAsync(baseUrl + "/plugin-panel?id=console-kit");
Check("插件面板 panel.html 仍走原通道可用",
    panel.Contains("界面控制台"),
    $"{panel.Length} 字符");

// ═══ 8. 生命周期 ═══
Console.WriteLine("\n── 8. 卸载即撤销（core 文件链不随插件走）──");
var unloaded = await kernel.UnloadAsync("writing-kit");
Check("★ 卸载 writing-kit 后它的 5 个写作工具立刻从工具表消失",
    unloaded && writingTools.All(t => !host.ToolNames.Contains(t)),
    string.Join(",", writingTools.Where(host.ToolNames.Contains)));
Check("★ core 文件链不受插件卸载影响（关掉插件不断手）",
    coreFileTools.All(host.ToolNames.Contains),
    string.Join(",", coreFileTools.Where(t => !host.ToolNames.Contains(t))));
Check("官方工具不受影响（只撤插件自己挂的）",
    host.ToolNames.Contains("read_file") && host.ToolNames.Contains("write_file"));

Console.WriteLine($"\n结果：{passes} 通过 / {failures} 失败");

try
{
    Directory.Delete(root, recursive: true);
}
catch (Exception)
{
    // 临时目录清不掉不影响结论（Windows 上偶发文件占用）
}

return failures == 0 ? 0 : 1;

static int? ExtractInt(string text, string label)
{
    var index = text.IndexOf(label, StringComparison.Ordinal);
    if (index < 0)
    {
        return null;
    }

    var rest = text[(index + label.Length)..];
    var digits = new string([.. rest.SkipWhile(c => !char.IsAsciiDigit(c)).TakeWhile(char.IsAsciiDigit)]);
    return int.TryParse(digits, out var value) ? value : null;
}

static int PickFreePort()
{
    var probe = new TcpListener(IPAddress.Loopback, 0);
    probe.Start();
    var port = ((IPEndPoint)probe.LocalEndpoint).Port;
    probe.Stop();
    return port;
}
