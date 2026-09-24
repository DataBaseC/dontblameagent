# 计划 · 记忆与进化（Memory & Evolution）

> 目标：把 MiMo Code 的**时机学**接到我们的**存储学**上。
> 一边学「什么时候写、谁来写」，一边守住我们已经验证过的「怎么写、怎么写不坏」。

## 0. 一句话主张

**两边各拿一半，正好互补：**

| | 我们的强项（守住） | MiMo Code 的强项（借来） |
|---|---|---|
| 问题 | **怎么写**：事件溯源、可审计、可回放、改主意有结构性表达 | **什么时候写、谁来写**：早期 checkpoint、独立 writer、rebuild |
| 现状 | 已有分层记忆 + append-only fold + 温度/归档/合并 + 前缀缓存安全装配 | 主 agent **不维护自己的记忆**，由独立 writer 在预算 20/45/70% 处提取 |

> 结论：**我们的存储学 + 他的时机学 = 逻辑会话无界，而物理窗口有界。**

## 1. 家底盘点（先看清自己，再谈借鉴）

**已经有的**（依据源码）：

| 能力 | 位置 |
|---|---|
| 分层记忆（global / project / `project:<绝对目录>` 多项目隔离） | `Contracts/Memory.cs:4-28` |
| **事件溯源记忆**：`assert / retract / score / archive / restore` 全是追加事件，「改主意」用 `Slot` 取代，当前状态靠 **fold** 算出 | `Contracts/Memory.cs:41-117` |
| 温度排序 / 置顶 / 归档 / 合并 / 清扫 | `Data/MemoryPrioritizer.cs`、VerifyMemory 第 9–10 组 |
| 上下文治理 L1–L6（预算感知 / 大输出落盘 / 旧结果遮蔽 / 任务卡 / 早期摘要 / 骨架化） | `Contracts/ContextGovernance.cs:13-110` |
| **前缀缓存安全装配**（冻结段 / 动态段靠结构分离，不靠注释约定） | `Contracts/ContextGovernance.cs:112-137` |
| 任务卡：恒定大小、每轮重投影、放上下文末尾（近期注意力区） | `Data/TaskCardBuilder.cs:17-97` |
| 工作小本本 `AGENT_NOTES.md`：**人机共写**、五小节、只改指定小节不覆盖别的 | `Data/WorkNotes.cs:29-50,132-183` |
| 会话轨迹 JSONL 全量可回放 + 全文可捞回（含被遮蔽内容） | `Data/JsonlEventLog.cs`、`Index/SqliteSessionIndex.cs:198-280` |
| **分叉基础设施**：取源日志前缀 → 复制成自包含日志 | `Data/SessionForker.cs:15-21` |
| 旁路 LLM 调用先例（不经主循环持有 `ILlmClient`） | `Llm/LlmContextSummarizer.cs:31`；子会话 `Host/SubAgentRunner.cs:17` |

**缺的**（对照 MiMo Code，全都落在「时机 / 角色」而不是「存储」上）：

1. **没有独立记忆写入角色**：主 agent 自己调 `remember` / `update_notes` 写记忆（`Host/*MemoryTools*.cs`）。
   → 正是 MiMo 明令禁止的模式：*「要求一个正在调棘手 bug 的模型同时维护结构化日志，往往会在两件事上各做差一件。」*
2. **没有周期性 checkpoint**：只在「该压缩了」这一个时刻才动手存状态。
3. **压缩时机在 80%**（`ContextGovernance.cs:22`）：而 MiMo 说 20/45/70%，理由是 *「95% 利用率下已无处思考」* + lost-in-the-middle。
4. **没有 rebuild**：遮蔽到收底就只能一直稀下去，物理窗口就是逻辑会话的上限。
5. **没有定期整理与固化**：`archive / merge / sweep` 的零件都在（测试第 10 组），但没有角色去**定期触发**它。

## 2. 融合设计

### 支柱一 · 双时机：**写入早，裁剪晚**（本轮最关键的一条）

看起来冲突：我们刻意把压缩定在 0.8，注释写着 *「压缩必然打断前缀缓存，宁晚不频」*（`ContextGovernance.cs:19-22`）；
MiMo 却要趁早，20/45/70% 就动手。**谁对？——两边都对，因为说的不是一件事。**

```
checkpoint（写入）   旁路落盘，上下文一个字节都不动   → 不碰前缀缓存 → 可以早、可以频繁
compaction（裁剪）   换投影函数，上下文变了           → 必打断前缀缓存 → 必须晚、必须少
```

