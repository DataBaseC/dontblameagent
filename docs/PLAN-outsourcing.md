# 外包任务书 · 六项功能扩展

> 用途：拆给外包实施的**需求提示**。每项自包含，可单独粘贴给一个外包。
> 本文只写「做成什么样 / 验收怎么过 / 哪里不许动」，不规定内部实现细节。
>
> 源码根：`AgentFramework-src-v3.5`（C# / .NET 10，Windows 桌面）。
> 设计文档：`README.md`、`docs/PLAN-toolset-exposure.md`、`docs/PLAN-memory-evolution.md`、`docs/PLUGIN-SDK.md`。

---

## 0. 所有任务共同约束（必读，粘贴时带上）

1. **语言与汇报**：代码注释/文档/提交信息用中文；问题说明带 `文件:行号`。
2. **禁止破坏事件配对**：`ToolCallRequested` 之后**任何退出路径**都必须发 `ToolCallCompleted`（含异常/取消/拒绝）。违反会让 UI 永久卡「执行中…」并导致下一轮 400。实现模式参考 `src/AgentFramework.Agent/AgentRunner.cs` 的 `completedEmitted` + `EmitToolCompletedAsync`。
3. **禁止破坏真相源**：JSONL 仅追加；日志修复只许修尾巴（`ScanAndRepair`），禁止销毁中段之后的完整事件。派生数据（SQLite 索引）可丢，记忆/JSONL 不可丢。
4. **Qwen/vLLM system 只能在 messages 最前**：`BuildPayload` 合并全部 system 到 index 0；近因锚点用 **user +「非用户发言」前缀**，禁止 system 挂末尾。
5. **命令子进程环境白名单**：API key 等密钥绝不进子进程（`ProcessRunner`）。
6. **所有 `*.cmd` 必须 ANSI/GBK（936）+ CRLF**；`pack-launcher.cmd` 优先纯 ASCII+CRLF。禁止 `chcp 65001`。含 `pause` 的脚本须支持 `AUTO_PAUSE=1` 跳过。
7. **回归**：`tests/AgentFramework.Verify*` **串行**跑；共享插件 DLL 争用时两套之间隔约 2s。改了 role/文案/计数，必须同批更新钉死这些形状的断言。
8. **安全红线不许放松**：写路径 TOCTOU（RealPath 穿透后同路径比较）；SSRF 连接时钉 IP（`ConnectCallback`）；HttpClient Bearer 只挂单次请求；写只能落工作区（`run_command` 除外，文档已如实降级）。
9. **打包**：开发/验证 `-c Debug`（`run.cmd` / `build-host.cmd` / `verify-all.cmd`）；发布走 `pack-launcher.cmd`（Release）。不要拿 Debug 半成品和 green 包混用。
10. **范围**：只改自己那项任务列出的文件面；跨项共享文件（尤其 `WebUiPage.cs`、`WebUiServer*.cs`）**一人一项、按时间片串行合并**，禁止多人同时改同一文件。

---

## 1. 对话界面 · 上下文与压缩设置（学 MiMo）

### 需求意图
对话页要有一个**设置入口**，用户可以调：
- 对话上下文大小（token 预算）
- 超过多少水位自动压缩 / 自动写盘（checkpoint）
- 压缩策略（是否遮蔽旧工具结果等）
改完**立即生效**（活配置），不要求重启。参考 MiMo 的设置面板形态：分组、可恢复默认、有当前水位可视化。

### 现状（后端已有，缺 UI 与 API 落点）
| 项 | 位置 |
|---|---|
| 活配置实例 | `HostOptions.Context`（`src/AgentFramework.Host/HostOptions.cs:110`）——「水位、窗口、遮蔽策略都能改完即生效」 |
| Token 预算 / 压缩水位 | `Contracts.ContextOptions`（`src/AgentFramework.Contracts/ContextGovernance.cs:13`）：`TokenBudget=24000`、压缩比例、`MaskOldToolResults`、`MaskedNoteMaxChars`、`MinMaskSavingChars` |
| 写盘水位 | `Contracts.CheckpointOptions.TriggerRatio`（默认 **0.35**）见 `Checkpoint.cs` |
| 压缩水位 | 默认 **0.8**（写入早、裁剪晚，**两个触发点勿绑成一个**） |
| 当前水位只读展示 | `AgentHost.ContextStatusWaterLevel()` / `/api/status` 的 `context` 字段 |
| 前端已有水位胶囊 | `WebUiPage.cs` 的 `#pill-ctx .bar` |

