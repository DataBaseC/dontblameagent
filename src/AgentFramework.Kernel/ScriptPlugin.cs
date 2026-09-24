using System.Text.Json;
using AgentFramework.Contracts;
using Jint;
using Jint.Native;
using Microsoft.Extensions.Logging;

namespace AgentFramework.Kernel;

/// <summary>
/// 脚本插件：用内嵌 JS 引擎（Jint）跑的插件 —— <b>agent 给自己写插件的主通道</b>。
///
/// <para>
/// <b>为什么是脚本而不是编译 C#</b>：目标机是自包含 exe，机器上没有 .NET SDK，
/// 编译不了 C#。所以「agent 自写插件」只能走免编译的路。
/// 官方 / 基石插件仍然走程序集插件（人写、随包分发），两条道各司其职。
/// </para>
/// <para>
/// <b>能力面是刻意收窄的</b>：Jint 默认不向脚本暴露任何 CLR 对象，宿主只塞进去几个
/// 扁平函数。所以脚本<b>碰不到文件系统、网络、进程</b> —— 这是结构性保证，不是约定。
/// 脚本要干实事只有两条路：
/// ① 注册工具，自己做转换 / 校验 / 编排；
/// ② <c>ctx.callTool()</c> 去借已经装好的工具 —— 而借谁必须在清单里
///    <c>capabilities</c> 显式声明，否则直接拒绝。
/// 这就是「未声明即拒」在脚本插件上的落点。
/// </para>
/// <para>
/// <b>线程</b>：Jint 的 Engine 不是线程安全的，而多会话可能并发调用同一个插件的工具，
/// 所以引擎调用一律串行化（<see cref="_gate"/>）。代价是脚本工具串行执行 ——
/// 对本地个人 agent 可以接受，换来的是不用为并发正确性提心吊胆。
/// Monitor 同线程可重入，因此 <c>ctx.callTool</c> 借回本插件自己的工具不会死锁；
/// 但「真正 await 之后再从别的线程调回」的跨线程重入仍不支持（见 __kernel_callTool）。
/// </para>
/// </summary>
internal sealed class ScriptPlugin : IPlugin
{
    /// <summary>
    /// 注入给脚本的引导代码：把宿主塞进来的扁平函数包成一个 <c>ctx</c> 对象。
    ///
    /// <para>
    /// 为什么绕这一层：JS 侧只该看见一个干净的 <c>ctx</c>，
    /// 而不是一堆以 <c>__kernel_</c> 开头的宿主函数（丑，且容易被误用）。
    /// </para>
    /// </summary>
    private const string BootstrapJs = """
        var ctx = {
          id: __kernel_id,
          version: __kernel_version,
          dataDir: __kernel_dataDir,
          capabilities: __kernel_capabilities,
          log: function (message) { __kernel_log(String(message)); },
          registerTool: function (config) { __kernel_registerTool(config); },
          on: function (name, handler) { __kernel_on(String(name), handler); },
          callTool: function (name, args) { return __kernel_callTool(String(name), args || {}); }
        };
        """;

    private readonly string _pluginDirectory;
    private readonly PluginManifest _manifest;
    private readonly Func<string, IReadOnlyDictionary<string, string?>?, CancellationToken, ValueTask<ToolResult>> _callTool;
    private readonly object _gate = new();
    private readonly List<string> _registeredTools = [];

    /// <summary>
    /// 当前脚本工具调用的取消令牌（Jint 回调是同步的，拿不到 InvokeAsync 的形参）。
    /// 回合被 stop 时借调也必须跟着停 —— 否则用户已经叫停，脚本还在跑。
    /// </summary>
    private static readonly AsyncLocal<CancellationToken> AmbientCallToken = new();

    private Jint.Engine? _engine;
    private IPluginContext? _ctx;

    public ScriptPlugin(
        string pluginDirectory,
        PluginManifest manifest,
        Func<string, IReadOnlyDictionary<string, string?>?, CancellationToken, ValueTask<ToolResult>> callTool)
    {
        _pluginDirectory = pluginDirectory;
        _manifest = manifest;
        _callTool = callTool;
    }