我们把这两件事**绑在同一个触发点**上了：只有决定「要压了」的时候，才顺手存一次状态。
MiMo 的贡献正是把「写」从「裁」里拆出来独立计时。

**落地**：

- 新增 `ContextOptions.CheckpointTriggerRatio`（默认 `0.35`），与既有 `CompressionTriggerRatio = 0.8` **并存**；
- 水位跨过 checkpoint 线 → 跑 writer，**只写盘，不改上下文**；
- 水位跨过压缩线 → 照旧走 `ContextCompactor`，一行不改（零回归）；
- 多个水位（0.25 / 0.5 / 0.75）→ 每次取**增量**（自上次 checkpoint 以来新增的事件），不是全量重写。

> 借鉴：MiMo Code 的 checkpoint 时机与「增量而非孤注一掷」。
> 偏离：默认单点 `0.35` 起步，不是三点。因为我们的预算（24k）比他的窗口小一个量级，且 L2/L3/L6 这些**零模型开销**的层已在低位顶着 —— 水位低时先由它们出手，不必请模型。

### 支柱二 · 角色分离：主 agent 不写自己的记忆（CQS）

MiMo 的做法：**主 agent 只读结构化记忆，唯一写口是一份自由格式 `notes.md`**，由 writer 在 checkpoint 时读取、路由到结构化字段、然后清空。

我们已有的东西和他**正好对上，而且更强**：

| | MiMo Code | 我们 |
|---|---|---|
| 随手记 | `notes.md`，主 agent 唯一可写 | `AGENT_NOTES.md`（`WorkNotes.cs`）—— **人也能写**，且「只改指定小节」结构性保证不覆盖别人的内容 |
| 结构化记忆 | `MEMORY.md` + checkpoint，writer 独占写 | `IMemoryStore` 事件流 —— 目前**谁都能写**（问题所在） |

**落地**：

- 新增契约 `ICheckpointWriter`（照 `IContextSummarizer` 的缝做，`Llm/LlmCheckpointWriter.cs` 实现）；
- writer 输入 = 对话窗口 + 现有任务卡 + `AGENT_NOTES.md`；输出 = **一条 checkpoint 事件**（追加，不覆盖）；
- writer 顺带把「反复出现、理应升级为项目知识」的观察写进 `IMemoryStore`（`Source = "writer"`，可与主 agent 手记区分、可审计）；
- 主 agent 的 `remember` **保留**（人机协同的价值不能砍），但语义降级为「随手记」：writer 在 checkpoint 时消费、去重、升级。
  → 这是与 MiMo 的**有意偏离**：他禁掉主 agent 的一切写入，我们只禁「结构化那一层」，自由层照写 —— 因为我们的自由层是**人机共写**的，砍掉等于砍掉人的入口。

### 支柱三 · rebuild：逻辑会话无界

MiMo 的 `cycle`：checkpoint 打点 → 窗口逼近上限 → **切断窗口、开新窗口、用持久化文件重建上下文**。逻辑会话是 cycle 的链，链没有长度上限。

我们缺的只是最后这一下。**而分叉基础设施已经在了**（`SessionForker.Fork`，事件日志「仅追加 + 可投影」让分叉很便宜）。

**落地**：

- `rebuild` = 用 `SessionForker` 分叉出新会话，把 checkpoint 块作为**种子**注入新会话开头；
- 父会话 id 记进事件 → 逻辑链不断，界面/检索看到的是连续的一条会话；
- 复用 MiMo 的**注入顺序**（已按实证排序）：任务清单 → checkpoint → 最近用户消息**逐字切片**（防 writer 改写偏离原意）→ 项目记忆 → 全局记忆 → notes → 可按需读取的记忆文件索引 → 尾部提醒；
- 每段**独立 token 上限**，总量封顶（MiMo 控制在 ~65k；我们按 24k 预算等比缩小）。

> 借鉴：cycle 概念与注入顺序（有实证支撑，不必重造）。
> 偏离：他「切断当前窗口」，我们**分叉新会话** —— 同一语义，但我们顺带白拿了历史（旧会话完整留档，可 search_history 回捞），且不动主循环的生命周期管理。

### 支柱四 · 进化：Dream / Distill，产物是**可执行插件**

MiMo：**Dream**（7 天，合并/去重/验证路径有效性/压缩记忆）+ **Distill**（30 天，把反复出现的工作模式固化成 skill、CLI 命令、SOP 文档）。

我们的处境：**零件全在，只缺触发者和产物形态。**

- Dream ≈ 我们已有的 `archive / restore / merge / sweep`（记忆分层第 10 组测试）+ 「跨会话观察提升到 project 层」；
- Distill ≈ 我们的**插件双通道**：B 通道 = agent 自写 JS 插件，Jint 热装载、免编译免重启、`selftest` 失败回滚 last-good。

