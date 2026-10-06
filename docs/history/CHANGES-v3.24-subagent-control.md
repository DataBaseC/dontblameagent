# v3.24 · 子 Agent 管控面（并行 / 追加指令 / 打断）

> 主题：把 v3.4 就装上的「子 Agent」从**同步一次性派发**升成**可管控的并行委派**
> （对齐 dsh 的 `dsh-tool-subagent` + `dsh-tool-subagent-control`）。
> 全量 **20 套验证工程 0 失败**（1268 项）。

---

## 0. 一句话

从前派子 Agent 只能**干等**（`spawn_subagent` 同步跑完才回 500 字摘要）——
不能并行、不能中途追加指令、不能叫停。而真实长任务正是「几块活同时推 + 谁出岔子停谁」。
这一版补上：后台派发 · 名单 · 状态 · 取结果 · **追加指令** · **打断** · 等待。

---

## 1. 做了什么

| 层 | 落点 | 说明 |
|---|---|---|
| 契约 | `Contracts/SubAgents.cs` | `ISubAgentControl`（派发 / 名单 / 状态 / 取结果 / 追加 / 打断 / 等待 / 停全部）+ `SubAgentInfo`（只读快照）+ `SubAgentStates` 稳定字面量 |
| 宿主 | `Host/SubAgentRegistry.cs`（新） | 名单与生命周期。**运行期状态、不进事件流**（它回答「此刻谁在跑」，事件流记的是历史 —— 与 `JobManager` 同一取舍）。内含子 Agent 主循环 |
| 宿主 | `Host/SubAgentRunner.cs` | 瘦成两样：**子 Agent 的系统提示词**（委派语义一处定义）+ 前台派发的薄壳（`SpawnAsync(background:false)`）|
| 宿主 | `AgentHost` | `SubAgents` 属性（界面、工具、外部看到**同一份名单**，不双轨）；`DisposeAsync` **先 StopAll 再收会话**（否则在跑的子 Agent 会撞上已 Dispose 的回合闸）|
| 工具 | `spawn_subagent` | 新增 `run_in_background`：true = 立刻返回句柄不阻塞；false = 老行为（等到跑完）|
| 工具 | `subagent`（新） | 一个工具多动作（与 `job` 同一手法）：`list` / `status` / `result` / `wait` / `message` / `stop` |
| HTTP | `GET /api/subagents` | 界面/外部查同一份名单（可按 `sessionId` 过滤）|

---

## 2. 几处刻意的设计

- **「追加指令」的语义做得很小**：它只是**下一个回合的输入**。
  子 Agent 跑完当前回合时检查一次收件箱，非空就带着新指令再跑一轮。
  于是不必引入抢占式中断（那是另一件更复杂的事），而「人机协作式的追加」已经够用。
- **前台 / 后台共用一条实现**：`SpawnAsync` 总是先起后台任务；前台只是 `await` 它的 `Work`。
  只有一份编排逻辑，不会出现「前台路径」与「后台路径」行为漂移。
- **父回合取消 → 子 Agent 一起停**（前台路径注册 `ct.Register(Cancel)`）：
  它不该比派它的人活得更久。
- **留痕照旧**：派发 / 完成都进父会话事件流（`SubAgentDispatchedEvent` / `SubAgentCompletedEvent`）——
  「模型可见即已记录」。异步完成时向父会话写事件是安全的：日志 `Append` 自带锁，事件顺序按 Seq 投影。
- **接口而非直接引用**：工具只持有 `ISubAgentControl`，编排住在宿主（Tools 工程不引用 Host，分层不倒挂）。

---

## 3. 未做（如实记账）

- **抢占式中断**：现在的 `message` 是「下一轮接上」，不是「立刻把当前这轮掰过来」。
- **派生深度闸**：子 Agent 自己也能再派，没有显式层数上限。
- **界面面板**：只给了 `GET /api/subagents`，Web UI 里还没有可视化的名单视图。
- 其余见 README「仍未完成」。

---

## 4. 验证与回归

- 全量构建：**0 错误**。全量验证：**20 套 / 1268 项 / 0 失败**（VerifyHost 107 → **121**）。
- 新增断言用「会卡住的假模型」（`GatedLlmClient`：每次调用等一次 `Release`）把**时序**行为变成确定可断言的：
  - 后台派发**立刻**返回句柄（不阻塞）；名单只列本会话派出的；
  - 追加一条指令 → **真的带来第二轮**（`Rounds >= 2`，不是被丢弃）；对已结束/不存在的如实失败；
  - 对运行中的子 Agent 请求打断 → 状态**如实**变 `stopped`；
  - `wait` 对已结束的立刻返回状态描述；
  - 父会话事件流里派发 / 完成各一条以上；
  - 工具面：`spawn_subagent` 的 schema 暴露 `run_in_background`、`subagent list` 列得出、未知 action 如实报错。
