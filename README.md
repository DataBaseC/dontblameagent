# Agent Framework — 一个能跑的桌面 Agent 框架

一个**自研 Windows 桌面 Agent 应用框架**（C#/.NET），借鉴 DeepSeek Harness 的「一切皆插件」思想从零重写。
架构与铁律以本 README 为准。

> **文档索引**：计划与变更见 [`docs/CHANGELOG.md`](docs/CHANGELOG.md)
> （`docs/PLAN-*` 路线图 · `docs/PLUGIN-SDK.md` 脚本插件 · `docs/history/CHANGES-*` 演进记录）。

不是从底层往上堆，而是先打通**五条最细但端到端的线**，把架构假设全部验证掉，再往上加肉。

## 当前进度：二十二条切片已跑通（总数以 verify-all 输出为准）

| 切片 | 验证内容 | 结果 |
|------|---------|------|
| **① 插件内核** | 加载 → 注册 → 调用 → 审批拦截 → 卸载 → **程序集 GC 回收** | Verify 15 项 ✔ |
| **② 数据平面** | 追加 → 读取 → 投影 → 重启恢复 → 分叉 → **崩溃自修复** | VerifyData 41 项 ✔ |
| **③ Agent 主干** | 调模型 → 工具调用 → 审批 → 执行 → **落日志**（含模型路由） | VerifyAgent 26 项 ✔ |
| **④ 工具集** | 文件 / 命令 / 搜索 / 抓取，含**路径逃逸与 SSRF 防护** | VerifyTools 26 项 ✔ |
| **⑤ 宿主装配** | 五层装成可运行整体 + **重启续接** | VerifyHost 21 项 ✔ |
| **⑥ 启动器** | 像游戏启动器一样**勾选插件** → profile → 按勾选装配 | VerifyLauncher 26 项 ✔ |
| **⑦ 对话界面** | Web UI：**多会话管理** + 流式输出 + 工具卡片 + **交互式审批** | VerifyWeb 41 项 ✔ |
| **⑧ 本地摘要 + 注入防护** | 正文**不出机**、网页内容可信度标注 | VerifySummary 25 项 ✔ |
| **⑨ SQLite 派生索引** | 真相源之外的**可查历史**：增量写入 + 落后自动重建 + 中文检索 + 不可用即降级 | 索引 13 项 ✔ |
| **⑩ 桌面壳** | WebView2 原生窗口装下整个界面（编译验证，Linux 亦可） | 编译 0 错误 ✔ |
| **⑪ 输入转述（澄清模式）** | 便宜模型先把话说清楚 → 再交给主模型；`✨ 优化` 按钮 + 可选自动 + **失败一律回退原文** | VerifyRephrase 96 项 ✔ |
| **⑫ 长任务上下文治理** | **上下文是投影不是日志**：水位 / 大输出引用化 / 旧结果遮蔽 / 任务卡常驻 / 折叠留痕 + `search_history` 捞回 | VerifyContext 91 项 ✔ |
| **⑬ 分级记忆 + 工作模式** | 短期（会话）/ 长期（项目）/ 全局三级记忆按模式加载；**闲聊模式不挂工具** —— 靠「不加载」减少调用 | VerifyMemory 115 项 ✔ |
| **⑭ 可观测性** | 用量账目（含**缓存命中率**、思考 token、首字延迟）+ 思考过程留痕；**端点不回报就记「未知」，不记 0** | 可观测性若干项 ✔ |
| **⑮ 模型管理** | 端点在**界面里配、启动之后改**：探测分类报错 + 密钥 DPAPI 加密 + 换模型不重启 | 模型管理 23 项 ✔ |
| **⑯ 架构优化（地基）** | **宿主自己也是一组插件**：宿主模块走内核 seam（注册即副作用）→ **加功能 = 加模块**；工具注册表（**注册 ≠ 可见**、运行期可挂卸）；交互缝（审批只是它的特例）；会话派生（子 agent 地基）；路由可注册 | 架构 26 项 ✔ |
| **⑰ 安全加固（外部审查）** | SSE **坏帧容错** · **悬空工具调用自愈**（否则会话每轮 400）· 切会话等回合 · Origin 校验 + 会话 id 白名单 · **SSRF 归一化**（`::ffff:` / ULA / CGNAT）· 密钥不出门 · cmd 参数 · 订阅者异常隔离 · SSE 队列化 · 命令输出过程中封顶 | +18 项异常路径 ✔ |
| **⑱ 脚本插件 + 自我升级** | **agent 自己写插件**（JS，免编译）→ 热装载、免重启 → 下一轮工具即可见；坏版本**自动回滚**到上次装载成功的版本；`capabilities` **未声明即拒**；`tools/pre-execute` 脚本钩子做**确定性把关**；装载即自测 | VerifyPlugins 20 项 ✔ |
| **⑲ 基石插件（官方三件套）** | **编程扩展工具包**（`edit_file` 精确替换 / `grep_files` / `find_files` / `read_lines` / 建目录 / 搬移 / 删除：agent 从「整篇重写」升级为「局部改」）· **写作扩展工具包**（字数 / 段落节奏 / 口头禅 / 标点 / 提纲）· **基础 UI 美化控制台**（主题色板 + 阅读模式，**注入主界面**而不只是 iframe 面板）；配套新增 `IWorkspaceService` 工作区 seam 与 `ui.styles/scripts` 界面注入声明 | VerifyBaseKit 41 项 ✔ |
| **⑳ 命令沙箱** | 跑命令真的「关进笼子」：**临时目录重定向进工作区**（堵住绕开写边界的暗道）· **超时/取消连子孙进程一起终结**（不再留孤儿）· Windows 上用 **Job Object** 加内存/CPU/进程数配额 · 档位可替换（**换沙箱 = 加插件**） | VerifySandbox 24 项 ✔ |
| **㉑ 工具包与暴露面** | **装得多 ≠ 负担重**：工具按**包**（core / exec / web / devkit …）分组，会话里按包开合；**装载与暴露解耦** —— 插件照常装着，用不上的包收起来，省每轮的 schema token，也少让模型在几十个工具里挑错；顺手统一了两处各算一套的可见面逻辑 | VerifyToolsets 31 项 ✔ |
| **㉒ 记忆与进化（计划 + 契约）** | 对照 MiMo Code 把差距**从「缺功能」重新定位成「缺时机与角色」**：拆开「**写入**」（旁路落盘、上下文不动 → 可以早）与「**裁剪**」（换投影函数、打断缓存 → 必须晚）两个触发点，压缩线因此一个字不用改；契约先行 —— `CheckpointEvent`（**不进模型上下文**、追加不覆盖、带增量区间）+ `ICheckpointWriter`；**31 项契约桩**先立住 | VerifyCheckpoint 31 项 ✔ |
| **㉓ 记忆降档按「使用频率」判定** | 降档判据从「30 天时间线」改为**调用频率 + 半衰期衰减**：热度 = `(1 + 使用次数) × 0.5^(闲置天数 / 半衰期)`，**越用越新、老而常用永不掉线**；给「本轮检索命中」补上**隐式使用信号**（不再只看显式 `recall_memory`）；预览与执行**共用同一判据**；面板「清扫」改称「巩固」 | VerifyMemory **165 项** ✔ |