### 要做什么
1. **设置面板**（建议挂在现有 `footer .settings` 折叠区，或独立「⚙ 上下文」面板）：
   - `TokenBudget`（上下文预算，单位 token 或「约 N 字」，换算要标注口径）
   - 自动写盘水位 `CheckpointOptions.TriggerRatio`（默认 0.35，可 10%–80%）
   - 自动压缩水位（默认 0.8，可 40%–95%）
   - `MaskOldToolResults` 开关、`MaskedNoteMaxChars`、`MinMaskSavingChars`（可进「高级」折叠）
   - 「恢复默认」按钮（对照 `ContextOptions` 初始值 / `CheckpointOptions` 初始值）
2. **API**：`GET /api/context`（读当前值 + 当前水位 + 最近一次 compaction/checkpoint 时间）与 `POST /api/context`（写回 `HostOptions.Context` 与 checkpoint 选项，**活实例**）。写入要做范围校验（预算 > 0、比例 0–1、两水位关系合理）。
3. **持久化**：随会话/工作区配置落盘（对齐现有 `ModelSettingsStore` / `RephraseSettingsStore` 的落法），重启后仍生效。
4. **可观察**：发生自动压缩/写盘时，时间线已有事件（`ContextCompactedEvent` / `CheckpointEvent`）——设置面板里给「最近一次」摘要即可，不必新做时间线。

### 验收
- [ ] 改预算/水位后**不重启**，下一轮对话生效（可用 debug 面板或 `/api/context` 复证）。
- [ ] 压缩水位 > 写盘水位的约束有提示或自动纠正（禁止配成「还没写盘就先裁剪」语义颠倒还静默通过）。
- [ ] 恢复默认后与 `ContextOptions`/`CheckpointOptions` 源码默认值一致。
- [ ] `verify-all.cmd` 全绿；新增 `Verify*` 断言覆盖写回与范围拒绝。
- [ ] UI 在窄窗口下面板可滚动，不出现「点不到保存」（见任务 3 的布局要求）。

### 禁止
- 不要把两个水位合并成一个开关。
- 不要改 `SessionProjector` / 压缩算法本体 —— 本项只做**暴露与配置**，算法另开任务。

---

## 2. 扩展「生成技能」相关工具种类（按设计文档扩工具）

### 需求意图
技能/创意工坊（skill）目前偏「声明包」。需要**更多能生成、校验、管理技能与工具的工具**，扩展工具种类，让 agent 能自助产出可用技能，而不是只靠人手写 `skill.json`。

### 现状
| 项 | 位置 |
|---|---|
| 工具包与暴露面设计 | `docs/PLAN-toolset-exposure.md`（三层：Toolset → deferred → Tool Search） |
| 插件 SDK | `docs/PLUGIN-SDK.md`；脚本插件 Jint 无 CLR，capabilities 未声明即拒 |
| 工具包清单 | `BuiltinToolsets`（`src/AgentFramework.Contracts/Toolsets.cs`）：core / meta / exec / memory / search / plan / web / self |
| 官方工具→包映射 | `HostBuilder.OfficialToolsetMap()`（`src/AgentFramework.Host/Hosting/HostBuilder.cs:748`） |
| 技能导入/清单 | `WebUiServer.Routes.Chat.cs` 的 `/api/skills*`；`SkillLoader`；`plugin_write` 等 `self` 包工具 |
| 自写插件 | `PluginStore` + `ScriptPlugin`（路径必须相对且落根内） |

### 要做什么
按 `PLAN-toolset-exposure.md` 的包模型扩展，**至少**补出下列工具（名称可微调，包归属必须清晰）：

