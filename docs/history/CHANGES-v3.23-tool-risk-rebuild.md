# v3.23 · 审批按工具自报风险 + 路由 fail-open + 重建（rebuild）

> 主题：**把三条「同一语义多处实现」接缝收敛掉**，并兑现一条从 v3.11 记到今天的账（rebuild）。
> 前两项都直接来自外部审查报告的 P1 条目。全量 **20 套验证工程 0 失败**（1254 项）。

---

## 0. 一句话

「哪些动作有副作用」这条知识从前写了**三份且互不同步**（还含一枚从未注册的幽灵工具名），
现在**工具自己说**（`IToolWithRisk`，没声明一律按最保守档）；路由规则指错端点不再把整轮打挂；
长上下文判据从字符数换成 token 估算；`rebuild`（checkpoint 当种子开新窗口）落地。

---

## 1. 审批按「工具自报的风险」判定（审查 P1-1）

### 1.1 问题

同一个「哪些工具是只读 / 安全」的知识，被写在**三个地方**且内容不一致：

| 位置 | 内容 | 症状 |
|---|---|---|
| `DefaultApprovalPolicy.Decide`（Ask 档） | 只放行 5 个名字 | `read_lines` / `grep_files` / `find_files` / `read_image` 在**默认档会弹卡**，在 Build 档却被当只读放行 —— **同一动作两档判成两种性质** |
| `ApprovalTiers.BuildDecide`（Build 档） | 一长串名字 + 区内写 + 危险命令 | 名单与上面那份不同步 |
| `ExternalDirectoryGuard.StateChangingTools` | 6 个「有副作用」名字 | 两份名单**都含 `copy_path`**，而该工具**从未被注册** —— 幽灵工具名，一旦有人真加了同名工具，其审批分类会「意外正确」，掩盖这张表靠人肉维护的事实 |

根因：审批要回答的唯一问题是「这个动作会不会改变什么」，而这**只有工具自己知道**。

### 1.2 做法

- 契约新增 **`ToolRisk`**（`ReadOnly` / `Write` / `Destructive` / `Execute`）+ **`IToolWithRisk`**
  （「可加不可改」的第四次实践：不实现它的老工具照样工作，只是按最保守的 `Execute` 算）。
  对齐的业界做法：MCP 的 tool annotations（`readOnlyHint` / `destructiveHint`）、Claude Code 的工具分类。
- `ToolPreExecuteEvent` 增 `Risk`（默认 `Execute`）——**两个构造点都填**：
  主循环 `AgentRunner`（从工具列表查）与 `PluginHost.InvokeToolAsync`（插件**借调**工具那条路）。
  后者此前构造事件时不带任何风险信息，于是插件借调的工具在审批链里只能落最保守档。
- 判定改为只读一个来源：
  - **Ask 档**：`ReadOnly` → 放行，其余 → 问；
  - **Build 档**：`ReadOnly` → 放行；`Write` → 无路径参数（内部落点）或区内 → 放行、区外 → 问；
    `Destructive` → 问（删除风险高，常规放行档也问）；`Execute` → 能看命令内容的按内容判危险，判不了内容的保持问。
- **37 个内置工具**逐个声明风险（`read_file`/`list_dir`/`grep_files`/`read_lines`/`read_image`/`web_search`/
  `web_fetch`/`ask_user`/`recall_memory`/`search_history`/`tool_catalog`/`csv_to_json`/`toolsets`/`skill_validate`/
  `plugin_list` = 只读；`write_file`/`edit_file`/`make_dir`/`move_path`/`update_plan`/`update_notes`/
  `remember`/`forget`/`use_skill`/`use_toolset`/`skill_*`/`plugin_write` = 写；
  `delete_path`/`plugin_uninstall` = 破坏性；`run_command`/`shell`/`job`/`spawn_subagent`/`plugin_reload` = 执行）。
  扫描类工具（Computer Use）由基类给默认执行档，只读的（`screenshot`/`screen_size`/`list_windows`/`wait`）覆写。
- **MCP 工具**：从 server 的 `annotations` 映射（`destructiveHint` → 破坏性、`readOnlyHint` → 只读），没标就是执行档。

