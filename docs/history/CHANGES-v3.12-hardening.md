# CHANGES-v3.12 — 安全与正确性加固（外部代码审查）

> 对照「架构 / 安全 / 数据 / 代码质量 / 文档」五维审查结论，清理全部 Critical 与 P0，
> 并把 P1 中可独立落地的并发、泄漏、协议非法序列一并修掉。
> 验证口径：`verify-all.cmd` 全绿（套件数与条数以实际输出为准）。

## 一、安全（Critical / High）

| 问题 | 落点 | 修法 |
|------|------|------|
| **插件 id 路径穿越**（`IsValidId` 允许 `.` / `..`，可递归删仓库父目录） | `PluginStore` | 白名单拒纯点段；`DirectoryFor` / `BackupDirFor` 做 `GetFullPath` + 前缀校验 |
| **命令环境泄漏 API key** | `ProcessRunner` | 环境整表清空后按白名单放行（PATH / SystemRoot 等），密钥不进子进程 |
| **插件清单 `script` / `assembly` 路径穿越** | `PluginHost` / `ScriptPlugin` | 必须是插件目录内相对路径，拒 `..`、绝对路径、越界解析 |
| **SSRF DNS rebinding TOCTOU** | `WebTools` | `SocketsHttpHandler.ConnectCallback` **连接时**校验目标 IP；IPv4-mapped 先归一；拒 `::` / 未指定地址 |
| **写边界 TOCTOU**（校验与落盘之间换 junction） | `FileTools` | 落盘用 RealPath 穿透后的最终路径；Windows 忽略大小写、敏感 FS 用 Ordinal |
| **pre-execute 钩子 fail-open** | `ScriptPlugin` | 钩子抛异常 = 拒绝（与「审批默认拒」同纪律） |

**文档如实降级**：「写圈死」只覆盖**文件工具 / 插件写**；`run_command` 可写全盘，靠审批 + 沙箱（cwd + TMP 重定向）。

## 二、正确性 / 数据（P0）

| 问题 | 落点 | 修法 |
|------|------|------|
| **中段坏行截掉后续真相源** | `JsonlEventLog.ScanAndRepair` | 只截「尾部垃圾」；中段损坏后的好行保留 |
| **Read 遇坏行停读**（违背铁律 7「坏行跳过」） | `JsonlEventLog.Read` | 改为跳过坏行继续读 |
| **工具异常漏 `ToolCallCompletedEvent`**（悬空 tool_call → 下轮 400） | `AgentRunner` | `InvokeAsync` 包 try/catch，Requested 必有 Completed |
| **max-steps 丢末步正文** | `AgentRunner` | 返回 `lastAssistantText` |
| **记忆合并非原子**（先 retract 再 assert，崩溃即丢） | `JsonlMemoryStore` | 单条 `MemoryBatch` 行 + `flushToDisk` |
| **记忆读共享模式过严** | `JsonlMemoryStore` | 与事件日志同款宽容共享 |
| **分叉 Seq 交叉引用失真** | `SessionForker` | oldSeq→newSeq 重写 `MaskedSeqs` / `FromSeq` / `ToSeq` |
| **孤儿 tool 结果产出非法 `role=tool`** | `SessionContextBuilder` | 丢弃无 requested 的 completed |
| **SQLite 连接失败泄漏** | `SqliteSessionIndex` | catch 里 `DisposeAsync` |
| **HttpClient 污染共享实例 / 不释放** | `OpenAiCompatibleClient` | Bearer 挂单次请求；自建实例实现 `IDisposable` |

## 三、内核 / 并发（P1）

| 问题 | 落点 | 修法 |
|------|------|------|
| **ScriptPlugin 同步桥跨线程死锁** | `ScriptPlugin` | 去掉 `Task.Run`，同线程 GetResult（Monitor 可重入）；保留超时 + 取消令牌 |
| **PluginHandle 并发 Dispose 双重撤销** | `PluginHandle` | `Interlocked.Exchange` |
| **`Registries.Changed` 裸调** | `Registries` | 逐订阅者 try/catch 隔离 |
| **`DescribeToolset` 半更新** | `Registries` | copy-on-write |
| **ALC 共享面过窄**（`ILogger` 类型同一性） | `PluginLoadContext` | Logging* / Text.Json / Runtime 走 default ALC |
| **`static AsyncLocal` 串 Host** | `HostModule` | 实例字段 |
| **`_approvals` 无锁 List** | `AgentHost` | `ConcurrentQueue` |
| **`.Result` sync-over-async** | `WebUiServer.Routes.*` | async 端点 |
| **工具包 `Enabled` 与可见面不一致** | `AgentHost` | 与 `VisibleToolsCore` 同谓词 |
| **自定义路由丢 `CancellationToken`** | `WebUiServer` | 传请求 ct |
| **索引重建用 Count 判落后**（空文本事件永远对不齐） | `HostBuilder` + `ISessionIndex` | seq 水位 `GetIndexedWatermarkAsync` |
| **plan id 同秒碰撞** | `PlanTools` | 随机后缀 |
| **CommandTool 魔法子串筛降级备注** | `CommandTool` | 稳定前缀 `sandbox-degrade:` / `sandbox-start-fail:` |
| **Tools→Sandbox 死引用** | `AgentFramework.Tools.csproj` | 删除 |

## 四、文档口径

- 检查总数：不写死 907，**以 `verify-all.cmd` 输出为准**
- 官方工具 **20**（含 `search_history`；Index 不可用时 19）
- 事件类型 **16** 种 `JsonDerivedType`（含 `checkpoint`）
- 路线图统一：v3.11 剩余 → v3.12 rebuild → v3.13 Dream → v3.14 MCP → v3.15 Goal
- 铁律 13 收窄为「文件工具 / 插件写圈死」
- 铁律 56：「结构化提取交独立 writer；主 agent 仍可显式 remember」
- 模式三档：Work / Chat / Design
- `core` 不含 `run_command`（属 `exec`，可关）
- DESIGN.md 死链改为「以本 README 为准」

## 五、刻意未做（后续）

1. Host 上帝化拆分（WebUiPage 外置、AgentHost 瘦身）——结构演进，单独一刀
2. `run_command` 文件系统级隔离（AppContainer / 低完整性）——插件位
3. 真实模型联调、`LlmCheckpointWriter`、rebuild —— 路线图能力
