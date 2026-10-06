# v3.22 · checkpoint 接入 + MCP 资源 + 按调用选 shell（能力与实用性批）

> 主题：**把「学 dsh」落到能跑的东西上** —— 兑现一条挂了三个版本的死契约，
> 补上三处长尾接缝，顺手清掉审查报告点名的几个「不好用 / 不安全」细节。
> 全部落点都有验证断言；全量 **20 套验证工程 0 失败**。

---

## 0. 一句话

`CheckpointEvent` / `ICheckpointWriter` 从 v3.11 就「契约就位」，三个版本没人产、没人消费（README 一度如实承认）；
这一版把它**真接进主循环**（写盘不动上下文），并补齐 **MCP 资源**、**按调用选 shell**、**原子写**、
**Goal 验证器格式纠正**、**bwrap 网络档统一**。压缩水位（`0.8`）一个字没改。

---

## 1. checkpoint 真接入（本版主角）

### 1.1 为什么值得单独做

外部审查的原话是「把死契约当成核心贡献宣传」。设计本身没问题（写入早、裁剪晚；写盘不动上下文），
问题在于它一直是**设计**。这一版让它变成**行为**。

### 1.2 做了什么

| 层 | 落点 | 说明 |
|---|---|---|
| writer | `src/AgentFramework.Llm/LlmCheckpointWriter.cs` | 旁路小模型调用；要求按固定字段吐 **JSON**（不是自由文本，否则进不了结构化事件）→ 解析成 `CheckpointEvent`；**解析不出返回 null**（宁可不写也不写废稿） |
| 恒定大小 | `TrimToLimit` | 超限按「杂项 → 错误修复 → 发现 → 文件 → 约束 → 决策 → 当前工作 → 下一步 →（最后）**意图**」逐级裁；**意图是锚点的锚点，留到最后** |
| 触发 | `MaybeCheckpointAnchorAsync`（`AgentHost.Context.cs`） | 水位跨 `TriggerRatio`（0.35）**且**距上次至少新增 `MinNewEvents`（8）个事件（防抖）；`Enabled` 默认**关** |
| 纪律 | 后台派发 + 回合边界消费 | 复用早摘要的 single-writer 模式：writer 跑在 `Task.Run` 里，产物进 `PendingCheckpointAnchor`，由回合边界取走落盘 —— **日志追加仍严格单线程**，且**不占回合时间** |
| 记忆升级 | `PromoteCheckpointMemoryAsync` | `PromoteMemory`（默认开）把「跨任务发现」（Discoveries）升进记忆，**id 写回事件**（可审计「这条记忆是谁写的」）；逐条独立 try，单条失败不影响其余 |
| 手动 | `POST /api/context/checkpoint` | 与「立即压缩」并列；取不到回合闸就如实回「会话正忙」，不硬插 |
| 人可读 | `sessions/checkpoints/<id>.anchor.md` | **追加**写入（符合契约的「追加而非覆盖」）；与早摘要的 `<id>.md` 分开 |

### 1.3 与「早摘要」的分工（别混）

- **早摘要**（`EarlySummarizeRatio` 水位派发）：产 `ContextCompactedEvent`，**会改变模型可见上下文**；
- **checkpoint**（本版接入）：产 `CheckpointEvent`，**投影器不处理它**，只落盘、只供 rebuild 读。

两条通道各有各的 pending 槽、各有各的 in-flight 标志。

### 1.4 验证

`tests/AgentFramework.VerifyCheckpoint` **32 → 49 项**：新增 writer 解析（围栏 / 裸 JSON / 全空 / 无法解析）、
裁剪（超限内 + 意图保留 + 杂项先裁）、宿主接入（默认关不写不调用 / 手动写成功 / 事件追加 / 触发来源 / 增量窗口 /
记忆 id 回写 / 文件落盘）。

---

## 2. MCP 资源面（对齐 dsh-mcp-resources）