| 能力 | 建议工具 | 建议包 |
|---|---|---|
| 生成技能骨架 | `skill_scaffold`：给 id/名称/描述 → 写出 `skill.json` + README + 提示词草案 | `self` 或新包 `skill` |
| 校验技能包 | `skill_validate`：schema、工具白名单是否引用了未注册工具、与内置/工坊重名 | `skill` |
| 从对话/文档提炼技能 | `skill_extract`：输入一段材料 → 产出技能描述 + 提示词 + 工具白名单草稿 | `skill` |
| 工具目录查询 | `tool_catalog`：按包/关键词列出已注册工具与 schema 摘要（支持 deferred 索引） | `meta` |
| 扩展工具类型 | 支持**新工具类型**注册面：至少补一类「结构化输出工具」或「多模态输入工具」的官方样例（二选一，跟设计文档口径），并走 toolset 声明 | 新包 `lab` 或并入 `core` 旁路 |

约束：
- 新工具必须实现 `ITool`（+ `IToolWithSchema` / `IToolWithToolset` 视情况），并声明 toolset；**装载 ≠ 暴露**，默认策略对齐 `PLAN-toolset-exposure.md` §3.3。
- 写路径一律走 `IWorkspaceService` / 现有写边界，禁止另起一套路径解析。
- 技能导入已有 409 重名拒绝、400 引用未注册工具等校验 —— 新工具要复用同一套校验，不要复制粘贴出第二套规则。
- 脚本插件能力：新能力若给 Jint 用，须 capabilities 显式声明，pre-execute 钩子异常 **fail-closed**。

### 验收
- [ ] 上表工具可被 agent 调用，且在 `GET /api/tools` 的 `source`/toolset 可见。
- [ ] `skill_scaffold` → `skill_validate` → 导入成功，构成闭环；故意造坏包能被指出具体字段错误。
- [ ] `PLAN-toolset-exposure.md` 的暴露面公式仍然成立（包 ∩ 启用 ∩ 模式白名单）。
- [ ] `VerifyTools` / `VerifyPlugins` / `VerifyToolsets` 全绿；为新工具补测试。
- [ ] 文档：`docs/PLUGIN-SDK.md` 与 `README.md` 工具计数改为「以 `verify-all.cmd` 输出为准」口径。

### 禁止
- 不要为了加工具去改压缩/事件配对/安全边界。
- 不要做 MCP 通道与 Tool Search 本体（设计文档标为下一步，本项只留数据结构位）。

---

## 3. UI 流畅度与底部菜单可用性

### 需求意图
人机交互要更跟手；**底部菜单/设置一开多就看不到下面的按钮**（模式、转述、模型、技能、记忆等 `footer .settings` 面板堆叠时超出视口）。需要更流畅的布局与操作反馈。

### 现状
| 项 | 位置 |
|---|---|
| 底部结构 | `WebUiPage.cs` `footer`（约 218 行起）：`#phase` + `.composer` + 多个 `.settings` 折叠块 |
| 面板清单 | `#settings`（转述）、`#model-panel`、`#skills-panel`、记忆/工具包等 |
| 侧栏/主区 | `body { overflow: hidden }` + `#stream` 独立滚动（约 87–138 行） |
| 已知大文件 | `WebUiPage.cs` ~2300+ 行（HTML+CSS+JS 单文件） |

### 要做什么
1. **布局修复（硬验收）**：
   - 任意数量面板同时打开时，**发送按钮、「保存」类按钮、模式切换**必须始终可见可点（固定可达，或面板自身滚动 + 吸底操作条）。
   - 小高度窗口（高度 ≤ 700px）不丢按钮。
2. **流畅度**：
   - 面板开合、工具卡渲染、流式打字不做整页重排导致的跳动；长列表（工具卡/技能/记忆）虚拟化或分页（列表 > 100 条时滚动不掉帧）。
   - 打字输入与发送不被 JS 长任务卡住；`requestAnimationFrame` / 事件委托优先，避免每条 SSE 重建整棵 DOM。
