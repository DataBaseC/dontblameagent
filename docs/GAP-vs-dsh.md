# dba vs dsh · 能力差距对账（v3.27 现状）

> **基线**：dsh `0.2.0-rc.2`（2026-10 从 npm 抓取，**74 个子包**，逐包读 description）。
> **对账对象**：dba v3.27（本仓当前代码，逐项 `grep` 确证）。
> **与旧审查的关系**：`review/D-capabilities-gap.md` 的矩阵做的较早（当时结论 22 对齐 / 28 部分 / 26 缺失），
> 其中**已有 8 项在本仓后续版本补上**（下表标「v3.x 补」）。本文是**更新后的现状**。

---

## 一、一句话结论

**编码 agent 的骨架已经齐了，且几处比 dsh 更细；差的是「自动化与集成的长尾」和「隔离强度」。**

三档：

| 档 | 内容 | 量 |
|---|---|---|
| **已对齐** | 文件链 / 搜索 / 编辑 / 会话持久化 / 投影 / 压缩 / 工具结果瘦身 / 原子写 / Plan 模式 / 沙箱可注册 / 设置持久化 / MCP tools / 持久 shell（v3.20 补）/ 后台 jobs（v3.20 补）/ 子 agent 管控（v3.24 补）/ MCP resources（v3.22 补）/ 技能按需加载（补）/ Tool Search（v3.26 补） | ~30 项 |
| **部分** | 搜索靠 HTML 抓取 · 读默认放行全盘 · auto 档无真隔离 · 无 PowerShell 通道 · MCP 仅 stdio · token 是估算 · 无 office 生成 · persona/指令文件弱 · 无 CLI 命令面 | ~18 项 |
| **缺失** | 定时调度 · Webhook/GitHub · Hooks 兼容 · ACP · HTTP 代理 · Headless/SDK 服务面 · Agent Team · Workflow/PTC · 语音 · auto-review · 会话引用 · 时间/tmux 上下文 · 跨平台凭据 · goal 工具 · present | **~20 项** |

---

## 二、已对齐（含本仓后续补上的 8 项，标「补」）

| dsh 能力 | dba 对应 | 备注 |
|---|---|---|
| `dsh-tool-fs` / `-fs-search` / `-str-replace-editor` | `read_file`/`write_file`/… + `find_files`/`grep_files` + `edit_file` | `edit_file` 的唯一性校验 + CRLF 宽容比裸 replace 稳；另有 `read_lines` 流式续读 |
| `dsh-tool-web` | `web_search` / `web_fetch` | SSRF 私网默认禁；弱在搜索后端（见「部分」） |
| `dsh-tool-ask-user` | `ask_user` | 选项 / 超时 / 无界面如实失败；**v3.27 起有 jsdom 端到端** |
| `dsh-session*`（jsonl / 投影 / 查询） | `JsonlEventLog` + `SessionProjector` + `SqliteSessionIndex` | 检索是 LIKE，非结构化查询 |
| `dsh-compaction-basic` + `-tool-result-pruner` | 早摘要 + 裁剪 + `ArtifactStore` | dba 把「**写入**（可早）」与「**裁剪**（必须晚）」拆成两个触发点，另加 checkpoint 与 rebuild |
| `dsh-atomic-write` | `FileOps/AtomicFile.cs` | **v3.22 补** |
| `dsh-plan-mode` | `PlanModePolicy` | 用**权限层焊死**只读（比提示词强） |
| `dsh-sandbox-local` | `SandboxRegistry`（off/process/job/bwrap/container） | 换沙箱 = 加插件 |
| `dsh-tool-bash-persistent` | `shell`（常驻会话，cwd/env/venv 跨调用保留） | **v3.20 补** |
| `dsh-tool-jobs` / `dsh-jobs-local` | `job` + `JobManager`（后台作业，不阻塞回合） | **v3.20 补** |
| `dsh-tool-bash` 的 shell 选择 / timeout | `shell` 的 `timeout` / `run_in_background`；`run_command` 的按调用选 shell | **v3.22 补** |
| `dsh-tool-subagent` + `-subagent-control` | `spawn_subagent` + `subagent`（名单/状态/取结果/wait/追加/打断/**抢占**/**深度闸**） | **v3.24–25 补**，覆盖面**超过** dsh-control（它只有 send_message / interrupt / list） |
| `dsh-mcp-client`（tools） | `McpClient`（stdio、握手、环境清洗、封顶） | — |
| `dsh-mcp-resources` | `mcp__<id>__resource_list` / `resource_read` | **v3.22 补** |
| `dsh-tool-skill` | `use_skill`（模型按需加载技能） | **补** |
| `dsh-shell-env` | `ProcessEnvironment.ToolingAllowlist` | 密钥不进子进程 |
| `dsh-settings` | `ModelSettingsStore` / `ContextSettingsStore` / `RephraseSettingsStore` | — |
| `dsh-hmr` | `plugin_write` / `plugin_reload`（脚本插件热装） | 插件级热重载；dsh 是模块/配置级 |

