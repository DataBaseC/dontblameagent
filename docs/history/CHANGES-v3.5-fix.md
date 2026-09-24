# v3.5 审查后修复说明（fix-1 + fix-2）

> 对应审查报告：`../REVIEW-v3.5.md`
> 两轮共修 **21 项**（4 条 P1 + 17 项 P2）。
> **编译 0 错误 / 3 警告；11 个验证工程 740 项 0 失败**（原 731，新增 9 条针对性断言）。
> 纪律：每条修复都配「能区分修前/修后」的断言；无法确定性测试的项在第四节如实标注。

---

## 一、P1（fix-1，4 条）

| # | 位置 | 改动 |
|---|---|---|
| P1-1 | `Hosting/HostModule.cs`、`Host/AgentHost.cs`、`Hosting/HostBuilder.cs` | 新增 `AsyncLocal TurnSessionId` + `BeginTurn(modeId, projectDir, sessionId)`；`CreateCore` 回填 `CurrentSessionIdProvider`；`remember` 也改走同一 provider。多会话下 `search_history`/`spawn_subagent`/`update_plan` 读本回合所属会话 |
| P1-2 | `Llm/OpenAiCompatibleClient.cs` | 改用 `SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)`；`Timeout` 改无限总时限 + 帧间空闲超时；坏帧计数改「连续」 |
| P1-3 | `Host/WebUiPage.cs` | `renderScope` 补 `const scope = isProject ? data.scope : 'global'`；合并分组改 `lastIndexOf(':')` |
| P1-4 | `Host/AgentHost.cs` | 温度档改「先取全量视图排序、后截断」；Insertion 档维持原语义 |

> ⚠️ **对报告的一处更正**：报告建议的 `PostAsync(..., HttpCompletionOption, ct)` **在 .NET 里不存在该重载**（只有 `GetAsync` 有 `HttpCompletionOption`），POST 要流式只能走 `SendAsync`。修复按此实现。

## 二、P2（fix-1，6 项）

| # | 位置 | 改动 |
|---|---|---|
| 1 | `Data/ContextCompactor.cs` | 删除恒假的「L6 兜底」死代码 |
| 2 | `Host/WebUiServer.Routes.Chat.cs` | 导入与**已存在 workshop 技能**同名 → 409 |
| 3 | 同上 | `skills/import` 的 500 不再回传 `ex.Message` |
| 4 | 同上 | `/api/memory/action` 与 `/merge` 的 project scope 改**精确等于本会话作用域** |
| 5 | `Launcher/LauncherServer.cs` | `/debug` 的「装配序: {id}」与 AddRow 数据项补 `Escape` |
| 6 | `Kernel/PluginHandle.cs`、`PluginHost.cs` | 卸载时置空 `_entryAssembly` + 回调 `Forget(this)`（原先 `Forget()` 全仓无调用点） |

---

## 三、第二轮（fix-2）：先亲验、再修复的 11 项

> 本轮先逐条核对（不采信上轮子流程的转述），确认属实才改。

| # | 缺陷（已亲验） | 改动 |
|---|---|---|
| 1 | UI 水位条读原始配置、真实回合读档位生效配置 → **两者口径不同** | 新增 `AgentHost.EffectiveContextSettings()`，`WebUiServer.ContextStatus` 改用它 |
| 2 | 清扫**预览**只数最近 500 条、**执行**扫全量 → 数字对不上 | `SweepPreview` 改全量视图 |
| 3 | 记忆面板的全局**归档层恒为空数组** → 被清扫的全局记忆成 UI 孤儿（无法恢复） | `MemoryListPayload` 取 `LoadArchivedAsync(Global)` |
| 4 | 合并结果**不继承 `IsImportant`** → 合并一条置顶约定后钉住语义丢失 | `IsImportant = sources.Any(x => x.IsImportant)` |
| 5 | `SwapTo` 分 4 次赋值 → 并发读者可见撕裂态（新 `_session` + 旧 `_log`） | `_log`/`_sink`/`_runner` 改为 `_session` 的**派生只读属性**，切会话退化为一次引用赋值 |
| 6 | `RouterLlmClient._history` **无界增长**（长会话每轮一条） | 加 `MaxHistory = 200` 上限 |
| 7 | 子会话创建后**永不回收**（内存事件表 + 日志句柄常驻） | `SubAgentRunner` 加 `finally { CloseSessionAsync(childId) }`（日志文件保留） |
| 8 | `Fold` 缓存**填充/失效 TOCTOU**（读线程回填可覆盖写线程的失效） | 加 `_writeGeneration` 世代计数：折叠期间有写入则不回填 |
| 9 | `CloseSessionAsync` 先还闸后摘除 → 存在「用已释放的闸」窗口 | 改为**摘除先于还闸** |
| 10 | 文件沙箱只比字符串前缀 → **符号链接可越界读写** | `FileTools.RealPath` 逐段穿透符号链接后再比较 |
| 11 | 工坊导入用 `EnumerateFiles(AllDirectories)` **跟随符号链接** | 新增 `EnumerateFilesSafe`，跳过 `LinkTarget` 非空的目录 |

