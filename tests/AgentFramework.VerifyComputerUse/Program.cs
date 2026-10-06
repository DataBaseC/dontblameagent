using AgentFramework.Contracts;
using AgentFramework.Plugins.ComputerUse.Desktop;
using AgentFramework.Plugins.ComputerUse.Tools;
using static AgentFramework.Harness.Suite;

// ═══════════════════════════════════════════════════════════
//  Computer Use 插件 垂直切片验证
//    键名解析（纯逻辑）→ 驱动能力自述 → 工具参数校验 → 不支持平台如实拒绝
//  不需要 GUI / 不需要真操作桌面：验的是「出错路径」与「纯逻辑」这两块最该钉死的。
// ═══════════════════════════════════════════════════════════


Console.WriteLine("═══ Computer Use 插件验证 ═══");

async ValueTask<ToolResult> Call(ITool tool, params (string Key, string Value)[] args)
{
    var dict = args.ToDictionary(a => a.Key, a => (string?)a.Value);
    return await tool.InvokeAsync(new ToolInvocation(tool.Name, dict));
}

// ── 1. 键名解析（纯逻辑：最容易错、也最该测）──
Console.WriteLine("\n── 1. 键名解析 ──");

Check("★ ctrl+c → [Ctrl, c]",
    KeyNames.Parse("ctrl+c") is ["Ctrl", "c"],
    string.Join('+', KeyNames.Parse("ctrl+c")));

Check("大小写与组合：ctrl+shift+T → [Ctrl, Shift, T]",
    KeyNames.Parse("ctrl+shift+T") is ["Ctrl", "Shift", "T"]);

Check("别名：enter → Return", KeyNames.Parse("enter") is ["Return"]);
Check("别名：esc → Escape / cmd → Win",
    KeyNames.Parse("esc") is ["Escape"] && KeyNames.Parse("cmd+space") is ["Win", "Space"]);
Check("功能键：F5 原样（大写）", KeyNames.Parse("f5") is ["F5"]);
Check("方向键别名：up → ArrowUp", KeyNames.Parse("up") is ["ArrowUp"]);
Check("连字符也当分隔符：alt-tab → [Alt, Tab]", KeyNames.Parse("alt-tab") is ["Alt", "Tab"]);

var emptyThrew = false;
try { KeyNames.Parse("   "); } catch (DesktopException) { emptyThrew = true; }
Check("★ 空键名抛错（不静默）", emptyThrew);

var unknownThrew = false;
try { KeyNames.Parse("ctrl+no_such_key"); } catch (DesktopException) { unknownThrew = true; }
Check("★ 无法识别的键名抛错（不静默丢字）", unknownThrew);

Check("修饰键识别：Ctrl/Shift/Win 是，字母不是",
    KeyNames.IsModifier("Ctrl") && KeyNames.IsModifier("Win") && !KeyNames.IsModifier("c"));

// ── 2. 驱动能力自述 ──
Console.WriteLine("\n── 2. 驱动与能力自述 ──");

var driver = DesktopDriverFactory.Create();
Check("★ 工厂在当前平台总能给出一个驱动（不支持也不为空）", driver is not null, driver.Name);
var describe = driver.Describe();
Check("驱动自述不为空", !string.IsNullOrWhiteSpace(describe), describe.Length > 50 ? describe[..50] + "…" : describe);

var unsupported = new UnsupportedDesktopDriver("测试用不支持平台");
Check("★ 兜底驱动 IsAvailable=false，且说明含「不可用」",
    !unsupported.IsAvailable && unsupported.Describe().Contains("不可用"), unsupported.Describe());
Check("兜底驱动的能力全 false",
    !unsupported.Capabilities.Screenshot && !unsupported.Capabilities.Pointer && !unsupported.Capabilities.Keyboard);

var threw = false;
try { unsupported.Click(1, 2, MouseButton.Left, 1); } catch (DesktopException) { threw = true; }
Check("★ 兜底驱动操作时抛 DesktopException（而不是静默成功）", threw);

// ── 3. 工具在「不支持平台」下如实拒绝 ──
Console.WriteLine("\n── 3. 工具失败路径（不支持平台）──");