    /// <summary>装载自测的结果（<c>selftest()</c> 的返回值）；null = 脚本没定义自测。</summary>
    public string? SelfTestReport { get; private set; }

    /// <summary>本次装载注册的工具名（诊断 / 报告用）。</summary>
    public IReadOnlyList<string> RegisteredToolNames => _registeredTools;

    public Task ActivateAsync(IPluginContext ctx, CancellationToken ct = default)
    {
        _ctx = ctx;

        var scriptPath = ResolveScriptPath();
        var source = File.ReadAllText(scriptPath);

        var engine = new Jint.Engine(options => options
            .LimitRecursion(64)
            .MaxStatements(500_000)
            .TimeoutInterval(TimeSpan.FromSeconds(10))
            .Strict(false));

        // 先把引擎挂上：工具回调与事件钩子都要用它（它们都在激活之后才触发，这里只是防御）。
        lock (_gate)
        {
            _engine = engine;
        }

        ExposeHostFunctions(engine);

        try
        {
            engine.Execute(BootstrapJs);
            engine.Execute(source);

            var activate = engine.GetValue("activate");
            if (!activate.IsCallable())
            {
                throw new InvalidOperationException(
                    $"插件 {_manifest.Id}：脚本必须定义 function activate(ctx) {{ ... }}（当前没有可调用的 activate）");
            }

            activate.Call(engine.GetValue("ctx"));

            // ★ 装载即自测：主人要求「装载成功」的判定必须包含冒烟 ——
            //   否则 agent 写完就报成功，下一轮才发现是坏的。
            SelfTestReport = RunSelfTest(engine);
        }
        catch
        {
            lock (_gate)
            {
                _engine = null;
            }

            throw;
        }

        return Task.CompletedTask;
    }

    private string? RunSelfTest(Jint.Engine engine)
    {
        var selftest = engine.GetValue("selftest");
        if (!selftest.IsCallable())
        {
            return null;
        }

        var result = selftest.Call();

        // 约定：抛异常 = 失败（由上层捕获并回滚）；返回 false = 失败；其它返回值只作报告。
        if (result.IsBoolean() && !result.AsBoolean())
        {
            throw new InvalidOperationException(
                $"插件 {_manifest.Id}：selftest() 返回 false —— 自测未通过，拒绝装载");
        }

        return result.IsUndefined() || result.IsNull() ? "ok" : result.ToString();
    }

    // ── 交给脚本的宿主函数（全部是扁平的：不给 CLR 对象，就断掉了脚本乱摸宿主的可能）──

    private void ExposeHostFunctions(Jint.Engine engine)
    {
        engine.SetValue("__kernel_id", _manifest.Id);
        engine.SetValue("__kernel_version", _manifest.Version);
        engine.SetValue("__kernel_dataDir", _ctx!.DataDirectory);
        engine.SetValue("__kernel_capabilities", _manifest.Capabilities.ToArray());

        engine.SetValue("__kernel_log", new Action<string>(message =>
            _ctx.Log.LogInformation("[script] {Message}", message)));

        engine.SetValue("__kernel_registerTool", new Action<JsValue>(config =>
        {
            var obj = config.AsObject();
            var name = obj.Get("name").ToString();
            var description = obj.Get("description").ToString();
            var run = obj.Get("run");

            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException("registerTool 缺少 name");
            }

            if (!run.IsCallable())
            {
                throw new InvalidOperationException($"工具 {name} 缺少可调用的 run 函数");
            }

            var parameters = obj.Get("parameters");
            var schema = parameters.IsUndefined() || parameters.IsNull()
                ? ToolSchemas.Empty
                : parameters.IsString()
                    ? parameters.AsString()
                    : JsonSerializer.Serialize(parameters.ToObject());

            // 归属哪个工具包：脚本里可显式写 toolset；不写就按插件 id 自动成包，
            // 于是「装了这个插件」与「这一轮要不要给它上工具」就能分开开关。
            var toolsetValue = obj.Get("toolset");
            var toolset = toolsetValue.IsUndefined() || toolsetValue.IsNull() || !toolsetValue.IsString()
                ? null
                : toolsetValue.AsString();

            _ctx.RegisterTool(new ScriptTool(this, name, description, schema, run), toolset);
            _registeredTools.Add(name);
            _ctx.Log.LogInformation("脚本注册工具：{Tool}（包 {Toolset}）", name, toolset ?? _manifest.Id);
        }));