**第 10 项的实况记录（值得留档）**：第一版只解析了「最后一级」是否链接，测试当场抓住 —— 写不存在的文件被正确拒绝，**读已存在的文件却穿过去了**（`链接目录/真实文件` 这条最常见的形态）。改成逐段解析后才真正闭合。这正是「断言必须能区分修前/修后」的价值：它没让一个半对的修复蒙混过关。

---

## 四、未修复（明确记录，不假装）

| 项 | 原因 |
|---|---|
| `WebFetch` 的 DNS-TOCTOU | 彻底封法要用**已解析的 IP 手动建连**（两次独立 DNS 解析的窗口无法用校验消除），改动面大，留待专门一轮 |
| 水位未计入冻结段与 tools schema | `TokenBudget` 默认 24k，相对常见模型窗口仍属保守，低估不会立刻致命；精确修法要给 `ContextOptions` 加跨层预留契约，收益/复杂度不划算 |
| `MaskedResults` 混计「助手骨架」与「工具遮蔽」 | 改它等于改事件字段语义（`MaskedSeqs` 是视图契约），需连带回归整套 L3/L6，风险大于收益 |
| 占位符 `#Seq` 无法被 `search_history` 按序号捞回 | 需给工具加 `seq` 参数（工具 API 变更），属功能增强而非缺陷修复 |
| 投影级缓存（超限轮恒定跑两遍 `Project`） | 纯性能项，改动面大 |
| 插件可 `Get<IMemoryStore>()/Get<ISessionIndex>()`（无声明式授权面） | 设计决策，需主人定「插件能力边界」，不宜单方面改 |
| `session-meta.json`/`titles.json` 写原子性、`/api/stop` 会话化、`CommandTool` 工作目录校验、`IsBusy` 语义 | NIT 级，逐条记录待办 |
| 插件 `id` 字符白名单 | 转义已消除 XSS 面，加白名单属纵深防御 |
| `MarkRetired()` 零调用 | 经核实**是有意保留**（`AgentHost.cs:185-187` 注释已说明），属文档描述过时，非缺陷 |

---

## 五、验证

```
dotnet build AgentFramework.sln -c Debug  →  0 错误 / 3 警告（均为已知项）

Verify 24 · VerifyAgent 30 · VerifyContext 109 · VerifyData 41 · VerifyHost 53
VerifyLauncher 44 · VerifyMemory 155 · VerifyRephrase 96 · VerifySummary 25
VerifyTools 35 · VerifyWeb 128
                            合计 740 通过 / 0 失败
```

**两轮共新增 9 条断言**（均针对修复点，且都能区分修前/修后）：

| 工程 | 断言 |
|---|---|
| VerifyMemory +3 | ★ 被 10 条新流水账挤出时间窗的老约定凭热度仍进索引卡；老约定排在时间序最末（反证）；★ 合并继承置顶标记 |
| VerifyWeb +2 | ★ 越界 project scope 在进存储前被 400 拒（断言含 `scope` 字样以区分「记忆不存在」那类 400）；★ 与已导入工坊技能重名被 409 拒 |
| VerifyTools +4 | ★ 经符号链接读区外被拒；★ 经符号链接写区外被拒；区外文件未被写入；修复未误伤区内正常路径 |

