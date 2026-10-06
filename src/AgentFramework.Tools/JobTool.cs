using System.Text;
using AgentFramework.Contracts;

namespace AgentFramework.Tools;

/// <summary>
/// 后台作业工具（对齐 dsh 的 <c>jobs</c>）。
///
/// <para>
/// 一个工具多动作（<c>action</c>）：<c>start</c> / <c>status</c> / <c>output</c> / <c>kill</c> / <c>list</c>。
/// 之所以不拆成四个工具：它们共用同一份 schema 前缀，拆开等于每轮都多交几份税。
/// </para>
///
/// <para>
/// 作业池由宿主注入（<see cref="JobManager"/>）：<c>shell</c> 工具的 <c>run_in_background</c>
/// 与这里的 <c>action=start</c> 落到<b>同一张表</b> —— 两个入口、一个作业池，
/// 于是「shell 起的后台」也能被 <c>job(action=list/output)</c> 看见并收结果。
/// 生命周期挂在宿主装配作用域上，不再由本工具独有（所以本类不再 <c>IDisposable</c>）。
/// </para>
///
/// <para>
/// v3.28：错误文案补上「下一步」——<c>id</c> 打错时说清用 <c>action=list</c> 看现有作业，
/// 而不是只回一句「作业不存在」（模型只能靠猜重试）。作业**退出码非零不是工具失败**：
/// 那是作业跑完的结论，<c>status</c> / <c>output</c> 如实回报即可。
/// </para>
/// </summary>
public sealed class JobTool(ToolkitOptions options, JobManager jobs) : IToolWithRisk, ITool, IToolWithSchema
{
    /// <summary>id 打错时的统一出路：先看有哪些作业，再拿对 id 重试。</summary>
    private const string IdRescue = "；用 action=list 看现有作业，再拿对 id 重试";

    public string Name => "job";

    public ToolRisk Risk => ToolRisk.Execute;

    public string Description =>
        "后台作业：把长时间命令扔到后台跑，回合不再干等（run_command 会同步阻塞到超时）。" +
        "action=start 启动并返回作业 id；status 查是否跑完与退出码（退出码非零只是命令的结论，不是本工具失败）；" +
        "output 看累积输出（stdout+stderr 合一）；kill 终结作业；list 列出全部作业。属危险操作。";

    public string ParametersJsonSchema =>
        """
        {"type":"object","properties":{
        "action":{"type":"string","enum":["start","status","output","kill","list"],"description":"操作类型"},
        "command":{"type":"string","description":"action=start 时的完整命令"},
        "id":{"type":"string","description":"action=status/output/kill 时的作业 id（可用 action=list 查）"}},
        "required":["action"]}
        """;

    public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        invocation.Arguments.TryGetValue("action", out var action);
        action = action?.Trim().ToLowerInvariant();

        return action switch
        {
            "start" => Start(invocation),
            "status" => Status(invocation),
            "output" => Output(invocation),
            "kill" => Kill(invocation),
            "list" => List(),
            _ => ValueTask.FromResult(ToolResult.Fail(
                $"缺少或无法识别的 action「{action}」（可用：start / status / output / kill / list）")),
        };
    }

    private ValueTask<ToolResult> Start(ToolInvocation invocation)
    {
        if (!invocation.Arguments.TryGetValue("command", out var command) || string.IsNullOrWhiteSpace(command))
        {
            return ValueTask.FromResult(ToolResult.Fail("action=start 需要参数 command"));
        }

        var root = Path.GetFullPath(options.EffectiveRoot);
        var tempDirectory = Path.Combine(root, ".agent-sandbox", "tmp");
        var shell = options.EffectiveShell;

        var id = jobs.Start(command, shell, root, tempDirectory, out var error);
        if (id is null)
        {
            return ValueTask.FromResult(ToolResult.Fail(error ?? "启动作业失败"));
        }

        return ValueTask.FromResult(ToolResult.Ok(
            $"已启动后台作业 {id}\ncommand: {command}\nshell={shell.Id}\n" +
            $"用 job(action=status, id={id}) 查是否跑完，job(action=output, id={id}) 看输出。"));
    }

    private ValueTask<ToolResult> Status(ToolInvocation invocation)
    {
        var id = Arg(invocation, "id");
        if (id is null)
        {
            return ValueTask.FromResult(ToolResult.Fail("action=status 需要参数 id"));
        }

        var (found, running, exit, seconds) = jobs.Status(id);
        if (!found)
        {
            return ValueTask.FromResult(ToolResult.Fail($"作业不存在：{id}{IdRescue}"));
        }

        var state = running
            ? $"运行中（已 {seconds:F1}s）"
            : $"已结束 exit={exit}（历时 {seconds:F1}s）";
        return ValueTask.FromResult(ToolResult.Ok($"{id}: {state}"));
    }

    private ValueTask<ToolResult> Output(ToolInvocation invocation)
    {
        var id = Arg(invocation, "id");
        if (id is null)
        {
            return ValueTask.FromResult(ToolResult.Fail("action=output 需要参数 id"));
        }

        var (found, output, truncated, running) = jobs.Read(id);
        if (!found)
        {
            return ValueTask.FromResult(ToolResult.Fail($"作业不存在：{id}{IdRescue}"));
        }

        var header = running ? $"{id}（仍在运行）\n" : $"{id}（已结束）\n";
        var body = string.IsNullOrEmpty(output) ? "(暂无输出)" : output;
        if (truncated)
        {
            body += "\n...[较早输出已裁剪]";
        }

        var text = options.ShrinkResult("job", header + "--- output ---\n" + body);
        return ValueTask.FromResult(ToolResult.Ok(text));
    }

    private ValueTask<ToolResult> Kill(ToolInvocation invocation)
    {
        var id = Arg(invocation, "id");
        if (id is null)
        {
            return ValueTask.FromResult(ToolResult.Fail("action=kill 需要参数 id"));
        }

        if (!jobs.Kill(id, out var wasRunning))
        {
            return ValueTask.FromResult(ToolResult.Fail($"作业不存在：{id}{IdRescue}"));
        }

        return ValueTask.FromResult(ToolResult.Ok(wasRunning ? $"已终结作业 {id}" : $"作业 {id} 早已结束"));
    }

    private ValueTask<ToolResult> List()
    {
        var ids = jobs.Ids;
        if (ids.Count == 0)
        {
            return ValueTask.FromResult(ToolResult.Ok("当前没有后台作业"));
        }

        var report = new StringBuilder();
        foreach (var id in ids)
        {
            var (_, running, exit, seconds) = jobs.Status(id);
            report.Append(running
                ? $"{id}: 运行中（{seconds:F1}s）\n"
                : $"{id}: 已结束 exit={exit}（{seconds:F1}s）\n");
        }

        return ValueTask.FromResult(ToolResult.Ok(report.ToString().TrimEnd()));
    }

    private static string? Arg(ToolInvocation invocation, string name)
        => invocation.Arguments.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;
}
