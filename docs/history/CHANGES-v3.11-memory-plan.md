# v3.11 · 记忆与进化（计划 + 契约骨架）

> 这一版交付的是**计划与契约**，不是完整实现 —— 先把「学什么、不学什么、为什么」定死，
> 再把骨架按进来。下一版照着桩填实现。

## 起因：我们离 MiMo Code 差在哪

对照小米 MiMo Code（基于 OpenCode 的终端编程 Agent，也围绕「长程任务」设计）后，把差距**从「缺功能」重新定位成「缺时机与角色」**：

盘点家底发现，我们的**存储学**其实比它厚：

| 能力 | 位置 |
|---|---|
| 分层记忆（global / project / 多项目隔离） | `Contracts/Memory.cs` |
| **事件溯源记忆**：`assert / retract / score / archive / restore` 全是追加事件，「改主意」用 `Slot` 取代，当前状态靠 **fold** 算出 | `Contracts/Memory.cs` |
| 温度排序 / 置顶 / 归档 / 合并 / 清扫 | `Data/MemoryPrioritizer.cs` |
| 上下文治理 L1–L6（预算感知 / 大输出落盘 / 旧结果遮蔽 / 任务卡 / 骨架化） | `Contracts/ContextGovernance.cs` |
| **前缀缓存安全装配**（冻结段 / 动态段靠结构分离，不靠注释约定） | `Contracts/ContextGovernance.cs` |
| 会话轨迹 JSONL 全量可回放 + 全文可捞回 | `Data/JsonlEventLog.cs`、`Index/SqliteSessionIndex.cs` |
| **分叉基础设施**（事件日志「仅追加 + 可投影」让分叉很便宜） | `Data/SessionForker.cs` |
| 工作小本本 `AGENT_NOTES.md`（**人机共写**、只改指定小节） | `Data/WorkNotes.cs` |

**缺的只有两件事** —— 而且都是 MiMo Code 的强项：

1. **没有独立记忆写入角色**：主 agent 自己调 `remember` / `update_notes` 写记忆。
   正是它明令禁止的模式：*「要求一个正在调棘手 bug 的模型同时维护结构化日志，往往会在两件事上各做差一件。」*
2. **没有周期性 checkpoint**：只在「该压缩了」这一个时刻才顺手存一次状态。

> 一句话：**它有「时机学」，我们有「存储学」，合起来正好互补。**

## 核心论断：写入早、裁剪晚

看起来冲突：我们的压缩刻意定在 0.8（注释写着「压缩必然打断前缀缓存，宁晚不频」），
MiMo 却要趁早，在预算 20/45/70% 处就动手。**两边都对 —— 因为说的不是一件事。**

```
checkpoint（写入）   旁路落盘，上下文一个字节都不动   → 不碰前缀缓存 → 可以早、可以频繁
compaction（裁剪）   换投影函数，上下文变了           → 必打断前缀缓存 → 必须晚、必须少
```

我们把这两件事**绑在同一个触发点**上了（`CompressionTriggerRatio = 0.8` 一次管两件）。
拆开后：**checkpoint 走 0.35 早写、压缩仍走 0.8 不动 —— 现有 L1–L6 一行都不用改。**

## 这一版落地了什么

| 落点 | 说明 |
|---|---|
| `docs/PLAN-memory-evolution.md`（新） | 完整计划：五个支柱、分期（v3.11 → v3.15）、非目标、纪律 |
| `src/AgentFramework.Contracts/Checkpoint.cs`（新） | `CheckpointOptions`（默认关 / 水位 0.35 / 上限 2000 字符 / 防抖 8 事件）+ `CheckpointRequest` + `ICheckpointWriter` + `CheckpointTrigger` |
| `src/AgentFramework.Contracts/SessionEvents.cs` | 新增 `CheckpointEvent`（第 17 种事件）：结构化状态 + 增量区间 + `RenderBlock()` 渲染成 rebuild 种子 |
| `tests/AgentFramework.VerifyCheckpoint/`（新） | **31 项**契约验证（A 批）：选项默认值 / 序列化往返 / 渲染块 / **投影隔离** / 追加语义 |

### 三条纪律写进了类型

- **不进模型上下文**：投影器不处理它（与 `UserInputRephrasedEvent` 同）—— 验证桩专门断言「加入 checkpoint 后投影消息数不变、哨兵文本不出现」；
- **追加而非覆盖**：每次 checkpoint 是一条新事件，旧的那条永不改（文件存变更，不存状态）；
- **增量**：`FromSeq` / `ToSeq` 标明这份是从哪一段提取的，于是「改主意」在时间线上可见，而不是被悄悄覆盖。

## 待做（v3.11 剩余）

- `LlmCheckpointWriter`：旁路模型调用实现（照 `Llm/LlmContextSummarizer.cs` 的缝），独立于主 agent；
- 水位触发接进主循环（`AgentHost.SendAsync` 里与压缩判定并列，但**各管各的**）；
- 记忆升级：writer 把稳定观察写进 `IMemoryStore`（`Source = "writer"`，可审计）；
- `rebuild`：用 `SessionForker` 分叉新会话，把 checkpoint 块当种子注入 —— 逻辑会话无界、物理窗口有界。

## 两处刻意偏离 MiMo Code（有理由）

1. **不砍主 agent 的 `remember`**，只把它降级成「随手记」（他是全禁）。
   理由：我们的 `AGENT_NOTES.md` 是**人机共写**的，砍掉等于砍掉人的入口。
2. **checkpoint 默认关、水位单点 0.35**（他默认开、三点 20/45/70）。
   理由：默认开 = 给所有会话白加模型调用成本；**零回归优先**。

另：`rebuild` 我们做「**分叉新会话**」而不是「切断当前窗口」—— 同一语义，
但顺带白拿了历史留档（旧会话完整保留，可 `search_history` 回捞）。

## 验证

```
新： AgentFramework.VerifyCheckpoint   31 通过 / 0 失败
全量：907 通过 / 0 失败（22 个工程；VerifySandbox 另有 2 项 Job Object 专有项在非 Windows 上跳过）
```
