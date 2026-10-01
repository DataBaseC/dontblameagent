# CHANGES v3.17 —— 压缩产物再入禁止（INV-C1）+ 权限输入级匹配与越界防护

> 两个 P2 项的落地记录。对应 [PLAN-mimocode-adoption.md](../PLAN-mimocode-adoption.md) §3 第 4、5 项。
> 参考：[CliffCompaction（只截断不改写、绝不压缩压缩产物）](https://arxiv.org/abs/2609.26779)、
> MiMo Code 的 permission rules（`bash: {"git *":"allow","rm *":"deny"}`，last-match-wins）与
> `external_directory` 特殊防护。

---

## 1. 压缩产物再入禁止（INV-C1）

### Why

CliffCompaction 的核心告诫是「**绝不压缩压缩产物**」。违反它的每一步都看起来无害 ——
摘要只是又一条文本，再压一次似乎还能更精炼；实测下来却是**只减不增的漂移放大器**：

- 同一份信息被计两次（旧摘要 + 它自己的原料同台被压）；
- 每一轮摘要往返都放大一次误差，且坏得无声无息（每轮输出都合法，直到任务悄悄跑偏）。

本项目把这条告诫升格为**显式不变量 INV-C1**，并在代码里焊死、在测试里锁死：

> **摘要产物永不作为压缩输入主体。**

展开成三条可执行的纪律：

1. 上一次压缩的产物（`ContextCompactedEvent.Summary` / checkpoint 稿）只以
   `previousSummary` 身份进摘要器 —— 作**背景**供增量合并，绝不混进待压缩正文（transcript）；
2. 已被摘要产物**覆盖**的原始轮次不再进压缩输入 —— 增量摘要的语义本来就是
   「旧摘要 + 只补新进展」，原料与产物同台 = 二次压缩；
3. **合成复述块**（`【…·非用户发言】`：摘要注记、任务卡、计划文件、记忆、小本本……）
   本身就是复述/压缩产物的载体，任何压缩入口都不得把它当对话正文再压一遍。

### 落点

| 纪律 | 落点 | 说明 |
|------|------|------|
| 覆盖轮次不再取材 | `SessionContextBuilder` 原料取材口 | `OlderMessages` 现在只收「窗口外、未被 `SummaryThroughTurn` 覆盖、非合成复述」的对话原文。覆盖线未知（老日志）时**保守放行**：宁可多重一遍，不静默丢轮次 |
| 合成块不取材 | 同上 + `LlmContextSummarizer` 边界 | 双重防线：投影层不取材；摘要器层再滤一遍（防将来「注记事件化」时从别处漏进来）。原料全被滤掉时不空转，调用方沿用旧摘要 |
| 产物只作背景 | `IContextSummarizer` 契约 + `LlmContextSummarizer.BuildPrompt` | 旧摘要只出现在「已有的工作记忆」背景段，永远不在正文里 |
| 覆盖线如实延伸 | `ManualCompactAsync` | 摘要**没更新**（失败/超时/无新原料）时沿用旧覆盖线 —— 把没吸收的轮次记成「已覆盖」会让下一次增量摘要静默跳过它们，信息就此蒸发 |

新增 `AgentFramework.Contracts.SyntheticContent`：把「·非用户发言】」约定收成一处判定，
并在注释里完整记录 INV-C1 的条文与理由（今后所有合成注入都沿用这个标注，即自动受保护）。

### 测试（VerifyContext §13，16 条）

- 覆盖线内的轮次不再进压缩原料；覆盖线之后的旧轮仍是原料；
- 压缩产物本身永不在原料里；合成复述块不进原料、旁的真对话照常取材；
- 无摘要时全量取材（老语义不回归）；覆盖线未知时保守放行；
- 摘要器边界把合成块挡在正文外；原料全滤掉时不空转；
- **端到端两次压缩**：第二次的 `previousSummary` 是第一版摘要，正文里没有它、
  也没有它覆盖过的任何轮次，只含新进展。

---

## 2. 权限输入级匹配 + external_directory

### Why

现有审批是**分级档位**（ask / plan / build / yolo），判的是「动作的性质」：
只读放行、写/执行询问、高危再问。但用户自己的口径只有**输入级规则**说得清：

```
run_command git *   → allow    # git 只读操作别打断我
run_command rm *    → deny     # 删东西必须走显式授权
edit_file *.mdx     → allow
```

MiMo Code 的对应机制是 permission rules（三值 `allow / ask / deny`，**last-match-wins**），
外加 `external_directory` 等**特殊防护**。后者不是规则而是硬闸 ——
越界访问是独立的危险维度，不允许被一条宽泛规则（如 `write_file * = allow`）静默放行。

### 落点

新增 `src/AgentFramework.Host/ApprovalRules.cs`：

| 组件 | 职责 |
|------|------|
| `ApprovalRule` / `ApprovalRuleSet` | 输入级规则：对「工具名 + 关键参数」拼出的**签名**做通配匹配（`*`/`?`，大小写不敏感），last-match-wins。签名优先取 `command / path / from / to / dir / directory` 参数（内容噪音不进签名），如 `run_command git status`、`write_file docs/a.md` |
| `ExternalDirectoryGuard` | `external_directory` 硬闸：**越界写**（含 `..` 逃逸）不许静默放行，最多升级为 Ask（无界面即拒绝）。只管写不管读 —— 伤害向量是越界写，越界读是常见合法需求 |
| `ApprovalPolicyChain` | 判定链合流点，两条装配路径（`HostModule` / `HostBuilder`）与运行期切档共用 |

判定链优先级（高 → 低）：

1. **计划模式闸**（`PlanModePolicy`）—— 模式的承诺，规则和档位都推不翻；
2. **输入级规则** —— 用户显式写下的内容口径，命中即定（可比档位更松或更紧）；
3. **档位策略**（`ApprovalTiers`）—— 按动作性质判定；
4. **external_directory 硬闸** —— 越界写最多问，除非用户显式授予。

### 配置面

`agent.json`：

```json
{
  "approvalRules": [
    { "match": "run_command git *", "action": "allow" },
    { "match": "run_command rm *",  "action": "deny"  }
  ],
  "allowExternalDirectory": false
}
```

- `action` = `allow` / `ask` / `deny`；按数组顺序匹配，后写覆盖先写；
- `allowExternalDirectory: true` = 显式授予越界访问（默认不授予）；
- 一次性 CLI 的 `--allow-command` 是全放行通道，连越界访问一并显式授予（改动点）；
- 规则拒绝时拒绝理由写明命中的模式，方便模型与人对账。

### 测试（VerifyHost G5，20 条）

签名拼装、通配与大小写、规则盖过档位（双向）、last-match-wins 顺序语义、
规则收紧到询问、越界写升级 Ask（含 `..` 逃逸）、越界闸压过规则放行、
显式授予后放行、越界读不管、无工作区不瞎拦、计划模式闸最高优先（规则推不翻），
以及**端到端**：真实宿主 + 规则配置，`run_command echo *` 放行成功、
未获规则放行的命令被如实拒绝。

---

## 3. 验证

`cmd /c verify-all.cmd`：**18 套件 1104 通过 / 0 失败 / 1 跳过**（v3.16 基线 1074 → +30）。

| 套件 | 数字 | 变化 |
|------|------|------|
| VerifyContext | 139 | +16（§13 INV-C1） |
| VerifyHost | 101 | +20（G5 权限规则） |
| 其余 16 套件 | — | 全绿不变 |

> 注：VerifyTools 的检查数随 `linkSupported`（符号链接穿透测试）在环境间浮动，属条件性检查。

事件 JSONL 兼容性不变：本轮没有新增事件字段（`SummaryThroughTurn` 等均为既有可空字段）。
