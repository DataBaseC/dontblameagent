# v3.4 功能批次：四个功能包（统一 debug）

> 本批 = 上下文压缩后的新起点。前置状态全部固化在 PLAN.md（§1–13）与 BATCH-v3.3.md，
> 代码以 v3.3 源码包为准。本批四个功能包共用 P1 会话地基，一次写完统一 debug。

## G1 · 子 Agent 编排层

- `AgentFramework.Agent/SubAgentRunner.cs`（新）：`RunSubAgentAsync(host, parentSession, task, opts)`
  - 开子会话（`OpenSession(id, systemPrompt, relayToUi:false)`）→ `SendAsync` → 取 `FinalText`
  - 父会话落两条事件：`subagent-dispatched`（任务+子会话 id）/ `subagent-completed`（结果摘要，截 500 字）
  - 子会话标题写 `sub-agent: {task 前 20 字}`
- 契约：`SessionEvents.cs` 加两个事件类（JSON 多态注册）
- 工具化：`SpawnSubAgentTool`（`spawn_subagent {task}`），让**主模型自己决定派工**；子会话 relayToUi=false 不串台
- WebUI：/debug 窗显示子会话列表与血缘

## G2 · 技能系统（最小版）

- `skills/{name}/skill.json`：`{ name, description, tools: [白名单], systemPromptSuffix }`
- `AgentFramework.Host/SkillLoader.cs`（新）：扫描目录 → `SkillDefinition`；`Enable/Disable` 运行期生效
- 机制 = 模式白名单的动态版：启用技能时把工具白名单 intersect 到 VisibleTools（HostModule 已支持每轮现取）
- WebUI：/api/skills 列表 + 启用开关（回合边界生效，不改装配）
- 本批只做白名单型技能；prompt 注入型留给下一批

## G3 · 计划模式

- 契约：`PlanCreatedEvent { PlanId, Steps[] }` / `PlanStepUpdatedEvent { PlanId, Index, Status }`
- `TaskCardBuilder` 扩展：有活跃计划时任务卡顶部显示 `计划：1[✓] 2[…] 3[ ]`
- 主循环不动——计划状态仍是"事件投影"，与任务状态同机制
- WebUI：/api/history 渲染计划事件；工具 `update_plan {steps}` 让模型维护

## G4 · 设计对话模式（Preset 第四档）

- `AgentMode.Design` + `AgentModes.Design` 档：全工具 + 项目记忆 + 高索引卡（MemoryIndexLimit=12）+ 设计专用 SystemPromptSuffix
- `/api/mode` 支持 "design"；WebUI 模式按钮三态循环（工作 → 设计 → 闲聊）

## 附带（本批顺手）

- X1: LauncherServer GET API 的 Origin 白名单化已由 F1 覆盖（GET 页面放行、同源校验共用）
- X2: VerifyHost 补 G1/G3 验证桩；VerifyWeb 补 /debug 子会话血缘断言

## 统一 debug 顺序

1. `dotnet build`（报错→修）
2. 12 个验证工程回归（预期 649 + 本批新增 ~14 = 663 项）
3. 实机：Launcher 启动 → 工作模式跑真实任务 → spawn_subagent 分工 → update_plan 维护计划 → 设计模式做界面 → /debug 全程盯内存表一致性
