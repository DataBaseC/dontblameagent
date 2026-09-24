# v3.9 · 命令沙箱（编程必备）

## 起因

`run_command` 是最危险的工具，此前只有两道防线：**审批**（放不放行）与**超时**。
「放行了之后」能造成什么破坏没人管 —— 有三条现实路径：

1. **临时目录**：命令随手往系统 temp 写东西，那是绕过「写只能在工作区内」最方便的一条暗道；
2. **孤儿进程**：超时只杀了直接子进程，它起的子进程还在后台跑（旧代码 `Kill(entireProcessTree)`
   在 Unix 上依赖 `/proc` 遍历，孙辈一旦被 `init` 收养就抓不到）；
3. **资源失控**：一条构建命令的日志能吃掉几百 MB 内存；跑疯的进程把整机拖死。

## 方案：三档可替换的沙箱后端

参照 dsh 的做法 —— **sandbox 本身是插件**：宿主给契约与内置实现，插件可以换。

| 档位 | 管住什么 | 平台 |
|------|---------|------|
| `off` | 不管。保留它是为了排查问题时能一键退回「没有沙箱的世界」 | 全平台 |
| **`process`**（非 Windows 上 auto 的落点） | 工作目录钉在工作区；临时目录重定向进工作区；超时/取消连子孙一起终结；输出过程中封顶 | 全平台 |
| **`job`**（Windows 上 auto 的落点） | 在 process 之上加内核配额：内存上限、CPU 时间上限、活动进程数上限；宿主退出时连同子孙一并终结 | Windows |

选 Job Object 而非容器的理由：它是系统自带、零依赖的进程配额机制，而 agent 跑命令真正要的
就是这几条。容器（要装运行环境）、低完整性级别（会把普通程序跑坏）、防火墙（要管理员）
在这里都是代价大于收益 —— 需要更强隔离时，那是**插件**该干的事。

## 落点

| 文件 | 说明 |
|------|------|
| `src/AgentFramework.Contracts/Sandbox.cs`（新） | 契约：`SandboxMode` / `SandboxLimits` / `SandboxRequest` / `SandboxOutcome` / `ISandboxBackend` / `ISandboxRegistry` |
| `src/AgentFramework.Sandbox/`（新工程） | `ProcessRunner`（两个后端共用的收尾）/ `SandboxBackends`（off + process）/ `NativeJob` + `WindowsJobSandboxBackend`（Job Object）/ `SandboxRegistry` |
| `src/AgentFramework.Tools/CommandTool.cs` | 不再自己 `Process.Start`，改为 `ISandboxRegistry.Resolve(...)` 后交后端执行；结果里加一行 `sandbox=<档位>` |
| `src/AgentFramework.Tools/ToolkitOptions.cs` | 新增 `SandboxName` / `CommandMaxMemoryBytes` / `CommandMaxProcesses` / `CommandMaxCpuSeconds` |
| `src/AgentFramework.Host/HostOptions.cs` + `AgentConfig.cs` + `Program.cs` | `--sandbox` 参数（命令行 > 环境变量 `AGENT_SANDBOX` > `agent.json`）；`CloneWith` 保留档位 |
| `src/AgentFramework.Host/Hosting/HostBuilder.cs` | 建 `SandboxRegistry`、`Provide<ISandboxRegistry>`（插件可注册新后端）、传给 `RunCommandTool` |
| `src/AgentFramework.Host/AgentHost.cs` + `WebUiServer.cs` | `SandboxInfo` 与 `/api/status` 的 `sandbox` 字段 —— 档位与回落原因在界面上可见 |
| `tests/AgentFramework.VerifySandbox/`（新） | 24 项垂直切片验证 |

## 关键设计

- **临时目录必须重定向**（TMP/TEMP/TMPDIR → `<工作区>/.agent-sandbox/tmp`）。
- **挂载时机**：进作业必须在进程刚起的那一刻（`OnStarted`）——
  晚一步命令已经起好自己的子进程，那些就漏在作业之外，「限制住了」只是错觉。
- **降级必须可见**：建作业失败（嵌套作业、权限受限）自动退化为进程护栏并写入结果 Notes；
  档位名不认识/平台不可用则回落并把原因放进 `ResolveNote`，界面与工具结果都带上。
- **收尾只有一份实现**（`ProcessRunner`）：护栏不同、收尾必须相同，否则迟早出现
  「job 档位少收一半输出」这种只在某些机器上复现的毛病。
- **终结两层**：先让后端动手（`TerminateJobObject`），再用 `Kill(entireProcessTree)` 兜底。
- **off 档不重定向临时目录** —— 差异是真的，验证里专门拿它做对照。

## 验证（`tests/AgentFramework.VerifySandbox`，24 项 0 失败 / 2 项按平台跳过）

档位解析（内置名单、auto 落点、未知名回落 + 原因可读）· 命令跑通与退出码传递 ·
工作目录钉在工作区 · **临时目录重定向** · 输出过程中封顶 · **超时连子孙一起终结**
（三步采样：起之前 → 跑起来时 → 终结之后，先证明命令真起了子进程） · 取消与超时分开报 ·
stderr/stdout 分收 · off 档对照 · 后端注册/撤销/重名拒绝 · `run_command` 端到端走沙箱且标出档位 ·
宿主层回落说明可见。

全量回归：**15 个验证工程 845 项 0 失败**。

## 本轮未做（记在账上）

- **容器后端插件**（Docker/Podman）：机制已就位（`ISandboxRegistry`），缺的是那个后端。
- **网络隔离**：需要防火墙规则或 AppContainer，成本与副作用都高，暂不做。
- **低完整性级别（写权限 ACL）**：能把写真正限制在授权目录，但会把普通程序跑坏，作为可选档位留待评估。