### 1.3 行为变化（如实记账）

- 默认（Ask）档：`read_lines` / `grep_files` / `find_files` / `read_image` / `recall_memory` /
  `search_history` / `tool_catalog` / `csv_to_json` / `toolsets` / `skill_validate` / `plugin_list` **不再弹卡**（它们是只读）。
- 幽灵工具名 `copy_path` 随名单一起消失。
- Build 档：`spawn_subagent` 从「静默放行」变为**要问**（它是执行类且没有可判的命令内容 —— 收紧，安全方向）。

---

## 2. 路由不再把整轮打挂（审查 P1-7）

- 原状：规则把请求指向一个未注册端点、且既没设 `FallbackTarget` 又有多个目标时，
  直接抛 `InvalidOperationException` —— **配置少写一个端点 = agent 完全不能用**。
  而路由规则的定位本就是**偏好**，不是前提。
- 改法：回落顺序 **显式回落目标 → 默认目标（新增，通常是当前选中端点）→ 唯一目标 → 按名字排序的第一个**；
  **顺序确定**（不再依赖字典枚举顺序），并如实记进路由历史（`Source=fallback`，诊断看得见）。
  只有「一个目标都没有」才抛 —— 那时确实是配置错了。
- 判据：长上下文阈值的口径从**消息字符数**换成 **token 估算**（宿主注入 `Data.TokenEstimator`；
  Llm 层自带近似兜底，避免反向依赖 Data）。配置项 `LongContextChars`(6000) → `LongContextTokens`(2000)。
  理由：一个汉字 ≈ 1 token，而 4 个 ASCII 字符才 ≈ 1 token，字符数口径在中英混排 / 代码场景下偏差显著。

---

## 3. 重建 rebuild：checkpoint 当种子开新窗口（v3.12 的账）

- `AgentHost.RebuildSession(sourceSessionId, checkpointSeq?)`：
  从**最新（或指定）checkpoint** 起一个新会话 —— **只带状态、不带史**（header 记血缘 + 一条开场状态）。
- 与 `ForkSession` 的分工：分叉复制**历史前缀**（「换个决定重试」）；
  重建**不带历史**（「上下文满了但状态很清楚」时轻装上阵）。
- 落点：界面会话菜单「**重建**」按钮 + `POST /api/sessions/rebuild`。
- **验证抓到的一个真缺陷**：起初用 `ContextCompactedEvent`（摘要）承载种子 —— 但摘要事件是靠
  **「替换被折叠的旧轮次」**生效的，而新窗口**根本没有旧轮可替换**，投影出来是空的。
  改为落一条**开场助手消息**（`AssistantMessageEvent`），模型第一轮就带着状态。
  这也说明了「同一份文本，在哪条通道上承载」是有语义的。

---

## 4. 未做（如实记账）

- **子 agent 管控面**：并行派发 / 发消息 / 打断（仍为同步一次性）。
- **MCP prompts / sampling**、HTTP/SSE 传输。
- **长尾集成**：定时调度 / Webhook / ACP / Claude Code·Codex Hooks 兼容。
- **Tool Search**（MCP 工具已落 `mcp:<server>` 延迟包，按需拉 schema 未做）。
- 其余见 README「仍未完成」。

---

## 5. 验证与回归

- 全量构建：**0 错误**。
- 全量验证：**20 套 / 1254 项 / 0 失败**（本版新增 13 项）。
  - VerifyHost 104 → **107**（工具自报风险：只读放行 / 执行被拦 / 未声明按最保守）
  - VerifyAgent 60 → **65**（规则指错不抛 + 回落顺序确定 + 记 `fallback`；token 判据（中文 300 字 vs ASCII 400 字符））
  - VerifyCheckpoint 49 → **54**（重建：只带 header + 种子、血缘、投影可见）
  - 既有审批用例随之改为**显式声明风险**（测试不再依赖「按名字判」）
- 宿主冒烟：`POST /api/sessions/rebuild` 在源会话无 checkpoint 时**如实报错**（不静默造空窗口）。
