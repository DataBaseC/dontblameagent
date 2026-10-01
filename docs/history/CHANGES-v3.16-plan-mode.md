# CHANGES-v3.16 · Plan 模式（只读规划 + 计划持久化）

> MiMo Code 长程机制落地的第四步（v3.13 修复批 → v3.14 Goal → v3.15 checkpoint-writer → 本篇）。
> 一句话：**计划模式的"不动代码"承诺由机制保证，不靠提示词自觉** ——
> 权限层焊死「禁写禁执行，唯一例外 plans/*.md」；计划文件每轮从磁盘重注入，压缩冲不掉。

## 1. 机制（对照 MiMo Code 的 plan 模式）

| 设计点 | 落地 |
|--------|------|
| 模式即约束 | 内置 `plan` 档（`ModeProfile.ReadOnly` 标记）：`agent.json` 写 `"mode": "plan"` 或界面/`POST /api/sessions/new {"mode":"plan"}` 即启用 |
| 权限层焊死（不是提示词） | `PlanModePolicy` 审批门：写源码/执行命令/删除/记忆写入/插件操作 → **拒绝**，拒绝理由写明出路（切编程/工作模式） |
| 唯一可写 = 计划文件 | `write_file / edit_file` 目标在 `plans/` 下 → 放行；`plans/../` 逃逸一律拒绝；绝对路径必须真的落在 `<workspace>/plans/` 内 |
| 计划重注入（rebuild 不丢） | `plans/plan.md` 每轮从磁盘读入动态段（`【计划文件·非用户发言】`，上限 1200 字符）——压缩/重启都不影响它 |
| 工具面不收窄 | 调研需要 grep/web/子代理；真正的约束是权限门（工具被拒时模型能看到原因并换路子） |
| 切档即生效 | 门读**会话当前 ModeId**（运行期切档立即生效），非计划模式走原策略零影响 |

## 2. 代码改动清单

| 文件 | 改动 |
|------|------|
| `AgentFramework.Contracts/Memory.cs` | `ModeProfile.ReadOnly`；内置 `AgentModes.Plan` 档（id=`plan`）；注册 id 解析/内置保护/目录 |
| `AgentFramework.Host/PlanModePolicy.cs` | 新增：只读门（例外 → 只读白名单 → 兜底拒绝 + 出路提示） |
| `AgentFramework.Host/Hosting/HostModule.cs`、`HostBuilder.cs` | 两条会话装配路径的审批委托都包上只读门（读当前 ModeId，动态） |
| `AgentFramework.Host/HostEventSink.cs` | 策略拒绝时透传预置理由（模型能看到「为什么被拒、出路在哪」） |
| `AgentFramework.Host/AgentHost.Turn.cs` | `plans/plan.md` 每轮重注入动态段（存在才注入，恒定上限） |
| `tests/AgentFramework.VerifyHost` | G4 节 16 项：纯策略 10（含 `../` 逃逸、非 plan 零影响）+ 端到端 6（真实宿主切档拦截、计划落盘、切回放行、重注入） |

## 3. 用法

```jsonc
// agent.json —— 先让 agent 出计划，审阅后切 code 执行
{ "mode": "plan" }
```

- 会话内切换：`POST /api/sessions/new {"mode":"plan"}` / 界面新建会话选「计划模式」；
  执行阶段切 `code`/`work`（`POST /api/mode`）。
- 计划文件：模型写 `plans/plan.md`（目标 / 约束 / 步骤清单 / 每步验证方式），
  此后每轮自动出现在上下文里；人可直接改这个文件来纠偏。

## 4. 验证

- `VerifyHost` 81 通过 / 0 失败（新增 G4 节 16 项全过）：写源码被拒、理由写明出路、
  plans/ 唯一可写、`../` 逃逸被拒、run_command/delete 被拒、只读工具放行、
  非 plan 零影响；端到端 plan 档拦截/计划落盘/切回放行/每轮重注入。
- **全量 `verify-all`：17 套件全绿（1074 通过 / 0 失败 / 1 跳过）**，见 `verify-all-plan.txt`。

## 5. 兼容性

- 非计划模式会话完全走原策略（门只在 `ReadOnly` 档生效）。
- 事件 JSONL 无新类型；`plans/plan.md` 是普通工作区文件，用户可随意编辑/删除。