var click = new ClickTool(unsupported);
var clickResult = await Call(click, ("x", "10"), ("y", "20"));
Check("★ click 在不支持平台返回失败、且原因可读",
    !clickResult.Success && (clickResult.Error?.Contains("不支持") ?? false),
    clickResult.Error);

var shot = new ScreenshotTool(unsupported);
var shotResult = await Call(shot);
Check("screenshot 在不支持平台返回失败",
    !shotResult.Success && (shotResult.Error?.Contains("不支持") ?? false),
    shotResult.Error);

var keyTool = new KeyTool(unsupported);
var keyResult = await Call(keyTool, ("keys", "ctrl+c"));
Check("key 在不支持平台返回失败", !keyResult.Success, keyResult.Error);

// ── 4. 参数校验（不碰驱动就该拒掉的，一律别走到驱动）──
Console.WriteLine("\n── 4. 参数校验 ──");

var noX = await Call(click, ("y", "20"));
Check("★ click 缺 x → 报「缺少参数 x」",
    !noX.Success && (noX.Error?.Contains("x") ?? false), noX.Error);

var badButton = await Call(click, ("x", "1"), ("y", "2"), ("button", "wheel"));
Check("★ click 非法鼠标键 → 明确拒绝",
    !badButton.Success && (badButton.Error?.Contains("未知鼠标键") ?? false), badButton.Error);

var badRegion = await Call(shot, ("region", "0,0,400"));
Check("★ screenshot region 格式非法 → 明确拒绝",
    !badRegion.Success && (badRegion.Error?.Contains("区域格式非法") ?? false), badRegion.Error);

var scroll = new ScrollTool(unsupported);
var zeroScroll = await Call(scroll, ("x", "1"), ("y", "2"));
Check("★ scroll 两个方向都为 0 → 拒绝（无意义的调用不当成成功）",
    !zeroScroll.Success, zeroScroll.Error);

var type = new TypeTool(unsupported);
var noText = await Call(type, ("text", "   "));
Check("type 缺文本 → 拒绝", !noText.Success, noText.Error);

var focus = new FocusWindowTool(unsupported);
var noWindow = await Call(focus);
Check("focus_window 缺 window → 拒绝",
    !noWindow.Success && (noWindow.Error?.Contains("window") ?? false), noWindow.Error);

// ── 5. 与驱动无关的工具 ──
Console.WriteLine("\n── 5. 纯本地工具 ──");

var wait = new WaitTool();
var waitResult = await Call(wait, ("ms", "1"));
Check("wait 正常返回", waitResult.Success, waitResult.Output);

var waitClamped = await Call(wait, ("ms", "999999"));
Check("wait 上限被夹住（不阻塞回合）", waitClamped.Success, waitClamped.Output);

// ── 6. 工具契约完整性 ──
Console.WriteLine("\n── 6. 工具契约完整性 ──");

var tools = new ITool[]
{
    new ScreenshotTool(driver), new ScreenSizeTool(driver), new ClickTool(driver), new MoveTool(driver),
    new DragTool(driver), new ScrollTool(driver), new TypeTool(driver), new KeyTool(driver), new WaitTool(),
    new ListWindowsTool(driver), new FocusWindowTool(driver), new OpenAppTool(driver), new OpenUrlTool(driver),
};

Check("★ 13 个工具（对齐 dsh-orb 的能力面）", tools.Length == 13, tools.Length.ToString());
Check("工具名唯一且非空",
    tools.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count() == tools.Length
    && tools.All(t => !string.IsNullOrWhiteSpace(t.Name)),
    string.Join(",", tools.Select(t => t.Name)));

var schemasOk = tools.All(t => t is IToolWithSchema s
    && s.ParametersJsonSchema.TrimStart().StartsWith('{')
    && s.ParametersJsonSchema.TrimEnd().EndsWith('}'));
Check("★ 每个工具都带合法形状的参数 schema", schemasOk);

Check("每个工具都有非空描述（模型靠它选对工具）", tools.All(t => !string.IsNullOrWhiteSpace(t.Description)));

Console.WriteLine($"\n结果：{passes} 通过 / {failures} 失败");
return failures == 0 ? 0 : 1;
