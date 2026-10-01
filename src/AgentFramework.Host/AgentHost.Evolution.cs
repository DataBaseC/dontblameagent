using AgentFramework.Contracts;
using AgentFramework.Data;

namespace AgentFramework.Host;

public sealed partial class AgentHost
{
    // ── 进化（PLAN-memory-evolution 支柱四）：Dream / Distill ─────────
    //
    // 触发口径 = 累计回合数（桌面宿主不常驻定时器；「攒够 N 轮经验」比「过了一周」更对题）。
    // 两个动作都在后台跑、单飞（同刻至多一个）、失败吞掉不打扰主流程 —— 与 checkpoint-writer 同一套纪律。

    private long _evolutionTurns;
    private int _dreamInFlight;
    private int _distillInFlight;

    /// <summary>累计已跑过的回合数（进化触发的计时器）。</summary>
    public long EvolutionTurns => Interlocked.Read(ref _evolutionTurns);

    /// <summary>
    /// 回合结束后的进化检查点（<see cref="SendAsync(Hosting.SessionRuntime, string, CancellationToken, IReadOnlyList{LlmImage})"/> 收尾时调用）。
    /// 到周期就派后台作业；没到就这一行计数结束，零成本。
    /// </summary>
    internal void MaybeEvolve(string projectDir)
    {
        var turns = Interlocked.Increment(ref _evolutionTurns);
        var evolution = Options.Evolution;

        if (evolution.DreamEveryTurns > 0 && turns % evolution.DreamEveryTurns == 0)
        {
            DispatchDream(projectDir);
        }

        if (evolution.DistillEveryTurns > 0 && turns % evolution.DistillEveryTurns == 0)
        {
            DispatchDistill();
        }
    }

    private void DispatchDream(string projectDir)
    {
        if (Interlocked.CompareExchange(ref _dreamInFlight, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RunDreamAsync(projectDir, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // 进化永不打扰主流程
            }
            finally
            {
                Interlocked.Exchange(ref _dreamInFlight, 0);
            }
        });
    }

    private void DispatchDistill()
    {
        if (Interlocked.CompareExchange(ref _distillInFlight, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RunDistillAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // 同上
            }
            finally
            {
                Interlocked.Exchange(ref _distillInFlight, 0);
            }
        });
    }

    /// <summary>
    /// 手动跑一轮 <b>Dream</b>（记忆周期整理：去重合并 / 跨项目提升 / 频率降档 / 路径核验）。
    /// 全部动作经既有记忆事件落盘 —— 可审计、可 restore。界面「巩固」之外的全量整理走这里。
    /// </summary>
    public Task<DreamReport> RunDreamAsync(CancellationToken ct = default)
        => RunDreamAsync(_session?.ProjectDir ?? Options.WorkspaceRoot, ct);

    private async Task<DreamReport> RunDreamAsync(string? projectDir, CancellationToken ct)
    {
        var root = string.IsNullOrWhiteSpace(projectDir) ? Options.WorkspaceRoot : projectDir;
        var scopes = new List<string> { MemoryScope.Global };
        scopes.AddRange(MapScopes(ModeProfile.MemoryScopes, root ?? Options.WorkspaceRoot));
        scopes = scopes.Distinct(StringComparer.Ordinal).ToList();

        return await DreamJob.RunAsync(
            Memory,
            scopes,
            DateTimeOffset.Now,
            Options.Evolution,
            root,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 手动跑一轮 <b>Distill</b>（技能固化：从最近会话挖反复出现的调用模式 → skills/ 草稿）。
    /// 产物是纯声明式技能，<see cref="SkillLoader"/> 立即可装载；已有技能目录不覆盖。
    /// </summary>
    public Task<DistillReport> RunDistillAsync(CancellationToken ct = default)
    {
        var batches = new List<(string SessionId, IReadOnlyList<SessionEvent> Events)>();

        try
        {
            var files = Directory
                .EnumerateFiles(Options.SessionsDir, "*.jsonl")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(Math.Max(1, Options.Evolution.DistillRecentSessions));

            foreach (var file in files)
            {
                try
                {
                    batches.Add((Path.GetFileNameWithoutExtension(file), JsonlEventLog.Read(file).ToList()));
                }
                catch
                {
                    // 坏日志跳过 —— 挖矿不能被一个坏文件拖死
                }
            }
        }
        catch
        {
            // 目录不存在 / 无权限：挖不到矿不算错误
        }

        var report = DistillJob.Run(Options.WorkspaceRoot, batches, Options.Evolution, ReloadSkills);

        if (report.SkillsWritten.Count > 0)
        {
            Console.WriteLine($"[evolution] Distill 固化 {report.SkillsWritten.Count} 个技能草稿：{string.Join(", ", report.SkillsWritten)}");
        }

        return Task.FromResult(report);
    }
}