> 跑测试提示：先把 `AgentFramework.SamplePlugin` 产物复制到 `Verify` / `VerifyHost` / `VerifyLauncher` 输出目录的 `plugins/hello/`（这三个工程的 `CopySamplePlugin` target 硬编码 `bin/$(Configuration)/$(TFM)`，用 `--artifacts-path` 或自定义输出根时会复制不到 —— **待修的第 3 类问题**，两轮均未动）。

---

## 六、fix-3：Windows 平台适配（本机验证轮）

> 在 Windows 上首次实际编译与运行时发现：验证声称 740 项全过，但其中 4 个工程在本机
> 直接崩溃（Unhandled exception）。原因是验证此前跑在无强制文件锁的平台上 ——
> Windows 对文件共享模式是强制的，同类问题一处在本体，三处在测试代码。

| # | 位置 | 缺陷（已亲验） | 改动 |
|---|---|---|---|
| W1 | `Data/JsonlEventLog.cs` | `Open()` 以写模式常驻持有日志句柄，`Read()` 却用 `File.ReadLines`（共享模式不容忍并发写句柄）→ **宿主启动必然崩**（SessionRuntime 构造第一行就重读日志） | `Read` 改宽容共享模式（`FileShare.ReadWrite\|Delete`）打开；`ScanAndRepair` 同理（`ReadAllBytesTolerant`）；新增 `ReadRawText()` 供原始文本断言 |
| W2 | `tests/VerifyHost` | 测试用 `File.ReadAllText` 直读还开着写句柄的日志 | 改 `JsonlEventLog.ReadRawText` |
| W3 | `tests/VerifyRephrase` | 同上 | 同上 |
| W4 | `tests/VerifyContext` | 同上；另：`run_command` 断言写死了 bash 语法，Windows 上 cmd.exe 跑不动 → 断言失败 | 读取改 `ReadRawText`；大输出命令按平台选等价命令（cmd `for /l` / bash `seq`） |
| W5 | `tests/VerifyMemory` | 「重新装配」测试未先释放旧宿主就再开同一会话 → Windows 排他写冲突崩溃 | 先 `await host.DisposeAsync()` 再重开（这才是「重启」的本义） |

验证：Windows 11 + .NET SDK 10.0.401，Debug/Release 均 0 错误（警告为已知 MSB3277）；
11 个验证工程 **740 通过 / 0 失败 / 0 崩溃**；Host Web 界面实机冒烟通过（HTTP 200）。

> 本次修复未新增能区分修前/修后的独立断言 —— W1 的「修前」在本机就是启动即崩，
> 冒烟本身即断言；W2–W5 的修前形态在本机是进程崩溃，非断言失败。

---

## 七、fix-4：单文件启动器 exe（分发形态）

> 需求：把启动器整成一个 exe，其他的由启动器拉起 —— 目标机免装 .NET、免源码。