> 口径：表中「N 项」= 该切片验证工程的通过条数；**合计以 `verify-all.cmd` 输出为准**（工程有增减时总数会变，不要写死）。

---

# 🚀 快速开始

## 第 0 步：装 .NET 10 SDK

```powershell
winget install Microsoft.DotNet.SDK.10
```

装完**新开一个**终端，`dotnet --version` 应输出 `10.x.x`。

## 第 1 步：跑起来（无需 API key）

```powershell
cd D:\dev\agent-framework

# ① 图形对话界面（推荐，浏览器打开）
dotnet run --project src\AgentFramework.Host -- --web

# ② 命令行交互模式
dotnet run --project src\AgentFramework.Host -- --workspace .\ws --sessions .\sessions

# ③ 一次性模式（便于脚本化）
dotnet run --project src\AgentFramework.Host -- --prompt "你好"

# ④ 原生桌面窗口（推荐给日常使用；Win10/11 自带 WebView2 运行时）
dotnet run --project src\AgentFramework.Desktop

# 也可以让桌面窗口连接一个已经跑着的宿主（启动器就是这么做）：
dotnet run --project src\AgentFramework.Desktop -- --url http://localhost:8090/
```

## 第 2 步：接真实模型

设好环境变量即可（云端与本地**协议相同**，都是 OpenAI 兼容）：

```powershell
# 云端
$env:AGENT_CLOUD_BASEURL = "https://api.deepseek.com/v1"
$env:AGENT_CLOUD_KEY    = "sk-..."
$env:AGENT_CLOUD_MODEL  = "deepseek-chat"

# 本地（LM Studio，在 LM Studio 里开启 Local Server）
$env:AGENT_LOCAL_BASEURL = "http://localhost:1234/v1"
$env:AGENT_LOCAL_MODEL   = "qwen2.5-7b-instruct"
```

再跑同一条命令，就会自动走**混合路由**：复杂任务走云端，长上下文简单任务走本地。

### 更省事的办法：启动之后再配（在界面里）

界面顶部有个 **`模型`** 按钮，点开就能：

- 加端点（填地址 + 密钥）→ 点「测试连接」当场验活（连不上会**分类报错**：钥匙不对 / 地址不对 / 网不通）
- 从拉到的模型列表里挑一个当**当前模型** —— **不需要重启**，下一轮对话就是新模型在答
- 密钥用 Windows DPAPI（当前用户）加密后落盘；**界面不回显密钥**，只告诉你「配没配」

配置写在 `agent.json` 的 `providers` / `active` 两个键里。第一次被界面改动前会自动留一份
`agent.json.bak`；老写法 `cloud` / `local` 会被认成两个端点，不用手工改。

## 🎛 用启动器勾选插件（像游戏启动器选 mod）

```powershell
dotnet run --project src\AgentFramework.Launcher -- --plugins .\plugins
```

浏览器会自动打开勾选页面：勾上要装载的插件 → 「保存并启动」→ **只装配被勾选的插件**。

勾选结果存在 `profiles\default.json`（纯 JSON，人能读能改）。
启动器只读清单、不加载程序集，所以扫描本身零风险；坏插件会在页面上被标出来。

---

## 第 3 步：跑全部验证

**推荐**（一键，配置与 `run.cmd` 一致）：

```bat
verify-all.cmd
```

手工逐个跑时请与脚本同配置（**Debug**）：

```powershell
$p = @("Verify","VerifyData","VerifyAgent","VerifyTools","VerifyHost",
       "VerifyLauncher","VerifyWeb","VerifySummary","VerifyRephrase","VerifyContext",
       "VerifyMemory","VerifyLlm","VerifyPlugins","VerifyBaseKit","VerifySandbox","VerifyToolsets",
       "VerifyCheckpoint")
foreach ($n in $p) {
  dotnet run --project "tests\AgentFramework.$n\AgentFramework.$n.csproj" -c Debug --no-build
}
```

预期合计 **全部 0 失败**（总数以 `verify-all.cmd` / 各工程输出为准，不要写死），退出码 0。**都不需要 API key、不需要联网、不需要真实模型。**
若个别套件偶发「文件被占用 / 找不到插件 DLL」，等 1–2 秒重跑该套件即可（共享插件输出目录的已知竞态）。

## 命令行参数一览

| 参数 | 说明 |
|------|------|
| `--web` | **打开图形对话界面**（浏览器），并启用**交互式审批卡片** |
| `--port <n>` | Web 界面端口（默认 8090） |
| `--no-open` | 本次不自动打开浏览器 |
| `--open` | 本次强制打开浏览器（优先于 `--no-open` 与启动器设置） |
| `--workspace <dir>` | 工作区根目录（**写**的边界；读默认不受限，见铁律 13） |
| `--sessions <dir>` | 会话日志目录（每会话一个 `.jsonl`） |
| `--plugins <dir>` | 插件目录（其下每个子目录一个插件） |
| `--plugins-enabled a,b` | 只装配这些插件（启动器的 profile 会生成它） |
| `--disable-toolsets a,b` | **本次收起的工具包**（逗号分隔）。留空 = 全部开启；`core` / `meta` 是保留包，写了不生效 |
| `--sandbox <档位>` | **命令沙箱**：`auto`（默认）/ `off` / `process` / `job`，或插件注册的后端名。名字不认识或平台不可用会回落并说明原因 |
| `--session <id>` | 会话 ID（同名即续接同一会话） |
| `--prompt "<text>"` | 一次性模式 |
| `--allow-command` | **放开命令审批**（默认需确认；无界面时直接拒绝） |

## 单文件启动器 exe（推荐分发形态）

```bat
pack-launcher.cmd
```

产物：`build\pack\out\AgentFramework.Launcher.exe` —— **就一个文件，约 111 MB**：