3. **人机交互**：
   - 常用操作有明确 hover/active/focus；快捷键已有（Enter 发送 / Shift+Enter 换行 / Esc 停止）保持并可在设置里看到说明。
   - 破坏性操作（删会话、停宿主、关工具包）二次确认；toast/状态位替代弹窗刷屏。
4. **可选加分**：面板改「抽屉 / Popover」而非把 footer 撑高；输入区自适应高度上限。

### 验收
- [ ] 手测矩阵：单开/双开/全开面板 × 窗口高度 600 / 800 / 1080，按钮均可点。
- [ ] 流式 1000+ token 回复，输入框仍可输入、滚动条不抖。
- [ ] `VerifyWeb` 全绿（DOM 钩子 id 可保留，避免大面积改名）。
- [ ] 不引入前端框架构建链（保持单文件 `WebUiPage.cs` 内嵌资源形态）；若必须拆文件，保持**运行时零构建**。

### 禁止
- 不要为了 UI 去改 API 语义。
- 不要改 SSE 协议帧格式（`type: delta/reasoning/event/approval/...`）。

---

## 4. 新建任务的模式体系（编程包 / 写作包等）

### 需求意图
「新建任务/会话」时应能选**更多模式**：编程扩展包带**编程模式**，写作扩展包带**写作模式**……模式决定提示词基调、可见工具、记忆范围，而不是只改个标签。

### 现状
| 项 | 位置 |
|---|---|
| 模式枚举 | `AgentMode`：Work / Chat / Design（`src/AgentFramework.Contracts/Memory.cs:379`） |
| 档位 | `ModeProfile`：`MemoryScopes`、`AllowedTools`、`AllowedToolsets`、`InjectTaskCard`、`InjectNotes`、`ContextGovernance`、记忆索引/召回参数（同文件 401+） |
| 自定义档位 | `ModeProfile.CustomId` + `AgentModes.Available` / `TryResolve` —— **插件可注册新模式，无需改枚举** |
| API | `POST /api/mode`、`GET /api/modes`（`WebUiServer.Routes.Chat.cs:163+`）；会话以字符串 id 钉住模式 |
| 已知限制 | UI 目前三档写死（`chat` / `design` / 默认 `work`），新建会话未引导选模式 |

### 要做什么
1. **模式注册面**：扩展包（插件/技能包）在清单里声明模式（id、名称、说明、`AllowedToolsets`、提示词模板、推荐工具包），宿主合并进 `AgentModes.Available`。
2. **内置补两档样板**（证明机制，不是写死业务）：
   - **编程模式 `code`**：提示词偏「读代码→改代码→跑测试」；默认开 `exec` + `plan` + `search`；任务卡开。
   - **写作模式 `write`**：提示词偏「结构与改写」；默认收起 `exec`；记忆召回参数可不同；对接 WritingKit 工具。
3. **新建任务 UI**：新建会话/任务时弹模式选择（卡片式：名称 + 一句话说明 + 预览工具包）；可「稍后切换」。会话列表显示当前模式。
4. **切换语义不变**：模式不改装配，只改「这一轮给模型看什么」——切换**无需重启**（保持 `ModeProfile` 设计观）。

### 验收
- [ ] `/api/modes` 返回内置 + 扩展包注册的模式；按 id 切换后工具可见面变化可复证（`/api/tools` 或状态接口）。
- [ ] 编程模式下 `run_command` 可用；写作模式下默认不暴露 `run_command`（可被用户手动打开工具包）。
- [ ] 新建任务未选模式时回退 `work`，与现行为兼容。
- [ ] 自定义模式 id 不与内置冲突；冲突时 400/409 明确报错。
- [ ] `VerifyAgent` / `VerifyWeb` 中补模式矩阵测试。

### 禁止
- 不要把模式做成「只有提示词不同」却还在 UI 上假装工具面不同 —— 要以 `ModeProfile` 真实约束为准。
- 不要为模式去重建宿主（旧 `/api/sessions/switch` 语义已改为换当前会话指针，勿走回重建）。

---

## 5. 工具审批档位：ask / plan / build / 全盘托管

