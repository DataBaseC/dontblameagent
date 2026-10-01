# CHANGES v3.18 —— Dream / Distill：记忆周期整理与技能固化

> MiMo Code 借鉴路线的最后一项（P3）落地。对应
> [PLAN-mimocode-adoption.md](../PLAN-mimocode-adoption.md) §3 第 6 项与
> [PLAN-memory-evolution.md](../PLAN-memory-evolution.md) 支柱四。
> MiMo 原案：**Dream**（7 天：合并 / 去重 / 路径有效性 / 压缩记忆）+
> **Distill**（30 天：把反复出现的工作模式固化成 skill / CLI 命令 / SOP 文档）。

---

## 0. 一句话

记忆的整理与技能的固化**不再等人想起来**：攒够 N 轮经验，Dream 自动把记忆整理干净，
Distill 自动把反复出现的工作方式固化成可装载的技能草稿。

## 1. Dream（记忆周期整理）

### 落点

| 组件 | 说明 |
|------|------|
| [`DreamJob`](../../src/AgentFramework.Host/DreamJob.cs) | 编排者：**零件全在，只缺触发者** —— merge / sweep 的存储原语早已就位（事件化、可回滚），Dream 只是那个定期触发并编排的角色 |
| [`EvolutionOptions`](../../src/AgentFramework.Host/EvolutionOptions.cs) | 触发节奏与阈值；`agent.json` 的 `evolution` 节 |
| `AgentHost.MaybeEvolve` | 回合收尾的检查点：按**累计回合数**触发（不是挂钟 —— 桌面宿主不常驻定时器，「攒够 N 轮」比「过了一周」更对题） |

四步（全部动作经既有记忆事件落盘 —— 可审计、可 restore，绝无账外改写）：

1. **合并去重**：同作用域、同槽位、规范化同文（空白折叠 + 大小写不敏感）的条目折叠成一条；
   合并产物保留全部来源 id（`src:` 标签）与最高热度 —— 可回溯、不降温。只压重复，不动别的；
2. **跨项目提升**：同一条事实在 ≥2 个项目作用域反复出现 → 它是常识，提升进 global
   并撤销各项目副本（MiMo 的「session 观察 → project 记忆」在我们作用域模型里的对应物）；
3. **频率降档清扫**：与界面「巩固」按钮**完全同一判据**（`(1+使用次数) × 0.5^(闲置天数/半衰期)`）；
   冷条目休眠进归档层（可 restore 恢复），老而常用永不掉线；
4. **路径有效性核验**：记忆里引用的文件路径逐个查存在性 —— **只报告不改动**
   （失效引用该由人/agent 决定改写还是撤销，梦里不擅自改记忆内容）。

Dream 默认**开**（30 回合一轮）：它只经既有事件动记忆，可审计、可恢复。
幂等：连跑两轮，第二轮无事可做。

## 2. Distill（技能固化）

### 落点

| 组件 | 说明 |
|------|------|
| [`DistillJob`](../../src/AgentFramework.Host/DistillJob.cs) | **挖矿**（纯函数、确定性）+ **固化**（写 `skills/`）双层 |

- **挖矿**：从最近 `DistillRecentSessions` 个会话日志提取工具调用序列，
  找「反复出现」且「横跨会话」的模式 —— 两步工作流（`A → B`，如
  `run_command dotnet build → run_command dotnet test`）优先于单步操作。
  双阈值：`DistillMinOccurrences`（默认 3 次）**且** `DistillMinSessions`（默认 2 个会话）
  —— 一次巧合不算工作方式；
- **固化**：每个模式写成 `skills/<id>/{skill.json, README.md}` —— **纯声明式技能**
  （工具白名单 + 提示词后缀），`SkillLoader` 立即可装载（round-trip 有测试）。
  README 如实写明提炼依据（次数 / 会话数 / 步骤），定位是**草稿**：人工修订后才是正典；
- **不覆盖**：技能目录已存在就跳过 —— 自动作业永不顶掉人工修订。

### 为什么确定性挖矿、不请模型

模式识别要的是**可复现**（同一份历史必出同一份产物，测试可对账）；措辞润色是锦上添花。
PLAN-memory-evolution 设想过「产物 = B 通道可执行插件」——可执行逻辑的生成必须有
沙箱 + 自检闭环，本轮不做（诚实边界）；技能文档恰是 MiMo Distill 的原始产物形态。

### 默认关

`DistillEveryTurns` 默认 0：固化会往工作区写文件，属于「改变工作区」的动作，
要用户显式打开（`agent.json` → `evolution.distillEveryTurns`），或手动 `RunDistillAsync`。

## 3. 配置面

```json
{
  "evolution": {
    "dreamEveryTurns": 30,
    "distillEveryTurns": 50,
    "distillMinOccurrences": 3,
    "distillMinSessions": 2,
    "distillMaxSkills": 3
  }
}
```

手动入口：`AgentHost.RunDreamAsync()` / `RunDistillAsync()`（界面按钮、测试共用）。

## 4. 验证

新验证工程 **VerifyEvolution**（19 套件清单里的第 18 位），30 条检查全绿：

- Dream：合并去重（含审计与热度继承）、幂等、频率降档（冷休眠 / 常青 / 可恢复）、
  路径核验（点名 / 只报告不改动 / 补齐后为空）、跨项目提升（提升 / 撤副本 / 不动特有事实 / 幂等）；
- Distill：挖矿（flow 优先 / 阈值双判据 / 确定性）、固化（落盘 / SkillLoader round-trip /
  白名单 / 提炼依据 / id 合法）、不覆盖人工修订；
- 触发：累计回合数驱动（第 1 轮只计数、第 2 轮自动整理）。

`cmd /c verify-all.cmd`：**19 套件 1140 通过 / 0 失败 / 1 跳过**（v3.17 基线 1104 → +30）。

> 备注：套件间偶发「插件 DLL 被占用」（verify-all.cmd 头注释已知现象）——
> 单独复跑该套件即绿，非回归。