- 自带 .NET 运行时（自包含），拷到任何 Windows x64 机器双击即用，目标机**不需要装 .NET、不需要源码**；
- 内嵌本体（Host）的自包含发布包 + **桌面壳窗口** + 示例插件（zip 资源）；
- 首次运行自动解压到 exe 旁的 `host\` 目录（幂等：包哈希没变就跳过；解压带路径穿越防护）；
- 点「启动宿主」直接拉起 `host\AgentFramework.Host.exe`（也是自包含），并**自动弹出原生桌面窗口**（`AgentFramework.Desktop.exe --url …`；找不到桌面壳时才退回浏览器）。
- 启动时**默认自动打开浏览器**显示管理页；不想被多塞一个标签页，就在页面上把「启动时自动打开浏览器」关掉
  （持久化到 `profiles/launcher-settings.json`），或启动时加 `--open` / `--no-open` 只对本次生效。
- 搜索后端默认 `bing,baidu,searxng` 降级链，可在配置单 `searchBackends` / `searxngBaseUrl` 或环境变量 `AGENT_SEARCH_BACKENDS` 调整。

放在**可写目录**运行（要解压 `host\`）；工作区 / 会话目录默认还是相对当前工作目录创建。
开发流程不受影响：没有 `Payload\host-bundle.zip` 时启动器就是普通构建（仍走 `dotnet <dll>` 找源码树）。

> 脚本注意事项：`pack-launcher.cmd` 必须保持 **ANSI/GBK 编码 + CRLF 行尾**（中文 Windows 的 cmd
> 默认代码页），不要另存为 UTF-8，也不要加 chcp 65001 —— 否则批处理会碎行错乱。

## 分别编译：启动器 与 本体

这两块是**分开编译**的 —— 改本体不用重编启动器，改启动器也不用重编本体。

```bat
build-host.cmd        :: 只编本体（Debug）
build-launcher.cmd    :: 只编启动器（Debug，不会连带编本体，约 1.5 分钟）
run.cmd               :: 编本体 + 同步三件基石插件到 plugins\ + 起对话界面
clean.cmd             :: 清掉 bin / obj / build / out / 打包 Payload（CS0009 占用时先跑这个）
verify-all.cmd        :: 全量验证（Debug；17 套件串行，套件间留 1 秒防 DLL 占用假失败）
```

> 本体与插件开发统一用 **Debug**（`run.cmd` / `build-host.cmd` / `verify-all.cmd` 一致）。
> 发布与打包用 **Release**（`pack-launcher.cmd` / 下文 `dotnet publish`）。
>
> **所有 `*.cmd` 必须保持 ANSI/GBK（代码页 936）+ CRLF**，**不要**存成 UTF-8，也**不要**加 `chcp 65001`。
> 中文 Windows 的 cmd 按 OEM 代码页**解析**批处理；UTF-8 中文会让脚本在执行前就解析失败——双击窗口一闪就关，看起来像「没编译」。
>
> 脚本里的 `dotnet build` 带了 **`-m:1`（单节点串行）**：并行编译会抢 `Contracts` 的 ref 程序集，报 CS0009「文件被占用」并连带刷出一堆假的 CS0234/CS0246。仍占用时脚本会自动关掉 build-server、清 Contracts 的 bin/obj 再重试一次。

为什么能分开：**启动器不引用本体工程**了。（原来引用，于是改一行本体就得把启动器重编一遍。）

代价是启动器得自己找本体在哪，按「越明确越优先」找：

1. `--host <路径>`，或环境变量 `AGENT_HOST_DLL`
2. `<启动器目录>\host\AgentFramework.Host.dll` —— 编启动器时若本体已编过，会顺手拷一份过来
3. `<启动器目录>\AgentFramework.Host.dll` —— 跟启动器并排
4. 从启动器输出目录往上找 `AgentFramework.Host\bin\<配置>\net10.0\` —— 源码树里本体自己的产物

所以**先编哪个都行**。`VerifyLauncher` 仍然同时引用两者 —— 它要做的正是端到端装配，那是它的职责。

## 发布绿色版

```powershell
dotnet publish src\AgentFramework.Host\AgentFramework.Host.csproj `
  -c Release -r win-x64 --self-contained true -o out
```

