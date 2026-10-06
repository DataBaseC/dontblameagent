# v3.26 · Tool Search（工具级按需检索）

> 主题：兑现 [`PLAN-toolset-exposure.md`](../PLAN-toolset-exposure.md) 的第四步 ——
> 模型的工具表不该在开跑前就塞满。
> Anthropic / Claude Code / Copilot CLI 三家都指向同一机制：**延迟定义 + 按需检索**。
> 全量 **20 套验证工程 / 1295 项 / 0 失败**。

---

## 一、问题（先算账）

| 项 | 数 |
|---|---|
| 已注册工具（本机基线） | 40 个 |
| 每轮固定开销 | ≈ 10k 字符 ≈ 3k token |
| Anthropic 实测 | 50 个工具吃 **10–20k token**；**30–50 个是选择准确率拐点** |
| 多 MCP server（GitHub / Slack / Sentry / Grafana / Splunk） | 光工具定义 **55k token**，还没开始干活 |

dba 已经做了一半：`Eager=false` 的包（如 `mcp:<server>`）**默认不进 schema**（v3.10）。
缺的是另一半 —— 没有「拉起来」的**工具级**路径，只能整包 `use_toolset`；
而一整包 30 个工具里往往只用得上 2 个。

## 二、借鉴与取舍

| 来源 | 做法 | 我们借了什么 / 偏离了什么 |
|---|---|---|
| **Anthropic Tool Search Tool** | `defer_loading: true` 的工具不进初始上下文；模型调 `tool_search_tool_regex` / `_bm25` → 返回 reference → 由平台自动展开成完整定义。**不破坏前缀缓存**（延迟工具根本不在初始 prompt 里） | 借：延迟工具**连名字都不给**（省到极致）、命中后把**完整定义**交回模型、缓存友好。偏离：我们自己实现「展开」—— 命中后下一轮把 schema 加进工具表（平台侧没有 `defer_loading` 可用） |
| **Claude Code `ToolSearchTool`** | 工具数超阈值才启用；规则：显式标记常驻的不延迟、**MCP 工具总是延迟**、**搜索工具自己不延迟** | 借：`tool_search` 必须常驻（否则死锁）。偏离：判据从「数量阈值」改成「**存在延迟包**」—— dba 的延迟是**显式声明**的（`Eager=false`），比按数量猜更本质 |
| **Copilot CLI `toolSearch`** | <30 个全量加载；用时检索并**保留** | 借：拉起是**粘性**的（本会话保持）；并给 `reset` 手动卸下 |

## 三、做了什么

### 3.1 会话级「已拉起」集合

- `HostState.ActivatedToolsBySession`：会话 → 工具名集合。运行期状态，**不进事件流**（与 `JobManager` 同一取舍）。
- `TurnActivatedTools`（AsyncLocal）：`BeginTurn` 时按 `sessionId` 绑定 —— 与 `TurnSessionId` 同一纪律，**不漏会话、不串会话**。
- `SetToolActivatedForTurn(name, on)`：拉起 / 卸下。只动可见性，不动注册表、不落盘。

### 3.2 可见面：只豁免「包」这一道闸

`VisibleToolsCore` 末尾补一路：

```
可见 = (模式包集 ∩ 包开关 ∩ 模式白名单 ∩ 技能白名单) ∪ 已拉起
                                                       ↑ 仍受前三道约束
```

延迟包正是靠 `DisabledToolsets` 实现的，所以这一路必须放在**包闸门之后**；
但「补一个工具」不该把闲聊模式变成全功能模式 —— 模式包集 / 模式白名单 / 技能白名单**原样照旧**。
合并后按名重排（工具表是请求前缀的一部分，顺序抖动会打掉前缀缓存）。

### 3.3 `tool_search` 工具（`meta` 包，`ReadOnly`）

| action | 行为 |
|---|---|
| `search`（默认） | `query` 空格分词 **AND**（命中 name / description）；命中即**拉起**；返回 **名称 + 包 + 描述 + 参数 schema**；`limit` 默认 5、上限 20 |
| `list` | 列出当前**没加载**的工具（按包分组），附「本会话已加载」 |
| `reset` | 卸下本会话拉起的工具 |

### 3.4 与既有机制的关系（不新开通路）

- **延迟** = 现成的 `Eager=false` 包 + `DisabledToolsets`（装配末自动登记）；
- `tool_catalog` 看包 · `use_toolset` **整包**开 · `tool_search` **单件**拉起。

### 3.5 刻意不做

- **索引段不进上下文**：Anthropic 的延迟工具连名字都不给（靠搜索发现），我们照做 ——
  索引浏览交给 `tool_catalog` / `tool_search list`，不占每轮 token。
- **`tool_search` 恒常驻**（不按数量开关）：它是发现入口，只占一份 ~200 字符 schema；
  没有延迟工具时它零行为差异（`list` 会说「没有延迟的工具」）。

## 四、验证

- 全量构建 **0 错误**；全量 **20 套 / 1295 项 / 0 失败**。
  - VerifyToolsets 38 → **45**（延迟包默认不可见 / `tool_search` 常驻 / 检索结果带完整定义 / 下一轮进表 / `reset` 后退出 / 回合外不外溢）
- 宿主冒烟：`/api/tools` 含 `tool_search`，`/api/toolsets` 正常，日志零异常。

## 五、仍未做

- **MCP prompts / sampling**、HTTP / SSE 传输。
- **长尾集成**：定时调度 / Webhook / ACP / Claude Code·Codex Hooks 兼容。
- **检索升级**：当前是关键词 AND；工具上百且描述相近时，值得上 **BM25 / 向量**（Anthropic 有 bm25 / regex 两档）。
- **子 Agent 会话日志**：`sessions/` 里的 `sub-*.jsonl` 仍不自动清理。
