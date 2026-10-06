# v3.21 —— 电脑操作插件（Computer Use）

> 背景：有些软件**没有 CLI，也没有 API**（剪辑、设计、聊天工具、老式内部系统）。
> 要接管它们，唯一的路是像人一样：**看屏幕、点按钮、打字**。
> 本批照 dsh-orb / computer-control 的能力面，把这条路铺进 dba —— 作为一个基石插件。

## 一、形态决策：能力面，不是悬浮球

dsh-orb 是一个 **Electron 悬浮球 + Computer Use + 双轨（前台 GUI / 后台会话）** 的完整产品。
我们的映射是**取其能力面**：

| dsh-orb 的做法 | dba 的落地 |
|---|---|
| 悬浮球常驻屏幕、面板里对话 | **不做** —— dba 的入口仍是 Web UI / 桌面壳（界面形态不同） |
| Computer Use：13 个 GUI 工具 | **照做**：13 个工具，逐一对齐 |
| 后台 `code_agent` 派发 | 复用既有 `spawn_subagent`（不重造） |
| Helper Electron + 回环 socket | **不需要** —— dba 宿主本身就是本机进程，直接调用本机 API |
| 「安装即同意门」 | **改成过宿主分级审批**（默认未知工具 = 询问），保留逐次确认的可能 |

## 二、13 个工具

`screenshot`（截图注入视觉上下文，`region` 可局部）、`screen_size`、`click`、`move`、`drag`、
`scroll`、`type`（Unicode，不依赖键盘布局）、`key`（组合键）、`wait`、
`list_windows`、`focus_window`、`open_app`、`open_url`。

**核心用法是「截图驱动闭环」**：看 → 动 → 再看。坐标永远基于刚看到的那张图。

## 三、平台驱动（可替换的后端）

- **Windows**（`user32` + GDI）：`SendInput` 注入鼠标 / 键盘；Unicode 文本用 `KEYEVENTF_UNICODE`（与键盘布局无关，
  中文 / emoji 直通）；`EnumWindows` 枚举窗口；截屏走 GDI+ `CopyFromScreen`（主屏）。
- **macOS**：`screencapture` 截屏；`osascript`（System Events）键盘；指针要 `cliclick`。
- **Linux(X11)**：`xdotool` 输入；`import` / `gnome-screenshot` / `scrot` 截屏；`wmctrl` 窗口。
- **不支持 / 缺依赖**：退化成 `UnsupportedDesktopDriver` —— 每个调用都**如实报「不可用」**，
  绝不假装点到（那会让模型的下一步建立在假前提上）。

驱动按平台 / 依赖**自报能力**（`DriverCapabilities`），工具据此给出可读的失败原因。

## 四、安全与失败纪律

- **安全交给审批链**：插件不判「危险性」，每个调用都过宿主的**分级审批**。默认策略下未知工具 = 询问，
  于是「点哪里、打什么字」天然要人确认（可配 `yolo` 全托管）。
- **失败不炸回合**：驱动一律 `throw DesktopException`；工具基类是**模板方法** ——
  `InvokeAsync` 在**最外层**包 try，把「参数校验抛的错」与「驱动操作抛的错」一起接住，转成 `ToolResult.Fail`。
  > 这个模板方法是验证逼出来的：最初 try 只包了驱动调用那一句，`click` 缺 `x` 时异常直接逃出去**炸掉整个进程** ——
  > `VerifyComputerUse` 第一条失败路径用例当场抓住。**「同一语义只许一处实现」在这里救了一次**。
- **参数校验前置**：缺参 / 非法鼠标键 / 非法区域格式 / 空文本，一律在碰驱动之前就拒掉。

## 五、验证

`tests/AgentFramework.VerifyComputerUse` **30 项**，不碰真实桌面：

- **键名解析**（纯逻辑，最易错）：`ctrl+c` / `ctrl+shift+T` / `enter→Return` / `cmd→Win` / `f5→F5` / 空与未知键抛错；
- **驱动能力自述**：工厂在当前平台总能给出驱动（不支持也不为空）；兜底驱动操作抛异常；
- **工具失败路径**：不支持平台下 `click` / `screenshot` / `key` 返回失败且原因可读；
- **参数校验**：缺参 / 非法键 / 非法区域 / 空滚动 / 空文本一律拒绝；
- **工具契约完整性**：13 个工具、名字唯一、schema 合法、描述非空。

## 六、边界（如实说明）

- **本沙盒无图形会话**，驱动只在「参数拼装 / 失败路径 / 纯逻辑」层验证 —— **真实点击 / 截屏未在此环境验证**，
  需在 Windows / macOS / Linux 桌面上实测。
- 只做**前台可见操作**：无悬浮球 UI、无后台会话派发、无截屏观察边框。
- Windows 的 **UAC 提升窗口**点不进、打不了字（UIPI 限制），如实报错；截屏只覆盖主屏。
- macOS 需授予「屏幕录制 + 辅助功能」权限；Linux 走 X11（Wayland 下 `xdotool` 多数失效）。
