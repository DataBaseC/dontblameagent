# CHANGES-v3.14 · Goal 终止验证器（防提前收工）

> MiMo Code 长程机制落地的第二步（第一步是 v3.13 的自动停止/压缩修复批）。
> 一句话：模型每次想收尾，先由**旁路验证者**独立裁决「目标真的达成了吗」——
> 未达成就把具体差距反馈回去继续干；确认做不到就如实收尾。
> 这是「自动停止」问题的另一面：不只是「不该停时停了」，还有「没干完就说干完了」。

## 1. 机制（对照 MiMo Code Goal / Claude Code /goal）

| 设计点 | 落地 |
|--------|------|
| 停止条件是自然语言可测终态 | `goal` 配置字段 / `--goal` 命令行 / `POST /api/goal`；如「add.py 运行输出 5 且 RESULT.md 存在」 |
| 独立裁决，无认同偏差 | `IGoalVerifier` 旁路模型调用（与摘要器共用「local 优先、回退 active」解析）；只看证据，不看自信 |
| 未达成 → 差距反馈继续 | 裁决 NotMet 时把 GAP 注入「【目标核验·非用户发言】」提示，回合继续；最多纠偏 3 次 |
| 确认做不到 → 如实收尾 | 裁决 Impossible 时报 `goal-impossible`，障碍写进最终答复，不空转 |
| 验证器绝不卡住收尾 | fail-open：超时（60s）/ 出错 / 解析不出裁决一律放行；用户停止照常上抛 |
| 不变成死循环 | 纠偏次数上限（`GoalMaxVerifications=3`）+ 步数预算双保险 |

## 2. 代码改动清单

| 文件 | 改动 |
|------|------|
| `AgentFramework.Contracts/GoalVerifier.cs` | 新增：`GoalVerdict`（Met/NotMet/Impossible）、`GoalVerification`、`IGoalVerifier` |
| `AgentFramework.Llm/LlmGoalVerifier.cs` | 新增：旁路模型验证器，严格三行输出（VERDICT/EVIDENCE/GAP）+ 容错解析（认不出→放行） |
| `AgentFramework.Agent/AgentRunner.cs` | 收尾分支接入核验：NotMet→差距反馈继续；Impossible→`goal-impossible`；验证器失败/超时 fail-open；新增 `Options` 活引用 |
| `AgentFramework.Host/AgentHost.Goal.cs` | 新增：`Goal` 属性 + `SetGoal()`（对所有打开会话立即生效） |
| `AgentFramework.Host/Hosting/HostModule.cs`、`HostBuilder.cs` | 装配 `GoalVerifier`（与摘要器同款旁路小模型），开会话时注入 `Goal/GoalVerifier` |
| `AgentFramework.Host/HostOptions.cs`、`AgentConfig.cs`、`Program.cs` | `goal` 配置字段、`--goal` 命令行、`GoalVerifierOverride`（测试用） |
| `AgentFramework.Host/WebUiServer.Routes.Chat.cs` | `GET/POST /api/goal`（界面读/设目标） |
| `tests/AgentFramework.VerifyAgent` | 场景 7c–7f：未达成→纠偏→达成、Impossible、fail-open、解析容错（12 项新检查） |

## 3. 用法

```jsonc
// agent.json
{
  "mode": "code",
  "goal": "所有测试通过（verify-all 全绿）且修复记录已写进 docs"   // 不写 = 不启用
}
```

```
dotnet run --project src/AgentFramework.Host -- --allow-command --no-open \
  --goal "fizzbuzz 运行输出正确且 RESULT.md 存在" --prompt "创建 fizzbuzz.py 并运行"
```

- 运行期改：`POST /api/goal {"goal":"..."}`（传空即关闭）。
- 建议把目标写成「可测终态 + 验证方式」（如「xx 命令退出码为 0」），少写主观形容词——
  验证者只认证据，写得越可测，纠偏越准（Claude Code /goal 的同款经验）。

## 4. 验证

- `VerifyAgent` 57 通过 / 0 失败（含 12 项 Goal 新检查：纠偏继续、goal-impossible、fail-open、解析容错）；
  `VerifyHost` 65/0；`VerifyWeb` 167/0。
- **E2E（AMD 免费 API）实测拦截成功**：用户请求只让跑 `add.py` 并运行、
  goal 额外要求「RESULT.md 记录运行结论含数字 5」——
  模型跑完脚本后第 3 步就想收工（"完成。"），验证者裁决 NOT_MET 并给出差距；
  模型随即（seq=13）明确表示"需要创建 RESULT.md"，补写并 read_file 核验后才真正收尾。
  步数 3 → 6，`RESULT.md` 落盘（见 `work/e2e-goal-console.txt`）。

## 5. 兼容性

- 不设 `goal` 时零行为变化（验证器挂着但不被调用）；老配置照常解析。
- `goal-impossible` 是新的 StopReason，UI/脚本按「未完成 + 有理由」处理即可。
