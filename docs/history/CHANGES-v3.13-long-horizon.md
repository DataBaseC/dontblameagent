# CHANGES-v3.13 · 长程任务修复批（自动停止 + 上下文压缩）

> 主题：让 agent **跑得完长任务**。两句话概括这一批：
> ① 「跑一会儿就自己停了」不是玄学 —— 是步数硬顶、回合内上下文无界增长、空响应误判三件事叠加；
> ② 「压缩不好用」不是压缩算法差 —— 是压得太早（预算空着也折叠）、压不动回合内、摘要又慢又脆。
> 设计参照 MiMo Code 的长程机制（checkpoint 早提取 / prune 旧工具输出 / 步数预算优雅降级 / doom-loop 防护）。

## 1. 根因诊断

### 1.1 主体 agent 自动停止（按出现频率排序）

| # | 根因 | 表现 | 修复 |
|---|------|------|------|
| 1 | **回合内上下文无界增长**：`AgentRunner` 把整个回合的工具结果全堆在内存历史里，回合外的投影治理只在回合**开始**生效一次 | 编码回合跑十几~几十步后 API 溢出 / 截断 / 猝死，「调用一段时间之后就停了」 | 回合内护栏 `PruneInTurnHistoryAsync`：超预算时旧工具结果折叠成占位符，保留最近 8 条逐字 |
| 2 | **MaxSteps = 12 硬顶** | 编码任务十几步到顶，静默收尾（`max-steps`） | 默认提到 **60**；末步注入「步数预算提示」要求输出交接摘要（MiMo 式优雅降级） |
| 3 | **空响应误判为「说完了」**：端点抖动吐空帧（无文本、无思考、无工具调用）时，回合以空文本成功收尾 | 界面显示「模型什么都没说」却算一轮结束 | 空响应重试（默认 2 次）；仍为空则如实报 `empty-response`（不再假成功） |
| 4 | **瞬时异常判死刑**：超时 / 断流 / 5xx 直接炸掉回合 | 慢端点上回合动辄作废 | 尚无部分输出时退避重试（默认 2 次）；有部分输出不重试（避免作废已流出内容）；**重试墙钟预算 2s**——失败本身来得慢就不再重试，免得一次 30s 故障被放大成 90s |
| 5 | **finish=length 当成功**：输出被 max_tokens 截断直接收尾 | 半截答案 + 「一切正常」 | 自动续写（默认 3 次），提示从中断处继续；耗尽后如实报 `length` |
| 6 | **doom loop 无防护**：同一工具同一参数反复调，结果不变 | 烧光步数后 `max-steps` 收尾 | 连续 3 次相同调用注入「换路子」提示（只提示不硬停，硬停仍由 MaxSteps 兜底） |
| 7 | **端点断流炸回合**：上游断流 / 错误帧抛异常直达进程（免费 API 实测真实复现） | 回合/进程整个崩掉，「突然停了」 | 有部分输出 → 按「截断」续写；无输出 → 重试后如实报 `endpoint-error`；**绝不抛异常炸回合**；CLI 一次性模式加异常兜底 |

### 1.2 上下文压缩不好用

| # | 根因 | 表现 | 修复 |
|---|------|------|------|
| 1 | **压得太早**：`forceCollapse` 一上来就把 6 轮以前的对话整段折叠，不管预算还剩多少 | 早前对话「莫名其妙消失」，agent 忘目标、忘约束 | **压缩阶梯**（L1→L2→L3）：预算够就全给；超水位才骨架化；再超才强制折叠 |
| 2 | **压不动回合内**：回合内的工具结果不归投影管（见 1.1 #1） | 长回合压缩「压了等于没压」，水位降不下来 | 回合内护栏 + 折叠留痕事件（`trigger=in-turn`） |
| 3 | **摘要又慢又脆**：压缩瞬间同步调摘要（慢端点上拖死回合），失败就只剩占位符 | 压缩后信息全丢，行为变差 | 摘要 90s 旁路超时；失败**保留旧摘要**（而非一无所有）；摘要变**增量更新**（旧摘要 + 新轮次） |
| 4 | **摘要覆盖不实**：摘要只覆盖写它时的轮次，之后变旧的轮次被悄悄折叠 | 模型以为摘要什么都有，实际有洞 | 新字段 `SummaryThroughTurn`，投影器对超出部分**如实标注** |
| 5 | **抢救式压缩**：只在 0.8 水位一次性处理，那时模型压缩能力正在退化 | 摘要质量差 | **提前摘要**（MiMo checkpoint 早提取）：0.45 水位就增量提取工作记忆落盘，不收紧窗口；真正压缩时只「变现」 |

## 2. 机制出处（MiMo Code 对照）

| 本批实现 | MiMo Code 对应机制 |
|----------|-------------------|
| 回合内护栏（保留最近 8 条工具结果） | `compaction.prune`（删旧工具输出，**默认开**） |
| 0.45 水位提前摘要 + 增量更新 | checkpoint 早提取（20%/45%/70%）+ writer 增量更新，「没有一次是孤注一掷的总结」 |
| 摘要覆盖线 + 如实标注 | rebuild 分层注入 +「最近用户消息逐字切片」防漂移 |
| 末步预算提示（交接摘要） | `steps` 到上限注入「输出工作摘要 + 建议剩余任务」，不硬杀 |
| 相同调用 3 次提示 | `doom_loop` 防护（同工具同输入重复 3 次触发） |
| 空响应/瞬时异常重试 | 运行时负责重试与状态连续性（模型无状态，连续性由 harness 提供） |

