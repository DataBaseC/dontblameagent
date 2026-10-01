# CHANGES-v3.15 · checkpoint-writer（后台记忆提取）

> MiMo Code 长程机制落地的第三步（v3.13 修复批 → v3.14 Goal 验证器 → 本篇）。
> 一句话：**主 agent 不维护自己的记忆** —— 摘要提取挪到后台 writer，回合永不等它；
> writer 产出**结构化检查点**（意图/下一步/约束/任务树/涉及文件/错误与修复/设计决策…），
> single-writer 纪律保证一致性。

## 1. 机制（对照 MiMo Code 的 checkpoint-writer 子代理）

| 设计点 | 落地 |
|--------|------|
| 主 agent 不等记忆提取 | 提前摘要从**回合内同步 90s 调用**改为 **fire-and-forget 后台 writer**；回合边界只做「消费稿子」（纯日志追加，微秒级）。实测：writer 门控不返回时 4 轮对话 76ms 全部完成 |
| checkpoint 早提取 | 保留 v3.13 的 0.45 水位触发；现在触发时**派发** writer 而不是当场摘要 |
| 结构化 checkpoint | 摘要提示词改为固定字段：`## 当前意图/下一步动作/工作约束/任务树/涉及文件/错误与修复/设计决策/产出与结论/杂项` |
| 稿子落盘、rebuild 有种子 | writer 产出同时写 `sessions/checkpoints/<sessionId>.md`（人可读、可清理） |
| single-writer | 每会话同一时刻**至多一个 writer**（`CheckpointInFlight` 门闩），稿子只有它写（`PendingCheckpoint` 槽），只有回合边界取走 → 日志追加仍严格单线程 |
| writer 不打扰主流程 | 模型调用失败/超时全部吞掉；自动压缩也不再阻塞——有稿用稿、没稿沿用旧摘要，覆盖线如实 |
| 手动压缩例外 | 界面「立即压缩」是用户主动发起，保留同步摘要（用户在等结果） |

## 2. 代码改动清单

| 文件 | 改动 |
|------|------|
| `AgentFramework.Host/Hosting/SessionRuntime.cs` | `CheckpointDraft` 记录 + `PendingCheckpoint`/`CheckpointInFlight` 槽位（single-writer 载体） |
| `AgentFramework.Host/AgentHost.Context.cs` | `MaybeEarlySummarizeAsync` 重写为「消费稿 + 派发 writer」两段式；新增 `DispatchCheckpointWriter`（后台任务）、`TryWriteCheckpointFile`；`CompactAsync`（自动）改为非阻塞（用稿/旧摘要 + 派发补写） |
| `AgentFramework.Llm/LlmContextSummarizer.cs` | 摘要输出改**固定字段结构**（MiMo 11 字段精神）；增量更新语义不变 |
| `tests/AgentFramework.VerifyContext` | 新增第 12 节（7 项）：不阻塞（76ms）、writer 派发、稿子不落空事件、single-writer 防堆积、回合边界消费、覆盖线、检查点文件落盘 |

## 3. 行为变化（有意为之）

- 提前摘要的时延从「当轮可见」变为「下一轮可见」——换来回合**永不为摘要停顿**。
  压缩（0.8 水位）前 checkpoint 通常早已写好；偶遇首次压缩恰逢无稿时，该次事件无摘要、
  折叠走占位符 + 覆盖线如实标注，后台 writer 随即补出下一份。
- `SummaryThroughTurn` 现在如实记「摘要真正覆盖到的轮次」（可能是旧稿的覆盖线），
  而不是一律记当次折叠线 —— 不假装摘要什么都有。

## 4. 验证

- `VerifyContext` 123 通过 / 0 失败（新增 7 项 checkpoint-writer 检查）。
- **全量 `verify-all`：17 套件全绿（1058 通过 / 0 失败 / 1 跳过）**，见 `verify-all-writer.txt`。
- 兼容性：事件 JSONL 只加字段不改语义；不启用摘要（无摘要器/关掉 SummarizeOlderHistory）时零变化。
