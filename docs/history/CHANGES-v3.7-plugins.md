# v3.7 —— 插件生态与自我升级

> 叠在 v3.6（四项修复 + 文件工具边界调整）之上。
> **不需要重新编译插件、不需要重启宿主** —— agent 自己就能给自己加能力。

## 一句话

给 agent 开了「写插件」的手：用 JS 写 → 热装载 → 下一轮就能用；写坏了自动回滚到上一可用版本。

## 两条通道，各管一段

| 通道 | 谁写 | 形态 | 怎么生效 |
|------|------|------|---------|
| **A · 程序集插件** | 人（官方 / 基石插件） | C#/`IPlugin`，要 `dotnet build` | 启动器勾选、启动时装配 |
| **B · 脚本插件** ★新 | **agent 自己** | **JS（内嵌 Jint）** | **`plugin_write` 写盘 + `plugin_reload` 热装载** |

> 为什么脚本用 JS：给 agent 编译 C# 要背上 Roslyn（包体积 / 复杂度都上一个台阶）；JS 引擎纯托管、随包分发无原生依赖，模型对 JS 最熟，而工具参数本来就是 JSON。

## agent 手上多了四个工具

| 工具 | 干什么 |
|------|--------|
| `plugin_write` | 把 `{plugin.json, main.js}` 写进统一仓库 `<workspace>/plugins/<id>/`（暂存目录 + 整体换上去，不写半截） |
| `plugin_reload` | 装载（先卸旧、再装新）；**卸载失败不装新版**，失败就回滚 |
| `plugin_uninstall` | 连目录带装载一起摘掉 |
| `plugin_list` | 看装了什么、各贡献了哪些工具、来源是仓库还是启动器预设 |

## 关键设计

- **脚本里没有任何 CLR 对象**：碰不到文件系统、网络、进程。要借调别的工具，必须先在 `plugin.json` 的 `capabilities` 里声明（`tool:read_file` / `tool:*`），**没声明就直接拒**。
- **装载即自测**：脚本可定义 `selftest()`，装载时自动跑；抛异常或返回 `false` = 拒装。否则 agent 会"写完就报成功，下一轮才发现是坏的"。
- **自动回滚到「上次装载成功」的那一版**（不是"写盘前的文件"—— 坏版本早就把磁盘覆盖了）。这是设计上踩过的坑。
- **装完立刻可见**：工具表本来就每轮现取（`PluginHost.GetTools()` 快照），热更新的地基早就在。
- **钩子做确定性保证**：`ctx.on('tools/pre-execute', fn)` 返回 `false` 即拦下这次工具调用 —— 对标 Claude Code 的 Hooks（不靠模型自觉，靠钩子兜底）。
- **重启不丢**：宿主启动时自动装载统一仓库里的插件，不受启动器 profile 过滤。

## 新增 / 改动文件

```text
新增  src/AgentFramework.Kernel/ScriptPlugin.cs          脚本插件适配器（核心）
新增  src/AgentFramework.Host/PluginStore.cs             统一仓库：快照 / last-good 回滚
新增  src/AgentFramework.Host/PluginTools.cs             agent 的四个自管理工具
新增  docs/PLUGIN-SDK.md                                 面向 agent 的 API 参考 + 踩坑清单
新增  samples/script-plugins/writing-kit/                示例插件（3 个纯计算工具 + selftest）
新增  tests/AgentFramework.VerifyPlugins/                20 项验收
改动  src/AgentFramework.Kernel/{PluginHandle,PluginHost}.cs   脚本分支、ALC 可空、运行期装卸
改动  src/AgentFramework.Contracts/Events.cs             清单加 script / description / capabilities
改动  src/AgentFramework.Host/Hosting/{HostBuilder,HostModule}.cs  建仓库、注册工具、装载自写仓库
改动  src/AgentFramework.Host/{HostOptions,AgentHost}.cs
改动  README.md / verify-all.cmd
```

## 验证

```text
13 个验证工程，780 项，0 失败（都不需要 API key、不联网）
其中 tests/AgentFramework.VerifyPlugins 20 项 —— 含「坏版本被拒 → 自动回滚到上一可用版并重新装载」
```

示例插件也过了真机验证：起宿主、把 `writing-kit` 放进仓库，`/api/tools` 里能看到
`word_count` / `paragraph_report` / `outline`，来源标 `writing-kit`，启动日志打印 `插件：writing-kit@1.0.0`。

## 已知边界（不假装做完）

| 项 | 状态 |
|----|------|
| 三个官方基石插件（基础 UI 美化控制台 · 编程扩展工具包 · 写作扩展工具包） | 机制就绪（`SamplePlugin` 即模板），**内容待补** |
| "改主界面本身"的 UI 片段注入扩展点 | 未做 —— 现在只有 iframe 面板；这是「UI 美化控制台」的前置 |
| MCP 子进程通道（接外部 server） | 未做 —— 按决策直接兼容 MCP 协议，不自造 RPC |
| 运行期连续失败 N 次自动停用 | 未做（装载失败的回滚已做；运行期脚本抛异常已隔离成工具 Fail） |
| 顺手发现（既有代码路径，未修） | 会话日志读到 I/O 错误会让**整个宿主崩掉**：`agent-sessions/default.jsonl` 是 0 字节且文件系统读返回 EIO 时复现，`JsonlEventLog.Read` 只兜了格式错、没兜 I/O 错 |
