using System.Diagnostics;
using System.Text;
using AgentFramework.Contracts;

namespace AgentFramework.Tools;

/// <summary>
/// 执行 shell 命令。
///
/// 这是最危险的一个工具 —— 因此它<b>故意不做任何"聪明"的安全分析</b>
/// （命令白名单/黑名单永远能被绕过），而是把把关交给审批事件：
/// 让宿主或插件用 <c>ToolPreExecuteEvent</c> 决定放不放行。
/// </summary>
public sealed class RunCommandTool(ToolkitOptions options) : ITool, IToolWithSchema
{
    public string Name => "run_command";

    public string Description => "在工作区目录下执行一条 shell 命令并返回标准输出与退出码。属危险操作。";

    public string ParametersJsonSchema =>
        """{"type":"object","properties":{"command":{"type":"string","description":"要执行的完整命令"}},"required":["command"]}""";

    public async ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        if (!invocation.Arguments.TryGetValue("command", out var command) || string.IsNullOrWhiteSpace(command))
        {
            return ToolResult.Fail("缺少参数 command");
        }

        var isWindows = OperatingSystem.IsWindows();
        var startInfo = new ProcessStartInfo
        {
            FileName = isWindows ? "cmd.exe" : "/bin/sh",
            WorkingDirectory = Path.GetFullPath(options.EffectiveRoot),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (isWindows)
        {
            // ★ ArgumentList 的 Win32 引号规则 cmd.exe 不认（.NET 官方文档明确警告不要
            //   对 cmd/bat 用 ArgumentList）：含引号的命令会被解析成畸形转义。
            //   安全性不受影响 —— 命令内容来自模型，把关本来就在审批层（本工具的设计原则）。
            startInfo.Arguments = $"/c {command}";
        }
        else
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(command);
        }

        using var process = new Process { StartInfo = startInfo };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var ioGate = new object();
        var truncated = false;

        // 过程中封顶（P1-F4）：从前是「先全收进内存、最后才截断」——
        // 一条 npm install 的输出就足以把内存吃掉。现在到上限即停收。
        void AppendCapped(StringBuilder target, string line)
        {
            lock (ioGate)
            {
                if (target.Length < options.MaxCommandOutputChars)
                {
                    target.AppendLine(line);
                }
                else
                {
                    truncated = true;
                }
            }
        }

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                AppendCapped(stdout, e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                AppendCapped(stderr, e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.CommandTimeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            // 再同步等一次，确保异步输出流已经排空（否则可能丢尾部输出）
            process.WaitForExit();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 用户叫停与超时是两件事，报错也该分开
            KillQuietly(process);
            return ToolResult.Fail("命令已被取消");
        }
        catch (OperationCanceledException)
        {
            KillQuietly(process);
            return ToolResult.Fail($"命令超时（{options.CommandTimeoutSeconds}s），已被终止");
        }

        var report = new StringBuilder();
        report.Append("exit=").Append(process.ExitCode).Append('\n');
        report.Append("--- stdout ---\n").Append(Truncate(stdout.ToString()));

        var errText = stderr.ToString();
        if (errText.Length > 0)
        {
            report.Append("\n--- stderr ---\n").Append(Truncate(errText));
        }

        if (truncated)
        {
            report.Append("\n...[输出超过上限，执行过程中已截断]");
        }

        // L2：命令回显是长任务里的另一个大头（构建日志、测试输出尤其）
        var text = options.ShrinkResult("run_command", report.ToString());
        return process.ExitCode == 0 ? ToolResult.Ok(text) : ToolResult.Fail(text);
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 进程可能已经退出 —— 忽略
        }
    }

    private string Truncate(string value)
        => value.Length <= options.MaxCommandOutputChars
            ? value
            : string.Concat(value.AsSpan(0, options.MaxCommandOutputChars), "\n...[输出已截断]");
}
