# v3.8 · 基石插件（把 agent 缺的基础能力补齐）

本次把 agent 缺失的基础能力做成**随包分发的基石插件**（A 通道 · C# 程序集插件），
而不是塞进宿主源码：**能当插件做的，就不进内核**。

起因是一句话：「你看看这个 agent 现在啥功能都没有，你帮我补全成为基石插件。」
当时的实情：19 个官方工具里，编辑能力**只有整篇覆盖**（改一行要重发整个文件），
想找一处代码只能 `run_command` 拼各平台不一样的 `findstr` / `grep`。

---

## 一、新增两条契约 seam

| 契约 | 位置 | 用途 |
|------|------|------|
| `IWorkspaceService` | `src/AgentFramework.Contracts/Abstractions.cs` | 插件唯一的文件正门：`Root` / 读写上限 / `TryResolve(path, forWrite)` / `Shrink` |
| `PluginManifest.Ui` | `src/AgentFramework.Contracts/Events.cs` | 插件的界面贡献声明（`ui.styles` / `ui.scripts`） |

为什么要 `IWorkspaceService`：插件跑在独立 ALC 里，**只有契约程序集是共享的** ——
宿主内部的 `ToolkitOptions` / 路径检查器跨不过去。给一个契约接口，
而不是让每个插件自己重造路径检查 —— 否则边界纪律会出现 N 个版本，迟早有一个忘了防符号链接。

实现 `src/AgentFramework.Tools/WorkspaceService.cs` 直接复用宿主同一份 `WorkspacePath.TryResolve`；
宿主 `ToolModule` 在 `official-tools` 作用域里 `Provide` 出去。

---

## 二、宿主侧：主界面可被插件注入

- **`src/AgentFramework.Host/PluginUi.cs`（新）**：按 id 定位插件目录、读清单里的 `ui` 声明、
  **只服务声明过的文件**（含路径穿越防护与 content-type 表）
- **`WebUiPage.WithPluginUi`（新）**：样式注入 `</head>` 之前、脚本注入 `</body>` 之前，按插件 id 稳定排序
- **`WebUiServer.Routes.Session.cs`**：首页做注入；`/api/plugins` 带上 `ui` 声明；
  新增 `GET /plugin-ui?id=<插件>&file=<文件>`

为什么需要它：`panel.html` 是 iframe，**改不了主界面** —— 换肤、调阅读密度这类需求没有别的正当出口。

---

## 三、三个基石插件（`src/AgentFramework.Plugins.*`）

### devkit · 编程扩展工具包（7 工具）

`edit_file` · `grep_files` · `find_files` · `read_lines` · `make_dir` · `move_path` · `delete_path`

- **`edit_file`**：以「旧串唯一匹配」为锚点；**多处匹配时拒绝执行**（报出现次数与首次行号），
  而不是猜第一处；`replace_all` 可选；**换行符宽容** —— 模型用 LF 写的 `old_string` 能改 CRLF 文件，
  且**不改动任何没碰过的字节**（只归一搜索串，替换仍发生在原文上）
- **`grep_files`**：默认按字面量搜（`regex=true` 才当正则，带 2 秒超时防回溯爆炸）；
  `include` 做文件名 glob 过滤；`context_lines` 带上下文；默认跳过 `.git` / `node_modules` / `bin` / `obj`
  等目录与超大、二进制文件
- **`find_files`**：glob 查找（`*` 段内 / `**` 跨段 / `?` 单字符；不带 `/` 的模式按「任意目录下」语义）
- **`read_lines`**：带行号读一段 —— 配合 grep 报出的行号做局部精读
- **`delete_path`**：删非空目录必须显式 `recursive=true`；**拒删工作区根**（门槛是结构性的，不靠自觉）

### writing-kit · 写作扩展工具包（5 工具）

`word_count`（汉字/英文词/数字分列）· `paragraph_report`（段落节奏体检）·
`repeat_words`（2–4 字窗口的口头禅检测）· `check_punctuation`（中英标点混用 / 重复标点 / 汉字间空格）·
`outline`（逐段首句拼骨架）。均支持 `text` 直接给，或 `path` 读工作区文件。

这些是「模型最不擅长自测」的那一类：字数、重复、标点 —— 定为确定性计数器，不靠目测。

### console-kit · 基础 UI 美化控制台（0 工具）

`theme.css`（4 套色板：深色 / 浅色 / 墨黑 / 护眼绿 + 阅读模式 + 滚动条/选区/焦点环细节）·
`theme.js`（localStorage 偏好 → `html` 上的 data 属性）· `panel.html`（控制台面板）。

一个工具都不注册 —— 它证明契约的另一半：插件不只会给 agent 加能力，也能给人改界面。

---

## 四、接线

| 位置 | 改动 |
|------|------|
| `AgentFramework.sln` | +3 个插件工程 +1 个验证工程 |
| `run.cmd` | 构建后把三个插件的 Debug 产物同步到 `plugins\`（否则开发态「插件写好了但界面上一个都没有」） |
| `pack-launcher.cmd` | Release 发布三个插件到 `%HOST_OUT%\plugins\<id>`，随包分发 |
| `verify-all.cmd` / `tools/run-verify.sh` | 加入 `VerifyBaseKit`；预期 **821 通过 / 0 失败** |

---

## 五、验证

`tests/AgentFramework.VerifyBaseKit` **41 项**（端到端，不需要 API key / 联网 / 模型）：

- seam：宿主确实向内核提供了 `IWorkspaceService`
- 三个插件全部装载成功；devkit 7 工具、writing-kit 5 工具、console-kit 0 工具，来源可追溯
- 编辑：精确替换 / **多处匹配被拒** / `replace_all` / **CRLF 宽容且字节不变** / **越界写被拒且原文件未动**
- 检索与搬移：grep（字面量 + 正则 + include 过滤）、find glob、read_lines 行号、
  move 越界拒绝、delete 非空目录拒绝 + recursive 生效 + **拒删工作区根**
- 写作：汉字数 27、段落数 2、重复词「小说 ×3」、标点混用与重复标点、提纲段数、path 读取
- UI 注入（**真实 HTTP**）：`ui` 声明被读到 / 未声明文件拒取 / `..` 拒取 /
  **首页真被注入 link 与 script** / `/plugin-ui` 取到样式 / 未声明文件 404 / 面板仍可用
- 生命周期：卸载 devkit → 它的 7 个工具立刻消失，官方工具与兄弟插件不受影响

**全量回归：14 个验证工程 821 项 0 失败。**