`out\` 拷到任意 Windows 机器即用，目标机不需要装 .NET。

---

## 工程结构

| 工程 | 角色 |
|------|------|
| `src/AgentFramework.Contracts` | 契约 + 事件模型（共享加载，non-collectible） |
| `src/AgentFramework.Kernel` | 插件内核：加载/卸载 + 服务注册表 + 事件总线 |
| `src/AgentFramework.Data` | 数据平面：JSONL 日志 + 投影 + 分叉 + 上下文重建 |
| `src/AgentFramework.Llm` | 模型接入：OpenAI 兼容客户端 + 模型路由 |
| `src/AgentFramework.Agent` | Agent 主干：主循环 |
| `src/AgentFramework.Tools` | 官方工具集 + 安全防线 |
| `src/AgentFramework.Host` | **宿主：把上面全部装成一个可执行程序** |
| `src/AgentFramework.Launcher` | 启动器：勾选插件 → profile → 按勾选装配（**不引用本体，各编各的**；可打包成内嵌本体的单文件 exe，见 `pack-launcher.cmd`） |
| `src/AgentFramework.Desktop` | **桌面壳**：WebView2 原生窗口装下整个界面（Windows） |
| `src/AgentFramework.SamplePlugin` | 示例插件 |
| `src/AgentFramework.Plugins.DevKit` | **基石插件 · 编程扩展工具包**（编辑/检索/搬移，注入 `IWorkspaceService`） |
| `src/AgentFramework.Plugins.WritingKit` | **基石插件 · 写作扩展工具包**（字数/段落/口头禅/标点/提纲） |
| `src/AgentFramework.Plugins.ConsoleKit` | **基石插件 · 基础 UI 美化控制台**（`ui.styles/scripts` 注入主界面） |
| `src/AgentFramework.Index` | **SQLite 派生索引**：可查历史 + 历史检索（随时可从日志重建） |
| `src/AgentFramework.Sandbox` | **命令沙箱**：`off` / `process`（跨平台护栏）/ `job`（Windows Job Object 配额）+ 可注册后端 |
| `tests/AgentFramework.Verify*` | 二十二条切片验证（**总数以 verify-all 输出为准**） |

## 核心铁律（全部有验证覆盖）

**插件内核**

1. **契约程序集走 default ALC 共享加载** —— `PluginLoadContext.Load()` 返回 `null`。
2. **每版插件一个独立 collectible ALC** —— 更新不需等旧版卸净。
3. **注册即副作用、卸载即撤销** —— 插件作者不可能忘记清理。
4. **卸载顺序：先撤销副作用，再 `Unload()`**。

**数据平面**

5. **仅追加**：历史永不修改。
6. **状态只来自事件投影**：没有独立的"任务表"。
7. **崩溃自修复**：坏行跳过；`Open()` 截断尾部垃圾。
8. **序列化以 `SessionEvent` 为静态类型**：否则漏写多态判别字段。
9. **模型可见即已记录**：`SessionContextBuilder` 能从事件流重建模型上下文。

**Agent 主干 / 工具集**

10. **路由即 Provider**：`RouterLlmClient` 本身就是 `ILlmClient`。
11. **MaxSteps 上限**：防无限调用工具。
12. **预算不进契约**：超时/条数/字符上限全在配置层。
13. **读放开、文件工具写圈死**：读文件 / 列目录默认可以到硬盘任意位置（`AllowReadOutsideWorkspace`，默认 on），
    **文件工具 / 插件的写**永远只能落在会话的工作区 / 项目目录内（含符号链接穿透检查）。
    **`run_command` 不受此约束**（命令可写全盘）—— 靠审批 + 命令沙箱（cwd + tmp 钉进工作区）兜底；
    全局记忆在 `sessions/memory/`，同样不走这条写圈。**私有网段一律拒绝**（SSRF）。
14. **命令不做白名单**：把关交给审批事件。

**宿主**

15. **会话续接靠"每次从事件流重建上下文"** —— 重启无需额外状态。
16. **危险动作默认拒绝**，必须显式放开。

**摘要与注入防护**

17. **摘要器必须挂本地端点** —— 挂云端则"原文不出机"落空。
18. **摘要失败退回原文** —— 省钱手段不该成为可用性单点。
19. **注入内容只标注不删改** —— 可信度该由模型与用户判断。

**输入转述（澄清模式）**

20. **转述失败 / 超时 / 无端点，一律回退原文** —— 增强项不得成为可用性单点。
21. **原文与「模型看到的」都进日志**（`Text` + `RephrasedText`）—— 「模型可见即已记录」在转述下依然成立。
22. **转述绕过路由器** —— 必须去用户指定的端点，不交给规则分流。
23. **提示词可在设置里整体替换** —— 内核与契约都不含提示词（学 dsh：策略在配置）。

**长任务上下文治理**

24. **上下文是投影，不是日志** —— 压缩只换投影函数，历史一字不改，且可回放。
25. **压缩的收益必须量出来** —— 实测已推翻两个「看起来更省」的设计。
26. **遮蔽必须配检索** —— 没有 `search_history` 的折叠是「丢失」，有它才是「卸载」。
27. **索引永远可以丢** —— 派生数据；打不开就降级，绝不阻断主流程。
28. **先落日志、再写索引** —— 反了就会出现「索引里有、日志里没有」。
29. **压缩点即缓存断点** —— 水位 80% 才压，宁晚不频。

**分级记忆与工作模式**

30. **记忆分三级** —— 短期（会话）/ 长期（项目）/ 全局；**不用的那一级根本不打开文件**。
31. **记忆是真相源** —— 索引可丢，记忆不可丢；只追加、显式写入。
32. **项目记忆存在工作区里** —— 它属于这个项目，拷走工作区就该一起带走。
33. **切模式不重启** —— 工具照常注册，换的只是「这一轮暴露什么」。
34. **闲聊模式靠「不加载」省** —— 不挂工具 schema、不注任务卡、不治理、只读全局记忆。

**宿主与模块（架构地基）**

35. **宿主自己也是一组插件** —— 宿主模块走内核同一套 seam；注册即副作用，关闭时逆序自动回收。
36. **加功能 = 加模块** —— 实现 `IHostModule`，序号即插队位置；内置五模块 model 100 → storage 200 → tools 300 → plugins 400 → loop 500。
37. **注册 ≠ 可见** —— 工具一直注册着，这一轮给模型看哪些由工作模式决定。
38. **工具名单按名排序** —— 它是请求前缀的一部分，顺序一抖，端点的前缀缓存就整段作废。
39. **官方工具先于插件注册** —— 重名时插件加载失败并说明原因；明确失败优于静默顶替。
40. **审批只是交互 seam 的特例** —— 一条缝回答「有没有人可问」；无界面时如实回「问不出去」，绝不假装用户答过。
41. **回合闸每会话一把** —— 同一会话不许两个回合并跑，不同会话互不相干（子 agent 的地基）。

**异常路径（外部代码审查后补的）**

42. **异常路径必须有验收桩** —— 这一轮新增的 18 项全是异常路径，而从前的套件里几乎全是正常路径。
43. **模型通道也要失败降级** —— SSE 坏帧跳过；但连续坏帧过多即断流（无限吞垃圾只会把问题藏得更深）。
44. **补状态靠追加事件** —— 悬空工具调用补一条合成结果，历史一字不改。
45. **本地接口也要防护** —— Origin 校验 + 会话 id 白名单：浏览器能替你发请求，本地 ≠ 安全。
46. **「看起来是 v6、连的其实是 127.0.0.1」** —— IPv4-mapped 地址必须先归一再判，否则整段 SSRF 防护被绕过。

**命令沙箱（v3.9）**

47. **默认 auto** —— Windows 掉 `job`、其他平台掉 `process`；档位名不认识一律回落，且回落原因必须可读。
48. **临时目录钉进工作区** —— 否则「写只能在工作区内」有暗道可走。
49. **超时与取消是两件事**，两者都要**连子孙进程一起终结**；`kill` 只杀直接子进程等于没杀。
50. **沙箱可替换** —— 换沙箱是加一个后端（插件），不是改宿主里的 `switch`。

**工具包与暴露面（v3.10）**

51. **装载 ≠ 暴露** —— 插件照常装着；这一轮给模型看什么，由工具包开关决定。
52. **默认全开** —— 不配置就等于从前；「收起来」是主动动作，不是默认惩罚。
53. **保留包不许关**（core / meta）—— 关掉它们是把 agent 关成残废，或让开关再也开不回来。
54. **可见面只算一次** —— 主循环与诊断面问同一个 `VisibleTools`，两处各算一套必然对不上。

**记忆与进化（v3.11）**

55. **写入早、裁剪晚** —— checkpoint 只落盘、上下文一个字节不动，所以可以早、可以频繁；压缩换投影函数、必打断前缀缓存，所以必须晚、必须少。**两个触发点各管各的，别绑成一个。**
56. **结构化提取交独立 writer；主 agent 仍可显式 remember** —— 让正在调 bug 的模型同时维护结构化日志，两件事会各做差一件；结构化提取交给独立 writer（CQS），但 `remember` 保留为人机协同的显式写口。
57. **锚点必须是恒定大小的** —— checkpoint 与任务卡都是「摘要」不是「账本」：宁可少说，也不能自己长成新的上下文负担。
58. **新增事件默认不进模型上下文** —— 投影器不处理它（只在落盘与重建时读）；验收桩要能**证明**这一点（哨兵文本不出现、消息数不变）。

## ✨ 输入转述（澄清模式）

让一个**便宜**的模型先把用户的话说清楚，再交给**干活**的模型 —— 贵的模型只处理澄清过的任务。

- **手动（主路径）**：输入框旁 `✨ 优化` 按钮 → 结果**回填输入框**，可再改、可**撤销**，然后自己发。
- **自动（可选，默认关）**：点头部 `转述` pill 打开设置，勾上「发送前自动澄清」。
- **可换提示词**：同一个面板里改系统提示词，或只加一句补充要求；也能随时「恢复默认提示词」。
- **模型可选**：`local`（本地 · 便宜）或 `cloud`（联网 · 贵）。
- 设置存在 `agent-sessions/rephrase.json` —— 属于**用户偏好**，所以不进会话 JSONL。

## 🧠 长任务上下文治理

dsh 跑长任务之后效果会明显变差 —— 根因是**全量回灌**：每轮把整条历史原样喂给模型。
我们原本也是这么做的，所以这一层是专门补上的。

**中心思想：上下文是投影，不是日志。**
日志 append-only 永不修改；「这一轮喂给模型什么」只是一个投影，换个投影函数就行。
于是压缩可逆、可审计、可回放，而真相源一字未动。

**六层治理（前五层零模型开销）**

| 层 | 做什么 |
|---|---|
| L1 预算 | CJK 感知的 token 估算 + 水位统计 |
| L2 引用化 | 工具结果超限 → 落盘 `.agent-artifacts/`，上下文只留摘要 + 路径 + 头尾 |
| L3 遮蔽 | 窗口外的旧结果 → **一行**占位符（至少省 200 字符才折） |
| L4 任务卡 | 目标 / 进度 / 阻塞，每轮重算，注入上下文**末尾**（防任务漂移） |
| L5 摘要 | 早期对话压成摘要（**默认关**，见下） |
| L6 索引 | 折叠掉的内容进 SQLite，模型可用 `search_history` 主动捞回 |

**为什么默认不用 LLM 摘要**：OpenHands 480 任务 / 6 领域的实证显示，
摘要型 condenser 让总 token **增加 24–94%**，而质量没有统计显著提升；
最有效的恰恰是零模型开销的结构性裁剪。所以模型只当兜底，默认关。

**L3 + L6 = 无损压缩**：折叠把内容从上下文里拿走，`search_history` 保证随时取得回来。
没有检索的折叠是「丢失」，有检索才是「卸载」—— 这是比 dsh 多出来的那个机制。

**实测推翻过两个设计**（留着当教训）：
① 占位符原本留 240 字符头部 → 7 条白占上千 token，**折叠等于没折**；
② 遮蔽原本无门槛 → L2 之后旧结果本就不长，替换只是「换个说法」。
现在：占位符只留一行，且**至少省 200 字符才折**。

**看得到**：界面头部的水位、遮蔽条数、压缩次数、索引状态都由 `/api/status` 实时给出。

## 🧩 分级记忆与工作模式

长任务缺的不只是上下文管理，更是**记忆的层次**。人干活时脑子里分三层：
手头这件事（秒级）、这个项目的历史（周级）、跨项目的常识（永久，但**此刻不必想起**）。

**三级记忆**

| 层级 | 落在哪 | 何时加载 |
|---|---|---|
| 短期工作记忆 | 会话事件流 + 上下文投影 | 每轮（由上下文治理裁剪） |
| 长期记忆 | `<工作区>/.agent-memory/project.jsonl` | **工作模式** |
| 全局记忆 | `<sessions>/memory/global.jsonl` | **闲聊模式** |

项目记忆放在**工作区内** —— 它属于这个项目，拷走工作区就该一起带走。
记忆是**真相源**（不同于索引：索引能重建、记忆不能），所以只追加、显式写入。

**三种模式（界面右上角一点即切，不用重启）**

| | 工作模式 | 闲聊模式 | 设计对话 |
|---|---|---|---|
| 记忆 | 项目 | **全局** | 项目（索引卡更多） |
| 工具 | 全部 | **一个都不挂** | 全部 |
| 任务卡 | 注入 | 不注入 | 注入 |
| 上下文治理 | 六层全开 | 关 | 六层全开 |
| 提示词基调 | 干活 | 简短自然 | 先澄清视觉意图 |

**「少调用」少在哪**（在验证里是**量出来的**，不是口号）：
闲聊模式下发给模型的工具 schema 数量是 **0**（工作模式是 9）、
上下文里没有任务卡、没有项目记忆、每轮也不做投影比较与压缩决策。

工具**照常注册着**（`remember` / `recall_memory` 都在），模式只决定"这一轮暴露什么"——
所以切换是瞬时的，装配不必重来。

## 🧩 架构：加功能 = 加模块

宿主走的是**插件同一套机制**（`Provide / Get / On / Effect`）—— 注册即副作用，
关闭时自动逆序回收，所以模块作者（很可能也是编码 AI）不需要记得清理任何东西。

```csharp
public sealed class MyModule : IHostModule
{
    public string Name => "my-thing";
    public int Order => 450;   // 序号 = 插队位置（内置五模块：100/200/300/400/500）

