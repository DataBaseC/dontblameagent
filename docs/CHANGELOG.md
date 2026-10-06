# 变更与计划文档索引

| 文档 | 内容 |
|------|------|
| [history/CHANGES-v3.30-file-hints.md](history/CHANGES-v3.30-file-hints.md) | **文件族工具统一口径**：抽出 `FileHints`（单一来源），把「失败必须给下一步」落到 `read_file` / `write_file` / `list_dir` / `read_lines` / `grep_files` / `find_files` / `make_dir` / `move_path` / `delete_path` —— 文件不存在给「find_files 找 / list_dir 看」、内容过长给「分两次写」、正则写错给「去掉 regex=true」、越界给「改用工作区内路径」 |
| [history/CHANGES-v3.29-command-hints-unified.md](history/CHANGES-v3.29-command-hints-unified.md) | **命令类工具统一口径**：把 v3.28 的约定推广到 `shell` / `job` / `edit_file` —— 抽出 `CommandHints`（「退出码 → 自救」单一来源）；`shell` 非零退出改判「跑完了」+ `[hint]` + 超时三步自救；`job` id 打错给 `action=list` 出路；`edit_file` 失败指路（**多处匹配列出每一处行号**） |
| [history/CHANGES-v3.28-command-ergonomics.md](history/CHANGES-v3.28-command-ergonomics.md) | **`run_command` 调用体验打磨**：退出码非零不再算工具失败（从前被套 `ERROR:` 前缀，模型以为工具坏了）+ 补 `timeout` / `description` 参数 + 非零退出给一行「怎么自救」（127/126/9009）+ 超时提示三步自救 + 描述写明与 `shell` / `job` 的分工 |
| [GAP-vs-dsh.md](GAP-vs-dsh.md) | **与 dsh 的能力差距对账（v3.27 现状）**：按 dsh `0.2.0-rc.2` 的 74 个子包逐项比对 —— 编码 agent 骨架已对齐（含 v3.20~v3.26 补上的 8 项），差在「自动化与集成长尾」与「隔离强度」；附建议优先级 |
| [history/CHANGES-v3.27-outsourcing-closeout.md](history/CHANGES-v3.27-outsourcing-closeout.md) | **外包任务书收口**：八项逐项对账（全 ✅）+ 两处补强 —— ask_user **前端端到端**（`tools/ask-user-e2e.js`，jsdom 11 项：弹卡 / 选项 / 自由输入 / 失败文案 / 不串会话）+ 冻结段**字节哈希诊断**（`HostOptions.CacheDiagnostics` 默认关，`/api/context.cache` 暴露） |
| [history/CHANGES-v3.26-tool-search.md](history/CHANGES-v3.26-tool-search.md) | **Tool Search**：延迟包里的**单件**工具按需拉起（`tool_search` 检索 → 完整定义进下一轮 · `list` 索引 · `reset` 卸下）；拉起是会话级粘性、只豁免「包」这道闸；对齐 Anthropic `defer_loading` 与 Claude Code `ToolSearchTool` |
| [history/CHANGES-v3.25-subagent-finish-bugfix.md](history/CHANGES-v3.25-subagent-finish-bugfix.md) | **子 Agent 收尾**（抢占式中断 / 派生深度闸 / 界面面板）+ **一轮主动 bug 排查**：并发回报致事件表乱序、子会话混进会话列表、关停不设退休（P1-2）、父会话检查缺失、宿主关停不叫停后台作业、子 Agent 异常无结局等 10 处 |
| [history/CHANGES-v3.24-subagent-control.md](history/CHANGES-v3.24-subagent-control.md) | **子 Agent 管控面**：后台并行派发（`run_in_background`）+ `subagent` 工具（名单 / 状态 / 取结果 / 追加指令 / 打断 / 等待）+ `GET /api/subagents` |
| [history/CHANGES-v3.23-tool-risk-rebuild.md](history/CHANGES-v3.23-tool-risk-rebuild.md) | **审批按「工具自报的风险」判定**（`IToolWithRisk`/`ToolRisk`，删三份硬编码名单与幽灵 `copy_path`）+ **路由 fail-open**（规则指错不打挂整轮 + 判据改 token 估算）+ **重建 rebuild**（checkpoint 当种子开新窗口） |
| [history/CHANGES-v3.22-checkpoint-mcp-shell.md](history/CHANGES-v3.22-checkpoint-mcp-shell.md) | **checkpoint 真接入**（写盘不动上下文，兑现挂了三个版本的契约）+ **MCP 资源面**（resources/list·read）+ **按调用选 shell**（白名单拒绝任意路径）+ **原子写** + **Goal 验证器格式纠正** + **bwrap 网络档统一** |
| [history/CHANGES-v3.21-computer-use.md](history/CHANGES-v3.21-computer-use.md) | **电脑操作插件（Computer Use）**：13 个 GUI 工具（截屏注入视觉 + 鼠标/键盘注入 + 窗口/应用操作），平台驱动可替换，缺依赖如实报不可用 |
| [history/CHANGES-v3.20-sandbox-shell.md](history/CHANGES-v3.20-sandbox-shell.md) | **容器沙箱后端（Docker/Podman）** + **完整 bash 工具**（`shell` 的 description / timeout / run_in_background） |
| [history/CHANGES-v3.19-ui-start-screen.md](history/CHANGES-v3.19-ui-start-screen.md) | **UI：启动不自动开任务窗口**（开始屏 + 会话随第一条消息创建）+ 交互打磨 |
| [history/CHANGES-v3.18-evolution.md](history/CHANGES-v3.18-evolution.md) | **Dream / Distill**：记忆周期整理 + 从历史固化技能草稿（进化支柱四） |
| [history/CHANGES-v3.17-invariants-permissions.md](history/CHANGES-v3.17-invariants-permissions.md) | **INV-C1 压缩产物再入禁止** + 权限输入级匹配与 external_directory 硬闸 |
| [history/CHANGES-v3.16-plan-mode.md](history/CHANGES-v3.16-plan-mode.md) | **Plan 模式**：权限层只读规划 + 计划文件持久化重注入（MiMo plan 档） |
| [history/CHANGES-v3.15-checkpoint-writer.md](history/CHANGES-v3.15-checkpoint-writer.md) | **checkpoint-writer**：后台记忆提取 + 结构化检查点（MiMo writer 子代理） |
| [history/CHANGES-v3.14-goal-verifier.md](history/CHANGES-v3.14-goal-verifier.md) | **Goal 终止验证器**：防提前收工（MiMo Goal 机制落地） |
| [history/CHANGES-v3.13-long-horizon.md](history/CHANGES-v3.13-long-horizon.md) | **长程任务修复批**：自动停止根因修复 + 上下文压缩改造（MiMo Code 机制落地） |
| [PLAN-mimocode-adoption.md](PLAN-mimocode-adoption.md) | MiMo Code 调研与借鉴路线（Goal 验证器 / checkpoint / 权限 / Dynamic Workflow） |
| [history/CHANGES-v3.12-hardening.md](history/CHANGES-v3.12-hardening.md) | **安全与正确性加固**（外部代码审查后的 Critical/P0 清理） |
| [history/CHANGES-v3.11-memory-plan.md](history/CHANGES-v3.11-memory-plan.md) | 记忆与进化：checkpoint 契约先行 |
| [history/CHANGES-v3.10-toolsets.md](history/CHANGES-v3.10-toolsets.md) | 工具包与暴露面解耦 |
| [history/CHANGES-v3.9-sandbox.md](history/CHANGES-v3.9-sandbox.md) | 命令沙箱（process / job） |
| [history/CHANGES-v3.8-basekit.md](history/CHANGES-v3.8-basekit.md) | 基石插件三件套 + `IWorkspaceService` |
| [history/CHANGES-v3.7-plugins.md](history/CHANGES-v3.7-plugins.md) | 脚本插件与自我升级 |
| [history/CHANGES-v3.6-feature.md](history/CHANGES-v3.6-feature.md) | 功能增强批 |
| [history/CHANGES-v3.5-fix.md](history/CHANGES-v3.5-fix.md) | v3.5 修复批 |
| [history/BATCH-v3.4.md](history/BATCH-v3.4.md) | v3.4 批次记录 |
| [history/BATCH-v3.3.md](history/BATCH-v3.3.md) | v3.3 批次记录 |
| [PLAN-memory-evolution.md](PLAN-memory-evolution.md) | 记忆与进化路线图 |
| [PLAN-toolset-exposure.md](PLAN-toolset-exposure.md) | 工具包暴露面设计 |
| [PLUGIN-SDK.md](PLUGIN-SDK.md) | 脚本插件开发文档 |

> 阅读顺序建议：README（铁律与切片）→ PLAN-*（下一步）→ history/CHANGES-*（怎么演进到今天）。
> 历史 CHANGES 里的检查数字是当时快照，**不要**拿它对今天的 `verify-all` 输出。

> **降档判据修订**：记忆降档由「30 天时间线」改为「**调用频率 × 半衰期衰减**」——
> 越用越新、老而常用永不掉线；并给「本轮检索命中」补上隐式使用信号。
> 详见 [PLAN-memory-evolution.md](PLAN-memory-evolution.md) 第 6 节。