更完整的调研与后续路线见 [PLAN-mimocode-adoption.md](../PLAN-mimocode-adoption.md)。

## 3. 代码改动清单

| 文件 | 改动 |
|------|------|
| `AgentFramework.Agent/AgentRunner.cs` | 重写主循环：空响应/瞬时异常重试、断流降级（`endpoint-error`，不炸回合）、length 自续、doom-loop 提示、末步预算提示、回合内护栏 `PruneInTurnHistoryAsync`（含折叠留痕事件与就地 token 估算） |
| `AgentFramework.Contracts/ContextGovernance.cs` | `ContextOptions.EarlySummarizeRatio`（0.45）；`CompactionTrigger.Early / InTurn`；`IContextSummarizer.SummarizeHistoryAsync` 增加 `previousSummary`（增量语义） |
| `AgentFramework.Contracts/SessionEvents.cs` | `ContextCompactedEvent.SummaryThroughTurn`（摘要覆盖线） |
| `AgentFramework.Data/SessionContextBuilder.cs` | 压缩阶梯（无压力全给 → 骨架化 → 强制折叠）；折叠掉的工具结果计入 `MaskedSeqs`（审计 + 钉子）；摘要覆盖线如实标注 |
| `AgentFramework.Data/ContextCompactor.cs` | `Plan(..., forceCollapse)` 透传，让 Before/After 与真实装配视图同口径 |
| `AgentFramework.Llm/LlmContextSummarizer.cs` | 增量摘要（旧摘要作背景合并），不再全量重写 |
| `AgentFramework.Host/AgentHost.Context.cs` | `TrySummarizeAsync`（90s 旁路超时、失败保旧摘要）；`MaybeEarlySummarizeAsync`（提前提取）；`LastSummary/LastSummaryCoverage`；压缩与手动压缩全部走增量摘要 + 覆盖线 |
| `AgentFramework.Host/AgentHost.Turn.cs` | 回合流程接入提前摘要 → 重投影 → 水位压缩 |
| `AgentFramework.Host/HostOptions.cs`、`Program.cs` | `MaxSteps` 默认 12→60；配置单 `mode` 支持模式 id（**`code` = 编程模式**，一行启用）；`earlySummarizeRatio` 可配置 |
| `tests/AgentFramework.VerifyAgent` | 新增场景 7：端点断流降级、瞬时异常重试（确定性回归） |
| `tests/AgentFramework.VerifyWeb` | 修复测试竞态：SSE 自动放行器延寿至全程；切换类检查前等回合收尾（`turn-ended` 帧）——死端点回合要几秒才失败，抢跑会撞上「回合正在进行」的合理拒绝 |

## 4. 编程模式（code）

- 已有档位（`AgentModes.Code`，id=`code`）：工具面 Core + Exec，提示词偏「读代码 → 改代码 → 跑测试」，全治理、项目记忆。
- 本批打通：配置单写 `"mode": "code"` 或界面切档即可；`--prompt` 一次性模式同样生效。
- 伴随修复：会话切档后工具面动态解析（旧实现把 modeId 钉死在闭包里，切档不换工具面）。

## 5. 验证

- **`verify-all` 全量回归：17 个套件全绿（1033 通过 / 0 失败 / 1 跳过）**，含
  VerifyAgent 步数上限、max-steps 保文、工具异常补 completed、**端点断流降级与瞬时异常重试**
  （新增场景 7）、VerifyContext 压缩留痕/回放一致性/幂等收敛、VerifyWeb 全链路 167 项。
- **编程模式 E2E**（AMD 免费 API，DeepSeek-V4.1-Flash）：FizzBuzz 任务（建文件 → run_command 运行 →
  校验 → 汇报）自然收尾 `步数=3 停止原因=stop`，2 次工具调用全部成功。
- **压缩质量 E2E**（小预算 2000 + 小窗口 2 逼出压缩）：
  - 压缩留痕：`trigger=early`（4608→4608，只提取摘要不收紧窗口）→ `trigger=auto`
    （4608→4562，遮蔽 8 条、增量摘要 1194 字、覆盖线 throughTurn=5）；
  - 压缩后记忆保持：agent 准确答出最早脚本名、乘法表第 7 行、词频第一名及其计数，
    并记得中途修过的 IndentationError（细节经 search_history 检索复核）；
  - 压缩后继续干活：follow-up 编码任务（建文件+运行）正常完成，收尾 `stop`，无自动停止。
- **断流韧性实测**（免费 API 真实抖动复现）：上游断流曾直接炸掉整个进程；
  修复后按「截断续写 / 如实 endpoint-error 收尾」降级，回合不再猝死。

## 6. 兼容性

- 事件 JSONL **只加字段不改语义**（`SummaryThroughTurn` 可空；老日志读出 null 按「覆盖未知」保守处理）。
- `ContextCompactor.Plan` / `SessionContextBuilder.Project` 新参数带默认值，老调用方行为不变（测试即回归基线）。
- 老行为可通过配置回退：`maxSteps: 12`、`context.earlySummarizeRatio: 0`、`context.summarizeOlderHistory: false`。