    public ValueTask ConfigureAsync(HostState state, CancellationToken ct)
    {
        var scope = state.Kernel.CreateKernelScope("my-thing");
        scope.Provide<IMyService>(new MyService());   // 提供服务，别人 Get 得到
        scope.RegisterTool(new MyTool());             // 挂工具（下一轮模型就看得见）
        scope.On<SessionEvent>(OnEvent);              // 订阅事件
        scope.Effect(() => StartBackgroundThing());   // 托管后台资源
        return ValueTask.CompletedTask;
    }
}
```

**界面加端点也不必改主分发**：实现 `IWebRoute`，`server.Map(...)` 一行即可。
现成的例子：`GET /api/tools` —— 列出工具，以及**它由谁挂上来的**。

**已经就位、只差填肉的三件地基**

| 地基 | 入口 | 将来谁用 |
|---|---|---|
| **会话派生** | `host.OpenSession(sessionId)` —— 共享模型/工具/索引/记忆，独立事件流与上下文 | 子 agent |
| **交互缝** | `host.UserInteraction = ...` —— 模型就能用 `ask_user` 反问用户；无界面时如实回「问不出去」 | 干活三件套 |
| **工具注册表** | 运行期挂上就可见、撤销即消失（`scope.RegisterTool`） | 技能系统 |

## 🧱 基石插件：随包分发的三件套（人写的 C# 程序集）

前面十八条切片里，agent 手上的官方工具虽有 20 个（含 `search_history`；索引不可用时为 19），编辑能力却**只有整篇覆盖** ——
改一行代码要重发整个文件；想找一处代码，只能 `run_command` 拼各平台不一样的 `findstr` / `grep`。
这一版把这些**基石能力**补齐，而且是**当插件补**、不是塞进宿主源码：
「能当插件做的，就不要进内核」。

| 插件（`src/AgentFramework.Plugins.*`） | 补什么 | 工具 |
|------|------|------|
| **devkit** · 编程扩展工具包 | 从「整篇重写」升级为**局部改**；找东西不再靠拼平台命令 | `edit_file` · `grep_files` · `find_files` · `read_lines` · `make_dir` · `move_path` · `delete_path` |
| **writing-kit** · 写作扩展工具包 | 写作里**模型最不擅长自测**的那几项：字数、段落节奏、口头禅、标点 | `word_count` · `paragraph_report` · `repeat_words` · `check_punctuation` · `outline` |
| **console-kit** · 基础 UI 美化控制台 | 界面外观可调：主题色板 / 阅读模式（**一个工具都不注册** —— 插件不只会给 agent 加能力） | 面板（主题 · 阅读模式） |

几处刻意的取舍：

- **`edit_file` 多处匹配时拒绝执行**，而不是猜第一处。猜错一次要用户花十句话纠正，拒绝一次只要模型补两行上下文。
- **换行符宽容**：模型用 LF 写的 `old_string` 能改 CRLF 文件，且**不改动任何没碰过的字节**（只归一搜索串，替换仍发生在原文上）。
- **`delete_path` 删非空目录必须显式 `recursive=true`**，且拒删工作区根 —— 门槛是结构性的，不靠自觉。
- **写作工具是确定性计数器**，不是「让模型目测」：字数口径写死在代码里（汉字 / 英文词 / 数字分列），
  重复词按 2–4 字窗口统计，标点逐字符看。

### 插件怎么拿到「工作区」：`IWorkspaceService`

插件跑在独立 ALC 里，**只有契约程序集是共享的** —— 宿主内部的 `ToolkitOptions` / 路径检查器跨不过去。
于是新增一条契约 seam：宿主 `Provide`，插件在清单里 `injects` 之后 `Get`。

```csharp
public interface IWorkspaceService
{
    string Root { get; }
    bool AllowReadOutsideWorkspace { get; }
    int MaxReadChars { get; }
    int MaxWriteChars { get; }
    bool TryResolve(string path, bool forWrite, out string fullPath, out string? error);
    string Shrink(string toolName, string content);   // 大输出引用化，与官方工具同一策略
}
```

关键在于它**复用同一份边界实现**（`WorkspacePath.TryResolve`）：官方文件工具与插件走的是同一段代码，
所以「插件能不能写到工作区外」不必再单独审计一遍 —— 多一层实现就等于多一个漂移点。
验证里专门钉了这条：插件的 `edit_file` / `move_path` 越界一律被拒，且失败后原文件一字未动。

### 插件怎么改界面：`ui.styles` / `ui.scripts`

`panel.html` 只能改 iframe 自己；换肤这类需求改的是**主界面**。所以清单多了一段声明：

```json
{ "ui": { "styles": ["theme.css"], "scripts": ["theme.js"] } }
```

宿主把样式注入 `</head>` 之前、脚本注入 `</body>` 之前（按插件 id 排序，覆盖顺序才可预期），
并用 `/plugin-ui?id=<插件>&file=<文件>` 服务这些文件 —— **只服务清单里声明过的文件**，
不是「插件目录随便读」。验证里钉了：未声明的文件、带 `..` 的路径，一律拒。

- 验证：`tests/AgentFramework.VerifyBaseKit` **41 项**（含端到端 HTTP：首页是否真被注入、`/plugin-ui` 是否真能取到样式）
- 开发态：`run.cmd` 会把三个插件的 Debug 产物同步到 `plugins\`；打包时 `pack-launcher.cmd` 会把它们 Release 发布进去
- 想再加一个基石插件：照 `AgentFramework.Plugins.*` 建工程 → 写 `plugin.json`（含 `injects`）→ `ActivateAsync` 里 `ctx.RegisterTool(...)`
  → 加进 `AgentFramework.sln` → 在 `run.cmd` / `pack-launcher.cmd` 里各加一行同步

## 🧰 命令沙箱：跑命令真的「关进笼子」

`run_command` 是最危险的工具。此前它只有两道防线：**审批**（决定放不放行）与**超时**。
可「放行了」之后能造成什么破坏，没人管：

- 命令往系统临时目录随手写 —— 那是绕过「文件工具写只能在工作区内」最方便的一条暗道（命令本身不受写圈约束，靠审批 + 沙箱）；
- 命令超时被杀了，可它起的子进程还在后台偷偷跑（**孤儿**）；
- 一条 `npm install` 的日志能吃到几百 MB 内存；跑疯的构建把机器一起拖死。

这一版把「放行之后」也管起来，而且**做成可替换的后端**（学 dsh：sandbox 本身就是插件）——
宿主内置三档，插件可以注册新后端（容器、远程执行、带审计的包装……）。

| 档位 | 管住什么 | 平台 |
|------|---------|------|
| `off` | 不管。**保留它是有意的**：出事时能一键退回「没有沙箱的世界」，才好判断问题出在命令本身还是沙箱身上 | 全平台 |
| **`process`**（非 Windows 上 auto 的落点） | 工作目录钉在工作区；**临时目录重定向进工作区**（TMP/TEMP/TMPDIR）；**超时/取消连子孙进程一起终结**；输出过程中封顶 | 全平台 |
| **`job`**（Windows 上 auto 的落点） | 在 process 之上加**内核配额**：内存上限、CPU 时间上限、活动进程数上限，且**宿主退出时连同子孙一并终结**（Job Object 的 `KILL_ON_JOB_CLOSE`） | Windows |

为什么 Windows 上选 Job Object：它是**系统自带、零依赖**的进程配额机制，而 agent 跑命令真正要的
就是这几条。容器（要装运行环境）、低完整性级别（会把普通程序跑坏）、防火墙（要管理员权限）
在这里都是「代价大于收益」的选择 —— 需要更强隔离时，那是**插件**该干的事。

几处刻意的设计：

- **临时目录必须重定向**。不然「文件工具写只能在工作区内」这条纪律有暗道可走，而且是最容易走的那条
  （`run_command` 本身可写全盘，写圈死只约束文件工具/插件；沙箱把 cwd + tmp 钉进工作区）。
- **降级必须可见**。配置里写了 `job`、机器上回落到了 `process`，诊断面会说明原因 ——
  「以为自己被保护着」比「知道自己没被保护」更危险。
- **结果里标明经过哪一档**：`run_command` 输出里有一行 `sandbox=process`，
  出问题时第一眼就知道当时有什么护栏。
- **shell 会话级一次决定**：优先 POSIX（bash → sh），Windows 找不到才回落 cmd（并写明）。
  同一会话内不再每条命令重猜；结果里有 `shell=bash` / `shell=cmd(非 POSIX)`。
  配置：`--shell` / `AGENT_SHELL` / `agent.json` 的 `shell`（`bash`/`sh`/`cmd`/`powershell`/`pwsh`/路径/`auto`）。
- **换沙箱 = 加插件**：`ctx.Effect(() => registry.Register(backend))`，卸载即撤销。
  沙箱档位不认识、或后端在平台不可用时一律回落，并且**回落原因必须可读**。
- **超时与取消是两件事**：前者是命令自己的问题，后者是用户叫停 —— 结果里分开报。

- 验证：`tests/AgentFramework.VerifySandbox` **25 项**，其中「超时连子孙一起终结」用的是
  **三步采样**（起之前 → 跑起来时 → 终结之后）：不先证明命令真起了子进程，
  这条断言就是假阳性。

## 🧳 工具包：装得多 ≠ 负担重

工具定义是**每轮都在交的税**。行业实测的准确率拐点就在 **30~50 个工具** ——
再多，模型挑对工具的能力开始下滑；接了 MCP 更是直接爆（典型多服务器配置 55k token 起步）。

病根不在「插件太多」，而在**装载与暴露被绑成了一个开关**：

> 插件一装 → 工具立刻全暴露；可见性只有「全开 / 全关 / 手列几十个工具名」。

现在把两者拆开：**工具按「包」分组，会话里按包开合**。
2026-09 重划：**能收 core 的全收 core**（完整文件链 + 记忆 + 计划 + 历史），包数从十余个收到 5 + 领域插件。

| 包 | 装什么 | 可否关闭 |
|---|---|---|
| `core` | 完整文件链（读/写/列/改/行读/建/移/删/搜/找）· 问用户 · 记忆 · 计划/笔记 · 历史 · 派子 Agent · 结构化转换 | **不可关**（关了 agent 就成残废） |
| `meta` | `toolsets` · `use_toolset` · `tool_catalog` | **不可关**（关了再也开不回来） |
| `exec` | `run_command`（受命令沙箱保护） | 可关 ——「这次不许它跑命令」有出口了 |
| `web` | `web_search` · `web_fetch` | 可关 |
| `self` | 插件四件套 + 技能工坊（给自己长能力） | 可关 |
| `writing-kit` | 基石插件带来的写作分析包（**包名与显示名来自插件清单**） | 可关 |
| `mcp:<server>` | （下一步）每个 MCP server 一个包 | 可关 |

**三个开关入口**（读的是同一份视图，永远一致）：

- **人**：界面顶栏「包」 → 一排开关，「收起 / 打开」，下一轮生效；
- **agent**：`toolsets` 看、`use_toolset` 开合 —— 谁最清楚这段活要什么？它自己；
- **配置**：`--disable-toolsets web,writing-kit` 或 `agent.json` 的 `disabledToolsets`。

几处刻意的取舍：

- **默认全开**：不配置就等于从前（零回归），「收起来」是个主动动作；
- **保留包不许关**：`core` 与 `meta` —— 关掉它们不是省负担，是把 agent 关成残废，
  或让开关本身再也开不回来（宿主直接拒绝，并说明理由）；
- **包 = 能力的用途，不是代码的来源**：所以「写小说时才用的那些」能和「天天要用的那些」分开；
- **包是可见性的最小单位**，也是下一步的两块地基：MCP 工具会落进 `mcp:<server>` 包并默认
  **延迟**（不进 schema），Tool Search 再按需把它们拉进上下文。

- 验证：`tests/AgentFramework.VerifyToolsets` **31 项**（含端到端 HTTP：
  开关是否真的改变暴露面、保留包是否真的被拒、配置里写保留包是否真的不生效）

## 🔌 脚本插件：agent 自己给自己写插件（免编译热更新）

上面那三个基石插件（devkit / writing-kit / console-kit）都是**人写的 C# 程序集**。这一层补的是另一半：
**agent 自己写的插件** —— 用 JS 写、免编译、**热装载**，写坏了自动回滚。

| 谁写 | 形态 | 怎么装 |
|------|------|--------|
| 人（官方 / 基石插件） | C# 程序集，要 `dotnet build` | 启动器勾选 |
| **agent 自己** | **JS 脚本**（内嵌 Jint 引擎） | **`plugin_write` 写盘 + `plugin_reload` 装载** |

```text
agent 想给自己加个能力
  → plugin_write   把 {plugin.json, main.js} 写进 <workspace>/plugins/<id>/
  → plugin_reload  装载（先卸旧、再装新）—— 不用重启、不用重新编译
      ├─ 成功 → 下一轮的工具表里就有它（工具表每轮现取，热更新的地基早就有了）
      └─ 失败 → 自动回滚到「上次装载成功」的那一版，并把失败理由交回 agent
  → 用着不满意？再写一版、再 reload。整个不想要了就 plugin_uninstall。