### dba 反过来更细的地方（值得知道）
- **Tool Search**（v3.26）：延迟工具检索 + 单件拉起 + `reset`；dsh 的 `agent-tool-presentation` 只有 Code Mode/native 呈现切换。
- **子 agent 管控面**：多了「等待 / 取结果 / 抢占式中断 / 派生深度闸 / 界面面板」。
- **上下文治理**：写入与裁剪两个触发点 + `PromoteMemory` + **rebuild**（从 checkpoint 开新窗口，只带状态不带史）。
- **Plan 模式**：权限层 Deny 而非提示词。
- **桌面 GUI**（Web UI + Launcher）、**工具包开关**（`Eager`/`Protected` 两个标志实现延迟与防自锁）。
- **密钥不落子进程**：命令与 MCP 子进程都先清空环境再白名单。

---

## 三、部分对齐（差一口气）

| # | 差距 | dsh | dba 现状 | 补的成本 |
|---|---|---|---|---|
| 1 | **默认档无真隔离** | `sandbox-policy` + `fs-sandbox` + `pwsh-sandbox` 把隔离做成默认路径 | `auto` 在非 Windows 落 `process`（只做 cwd/TMP/超时/输出）；`bwrap` 是 **opt-in 且不隔离网络** | 中（把 bwrap 纳入 auto 链 + 默认禁网） |
| 2 | **读默认放行全盘** | `fs-observation-policy`（模型能看到什么） | `AllowReadOutsideWorkspace = true`；只有**写**有硬边界 | 小（加观察面策略 + 默认收窄） |
| 3 | **搜索靠 HTML 抓取** | provider 可插拔 | 默认 `bing,baidu,searxng` 全是抓取，随改版失效、有合规风险 | 小（把 provider 做成插件点） |
| 4 | **无 PowerShell 通道** | `dsh-tool-pwsh` + `-persistent` | 单 POSIX（Windows 回落 cmd）；无 pwsh 持久通道 | 中 |
| 5 | **MCP 仅 stdio** | 多传输 | 无 HTTP/SSE；`prompts`/`sampling` 留白 | 中 |
| 6 | **token 计量是估算** | `token-meter`（replay-aware 精确） | `TokenEstimator` 估算 + 端点实报 usage 兜底 | 小 |
| 7 | **无 office 生成** | `skill-office`（Word/PPT/Excel 工作流 + 结构校验） | 只有 `read_document` 读 + `csv_to_json` | 中 |
| 8 | **工作区指令文件** | `agent-instructions`（AGENTS.md / CLAUDE.md） | 零命中（指令靠 `SystemPrompt` 与记忆块） | 小 |
| 9 | **persona / 预设组合面** | `persona` + `agent-preset`（YAML 声明能力组合） | 有模式体系 + SystemPrompt，无「预设组合」概念 | 小 |
| 10 | **CLI 命令面** | `command-compact` / `command-goal`（slash 命令） | 只有 HTTP API（`/api/context/compact` 等） | 小（形态差异） |
| 11 | **附件只支持图像** | `attachment-local` | `ReadImageTool` → Attachments | 中 |
| 12 | **无内置运行时 payload** | `load_workspace_dependencies`（打包 Python/Node/pnpm） | 依赖宿主环境 | 中 |

---

## 四、缺失（整块空缺，`grep` 零命中确证）