### 需求意图
调用工具**一个一个点确定太麻烦**。需要分档（命名可再对齐产品）：
- **ask**：每次敏感操作都问（接近现状）
- **plan**：先出计划/清单，用户一次批一批，再执行
- **build**：常规读写与执行自动放行，仅高危再问（写系统路径、网络外发、删大量文件等）
- **全盘托管**：全部自动（有明显风险横幅 + 随时可收回）
怎么合适怎么来，但**必须有「总有一档在生效」+「随时能收紧」**。

### 现状
| 项 | 位置 |
|---|---|
| 决策类型 | `ApprovalDecision`（Allow / Ask / Deny…）`src/AgentFramework.Host/Approval.cs` |
| 默认策略 | `DefaultApprovalPolicy.Decide`：读类 Allow；`write_file`/`run_command` Ask；未知 Ask |
| 可插拔 | `HostOptions.ApprovalPolicy`（`HostOptions.cs:97`）—— **活的 Func，本来就能换档** |
| UI 审批卡 | SSE `type: "approval"` → 卡片 → `POST /api/approve`（已修：原先 approval 帧不被前端消费导致假卡死） |
| 超时 | `AskDetailedAsync` 约 5 分钟超时后拒绝 |

### 要做什么
1. **四档（或三档+托管）枚举**落在 `HostOptions` / 配置单：例如 `Ask` / `Plan` / `Build` / `Yolo`（显示名中文可改）。
2. **策略实现**（替换 `ApprovalPolicy`）：
   - `Ask`：维持现状（未知 Ask）。
   - `Plan`：回合开始若将调用写/执行类工具 → 先聚合 **计划卡**（要做的工具+参数摘要列表），一次批准后本回合放行清单内调用；清单外仍 Ask。
   - `Build`：`read/list/search/remember/recall` Allow；`write_file` 在工作区内 Allow；`run_command` 按危险前缀/路径 Ask（沿用 `CommandTool` 前缀备注与沙箱档位）；出工作区写、网络外发、批量删 → Ask。
   - `Yolo/全盘托管`：Allow-all，但 UI 顶部固定横幅「已全盘托管」+ 一键收回；写审计事件。
3. **UI**：底部或命令面板一键切档；当前档位常显（徽章）。切档要写事件/日志，便于回看。
4. **审计**：每次自动放行仍发完整 `ToolPreExecute`/`ToolCallCompleted` 事件链，不得因免批而省略事件。

### 验收
- [ ] 同一工具调用在四档下行为符合上表（用 `Verify*` 钉死矩阵）。
- [ ] `Plan` 档：一次批准覆盖计划内 N 个写操作，计划外写操作仍弹卡。
- [ ] `Yolo` 档有横幅与收回入口；收回后下一次调用立刻按新档拦截。
- [ ] 超时仍拒绝且文案区分「用户拒绝」/「回合取消」（取消 ≠ 用户拒绝）。
- [ ] 不破坏 `ToolCallRequested`↔`Completed` 配对（自动放行也要成对发事件）。

### 禁止
- 不要把审批做成「只藏 UI 按钮」而策略仍每次都 Ask。
- 不要在 Yolo 档静默关闭审计事件。

---

## 6. 思考能力判定（不能思考的模型就别让人选强度）

### 需求意图
有的模型不能思考，但界面仍能选「思考强度」——应做**判定**：不能思考就不给选（或置灰并说明原因）。判定不出来就允许保守回退，但不要「假开关」。

### 现状
| 项 | 位置 |
|---|---|
| 思考参数风格 | `ReasoningStyles`：`none` / `openai`（顶层 `reasoning_effort`）/ `qwen`（user 尾部 `enable_thinking`/`thinking_budget`）—— `OpenAiCompatibleClient.cs` |
| 端点配置 | `ProviderConfig.ReasoningStyle` / `ReasoningEffort`（`ModelProfile.cs:35-41`） |
| UI | 模型编辑器「思考风格」+「默认思考强度」**始终可选**（`WebUiPage.cs:442-457`） |
| 请求侧 | `LlmRequest.ReasoningEffort`；style=`none` 时不注入 |