- `McpClient` 新增 `SupportsResources`（读 `initialize` 的 `capabilities.resources`）、
  `ListResourcesAsync`（`resources/list`）、`ReadResourceAsync`（`resources/read`）。
- `McpModule`：**只对声明了 resources 能力的 server** 注册两个只读工具
  `mcp__<id>__resource_list` / `mcp__<id>__resource_read`，落进同一个 `mcp:<id>` 延迟包。
  没声明的就不注册 —— 「调用即 method not found」的假门面比没有更糟。
- `McpContent.FlattenResource`：文本原样带出；**二进制只报大小**（不往上下文灌 base64）。
- 仍然留白：`prompts` / `sampling`、HTTP/SSE 传输。

---

## 3. run_command 按调用选 shell（对齐 dsh 的 bash / pwsh 双工具）

- `run_command` 新增可选参数 `shell`（`auto`/`bash`/`sh`/`pwsh`/`powershell`/`cmd`）。
- **安全要点**：`ShellResolver.Resolve` 会把认不出的字符串当**显式路径**直接执行 ——
  所以模型参数必须过白名单（只收关键字），否则等于开了一个「任意程序执行」入口。
  运维侧配置（`--shell`）仍允许写路径（那是可信输入）。
- 会话默认（`EffectiveShell`）行为不变；报告里 `shell=` 如实反映实际用的那个。

---

## 4. 原子写（对齐 dsh-atomic-write）

- 新增 `AtomicFile`：写同目录临时文件 → `File.Move(overwrite: true)`。
  临时文件与目标**同目录**（跨卷 Move 不是原子的，等于没做）。
- 换掉 4 处直接覆盖写：`write_file`、`edit_file`、`update_notes`、ArtifactStore。
- 附带好处：目标是符号链接时换的是**链接本身**，与写边界防护（realpath 落盘）方向一致。

---

## 5. Goal 验证器格式纠正（审查 P2-8）

- 原状：解析不出裁决 = `Met()`（fail-open）—— 方向恰好与 Goal 的初衷相反（防提前收尾，结果格式一歪就放行）。
- 改法：首次解析不出 → 带**强约束**（「只输出三行」）**重问一次** → 仍解析不出才放行。
  fail-open 保持（验证器不许卡死收尾），只是多给一次机会；新增 `HasVerdictLine` 区分
  「解析失败」与「模型真的判了 MET」。
- 成本：多一次旁路调用，**仅当首次格式歪掉**（罕见）。

---

## 6. bwrap 网络档统一（审查 P0-1 的剩余部分）

- 核实：`bwrap` 已经是 `auto` 首选档（此前已修），缺的是**网络语义**。
- 改动：`BwrapSandboxBackend` 支持 `isolateNetwork`；`--sandbox-network none` 时
  bwrap 一并 `--unshare-net`（与容器后端同一套档位语义）。默认仍放开（不打断 `npm install`）。
- 抽出纯函数 `BuildWrapper`，验证直接断言隔离参数（只读根 / 只挂工作区 / 可切换禁网）。

---

## 7. 未做（如实记账）

- **v3.12 rebuild**：分叉新会话 + checkpoint 当种子（checkpoint 事件已可供 rebuild 读，接线未做）。
- **子 agent 管控面**：并行 / 发消息 / 打断（仍同步一次性）。
- **MCP prompts / sampling**、HTTP/SSE 传输。
- **长尾集成**：定时调度 / Webhook / ACP / Hooks 兼容。
- **审批风险等级契约面**（审查 P1-1）：审批名单仍有两份硬编码工具名（含幽灵 `copy_path`）—— 未动。
- **路由 fail-open**（审查 P1-7）：未动。

---

## 8. 验证与回归

- 全量构建：**0 错误**。
- 全量验证：**20 套工程 0 失败**（本版新增/扩充 41 项断言）。
  - VerifyCheckpoint 32 → **49**
  - VerifySandbox 42 → **51**（+3 跳过）
  - VerifyTools 51 → **59**
  - VerifyAgent 57 → **60**
  - VerifyHost 101 → **104**
