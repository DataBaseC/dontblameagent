# CHANGES v3.19 —— UI：启动不自动开任务窗口 + 交互打磨（类 dsh）

> 直接来自用户反馈：**「一开始的时候，不要自动创建一个任务窗口，然后优化人机交互，让用户用得舒适，类似 dsh 这样。」**

---

## 1. 启动 = 开始屏，会话在第一条消息时才诞生

**改前**：打开界面就有一个任务窗口摊在面前（后端默认会话），侧栏还挂着一个「(空会话)」——
用户还没想好要干什么，就先被塞了一个空房间。

**改后**：

| 行为 | 说明 |
|------|------|
| **启动停在开始屏** | 一张白纸 + 一句话开工：问候语、模式 chips、可选工作文件夹、最近任务列表；不自动打开任何任务窗口 |
| **会话随第一条消息创建** | 开始屏输入 → `Enter` → 才调 `/api/sessions/new` 建会话（带模式与工作文件夹）→ 发出消息。**空会话从不存在**，自然不占侧栏 |
| **「＋ 新任务」/ 点标题** | 回开始屏（不再弹「先建空会话」的模式弹层） |
| **空会话不进侧栏** | 0 条消息的会话只是还没开工的壳，不显示；窗口只为「有内容或有回合在跑」的任务而开 |
| **刷新续接** | `localStorage` 记住正在看的任务，刷新直接回到它；点「＋」回到开始屏即清除 |

输入框全程共用一个（底部 composer）：贴图、✨优化、Enter/Shift+Enter 在开始屏照常可用 ——
第一句话就是完整的第一条消息，不丢字、不丢图。

## 2. 交互舒适度（细节处的 dsh 式手感）

- **侧栏时间轴**：`刚刚 / N 分钟前 / 昨天 / N 天前`，一眼排序心智；id 退到悬浮提示；
- **回合收束即刷新侧栏**：新任务的第一句话当场变成标题，条数/时间不过夜；
- **后台可见性**：停在开始屏时，别处跑着的回合会把对应会话标成「● 进行中」，
  等确认的审批也会标忙 —— 不串窗口，但不失踪；
- **模式 chips**：记住上次选择（`af-mode`）；工作文件夹折叠在「高级」里，浏览弹窗（`/api/fs/dirs`）照旧；
- **空窗口兜底**：删光任务后的自动空会话不单开窗口，回开始屏。

## 3. 实现落点

- 前端（单文件 [`Web/index.html`](../../src/AgentFramework.Host/Web/index.html)）：
  `#welcome` 开始屏 + `showWelcome/openWindow/startSend` + `renderSessions` 过滤 +
  `relTime` + `localStorage('af-open-session')` + SSE 帧按 `windowOpen` 分流；
- 后端 **API 契约零改动**（`/api/sessions/new|switch|send|history|status` 原样）——
  改动全部在展示层，服务端会话语义、测试契约不受影响；
- 旧「新建会话」弹层（`mode-pick`）删除，目录浏览（`dir-pick`）并入开始屏。

## 4. 验证

- `node --check` 通过（脚本段语法体检）；
- VerifyWeb **+3 契约检查**（开始屏存在 / 会话延迟创建 / 模式与最近任务），170/0；
- VerifyMemory 两条旧弹层断言更新为新 UI 口径（165/0）；
- 全量 `verify-all`：**19 套件 1143 通过 / 0 失败 / 1 跳过**（v3.18 基线 1140 → +3）。

> 备注：VerifyBaseKit 的插件 DLL 装载在重建后偶发 `0x80070020` 文件锁（套件间/杀软扫描，
> verify-all.cmd 头注释已知现象），单独复跑即绿，非本轮回归。

---

## 5. 补丁：「我的机器看不到思考动画」的三个真凶与修复

用户反馈：同一套包，自己机器上「思考中」动画不显示，别人的机器正常。排查结论：

| # | 真凶 | 机制 | 修复 |
|---|------|------|------|
| 1 | **系统开了「减少动态效果」** | 页面原有 `@media (prefers-reduced-motion: reduce) { animation: none !important; }` 一刀切 —— Windows 设置→辅助功能→视觉效果→动画效果=关 时浏览器上报 reduce，思考点/转圈全部静止，像坏了 | 功能性指示器（思考点/转圈/打字光标/忙点）在 reduce 模式下**保留最小动画**（纯透明度呼吸 + 慢速旋转，去掉位移）；装饰动画照停。另在开始屏明示原因与开关位置 |
| 2 | **浏览器缓存旧页面** | `/` 响应没有 `Cache-Control`，浏览器按「启发式缓存」扣住旧 HTML —— 换了版本还看老界面 | HTML 响应加 `Cache-Control: no-cache`（协商缓存，每次来问一句） |
| 3 | **没配端点（离线演示）秒回** | 离线演示模式回复是瞬时的，思考阶段一闪而过 | 不改行为 —— 属配置问题；顶栏「离线演示（未配端点）」徽标即提示 |

自查口诀：F12 控制台跑 `matchMedia('(prefers-reduced-motion: reduce)').matches`（true = 凶手 1）；
看顶栏模型徽标是否「离线演示」（是 = 凶手 3）；Ctrl+F5 强刷（好了 = 凶手 2）。

验证：VerifyWeb +2（reduce 模式保留状态指示 / 首页协商缓存），172/0；打包件随包交付。

---

## 6. 补丁 2：打开工作目录入口

「文件到底落在哪」一键可见：顶栏常驻 `📁 工作区`（显示文件夹名、悬浮看全路径）+
开始屏「📂 打开当前」。后端 `POST /api/fs/open`：不传 path = 当前会话项目目录 ?? 宿主工作区；
只收已存在的绝对目录（相对 400 / 缺失 404）。VerifyWeb +3（页面入口 + API 拒绝路径），175/0。

## 7. 补丁 3：POSIX-on-Windows 命令沙箱两处缺陷（CI VerifySandbox 失败暴露）

CI（windows-latest 自带 Git Bash）上 VerifySandbox 失败，本机（无 bash、回落 cmd）全绿 ——
差异精确指向**只有「机器上有 Git Bash」才走的 POSIX 分支**。用 PortableGit 在本地把 bash
摆上 PATH 复现后抓到两条真实缺陷（都不是测试问题，是产品承诺失效）：

| # | 缺陷 | 机制（实测） | 修复 |
|---|------|--------------|------|
| 1 | **临时目录重定向静默失效** | MSYS 运行时把 Windows 形式的 `TMPDIR` 改写成 `/tmp` ——「临时文件不出工作区」在 Git Bash 下破功 | `ProcessRunner`：POSIX-on-Windows 时 `TMPDIR` 改给 **POSIX 形式**（`/c/Users/…`，实测照单全收）；`TMP`/`TEMP` 保持 Windows 形式（bash 里唤起的原生 exe 只认它） |
| 2 | **`bash -c` 找不到自家 coreutils** | Git Bash 的 `sleep`/`seq`/`touch` 住在 `<git>\usr\bin`，Windows PATH 上通常只有 `Git\bin`/`Git\cmd` —— `bash -c "sleep 5"` 都 command not found | `ProcessRunner.AugmentPosixPath`：把 shell 自己的家当目录（自身目录 + `..\usr\bin` + `..\bin`）补进子进程 PATH —— Windows 上的 bash 才真的当 bash 用 |

复现矩阵（PortableGit 2.56）：bash + coreutils 可见 → 25/0；真实 GfW 布局（只见 `Git\bin`
的 bash、coreutils 不在 PATH）→ 25/0；无 bash（cmd 回落）→ 25/0。全量回归 19 套件 1148/0 不变。
