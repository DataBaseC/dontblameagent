# 变更与计划文档索引

| 文档 | 内容 |
|------|------|
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