### A. 自动化与集成长尾（差距最大的一整块）
| dsh 包 | 能力 | 说明 |
|---|---|---|
| `dsh-schedule` / `experimental-schedule-bundle` | **定时调度**（after / at / fixed-rate 提醒，落在会话事件日志上） | dba 的 `DreamJob`/`DistillJob` 是内部演化，不是用户调度 |
| `dsh-webhook` / `-webhook-github` | **Webhook 运行时** + 签名 GitHub 适配器（外部事件**创建会话**） | dba 是「打开界面才干活」 |
| `dsh-hooks-claude-code` / `-hooks-codex` | **Hooks 兼容桥**（直接跑 Claude Code / Codex 的 hooks.json） | 复用人家的生态 |
| `dsh-acp-app` | **ACP**（Agent Client Protocol，被编辑器/IDE 驱动） | dba 无对外协议面 |
| `dsh-http-proxy` | **进程级出网代理策略** | dba 各 HTTP 调用各自为政 |
| `dsh-headless` / `sdk-app` / `sdk-minimal` | **无 UI 一次性运行 + JSON-RPC stdio 服务** | dba 只有 Web UI + Launcher，**不能作为服务被外部调用** |
| `dsh-experimental-agent-team-profile` | **Agent Team**（多 agent 协作 profile） | dba 只有子 agent 派生 |
| `dsh-tool-workflow` / `workflow-ptc` | **Workflow / PTC**（用 JS 脚本在沙箱运行时里编排工具调用） | dba 无编排原语（工具是逐个调用） |
| `dsh-experimental-voice-input-bundle` | **语音输入**（本地 SenseVoice） | — |
| `dsh-experimental-auto-review` | **逐工具 LLM 授权复审** | dba 是规则 + 人工审批 |

### B. 机制出口未开（成本低、价值中）
| dsh 包 | 能力 | dba 现状 |
|---|---|---|
| `dsh-tool-goal` / `goal` / `goal-round-driver` | **模型可见的 goal 工具** + 会话内 goal 生命周期 + 轮次驱动 | 只有宿主侧 `SetGoal` + 旁路验证器（模型不能自查/改目标） |
| `dsh-tool-present` | **显式交付声明**（presentation 语义） | 只有 `ArtifactStore` 的引用化 |
| `dsh-session-reference` | **跨会话快照引用**（长期、不可信模型的上下文） | 零命中 |
| `dsh-time-context` / `-tmux-context` | 每步注入**当前时间**/tmux 位置 | 系统提示**不含时间**（零命中） |
| `dsh-credentials-local` | **跨平台本机凭据层** | 只有 Windows DPAPI；非 Windows 明文落盘 |
| `dsh-agent-instructions` | AGENTS.md / CLAUDE.md 加载 | 零命中（见「部分」8） |
| `dsh-fs-observation-policy` | 「模型能看到什么」策略 | 零命中 |
| `dsh-pwsh-sandbox` | 逐命令过沙箱并回报 denial/enforcement | dba 沙箱是后端级，非每命令策略 |

---

## 五、如果只做三件事（建议）

| 优先级 | 做什么 | 为什么 |
|---|---|---|
| **1** | **把 `bwrap` 纳入 `auto` 链并默认禁网**（失败回落 + `ResolveNote` 说明） | 唯一一条「默认部署下 agent 等同裸跑」的 P0 级风险；改动面小 |
| **2** | **Headless / SDK 服务面**（无 UI 跑 + stdio JSON-RPC） | 决定「dba 能不能被别人（脚本/CI/编辑器）调用」——这是长尾集成的**入口**，也顺手把 `ACP` 的路铺开 |
| **3** | **定时调度 + Webhook**（`schedule` + `webhook`） | 让 dba 从「打开界面才干活」变成「事件驱动」；dsh 已验证这条路的形态 |

> 其余（hooks 兼容 / PTC / team / 语音 / auto-review）都是「别人有、我们也许不需要」的长尾，
> 等真有需求再加 —— 与 dba 一贯的「机制留位、需求驱动」一致。

---

## 六、口径说明与存疑

- 本表「缺失」= `src/` 下 `grep -rilE` 零命中；「部分」= 有相近机制但不等价；「已对齐」= 能指到实现。
- dsh 的子包描述是**官方 npm description**，非猜测；但**包的语义只从描述判断**，未逐包读源码，个别项可能有偏差（如 `dsh-hmr` 的覆盖范围）。
- dba 的「领先项」（Tool Search / 子 agent / 上下文治理 / Plan 模式）是**按代码确证**，不是营销口径。