```

**脚本能碰到什么，由清单说了算**：脚本里**没有任何 CLR 对象** —— 拿不到文件系统、网络、进程。
想读文件、想借调别的工具，必须先在 `plugin.json` 的 `capabilities` 里写明（`tool:read_file` / `tool:*`），
**没声明就直接拒**。另可用 `ctx.on('tools/pre-execute', fn)` 挂钩子，返回 `false` 即拦下这次工具调用 ——
"确定性保证"那一类需求（对标 Claude Code 的 Hooks：不靠模型自觉，靠钩子兜底）。

- 清单格式、脚本骨架、`ctx` 全部成员、能力声明、热更新流程、**踩坑清单** → [`docs/PLUGIN-SDK.md`](docs/PLUGIN-SDK.md)
- 可直接抄的示例（纯计算、不声明能力） → [`samples/script-plugins/writing-kit/`](samples/script-plugins/writing-kit/)
- agent 手上多了四个工具：`plugin_write` · `plugin_reload` · `plugin_uninstall` · `plugin_list`
- **装载即自测**：脚本可定义 `selftest()`，装载时自动跑一遍，不过就拒装 —— 免得"写完就报成功、下一轮才发现是坏的"
- **重启不丢**：宿主启动时自动装载统一仓库里的插件，所以升级出来的能力是持久的
- 验证：`tests/AgentFramework.VerifyPlugins` **20 项**（含"坏版本被拒 → 自动回滚到上一可用版并重新装载"）

> 为什么脚本用 JS 而不是给 agent 编译 C#：编译 C# 要背上 Roslyn，包体积和复杂度都上一个台阶；
> JS 引擎纯托管、随包分发无原生依赖，模型对 JS 也最熟，而工具参数本来就是 JSON。
> **要"真能跑"的重插件（官方 / 基石那类）仍然走 C# 程序集 —— 两条通道各管一段。**

## 🧠 记忆与进化：写入早、裁剪晚（v3.11 起）

长任务的天花板不是窗口大小，而是**状态连续性**。对照 MiMo Code 后我们把差距重新定位了一次：**不在「存储」，在「时机与角色」**。

**存储我们本来就有** —— 事件溯源记忆（改主意用 `slot` 取代、撤销是追加一条）、前缀缓存安全装配（冻结段 / 动态段靠结构分离）、全量可回放轨迹、分叉基础设施。**缺的只有两件事**：

- **谁写？** 现在是**主 agent 自己**调 `remember` / `update_notes`。让一个正在调棘手 bug 的模型同时维护结构化日志，往往两件事各做差一件 —— 提取该交给**独立的 writer**。
- **什么时候写？** 现在只在「该压缩了」（水位 `0.8`）那一个时刻顺手存一次。

于是拆成两件事，**各有各的计时**：

| | 干什么 | 动不动上下文 | 时机 |
|---|---|---|---|
| **checkpoint** | 提取结构化状态 → 落盘 | **不动**（投影器不处理它） | 水位 **`0.35`** —— 可以早、可以频繁 |
| **compaction** | 换投影函数 → 收紧视图 | 动（必打断前缀缓存） | 水位 **`0.8`** —— 宁晚不频（**原样不动**） |

- **契约**：`CheckpointEvent`（第 16 种事件）+ `ICheckpointWriter` + `CheckpointOptions`（默认**关**，长任务显式开）
- **三条纪律**：**不进模型上下文** · **追加而非覆盖**（旧的那条永不改） · 带 `FromSeq` / `ToSeq` **增量区间**（于是「改主意」在时间线上可见，而不是被悄悄覆盖）
- **验证**：`tests/AgentFramework.VerifyCheckpoint` **31 项**（专门断言「加入 checkpoint 后投影消息数不变、哨兵文本不出现在任何消息里」）

完整计划（五个支柱 / 分期 / 非目标 / 纪律）见 `docs/PLAN-memory-evolution.md`。

### 降档判据：调用频率，而不是时间线（修订）

原清扫用「超过 30 天未写 && 热度 ≤ 1」判冷归档 —— **判据轴选错了**：价值不是年龄的函数，
只有「还用不用」守恒。而且它用**创建时间**（不是「距上次使用」），又只统计**显式** `recall_memory`，
每轮自动注入的记忆根本不加温 —— 于是「每轮都在被静默使用」的高频记忆反被当冷条目扫走，**激励是反的**。

现在改为**频率优先 + 衰减**：

```
热度 = (1 + 使用次数) × 0.5^(闲置天数 / 半衰期)      半衰期默认 30 天
热度 < 0.5 且未置顶  ⇒ 休眠（归档，可随时恢复）
```

- **越用越新**：每用一次，次数 +1、衰减时钟归零 → 热度只增不减 → 再老的记忆，只要还在用就永不掉线；
- **寿命是频率的函数**：用过一次就把衰减阈值推后一个半衰期，而不是「满 30 天必扫」；
- **时间只在衰减指数里出现一次**，绝不单独当门槛 —— 这正是与「30 天时间线」的分野；
- **隐式使用也计入**：本轮检索命中即记一次使用（`delta=0`，不动排序热度，一条 batch 落盘），消除「常驻即永冷」的信号盲区。

> 面板上的「清扫」也据此改称「**巩固**」，并展示「用过 N 次 · 最近使用」，让「为什么这条该睡 / 该留」可解释。

## 🔒 安全与正确性加固（v3.12）

外部代码审查后清理了全部 Critical / P0，包括：插件 id / 清单路径穿越、命令环境密钥泄漏、
SSRF DNS rebinding（连接时绑定 IP）、写边界 TOCTOU（RealPath 落盘）、中段坏行不再截掉真相源、
工具异常必补 completed（防悬空 tool_call 400）、记忆合并原子化、ScriptPlugin 同步桥死锁、
ALC 共享 `ILogger` 类型同一性、索引按 seq 水位判落后等。细节见
[`docs/history/CHANGES-v3.12-hardening.md`](docs/history/CHANGES-v3.12-hardening.md)。

## 下一步

**记忆与进化（进行中，与 `docs/PLAN-memory-evolution.md` 同一套版本号）**

- **v3.11 剩余**：`LlmCheckpointWriter`（旁路独立提取者，照 `LlmContextSummarizer` 的缝）→ 水位触发接进主循环 → writer 顺带把稳定观察升级进记忆（`CheckpointEvent` / `ICheckpointWriter` 契约已就位）
- **v3.12**：`rebuild` —— 用 `SessionForker` **分叉新会话** + checkpoint 当种子 = 逻辑会话无界、物理窗口有界
- **v3.13**：Dream / Distill —— 定期整理记忆、把重复出现的流程**固化成 B 通道插件**（可执行、可热装载，不只是文档）
- **v3.14**：MCP 子进程通道（默认延迟 + 首次用到才启动）
- **v3.15**：Goal 完成度验证器 + Max Mode —— 两个「用算力换可靠性」的开关，默认关

**其余**

- **真实模型联调**：`OpenAiCompatibleClient` 已就绪，设好环境变量就能连
- **干活三件套**：`ask_user`（缝已就位）/ 持久 shell / 后台 jobs
- **技能系统**：注册表 + 文件 provider + 按需加载（对齐 dsh 的 `dsh-skill`）
- **子 agent 委派**：会话派生已就位，差派活 / 发消息 / 打断 / 名单
- **计划模式 + 结构化 todo**：小本本升级版
- **工具包下一步**：MCP 工具落进 `mcp:<server>` 包并默认延迟加载 → Tool Search 按需拉 schema
- **更多基石插件**：后台 jobs + 持久 shell（长任务不再被 30 秒上限掐断）
- **MCP 子进程通道**：把外部 MCP 服务接进来当工具（决策已定：直接兼容 MCP，不自造 RPC）
- **沙箱后端插件**：容器档（Docker/Podman）—— 机制已就位，缺的是那个后端
- **运行期挂载插件**：已完成（`plugin_write` / `plugin_reload`，见上）