**这是本轮我们明显超过 MiMo 的一点**：他的 Distill 产物是**文档**（Anthropic skill 格式，纯提示词）；
我们的产物可以是 **B 通道插件 —— 可执行、可热装载、带自检**。
MiMo 自己都写：*「目前以 prompt 形式定义的许多 skill，未来会逐步演变为代码形式的 workflow 脚本。」* 我们一步到位。

**落地**：

- `DreamJob`：周期整理（去重、路径有效性、session 观察 → project 记忆）；
- `DistillJob`：从历史中识别重复模式，产出「B 通道插件（可执行）+ 技能文档（说明何时用）」；
- **触发方式偏离 MiMo**：他用挂钟（7 天 / 30 天），我们用**累计会话数 / 累计轮次** —— 桌面宿主不该常驻定时器，而且「跑了 N 个会话就整理一次」比「过了一周」更贴近「经验攒够了」这个真实语义。

### 支柱五 · 账上（本轮不做，位置先留）

| 项 | 借自 | 落点 |
|---|---|---|
| **Goal**：每次尝试终止时，独立验证者审查「完成条件真的满足了吗」 | MiMo（误拦比漏放常见，死循环 <0.5%） | loop 模块的可选 verifier，复用旁路 LLM 缝 |
| **Max Mode**：并行采样 N 个候选 + judge 选优 | MiMo（SWE-Bench Pro +10~20%，代价 4~5× token） | `model` 模块，实验性、手动开 |
| **MCP 通道** | — | 工具进 `mcp:<server>` 包 + **默认延迟 + 首次用到才启动** —— 我们的延迟机制正好补他的短板（他明说「延迟加载未体现」） |
| **编排（Dynamic Workflow）与多代理** | MiMo（`agent()` / `parallel()` / `pipeline()`） | B 通道加编排原语，底层接已有的 `SubAgentRunner` + `OpenSession` + `Sandbox` |

**明确不借**：受限 shell 工具调用语法（会动模型适配层，收益中等，收益/风险不划算）；FTS/向量检索（我们刻意选了 `LIKE + 自打分`，理由写在 `SqliteSessionIndex.cs:29-36`，不因别人做就跟着做）。

## 3. 分期

| 版本 | 内容 | 验收 |
|---|---|---|
| **v3.11** | `CheckpointEvent` 契约 + `ICheckpointWriter` + `LlmCheckpointWriter` + 水位触发（默认 0.35）+ 增量语义 + `AGENT_NOTES.md` 消费路径 + 记忆写入者标记（契约桩已就位；writer 与触发为剩余） | `VerifyCheckpoint` 新工程；全量零回归 |
| **v3.12** | rebuild：cycle 链 + 分叉种子注入 + 分层注入顺序与分段上限 | `VerifyRebuild`；跨 rebuild 的任务连续性端到端 |
| **v3.13** | Dream / Distill：按会话数触发；**产物 = B 通道插件 + 技能文档** | `VerifyEvolution`；产出的插件能被装载并通过 `selftest` |
| **v3.14** | MCP 子进程通道（默认延迟 + 首次用到才启动） | `VerifyMcp` |
| **v3.15** | Goal 验证器 + Max Mode（两个「用算力换可靠性」的开关，默认关） | `VerifyGoal` |

顺序理由：v3.11 是地基（writer 与 checkpoint 事件）；v3.12 消费它；v3.13 消费前两者积累的历史；MCP 是原有账；可靠性算力最后。

> 版本号与 README「下一步」一致：v3.11 剩余 → v3.12 rebuild → v3.13 Dream/Distill → v3.14 MCP → v3.15 Goal。

## 4. 非目标（本轮明确不做）

- 不做向量检索（`LIKE + 自打分` 是我们有意的取舍）；
- 不改现有 L1–L6 的任何默认值（`CompressionTriggerRatio = 0.8` 保持不动）；
- 不砍主 agent 的 `remember`（只降级语义）；
- 不动前缀缓存装配规则（冻结/动态段的纪律一个字不改）。

## 5. 纪律（沿用）

- **追加而非覆盖**：checkpoint 与记忆都是事件（文件存变更，不存状态）；
- **单一实现**：可见面/状态一份（v3.10 刚踩过「双实现」的坑）；
- **零回归优先**：每个新开关默认关闭或默认等于旧行为，全量验证必须保持 0 失败（总数以 verify-all 输出为准）；
- **先验收桩再实现**：每个新模块配新验证工程。
