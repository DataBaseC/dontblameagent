# v3.27 · 外包任务书收口（ask_user 端到端 + 缓存诊断）

> 主题：把 [`PLAN-outsourcing.md`](../PLAN-outsourcing.md) 的八项**逐项对账**，
> 并补上两处此前只有「单测通过」、没有「真实链路证据」的缺口。
> 全量 **20 套 / 1299 项 / 0 失败**。

---

## 一、为什么要收口

八项到 v3.26 都已落地，但有两处仍只有「单测通过」这一个证据：

- **任务 7**：`VerifyWeb` 验的是**后端**广播了 `type:"ask-user"` 帧、`POST /api/ask-user` 受理 ——
  前端 JS 有没有真的弹卡、真的把答案发回去，C# 测试看不见（当年用户「实测失败」正是这一层）。
- **任务 8**：验收里点名的「稳定段字节哈希诊断（开发开关，默认关）」一直没做。

## 二、任务 7 · ask_user 端到端（补上 C# 看不见的那一环）

新增 `tools/ask-user-e2e.js`：把 `src/AgentFramework.Host/Web/index.html` 的内嵌脚本放进
**jsdom** 真跑（`fetch` / `EventSource` 打桩），11 项断言：

1. 页面加载后建立 SSE 连接；
2. 外层 `type:"ask-user"` 帧 → **弹出**提问卡片；
3. 卡片标题「💬 模型在问你」；
4. 问题与上下文都在卡上；
5. 给了 `options` → 渲染成按钮，且**不挡自由作答**（输入框仍在）；
6. 点选项 → `POST /api/ask-user`（带 `id` 与答案）；
7. 提交后卡片收口（按钮禁用、状态如实）；
8. 自由输入 → `POST /api/ask-user`（带原文）；
9. **提交失败给出可诊断文案**（`✗ 等待回答超时`，不是静默）；
10. **别处会话的提问不在本页弹卡**（不串会话）；
11. 老帧（不带 `sessionId`）按当前会话处理，照常弹卡。

**结论**：前端链路全绿。当年「实测失败」的根因 —— 审批/提问帧从前只在 `renderEvent` 里认、
外层帧永远走不到那一支 —— 确已修掉，现在还有自动化守着。

运行：`npm i jsdom && node tools/ask-user-e2e.js`（退 0 = 全过；也可用 `JSDOM_PATH` 指向已装的 jsdom）。

## 三、任务 8 · 冻结段字节哈希诊断（默认关）

- 新增 `HostOptions.CacheDiagnostics`（**默认 false**）。
- 每次装配上下文对**冻结段**（缓存前缀那一段）取 SHA256 前 16 位；
  同一会话与上一次不一致 → 变化计数 +1，并打一行
  `[cache] 冻结段哈希变化（会话 X）：old → new —— 前缀必 miss`。
  「为什么 `cached_tokens` 偏低」因此从**猜**变成**看**。
- `/api/context` 新增 `cache` 段：`diagnosticsEnabled` / `frozenBlockChanges` / `frozenBlockHash`。
- **为什么默认关**：诊断只为查缓存命中率服务，不该给所有会话白加一次 SHA256（零成本优先）。

断言（`VerifyMemory` 165 → **169**）：

| 断言 | 语义 |
|---|---|
| 默认关不记哈希 | 开关关时 `FrozenBlockHashOf` 为 null、计数 0（零成本） |
| 开了就有 16 位哈希 | 可度量 |
| 状态不变 → 哈希不变、计数 0 | 前缀稳定（缓存可命中） |
| 记忆真变化 → 哈希变、计数 +1 | 该失效时如实记一笔 |

## 四、验证

- 全量构建 **0 错误**；全量验证 **20 套 / 1299 项 / 0 失败**（VerifyMemory 165 → 169）。
- `tools/ask-user-e2e.js` **11 / 0**。

## 五、对账

八项的落点与证据见 [`PLAN-outsourcing.md`](../PLAN-outsourcing.md) 顶部新增的
「实施状态（2026-10 对账）」表 —— **八项全部 ✅**。

## 六、仍未做

- **MCP prompts / sampling**、HTTP / SSE 传输。
- **长尾集成**：定时调度 / Webhook / ACP / Claude Code·Codex Hooks 兼容。
- **Tool Search 检索升级**：关键词 AND → BM25 / 向量（工具上百且描述相近时）。
- **子 Agent 会话日志**：`sessions/` 里的 `sub-*.jsonl` 仍不自动清理。
- **任务 8 的实测数字对比（改前 vs 改后）**：需真实模型端点，离线环境无法产出；
  口径已写清（压缩导致的 miss 是**预期** miss，与字节抖动要分开看）。
