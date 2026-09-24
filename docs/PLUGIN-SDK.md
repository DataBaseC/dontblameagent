# 脚本插件 SDK（agent 自写插件用）

> 给**写插件的那一方**看的（通常就是 agent 自己）。
> 程序集插件（C#，随包分发的基石插件）见 `src/AgentFramework.SamplePlugin/`。

---

## 1. 一条插件长什么样

```
<workspace>/plugins/<id>/
    plugin.json      ← 清单（必须有）
    main.js          ← 脚本（清单里 script 指过来）
```

`plugin.json`：

```json
{
  "id": "writing-kit",
  "name": "写作扩展工具包",
  "version": "1.0.0",
  "apiVersion": "1",
  "script": "main.js",
  "description": "一句话说明这个插件干嘛的",
  "capabilities": []
}
```

- `id`：字母数字与 `. _ -`，最长 64。**它同时是目录名**，所以不能带路径分隔符。
- `apiVersion`：必须是 `"1"`，与内核不匹配会被拒。
- `script`：脚本文件名。**有它就是脚本插件**（免编译）；换成 `assembly` + `entry` 就是程序集插件。
- `capabilities`：要借哪些已有工具，见第 4 节。**不声明就借不到**。

## 2. 脚本骨架

```js
function activate(ctx) {
  // 在这里注册工具、挂事件钩子。不做清理 —— 卸载时宿主会自动逆序撤销一切。
}

function selftest() {
  // 可选。装载时会自动跑一次：抛异常或返回 false = 自测失败 = 拒绝装载。
  return 'ok';
}
```

**必须**定义 `activate(ctx)`；`selftest()` 可选但强烈建议写（它就是你自己的冒烟测试）。

## 3. `ctx` 有什么

| 成员 | 说明 |
|------|------|
| `ctx.id` / `ctx.version` / `ctx.dataDir` | 自己的身份与私有数据目录 |
| `ctx.log(msg)` | 写日志 |
| `ctx.registerTool({...})` | 注册一个模型可调用的工具 |
| `ctx.on(name, handler)` | 订阅事件（目前支持 `tools/pre-execute`） |
| `ctx.callTool(name, args)` | 借调已有工具，返回 `{success, output, error}` |

### 3.1 注册工具

```js
ctx.registerTool({
  name: 'word_count',                       // 工具名，全局唯一（重名会让装载失败）
  description: '数一段中文的字数（不含空白）',
  parameters: {                             // JSON Schema，直接给模型做 function calling
    type: 'object',
    properties: { text: { type: 'string' } },
    required: ['text']
  },
  run: function (args) {
    return String(args.text || '').replace(/\s/g, '').length + ' 字';
  }
});
```

`run(args)` 的返回值：

- 返回**字符串** → 直接作为工具输出；
- 返回 `{ success, output, error }` → 明确表达成败；
- 返回其它对象 → 自动序列化成 JSON 文本。

`args` 里每个值都是**字符串**（工具参数本来就是 JSON，宿主统一字符串化）。要结构，自己 `JSON.parse(args.xxx)`。

### 3.2 事件钩子：把规则变成"一定会执行"

```js
ctx.on('tools/pre-execute', function (e) {
  // e.toolName / e.arguments
  if (e.toolName === 'run_command' && String(e.arguments.command || '').includes('rm -rf')) {
    return '这条命令不给跑';     // 返回理由字符串 = 拦下
  }
  // 返回 false 也等于拦下；返回其它值 = 放行
});
```

这跟"在提示词里请求模型别乱来"完全不同：**钩子是确定性的**，模型说什么都绕不过它。

## 4. 能力声明（`capabilities`）

脚本**默认碰不到文件系统、网络、进程** —— Jint 不暴露任何 .NET 对象给脚本，
宿主只塞进去第 3 节那几个扁平函数。这是结构性保证，不是"约定你别乱来"。

要干实事只有两条路：① 自己算（转换 / 校验 / 编排）；② 用 `ctx.callTool` 借已有工具。
借谁必须写明：

```json
"capabilities": ["tool:read_file", "tool:write_file"]
```

或 `["tool:*"]` 全放开。**没声明就调用 = 直接抛错。**

## 5. 热更新怎么走

```
plugin_write  →  plugin_reload  →  下一轮模型就能看见新工具
                     ↓ 失败
              自动回滚到「上次装载成功的那一版」
```

- **先卸后装**：同一个 id 在同一时刻只允许一个实例活着。
- **装载即自测**：`selftest()` 不过 = 装载失败。
- **回滚目标是"上次装载成功的那一版"**（不是"上次写盘前的文件"）——
  所以放心大胆改，改坏了会退回去，不会把自己的能力搞丢。
- 不需要重启宿主。

## 6. 几个容易踩的坑

- 工具**重名**会直接让装载失败（别的插件或官方工具已占用同名）。先用 `plugin_list` 看一眼。
- 脚本是**串行执行**的（引擎不是线程安全的）—— 别写长时间阻塞的循环。
- 单次执行有超时与语句数上限（10 秒 / 50 万条语句），死循环会被掐断。
- `plugin_write` 会**整体覆盖**同名插件的目录（不是合并文件），所以每次要把所有文件一起给全。
- `activate` 里注册的一切，卸载时由宿主自动撤销 —— 不要自己写清理逻辑，写了反而多余。

## 7. 一个完整的最小例子

见 `samples/script-plugins/writing-kit/`。

## 8. 另一条通道：C# 程序集插件（基石 / 官方插件）

脚本通道解决「agent 自己给自己加能力」；**随包分发的基石插件走的是程序集通道** ——
它们干的活更重（改文件、跑检索、动界面），而且要**由人审阅后随包分发**。

现成模板：`src/AgentFramework.Plugins.DevKit`（注册工具 + 注入服务）、
`src/AgentFramework.Plugins.WritingKit`（纯计算工具）、
`src/AgentFramework.Plugins.ConsoleKit`（纯界面插件，0 工具）。

三个要点：

1. **只引用契约程序集**：`ProjectReference` 到 `AgentFramework.Contracts`，并设
   `Private=false` + `ExcludeAssets=runtime`（契约不进插件产物）。插件跑在独立 ALC 里，
   宿主内部类型跨不过去 —— 硬引用会得到两个不同的 Type，运行时转换当场失败。
2. **宿主能力靠 `injects` + `ctx.Get<T>()`**：目前提供 `IClockService` 与 `IWorkspaceService`。
   `IWorkspaceService` 是文件活儿唯一的正门（`TryResolve(path, forWrite, ...)` / `Shrink(...)`），
   与官方文件工具**共用同一份边界实现**。
3. **界面贡献写在清单里**：

```json
{ "ui": { "styles": ["theme.css"], "scripts": ["theme.js"] } }
```

宿主把样式注入 `</head>` 之前、脚本注入 `</body>` 之前，并通过
`/plugin-ui?id=<插件>&file=<文件>` 服务它们 —— **只服务清单里声明过的文件**。
`panel.html` 仍然有效（iframe 面板），但它改不了主界面；换肤这类需求要走 `ui.styles`。

`ActivateAsync` 里注册的一切（工具 / 事件订阅 / 服务 / 定时器）都会在卸载时**逆序自动撤销** ——
与脚本插件同一条纪律：不要手写清理逻辑。
