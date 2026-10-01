# PLAN-mimocode-adoption · 借鉴 MiMo Code 的长程任务机制

> 调研对象：MiMo Code（小米 MiMo 团队的开源终端编程 Agent，基于 OpenCode，MIT 协议，
> [github.com/XiaomiMiMo/MiMo-Code](https://github.com/XiaomiMiMo/MiMo-Code)），
> 以及 Claude Code / Codex CLI / Gemini CLI / Qwen Code 的同构机制作交叉验证。
> 核心来源：[MiMo Code：将编程 Agent 扩展到长程任务（官方博客）](https://mimo.xiaomi.com/zh/blog/mimo-code-long-horizon)。

## 0. 一句话总结

MiMo Code 的设计围绕三个主题：**计算**（单轮决策质量）、**记忆**（多轮状态连续性）、
**进化**（跨 session 经验积累）。最独特、最值得借鉴的四件事：

1. **Goal 独立完成度验证器** —— 防止 agent 提前「宣称完成」；
2. **checkpoint-writer + 早提取（20%/45%/70%）+ rebuild** —— 逻辑会话无限长、物理窗口保持有界；
3. **四层记忆 + single-writer**；
4. **Dynamic Workflow** —— 编排逻辑从 prompt 变成代码。

## 1. MiMo Code 的机制要点

### 1.1 Agent 主循环
- 运行时是**主循环状态机**：模型无状态，连续性全部由运行时提供（工具管理、状态持久化、组装输入）。
- 继续/停止：模型请求工具调用就继续，产出纯文本即结束（Claude Code 同）。
- **步数预算**：`steps` 可配，不设则跑到模型自行停止；到上限**不硬杀**，
  而是注入特殊系统提示让 agent 输出「工作摘要 + 建议的剩余任务」（优雅降级，摘要可接续）。
- **Goal 机制（防提前停止）**：用户给自然语言停止条件；agent 每次尝试终止时，
  系统发起一次**独立模型调用**审查完整历史判断条件是否满足；未满足就把差距反馈回去继续，
  确认无法完成判 impossible。验证者不参与实际工作（无「对自己成果的认同偏差」），
  实测死循环概率 < 0.5%。Claude Code `/goal` 同构：评估器无工具、只看已浮现证据，
  裁决三态（Not yet met / Met / Impossible）。
- **doom_loop 防护**：同工具同输入重复 3 次触发（默认 ask）。
- **Dynamic Workflow**：复杂编排由主 agent 生成 JavaScript 在沙箱确定性执行
  （`agent()` / `parallel()` / `pipeline()`），「if 不会忘记分支、for 不会提前退出」。

### 1.2 上下文管理（Cycle / Checkpoint / Rebuild）
- 目标：**逻辑会话无限延伸，每个物理窗口保持有界**。
- **Checkpoint 早提取**：在预算约 **20% / 45% / 70%** 处触发（远低于上限），
  每次是**增量更新**而非孤注一掷的总结。理由：高上下文利用率下模型能力衰减
  （lost-in-the-middle），「要求模型在压缩能力正在退化的时刻做最关键的压缩是划不来的交易」。
- **Writer 子代理**：checkpoint 处派独立 writer 并发读对话、写结构化状态；
  **主 Agent 不维护自己的记忆**；11 字段 checkpoint；**每个文件 single-writer**。
- **Rebuild**：窗口将满时开新窗口，用持久化文件重建；分层注入：
  任务清单 → session checkpoint → **最近用户消息逐字切片**（防改写漂移）→ 项目记忆 →
  全局记忆 → notes → 记忆文件索引 → **tail reminder（下一步做什么）**；总量 ≤ 65K token。
- **四层记忆**：Session（checkpoint）→ Project（MEMORY.md）→ Global（偏好）→
  History（每会话完整 SQLite 轨迹，原文存储不建索引，兜底回溯）。
- 用户可见压缩配置：`compaction: { auto, prune, reserved }` —— 自动压缩、
  **prune 删旧工具输出（默认开）**、压缩预留缓冲。
- 业界共识（缓存友好）：少变内容在前（system → 项目上下文 → 对话）；
  压缩**只截断/遮蔽不改写**、**绝不压缩压缩产物**（CliffCompaction, arXiv:2609.26779）；
  `/rewind` 截断优于重写；Gemini CLI 约 70% 水位触发压缩、保留最近历史。

### 1.3 编程模式（工具面与权限）
- 工具面：`bash / edit / write / read / grep / glob / patch / skill / todowrite /
  webfetch / websearch / question`；`edit` 为主要修改方式（精确字符串替换）。
- 权限三值 `allow / ask / deny` + 输入级模式匹配（`bash: {"git *":"allow","rm *":"deny"}`），
  last-match-wins；特殊防护 `external_directory`（越界访问）与 `doom_loop`；
  `.env` 默认拒读；审批 once / always / reject。
- 模式：**build**（全工具）/ **plan**（只读规划，唯一可写 plans/*.md）/ **compose**（技能编排）。
- 子代理：`general` / `explore`，外加隐藏系统代理（compaction / title / summary）——
  **系统功能也建模为代理**，统一调度、可换便宜模型。

## 2. 本项目已落地（v3.13，见 [history/CHANGES-v3.13-long-horizon.md](history/CHANGES-v3.13-long-horizon.md)）

| MiMo 机制 | 本项目实现 | 状态 |
|-----------|-----------|------|
| prune 旧工具输出 | `AgentRunner.PruneInTurnHistoryAsync`（回合内护栏，超预算折叠、留痕事件） | ✅ |
| checkpoint 早提取 + 增量更新 | `EarlySummarizeRatio=0.45` + `MaybeEarlySummarizeAsync` + 摘要增量合并 | ✅ |
| 「绝不假装摘要什么都有」 | `SummaryThroughTurn` 覆盖线 + 投影器如实标注 | ✅ |
| steps 预算优雅降级 | `MaxSteps=60` + 末步「交接摘要」提示 | ✅ |
| doom_loop 防护 | 相同工具调用连续 3 次注入换路子提示 | ✅ |
| 运行时负责连续性 | 空响应/瞬时异常重试、length 自续、empty-response 如实报 | ✅ |
| 无压力不损失信息 | 压缩阶梯（全给 → 骨架化 → 强制折叠） | ✅ |
| 编程模式 | `mode: "code"` 一行启用（Core+Exec 工具面、读→改→测提示词） | ✅ |

## 3. 后续路线（按性价比排序）

1. **Goal 终止验证器** ✅（v3.14 已落地，见 [history/CHANGES-v3.14-goal-verifier.md](history/CHANGES-v3.14-goal-verifier.md)）：
   回合收尾前由旁路小模型裁决「目标是否达成」，未达成就带着差距反馈继续；
   Impossible 如实收尾（`goal-impossible`）；fail-open + 纠偏上限防死循环。
2. **checkpoint-writer 子代理** ✅（v3.15 已落地，见 [history/CHANGES-v3.15-checkpoint-writer.md](history/CHANGES-v3.15-checkpoint-writer.md)）：
   摘要提取挪到后台 writer、回合不等它；checkpoint 固定字段化并落盘
   （意图/下一步/约束/任务树/涉及文件/错误与修复/设计决策）；single-writer 防堆积。
3. **Plan 模式** ✅（v3.16 已落地，见 [history/CHANGES-v3.16-plan-mode.md](history/CHANGES-v3.16-plan-mode.md)）：
   权限层只读门（禁写禁执行，唯一例外 `plans/*.md`）；计划文件每轮重注入、压缩冲不掉；
   切 `code`/`work` 即进入执行。
4. **压缩产物再入禁止** ✅（v3.17 已落地，见 [history/CHANGES-v3.17-invariants-permissions.md](history/CHANGES-v3.17-invariants-permissions.md)）：
   显式不变量 **INV-C1**（摘要产物永不作为压缩输入主体）：覆盖线内的轮次不再进压缩原料、
   产物只以 `previousSummary` 作背景、合成复述块（`【…·非用户发言】`）永不取材；
   `ManualCompactAsync` 覆盖线只如实延伸到摘要真正吃进去的轮次。
5. **权限输入级匹配 + external_directory** ✅（v3.17 已落地，同上）：
   输入级规则（`run_command git * = allow` 式模式匹配，三值处置、last-match-wins）盖过档位；
   `external_directory` 硬闸：越界写不许静默放行（最多 Ask），显式授予才放开；
   计划模式闸仍最高优先。配置面：`agent.json` 的 `approvalRules` / `allowExternalDirectory`。
6. **Dream / Distill** ✅（v3.18 已落地，见 [history/CHANGES-v3.18-evolution.md](history/CHANGES-v3.18-evolution.md)）：
   Dream = 记忆周期整理（去重合并 / 跨项目提升 / 频率降档 / 路径核验），默认开、按累计回合数触发；
   Distill = 从历史会话挖「反复出现 + 横跨会话」的调用模式，固化成 skills/ 纯声明式技能草稿
   （SkillLoader 立即可装载；不覆盖人工修订）。确定性挖矿；B 通道可执行插件产物待沙箱闭环后再做。

至此 §3 路线 1–6 全部落地（v3.14 – v3.18）。

## 4. 参考链接

- [MiMo Code 长程任务官方博客](https://mimo.xiaomi.com/zh/blog/mimo-code-long-horizon)
- [MiMo Code 文档：代理](https://mimo.xiaomi.com/zh/mimocode/agents) / [工具](https://mimo.xiaomi.com/zh/mimocode/tools) / [权限](https://mimo.xiaomi.com/zh/mimocode/permissions) / [会话与上下文](https://mimo.xiaomi.com/zh/mimocode/sessions) / [模式](https://mimo.xiaomi.com/zh/mimocode/modes)
- [Claude Code: agent loop](https://code.claude.com/docs/en/agent-sdk/agent-loop) / [context window](https://code.claude.com/docs/en/context-window) / [prompt caching](https://code.claude.com/docs/en/prompt-caching) / [goal](https://code.claude.com/docs/en/goal)
- [Codex CLI 配置参考](https://mintlify.wiki/openai/codex/configuration/reference)
- [Qwen Code: Auto-Compaction Threshold Redesign](https://raw.githubusercontent.com/QwenLM/qwen-code/8bad83fca3a83915e31a1e467c8b7fc0e3908859/docs/design/auto-compaction-threshold-redesign.md)
- [CliffCompaction（只截断不改写、绝不压缩压缩产物）](https://arxiv.org/abs/2609.26779)