        engine.SetValue("__kernel_on", new Action<string, JsValue>((name, handler) =>
        {
            if (!handler.IsCallable())
            {
                throw new InvalidOperationException($"on({name}) 的处理器不可调用");
            }

            switch (name)
            {
                case "tools/pre-execute":
                    _ctx.On<ToolPreExecuteEvent>((e, _) => InvokeHook(handler, e));
                    break;

                default:
                    throw new InvalidOperationException(
                        $"未知事件：{name}（目前支持：tools/pre-execute）");
            }
        }));

        engine.SetValue("__kernel_callTool", new Func<JsValue, JsValue, JsValue>((nameValue, argsValue) =>
        {
            var toolName = nameValue.ToString();

            if (!IsToolCallAllowed(toolName))
            {
                throw new InvalidOperationException(
                    $"插件 {_manifest.Id} 未声明能力 tool:{toolName} —— " +
                    "要借别的工具，请在 plugin.json 的 capabilities 里写明（如 \"tool:read_file\"）");
            }

            var args = new Dictionary<string, string?>();
            if (argsValue.IsObject() && argsValue.ToObject() is IDictionary<string, object?> map)
            {
                foreach (var pair in map)
                {
                    args[pair.Key] = pair.Value?.ToString();
                }
            }

            // 同步桥：脚本是同步执行的，而工具调用是异步的。
            //
            // 为什么去掉 Task.Run：原先把等待挪到线程池线程上，而本插件的
            // InvokeScriptTool 持着 _gate —— 一旦 callTool 借回本插件自己的工具，
            // 新线程会在 _gate 上等本线程放锁，本线程又在等新线程交还结果，
            // 跨线程对锁成环，死锁。60s 超时只是把挂死降级成可诊断失败，问题本身还在。
            //
            // 同线程同步等待则不然：Monitor 同线程可重入，再入 InvokeScriptTool
            // 直接穿过 _gate，不会自己卡自己。项目全程 ConfigureAwait(false)、
            // 没有同步上下文，.AsTask().GetAwaiter().GetResult() 不会丢完成回调。
            //
            // 限制（仍在）：若被借调的工具**真正 await 之后**再从别的线程调回本插件工具，
            // 重入会落到其它线程，_gate 仍可能成环 —— 那种用法不支持。
            var callToken = AmbientCallToken.Value;
            using var bridgeCts = CancellationTokenSource.CreateLinkedTokenSource(callToken);
            bridgeCts.CancelAfter(TimeSpan.FromSeconds(60));
            var result = _callTool(toolName, args, bridgeCts.Token).AsTask().GetAwaiter().GetResult();

            var payload = JsonSerializer.Serialize(new
            {
                success = result.Success,
                output = result.Output,
                error = result.Error,
            });

            return _engine!.Evaluate("(" + payload + ")");
        }));
    }

    /// <summary>脚本路径必须落在插件目录内 —— 清单里的 script 不许用 ../ 逃出去。</summary>
    private string ResolveScriptPath()
    {
        var relative = _manifest.Script;
        if (string.IsNullOrWhiteSpace(relative)
            || Path.IsPathRooted(relative)
            || relative.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"插件 {_manifest.Id} 的 script 必须是插件目录内的相对路径：{relative}");
        }

        var rootFull = Path.GetFullPath(_pluginDirectory);
        var combined = Path.GetFullPath(Path.Combine(rootFull, relative));
        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;

        if (!combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"插件 {_manifest.Id} 的 script 越出插件目录：{relative}");
        }

        return combined;
    }

    private bool IsToolCallAllowed(string toolName)
        => _manifest.Capabilities.Any(cap =>
            string.Equals(cap, "tool:*", StringComparison.OrdinalIgnoreCase)
            || string.Equals(cap, "tool:" + toolName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 把 <c>tools/pre-execute</c> 派发给脚本钩子。
    /// 约定：钩子<b>返回 false 或一句理由字符串</b>即拦截这次工具调用（其余返回值 = 放行）。
    /// 这样脚本也能当「把关规则」用 —— 与内核 / 内置模块走的是同一条事件缝。
    /// </summary>
    private ValueTask InvokeHook(JsValue handler, ToolPreExecuteEvent e)
    {
        lock (_gate)
        {
            if (_engine is null)
            {
                return ValueTask.CompletedTask;
            }

            try
            {
                var payload = JsonSerializer.Serialize(new
                {
                    toolName = e.ToolName,
                    arguments = e.Arguments,
                });

                var jsEvent = _engine.Evaluate("(" + payload + ")");
                var verdict = handler.Call(jsEvent);

                if (verdict.IsBoolean() && !verdict.AsBoolean())
                {
                    e.Cancelled = true;
                    e.RejectReason ??= $"插件 {_manifest.Id} 的钩子拒绝";
                }
                else if (verdict.IsString() && !string.IsNullOrWhiteSpace(verdict.AsString()))
                {
                    e.Cancelled = true;
                    e.RejectReason ??= verdict.AsString();
                }
            }
            catch (Exception ex)
            {
                // 钩子的职责是「确定性把关」。炸了还放行 = fail-open，
                // 恶意/有缺陷的钩子只要抛异常就能绕过自己声称的拦截规则。
                // 与「审批默认拒」同一条纪律：说不清就拒，并把锅扣在插件头上。
                _ctx?.Log.LogError(ex, "插件 {PluginId} 的 pre-execute 钩子抛出异常（按拒绝处理）", _manifest.Id);
                e.Cancelled = true;
                e.RejectReason ??= $"插件 {_manifest.Id} 的钩子执行失败，已按拒绝处理";
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>脚本工具：把 JS 里的 <c>run(args)</c> 包成内核认识的 <see cref="ITool"/>。</summary>
    private sealed class ScriptTool(
        ScriptPlugin owner,
        string name,
        string description,
        string schema,
        JsValue run) : ITool, IToolWithSchema
    {
        public string Name => name;

        public string Description => description;

        public string ParametersJsonSchema => schema;

        public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
        {
            var previous = AmbientCallToken.Value;
            AmbientCallToken.Value = ct;
            try
            {
                return ValueTask.FromResult(owner.InvokeScriptTool(name, run, invocation.Arguments));
            }
            finally
            {
                AmbientCallToken.Value = previous;
            }
        }
    }

    private ToolResult InvokeScriptTool(string toolName, JsValue run, IReadOnlyDictionary<string, string?> arguments)
    {
        lock (_gate)
        {
            if (_engine is null)
            {
                return ToolResult.Fail($"插件 {_manifest.Id} 未装载");
            }

            try
            {
                // 参数用 JSON 往返成 JS 对象：JSON 是 JS 的子集，转义天然安全，
                // 而且省掉一堆 JsObject 手工拼装（那才是容易出错的地方）。
                var jsArgs = _engine.Evaluate("(" + JsonSerializer.Serialize(arguments) + ")");
                return ToToolResult(run.Call(jsArgs));
            }
            catch (Exception ex)
            {
                return ToolResult.Fail($"{toolName} 执行失败：{ex.Message}");
            }
        }
    }

    private static ToolResult ToToolResult(JsValue value)
    {
        if (value.IsUndefined() || value.IsNull())
        {
            return ToolResult.Ok("（脚本未返回内容）");
        }

        if (value.IsString())
        {
            return ToolResult.Ok(value.AsString());
        }

        var clr = value.ToObject();

        // 约定：返回 { success, output, error } 就能明确表达成败
        if (clr is IDictionary<string, object?> map && map.ContainsKey("success"))
        {
            var ok = map["success"] is bool flag && flag;
            var output = map.TryGetValue("output", out var outValue) ? outValue?.ToString() ?? "" : "";
            var error = map.TryGetValue("error", out var errValue) ? errValue?.ToString() : null;
            return ok ? ToolResult.Ok(output) : ToolResult.Fail(error ?? "脚本返回失败");
        }

        return ToolResult.Ok(clr is null ? "（空）" : JsonSerializer.Serialize(clr));
    }
}
