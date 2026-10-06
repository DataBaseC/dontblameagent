# v3.20 —— 容器沙箱后端 + 完整 bash 工具

> 本批两件事：**沙箱补上容器档**（`ISandboxBackend` 缝再落一子），**`shell` 补成完整 bash 工具**（对齐 Claude Code Bash 的可用面）。

## 一、容器沙箱后端（`container`）

### 为什么

`process` 只是进程护栏（非隔离）、`bwrap` 借宿主根且默认不禁网。要**独立 rootfs + 可完全隔绝网络 + 资源配额**，
唯一的路是容器。dsh 把 sandbox 做成可替换 provider（bwrap / Landlock / E2B / Docker 皆是后端）——
我们的 `ISandboxBackend` 缝照此而来，于是**加容器档不改一行消费方**。

### 落地

- 新文件 `src/AgentFramework.Sandbox/ContainerSandboxBackend.cs`：
  - 探测 `docker` → `podman`（显式 `sandboxRuntime` 优先；找不到 = 本档不注册）。
  - 命令形状：`docker run --rm -i --name <自生成> --network <none|bridge|host> -v <工作区>:<工作区> -w <工作区>
    [--read-only --tmpfs /tmp] [--memory <上限>] [--user uid:gid] <镜像> /bin/sh -c <命令>`。
  - **默认禁网**（`--network none`）——容器最该管的恰恰是网络；装依赖加 `--sandbox-network bridge`。
  - **根只读 + `/tmp` 可写**：命令只能写工作区与临时区。
  - **用户映射**（Linux）：不映射则容器内 root 写出的文件属主变 root，宿主用户反而改不动自己项目。
  - 超时/取消时按容器名 `docker rm -f` 强制清理，不留孤儿容器。
  - 执行收尾（环境白名单 / 输出封顶 / 超时终结）仍走 `ProcessRunner` —— 与另几档同源。
- `ProcessRunner.RunOptions` 增 `ExtraEnvironment`（容器运行时找 socket 需要的 `DOCKER_HOST` 等；白名单不含的少数变量，由后端负责不含密钥）。
- `ISandboxBackend` 增 `UnavailableReason`（**接口默认成员**，纯附加诊断，老后端与外部插件零改动）——
  回落文案因此能说清「是平台不对，还是镜像没配」。
- `SandboxRegistry` 增容器配置入参；**`container` 刻意不进 `auto`**：容器要镜像，而镜像没有普适默认。

### 配置面

`--sandbox-image` / `--sandbox-network` / `--sandbox-runtime`（或 `AGENT_SANDBOX_IMAGE` 等 / `agent.json` 的
`sandboxImage` / `sandboxNetwork` / `sandboxRuntime`）。网络名认不出来时提示并按 `none` 处理。

## 二、完整 bash 工具（`shell` 增强）

对齐 Claude Code Bash 工具的三个可用面：

| 参数 | 行为 |
|---|---|
| `description` | 一句话说明命令在干什么，进报告，便于审计 |
| `timeout` | 覆盖默认超时；**夹在运维硬上限**（`CommandMaxTimeoutSeconds`，默认 600s）内 —— 模型越不过上限 |
| `run_in_background` | 扔后台，立即返回作业 id；**与 `job` 工具共用同一份作业池**（两个入口、一张表） |

另：`PersistentShell` 的哨兵行增带 `$PWD`，报告多一行 `cwd=` —— `cd` 的效果一眼可见，不必再跑 `pwd` 确认。
`JobManager` 上收为宿主装配级共享实例（`JobTool` 与 `ShellSessionTool` 同用一份），生命周期挂装配作用域。

## 三、验证

- `tests/AgentFramework.VerifySandbox`：新增 4b 容器段（**参数拼装纯函数单测**：禁网 / 只读根 / 只挂工作区 /
  内存 / 记名 / 镜像与 shell 位置；档不可用原因可读；注册表按运行时进出名单；**真跑需 `AF_TEST_CONTAINER_IMAGE`**，缺则 SKIP）。
  容器命令拼装抽成 `BuildWrapper` 纯函数，正是为了脱离 docker 也能断言这些安全属性。
- `tests/AgentFramework.VerifyTools`：新增 2b 段（`shell` 跑通并回报 `cwd` / `description` 进报告 /
  `timeout` 夹取 / `run_in_background` 返回作业 id / **shell 起的后台在 `job` 里可见**）。
- 全量回归：19 套验证工程 0 失败。

## 四、边界（如实说明）

- **本沙盒无 docker / podman**，容器档的**真跑**未在此环境验证（只验了参数拼装与档位解析）——
  真跑留 `AF_TEST_CONTAINER_IMAGE=<镜像> dotnet <VerifySandbox.dll>` 给有运行时的机器。
- 容器档不隔离宿主 Docker 守护进程本身（不挂 `docker.sock`）；`--user` 映射仅 Linux。
