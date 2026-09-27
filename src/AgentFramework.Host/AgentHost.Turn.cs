using System.Text.Json;
using AgentFramework.Agent;
using AgentFramework.Contracts;
using AgentFramework.Data;
using AgentFramework.Index;
using AgentFramework.Kernel;
using AgentFramework.Llm;
using AgentFramework.Tools;

namespace AgentFramework.Host;

public sealed partial class AgentHost
{

    /// <summary>
    /// 发一条消息（在**当前会话**上）。
    /// 「会话续接」在这里落地：每次都从事件流重建上下文再交给主干 ——
    /// 因为日志才是唯一真相源，连进程重启都不需要额外状态。
    ///
    /// 用信号量串行化：同一会话不允许两个回合同时跑，
    /// 否则事件流会交错、上下文会撕裂。
    /// </summary>
    public Task<AgentRunResult> SendAsync(string input, CancellationToken ct = default)
        => SendAsync(_session, input, ct);

    public Task<AgentRunResult> SendAsync(string input, IReadOnlyList<LlmImage>? images, CancellationToken ct = default)
        => SendAsync(_session, input, ct, images: images);

    /// <summary>
    /// 在**指定会话**上跑一轮（P1）。
    ///
    /// <para>
    /// 为什么要有这个重载：**回合的归属应该在调用那一刻就定下来**。
    /// UI 的 <c>/api/send</c> 是「先回 202、回合在后台跑」，中间主人完全可能切走；
    /// 若回合跑在「当时的当前会话」上，它就会落进别人的日志里。
    /// </para>
    /// <para>
    /// 每个会话自己有回合闸，所以不同会话的回合可以并行 ——
    /// 「不许两个回合同时跑」这条纪律本来就只针对同一会话（同一个事件流不能被交错写入）。
    /// </para>
    /// </summary>
    public async Task<AgentRunResult> SendAsync(
        Hosting.SessionRuntime session,
        string input,
        CancellationToken ct = default,
        IReadOnlyList<LlmImage>? images = null)
    {
        await session.TurnGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 退休检查（P0 竞态防线）—— **必须在拿到闸之后**做：
            // 放在闸之前会漏掉「排队等待期间才被标记退休」的那一批，
            // 偏偏那正是竞态窗口的形态。抛在这里，下面的 finally 会把闸还回去。
            if (_retired)
            {
                throw new InvalidOperationException("该宿主已关闭，消息未送达（请重新发送）");
            }

            // 中断自愈：补完就不会再悬空，所以每个会话只做一次（幂等）。
            // v3.6 审查修复：修复**成功之后**才置位 —— 否则首次修复抛异常 / 被取消时标志已置 true，
            // 此后再不重试，日志里会永久留存「有 requested 无 completed」的不一致记录。
            if (!session.DanglingRepaired)
            {
                await RepairDanglingToolCallsAsync(session, ct).ConfigureAwait(false);
                session.DanglingRepaired = true;
            }

            // HCI：模式与项目目录钉在会话上 —— 本回合的档位、工具面、记忆层级、
            // 文件沙箱都取会话自己的；未钉的旧路径沿用宿主默认。
            // BeginTurn 让 AsyncLocal 随执行流流向工具闭包（含文件沙箱的路径解析）。
            var modeId = session.ModeId ?? AgentModes.IdOf(_mode);
            var profile = AgentModes.Resolve(modeId);
            var projectDir = session.ProjectDir ?? Options.WorkspaceRoot;
            _state.BeginTurn(modeId, projectDir, session.SessionId);
            var contextOptions = EffectiveContextOptions(profile);

            // 带治理的投影 —— **不是全量回灌**，那正是 dsh 长任务变差的根因。
            // events 现在直接来自会话的**内存事件表**（P2），不再每轮重读 JSONL。
            var events = session.Events;
            // forceCollapse：超预算且摘要器没能给出摘要时，仍折叠旧轮（放一句如实占位）——
            // 否则「没摘要就不折叠」会让上下文永远不收缩，水位永远降不下来。
            var projection = SessionContextBuilder.Project(events, contextOptions, forceCollapse: true);

            // 水位超了才压：压缩必然打断前缀缓存，所以宁晚不频。
            // 闲聊模式直接不做这件事 —— 闲聊没有长任务，省掉每轮的投影比较与压缩决策。
            if (profile.ContextGovernance && projection.NeedsCompression)
            {
                projection = await CompactAsync(session, events, projection, contextOptions, ct).ConfigureAwait(false);
            }

            // 上下文装配（改造后）：冻结段在前、动态段在后。
            //   · 冻结段 —— 模式说明 + 常驻记忆索引卡：内容稳定，进缓存前缀
            //   · 动态段 —— 投影出的对话 + 按**当轮输入**检索到的相关记忆：每轮可变，只能放尾部
            //
            // 原先这里是 messages.Insert(0, 记忆块) —— 把每轮可能变的内容放进了前缀，
            // 正是 Anthropic 官方 prompt-caching 文档点名的 "common mistake"。
            // 现在由 ContextAssembler 在**结构上**保证「动态内容不影响静态内容的位置」。
            var dynamicMessages = projection.Messages.ToList();

            // 工作小本本摘要（DESIGN.md 4.17）—— 与任务卡并列的那份「复述」：
            // 计划必须放在末尾，放在中部会被淹没。本子不存在就什么都不加（默认零成本）。
            // ★ 注入 dynamicMessages 保留在投影的 Dynamic 段（测试可观测）。
            //   前缀缓存问题：notes/recall 每轮变，确实会打穿同槽位前缀；
            //   后续通过事件化（落成事件进投影）解决，当前保持投影可观测。
            if (profile.InjectNotes)
            {
                var notesPath = Path.Combine(Options.WorkspaceRoot, WorkNotes.DefaultFileName);
                var notesSummary = WorkNotes.BuildSummary(
                    WorkNotes.TryRead(notesPath),
                    profile.NotesSummaryMaxChars);

                if (notesSummary is not null)
                {
                    // user 而非 system：system 只能出现在消息流最前（本地 Jinja 模板会 500）
                    dynamicMessages.Add(new LlmMessage { Role = LlmRole.User, Content = "【工作小本本·非用户发言】\n" + notesSummary });
                }
            }

            var recall = await BuildRecallBlockAsync(profile, MapScopes(profile.MemoryScopes, projectDir), input, session.SessionId, ct).ConfigureAwait(false);
            if (recall is not null)
            {
                dynamicMessages.Add(new LlmMessage { Role = LlmRole.User, Content = "【相关记忆·非用户发言】\n" + recall });
            }

            var frozen = await BuildFrozenBlockAsync(profile, MapScopes(profile.MemoryScopes, projectDir), ct).ConfigureAwait(false);
            var assembled = ContextAssembler.Assemble(frozen, dynamicMessages);
            LastAssembly = assembled;

            // 「发送前自动澄清」（可选，默认关）：
            // 先让便宜的模型把话说清楚，再交给主模型。
            // 转述失败/跳过时拿到的就是**原文** —— 所以这一步在结构上不可能挡住发消息。
            string? rephrasedText = null;
            string? rephraseModel = null;

            if (Options.Rephrase.AutoBeforeSend && _rephraser is not null)
            {
                var result = await _rephraser
                    .RephraseAsync(input, Options.Rephrase, ct)
                    .ConfigureAwait(false);

                if (result.Rephrased)
                {
                    rephrasedText = result.Text;
                    rephraseModel = result.Model;
                    await EmitRephraseRecordAsync(session, input, result, "auto", applied: true, ct).ConfigureAwait(false);
                }
            }

            return await session.Runner.RunAsync(input, assembled.Messages, ct, rephrasedText, rephraseModel, images)
                .ConfigureAwait(false);
        }
        finally
        {
            _state.BeginTurn(null);
            session.TurnGate.Release();
        }
    }

    /// <summary>
    /// 手动转述 —— 界面上「✨ 优化」按钮走这里。
    ///
    /// 结果**只回填输入框**：此刻尚未发送，仍属草稿，不进模型上下文。
    /// 成功时留一条 <see cref="UserInputRephrasedEvent"/>（Applied=false）作交互留痕，
    /// 这样事后能回答「那句话当时是怎么被改写的」。
    /// </summary>
    public async Task<RephraseResult> RephraseAsync(string text, CancellationToken ct = default)
    {
        var trimmed = (text ?? string.Empty).Trim();

        if (_rephraser is null)
        {
            return RephraseResult.Skip(trimmed, RephraseSkip.NoModel);
        }

        var result = await _rephraser
            .RephraseAsync(trimmed, Options.Rephrase, ct)
            .ConfigureAwait(false);

        if (result.Rephrased)
        {
            await EmitRephraseRecordAsync(_session, trimmed, result, "manual", applied: false, ct).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>保存转述设置（写「用户偏好」文件，不进 JSONL）。</summary>
    public void SaveRephraseSettings() => RephraseSettingsStore.Save(Options.SessionsDir, Options.Rephrase);

    /// <summary>保存上下文治理 / 写盘设置（同走「用户偏好」文件，不进 JSONL）。</summary>
    public void SaveContextSettings() =>
        ContextSettingsStore.Save(Options.SessionsDir, Options.Context, Options.Checkpoint);

    /// <summary>
    /// 切换审批档位（活配置）：改档位 + 据此重建策略委托 —— 下一次工具调用立即按新档判定。
    ///
    /// <para>
    /// 审计：写一条日志，便于回看「什么时候、切到了哪一档」。
    /// 「Yolo 档不静默关审计」这条纪律由工具事件链保证：自动放行照样发完整
    /// <c>ToolPreExecute</c> / <c>ToolCallRequested/Completed</c>。
    /// </para>
    /// </summary>
    public void SetApprovalTier(ApprovalTier tier)
    {
        Options.ApprovalTier = tier;
        var root = Options.WorkspaceRoot;
        Options.ApprovalPolicy = e => ApprovalTiers.Decide(tier, e, root);
        Console.WriteLine($"[approval] 审批档位切到 {tier}（{ApprovalTiers.DisplayName(tier)}）");
    }

    /// <summary>
    /// 按模式算出这一轮实际生效的上下文配置。
    ///
    /// 闲聊模式关掉治理：闲聊没有长任务，任务卡与水位压缩都是白花的钱。
    /// （注意是把配置**克隆**一份再改 —— 绝不改写用户的活配置对象。）
    /// </summary>
    /// <summary>把模式档位里的抽象层级名映射为实际作用域：project → 本会话项目目录的作用域。</summary>
    private IReadOnlyList<string> MapScopes(IReadOnlyList<string> scopes, string projectDir)
        => [.. scopes.Select(s => s == MemoryScope.Project ? MemoryScope.ProjectFor(projectDir) : s)];
}
