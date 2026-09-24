# v3.10 · 工具包与暴露面（装得多 ≠ 负担重）

## 起因

工具定义是**每轮都在交的税**。这次先算了账：

| 项 | 数值 |
|---|---|
| 已注册工具 | 31 个（官方 19 + 基石 12） |
| Description + Schema | 2,957 + 7,066 字符 |
| 每轮固定开销 | **≈ 10k 字符 ≈ 3k token** |

两条行业实测线（Anthropic 口径）：**30–50 个工具是选择准确率拐点**（我们已在线上）；
多服务器 MCP 配置（GitHub / Slack / Sentry / Grafana / Splunk）**光工具定义 55k token**。

调研到的共同解法只有一句：**默认少给，按需加载** ——
Anthropic 的 Tool Search（`defer_loading`，定义开销减 85%+）、Claude Code（MCP 工具只载名字）、
Copilot CLI（<30 个全量、超过则检索）、Cursor（工具索引落文件，MCP 任务 token −46.9%）。

**病根不在「插件太多」，而在装载与暴露被绑成了一个开关。** 本次先拆开。

## 做了什么（三层里的第一层）

```
装载（插件照常装）  ←→  暴露（这一轮给模型看什么）     本次：拆开
   ├─ 工具包（Toolset）         ← 本次
   ├─ 常驻 / 延迟（deferred）    ← 只留字段，供 MCP 与 Tool Search 用
   └─ Tool Search               ← 下一步
```

### 工具包

工具按**能力的用途**（不是代码来源）分组，会话里按包开合：

| 包 | 工具 | 可否关闭 |
|---|---|---|
| `core` | read_file · write_file · list_dir · ask_user | **不可关** |
| `meta` | toolsets · use_toolset | **不可关** |
| `exec` | run_command | 可关（「这次不许跑命令」有出口了） |
| `memory` / `search` / `plan` / `web` / `self` | 记忆 / 历史检索 / 计划派活 / 联网 / 自我升级 | 可关 |
| `devkit` / `writing-kit` | 基石插件的包（名字来自插件清单） | 可关 |
| `mcp:<server>` | 下一步 | 可关 |

### 三个开关入口（同一份视图，永远一致）

- **人**：界面顶栏「包」→ 一排开关；
- **agent**：`toolsets`（看）/ `use_toolset`（开合）—— 谁最清楚这段活要什么？它自己；
- **配置**：`--disable-toolsets a,b` / `AGENT_*` / `agent.json` 的 `disabledToolsets`。

## 落点

| 文件 | 说明 |
|------|------|
| `src/AgentFramework.Contracts/Toolsets.cs`（新） | `IToolWithToolset` / `ToolsetDescriptor` / `ToolsetDeclaration` / `BuiltinToolsets`（含保留名单） |
| `src/AgentFramework.Contracts/Abstractions.cs` | `IPluginContext.RegisterTool(tool, toolset)`（默认实现，兼容老内核） |
| `src/AgentFramework.Contracts/Memory.cs` | `ModeProfile.AllowedToolsets`（与 `AllowedTools` 取交集，兼容老语义） |
| `src/AgentFramework.Contracts/Events.cs` | 清单新增 `toolsets` 声明（显示名 / 说明 / 是否常驻） |
| `src/AgentFramework.Kernel/Registries.cs` | 工具注册表记录包 + 按包查询 + 包描述登记 |
| `src/AgentFramework.Kernel/PluginHost.cs` | 包查询转发 + 装载时把清单的名字与说明登记到插件包上 |
| `src/AgentFramework.Kernel/PluginScope.cs` · `ScriptPlugin.cs` | 注册工具时可指定包；脚本 `registerTool({ toolset })` |
| `src/AgentFramework.Host/Hosting/HostModule.cs` | `HostState.DisabledToolsets` + 可见面计算加两道闸门（基础包 ∩ 启用包） |
| `src/AgentFramework.Host/Hosting/HostBuilder.cs` | 官方工具的包归属表 + 内置包描述 + 启动时应用 `DisabledToolsets` |
| `src/AgentFramework.Host/ToolsetTools.cs`（新） | `toolsets` / `use_toolset` 两个工具（meta 包，不可关） |
| `src/AgentFramework.Host/AgentHost.cs` | `Toolsets` / `SetToolsetEnabled` / `DisabledToolsets`；**顺手修掉可见面双实现** |
| `src/AgentFramework.Host/WebUiServer.Routes.Chat.cs` · `WebUiPage.cs` | `/api/toolsets` + `/api/toolsets/toggle`；顶栏「包」与开关面板 |
| `tests/AgentFramework.VerifyToolsets/`（新） | 31 项垂直切片验证 |

## 顺手修掉的一个真问题

`AgentHost.ExposedToolNames` 原先**自己算了一套可见面**（只看 `AllowedTools`），
与主循环用的 `HostState.VisibleToolsCore` 是两份实现 —— 加了工具包闸门之后立刻暴露：
主循环收窄了，诊断面还在说「32 个全都暴露」，验证当场抓住。

现在两处都问同一个 `VisibleTools()`：**可见面只算一次**（已写进铁律）。

## 兼容性

- 默认不关任何包 → 现有验证工程全绿不动；
- 现存插件清单不写 `toolsets` 也能工作（包 id = 插件 id）；
- `ModeProfile.AllowedTools`（逐工具名白名单）原样保留，与包取交集。

## 验证（`tests/AgentFramework.VerifyToolsets`，31 项 0 失败）

包归属（13 个工具逐个核对）· 插件包显示名来自清单 · 默认全开（32/32）·
关 web / exec / writing-kit 后可见面精确变化且互不影响（32 → 24）· 工具仍注册着（再开即用）·
保留包拒关 · 再打开恢复 · agent 工具 `toolsets` / `use_toolset` 路径 · 关保留包被拒且理由清楚 ·
未知包被拒并列出可选 · 配置层面启动即收起（且写保留包不生效）·
HTTP：`GET /api/toolsets` · `POST toggle` 生效 · 保留包 400 · 未知包 404。

**全量回归：16 个验证工程 876 项 0 失败。**

## 下一步（已定型）

1. **MCP 通道**：每个 server 一个 `mcp:<server>` 包，**默认延迟**（不进 schema），
   server 进程首次用到才启动 —— 接 10 个 server 的初始开销 ≈ 0；
2. **Tool Search**：阈值（>30 个工具）触发，把延迟工具的完整 schema 按需拉进上下文。

计划书：`docs/PLAN-toolset-exposure.md`。