| 项 | 内容 |
|---|---|
| 新增 `pack-launcher.cmd` | 五步流水：编本体(Release) → 自包含发布 win-x64 → 收入示例插件 → 压成 `host-bundle.zip` → 内嵌进启动器并单文件发布。产物 `build\pack\out\AgentFramework.Launcher.exe`（约 111 MB） |
| `Launcher.csproj` | 有 `Payload\host-bundle.zip` 就内嵌为资源；单文件发布时跳过 `CopyHostIfBuilt`（开发态拷贝与分发形态互不污染） |
| `Launcher/Program.cs` | ① 单文件形态判定（`Assembly.Location` 为空，官方判据）：仅此时解压内嵌包到 exe 旁 `host\`（幂等：SHA256 整包哈希当版本，变了才重解；解压拒绝 zip 路径穿越）；② 宿主解析新增「自包含 exe 优先」：`host\AgentFramework.Host.exe` 直接拉起，不再依赖目标机的 dotnet；③ `--plugins` 未显式指定且 cwd 无 plugins 时退回 `host\plugins`（随包示例插件）。开发态行为完全不变 |

端到端实测（干净目录模拟目标机）：启动器页面 200 → 自动解压 host\（含本体 exe 与示例插件）→ POST /launch → 宿主 Web 界面 200 → api/state 报 running=true 且装配 hello 插件。VerifyLauncher 44/0 回归通过。

> 脚本踩坑留档：含中文的批处理在 UTF-8 编码下会在 `chcp 65001` 后因字节重定位错位而碎行；
> LF 行尾同样导致解析不稳。`pack-launcher.cmd` 定稿为 **GBK(ANSI) + CRLF、无 chcp、无 goto**。
---

## 八、fix-5：体验修复五连（思考块 / 思考强度 / 原生窗口 / 搜索 / 转述）

> 用户实测反馈的五项问题，逐条修复。

| # | 问题（用户原述） | 根因（已定位） | 修复 |
|---|---|---|---|
| 1 | 已回复完还显示「正在思考」 | UI 只在正文到达/用量帧时收口思考块；模型先思考→调工具→再思考、或一轮以工具/审批/报错结束时永远不收 | `WebUiPage`：assistant 落地、tool-call-requested、approval、turn-ended、error、sys、历史回放结束 **七处统一收口**；历史回放不留 live 块 |
| 2 | 模型管理没有思考强度选项 | 请求层无注入点 | `LlmRequest.ReasoningEffort` + `OpenAiCompatibleOptions.ReasoningStyle`（openai=顶层 `reasoning_effort`；qwen=末条 user 消息 `enable_thinking/thinking_budget`；none=不注入）；端点编辑器加「思考风格 + 默认强度」下拉；请求档位可盖端点默认 |
| 3 | 想用 Win 原生窗口而非浏览器 | Desktop 壳只会内置起宿主，无法连接已运行宿主 | Desktop 支持 `--url <地址>` 只当窗口；启动器「启动宿主」成功后**自动拉起原生窗口**（找不到桌面壳才退回浏览器）；打包脚本把 Desktop 一并嵌入 |
| 4 | 联网搜索直接超时 | 唯一后端写死 `https://searx.be`，当前网络不可达且无降级 | 新增 Bing(cn.bing.com) 与百度 HTML 抓取后端（免 key）；默认降级链 `bing,baidu,searxng`；`searchBackends`/`searxngBaseUrl` 可配置（配置单/环境变量/命令行）；失败信息列出各后端尝试结果。**已知**：百度对无 Cookie 的机房流量常弹验证页 —— 此时链自动换下一后端 |
| 5 | 转述不能选模型 | 转述目标只认 `local`/`cloud` 两个写死名字，多端点体系下解析不到就静默回退原文 | 目标解析升级：端点 id 或「端点:模型」皆可；转述面板模型下拉改为动态（列全部端点与模型，模型管理变更即时联动）；老配置不受影响 |

**新增验证工程 `VerifyLlm`（18 项，全离线）**：本地 HttpListener 捕获真实报文断言 openai/qwen 风格注入、Bearer 头来自配置、未配置不注入；Bing/百度 HTML 样本解析；降级链留痕。期间抓出并修掉两个实现 bug（Qwen 标记注入写法、Bing 块切分正则）与一个响应释放时机错误。

**真实联网冒烟**：`bing,baidu,searxng` 链搜「量子计算 最新进展」→ bing 命中 6 条相关结果（知乎/CCTV 等），无需降级。

回归：12 个验证工程 **758 通过 / 0 失败**（740 原有 + 18 新增）。
### fix-5 补遗：桌面壳打包的 runtimeconfig 覆盖事故（端到端实测抓出）

首轮「把 Desktop 与 Host 发布到同一目录」的打包方式被端到端测试当场抓住：
桌面壳的 publish 产物把自包含宿主的 `*.runtimeconfig.json` 覆盖成「依赖框架」版，
宿主从此去 `DOTNET_ROOT` 找运行时 —— 找到 8.0 就报「必须安装 .NET 10」。
事件日志（.NET Runtime id=1023）给出实锤。

修复：
1. 桌面壳改为**独立目录 + 自包含**发布（`build\pack\desktop` → 载荷内 `host\desktop\`），
   与宿主互不覆盖；目标机不需要 .NET、不受 `DOTNET_ROOT` 污染。
2. 启动器桌面壳查找覆盖三层布局（`host\desktop\` / `desktop\` / `host\`）。
3. 窗口进程 6 秒内非零退出 → 启动器自动回退打开浏览器（界面入口永不落空）。

最终端到端（干净目录模拟目标机）：启动器 3.5s 起页 → 解压（host + 自包含 desktop + 插件）→
宿主 5.9s HTTP 200 → **原生窗口自动弹出**（进程存活确认）。产物 155.9 MB。
