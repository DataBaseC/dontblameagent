# v3.3 功能批次清单（一次写完，统一 debug）

> 按主人的工作习惯：这批功能全部落地后统一编译 debug。
> 来源：三轮审查 + 功能盘点，挑"地基已就绪、实现成本低、实测立刻用得上"的。

## 本批落地（8 项）

| # | 功能 | 落点 | 状态 |
|---|------|------|------|
| F1 | 启动器 Origin 校验（与 WebUI 对齐，补我自己昨天引入的缺口） | LauncherServer | ✅ |
| F2 | HostProcess 竞态收口（launch/退出回调并发） | LauncherServer + lock | ✅ |
| F3 | **会话分叉 UI 化**（架构早就有 SessionForker，一直没有入口） | WebUI /api/sessions/fork + 页面按钮 | ✅ |
| F4 | **会话重命名**（列表里全是"首句前 28 字"，没法管理） | profile 外的 sessions.json + 端点 + 行内编辑 | ✅ |
| F5 | search_history 会话感知（修复：子 agent/多会话下它永远查"装配时那个会话"） | ToolModule 闭包改读当前会话 | ✅ |
| F6 | **Launcher debug 窗增强**：最近事件流时间线 + 宿主 stdout 尾部 | LauncherServer /debug | ✅ |
| F7 | **WebUI /debug 加最近 8 条事件**（实测事件顺序问题不用翻 JSONL） | RenderDebugPage | ✅ |
| F8 | **导出会话为 Markdown**（存档系统的"分享"出口；实测时贴报告给他人看） | /api/sessions/export | ✅ |

## 下一批候选（不在本批，写下来防丢）

- 子 agent 编排层（OpenSession 已就绪，缺父会话"派生了谁+结果摘要"规范）
- 技能系统（运行期挂卸工具已支持，缺 preset 文件格式与加载器）
- 计划模式（plan 事件 + 计划审批）
- 设计对话模式（Preset 机制，成本最低）
- Launcher 拖拽排序（原生 JS ~30 行；↑↓ 按钮已够用，实测后决定）
- 会话句柄 LRU（会话数大之前不做）
- 导出含事件回放的 HTML（比 Markdown 重，等真实需求）

## 本批刻意不做

- 内核装配语义改动（三轮零新问题，不碰）
- FTS5 / 数据库化（LIKE 够用）
- 极热更（设计已定：重启式 + 现场恢复）