### 要做什么
1. **能力判定（分层，从便宜到贵）**：
   - **静态**：`ReasoningStyle == none` → 直接禁用强度选择（UI 置灰 + 说明「该端点未启用思考参数」）。
   - **模型级**：`ModelEntry` 增加 `SupportsReasoning`（可空=未知）。已知系列（deepseek-reasoner、qwen-thinking、o 系列等）可预填；用户可手改。
   - **探测（可选）**：「测试连接」时发极小请求，若返回 `reasoning_content`/`thought` 或错误码表明参数不被接受 → 回填 `SupportsReasoning`。探测失败不阻断保存。
2. **UI 行为**：
   - 不支持：隐藏或置灰「思考强度」，悬浮说明原因；已保存的旧值保留但不生效。
   - 未知：显示「自动/端点默认」，避免假装能调。
   - 风格与能力矛盾（style=none 但勾了 high）→ 保存时警告。
3. **发送路径**：不支持/未知时不要把 `reasoning_effort` 塞进请求（防 400）；Qwen 风格仅在支持时注入标记。

### 验收
- [ ] style=`none` 的端点无法选到会生效的强度（UI 与发送两侧都挡）。
- [ ] 标记 `SupportsReasoning=false` 的模型不发思考参数；`true` 且 style=openai 时请求含 `reasoning_effort`。
- [ ] 未知能力不误报「已支持」；探测失败可手动改。
- [ ] `VerifyLlm` 补：none / openai / qwen × 支持/不支持 的请求形状断言。

### 禁止
- 不要通过「改了 UI 文案」假装判定成功。
- 不要为探测去打付费大请求；探测必须极小、可超时、可关闭。

---

## 附录 A · 建议拆分与合并顺序

| 顺序 | 任务 | 主要文件面 | 与其它项冲突 |
|---|---|---|---|
| A | 任务 3 UI 布局/流畅度 | `WebUiPage.cs` 为主 | 与 1/4/5 抢 `WebUiPage` —— **先做 3 或最后合** |
| B | 任务 1 上下文设置 | `HostOptions`/`Context*`/新 API/`WebUiPage` 面板 | 与 A 协调 |
| C | 任务 5 审批档位 | `Approval.cs`/`HostOptions`/审批 UI | 与 B 都要改设置区 |
| D | 任务 4 模式扩展 | `Memory.cs`（ModeProfile）/`Chat` 路由/新建会话 UI | 中等 |
| E | 任务 2 工具扩展 | `Tools/` / `HostBuilder` / `PLUGIN-SDK.md` | 相对独立 |
| F | 任务 6 思考判定 | `ModelProfile`/`OpenAiCompatibleClient`/模型面板 | 相对独立 |

**推荐**：E、F 可并行；A 一人收口 UI；B、C 在 A 之后合入设置区；D 可与 E 并行。

## 附录 B · 统一验收命令

```bat
rem 1) 全量构建 + 串行回归
build-host.cmd
verify-all.cmd

rem 2) 发布包（Release，自包含）
pack-launcher.cmd
rem 产物：build\pack\green\  与  build\pack\out\AgentFramework.Launcher.exe

rem 3) 看编译是否成功（启动器页头 BuildInfo 时间戳）
build\pack\green\AgentFramework.Launcher.exe
```

- 回归基线：当前仓库 `verify-all` 应 **0 失败**（若见共享 `DevKit.dll` 锁的假失败，间隔 2s 重跑）。
- UI 改动需手测：审批卡弹出、工具卡 completed 收口、底部按钮可达、模式切换即时生效。

## 附录 C · 交付物（每项外包统一交）

1. 源码 diff（可编译）+ 中文提交说明（`fix:` / `feat:` 风格）。
2. `verify-all.cmd` 输出末行截图/文本。
3. 简短变更说明：改了哪些文件、如何手测、有无未做项。
4. 不提交 `bin/`、`obj/`、`build/`、`.zwork`；`*.cmd` 保持 ANSI+CRLF。
