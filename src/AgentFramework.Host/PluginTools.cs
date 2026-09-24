using System.Text;
using System.Text.Json;
using AgentFramework.Contracts;
using AgentFramework.Kernel;

namespace AgentFramework.Host;

/// <summary>
/// 插件自管理工具集 —— <b>agent 给自己长能力的入口（自我升级）</b>。
///
/// <para>
/// 四个动作对应一条完整闭环：
/// <c>plugin_write</c>（写进仓库）→ <c>plugin_reload</c>（换版本 + 装载自测）
/// → 下一轮就能用新工具 → 不想要了就 <c>plugin_uninstall</c>；
/// <c>plugin_list</c> 随时看清现在装了什么。
/// </para>
/// <para>
/// <b>为什么 reload 要"先快照、失败回滚"</b>：agent 写出来的插件第一次大概率是坏的。
/// 若换版本失败后旧版也没了，agent 就把自己的一个能力永久搞掉了 ——
/// 热更新必须能退回去，否则它是一次性的赌。
/// </para>
/// </summary>
public sealed class PluginWriteTool(PluginStore store) : ITool, IToolWithSchema
{
    public string Name => "plugin_write";

    public string Description =>
        "把你的脚本插件写进统一插件仓库（<workspace>/plugins/<id>/）。" +
        "files 是「文件名 → 内容」的 JSON 对象，至少要含 plugin.json 与它声明的脚本文件。" +
        "写完不会自动生效 —— 再用 plugin_reload 装载。已存在同名插件会被整体覆盖（回滚靠装载前记的「上次可用版本」）。" +
        "脚本 API、约定与踩坑见 docs/PLUGIN-SDK.md（仓库根目录）。";

    public string ParametersJsonSchema => """
        {"type":"object","properties":{
          "id":{"type":"string","description":"插件 id：字母数字或 . _ -，作为目录名"},
          "files":{"type":"object","description":"文件名 → 文件内容（键是相对路径，如 plugin.json / main.js）"}
        },"required":["id","files"]}
        """;

    public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        if (!invocation.Arguments.TryGetValue("id", out var id) || string.IsNullOrWhiteSpace(id))
        {
            return ValueTask.FromResult(ToolResult.Fail("缺少参数 id"));
        }

        if (!invocation.Arguments.TryGetValue("files", out var filesRaw) || string.IsNullOrWhiteSpace(filesRaw))
        {
            return ValueTask.FromResult(ToolResult.Fail("缺少参数 files"));
        }

        Dictionary<string, string>? files;
        try
        {
            files = JsonSerializer.Deserialize<Dictionary<string, string>>(
                filesRaw,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            return ValueTask.FromResult(ToolResult.Fail(
                $"files 不是合法的「文件名 → 内容」JSON 对象：{ex.Message}"));
        }

        if (files is null || files.Count == 0)
        {
            return ValueTask.FromResult(ToolResult.Fail("files 不能为空"));
        }

        // 清单必须在，且它声明的脚本文件也必须一起给 —— 否则写进去也是个装不上的壳
        if (!files.TryGetValue("plugin.json", out var manifestJson))
        {
            return ValueTask.FromResult(ToolResult.Fail("files 里必须包含 plugin.json"));
        }

        string? scriptName = null;
        try
        {
            using var doc = JsonDocument.Parse(manifestJson);
            if (doc.RootElement.TryGetProperty("script", out var script)
                && script.ValueKind == JsonValueKind.String)
            {
                scriptName = script.GetString();
            }
        }
        catch (JsonException ex)
        {
            return ValueTask.FromResult(ToolResult.Fail($"plugin.json 不是合法 JSON：{ex.Message}"));
        }

        if (!string.IsNullOrWhiteSpace(scriptName) && !files.ContainsKey(scriptName))
        {
            return ValueTask.FromResult(ToolResult.Fail(
                $"plugin.json 声明了 script={scriptName}，但 files 里没有这个文件"));
        }

        try
        {
            store.WriteFiles(id, files);
        }
        catch (Exception ex)
        {
            return ValueTask.FromResult(ToolResult.Fail($"写入插件仓库失败：{ex.Message}"));
        }

        var list = string.Join("、", files.Keys.OrderBy(k => k, StringComparer.Ordinal));
        return ValueTask.FromResult(ToolResult.Ok(
            $"已写入插件仓库：{Path.Combine(store.Root, id)}（{files.Count} 个文件：{list}）。"
            + "下一步用 plugin_reload 装载它。"));
    }
}

/// <summary>重新装载（热更新）：换版本 + 装载自测 + 失败回滚。</summary>
public sealed class PluginReloadTool(PluginStore store, PluginHost kernel) : ITool, IToolWithSchema
{
    public string Name => "plugin_reload";

    public string Description =>
        "重新装载插件（热更新）：先卸掉旧版，再装仓库里的当前版本，并跑脚本的 selftest() 冒烟。" +
        "装不上会自动回滚到换版本前的快照并重装旧版 —— 不会把你的能力搞丢。" +
        "不带 id 时重装仓库里全部插件。";

    public string ParametersJsonSchema => """
        {"type":"object","properties":{
          "id":{"type":"string","description":"要重装的插件 id；留空 = 重装仓库内全部"}
        }}
        """;

    public async ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        invocation.Arguments.TryGetValue("id", out var id);

        var targets = string.IsNullOrWhiteSpace(id)
            ? store.ListInstalled()
            : [id!];

        if (targets.Count == 0)
        {
            return ToolResult.Ok($"插件仓库里还没有插件（{store.Root}）。先用 plugin_write 写一个。");
        }

        var report = new StringBuilder();

        foreach (var target in targets)
        {
            if (!PluginStore.IsValidId(target))
            {
                report.AppendLine($"✗ {target}：id 非法");
                continue;
            }

            var dir = store.DirectoryFor(target);
            if (!File.Exists(Path.Combine(dir, "plugin.json")))
            {
                report.AppendLine($"✗ {target}：仓库里没有这个插件");
                continue;
            }

            // ★ 回滚目标 = 上一次**真正装载成功**的那一版。
            //   不是"写盘前的文件" —— 坏版本写进仓库时就已覆盖磁盘，写盘前未必是能跑的那版。
            var lastGood = store.GetLastGood(target);

            try
            {
                var handle = await kernel.ReloadAsync(dir, ct).ConfigureAwait(false);
                store.MarkLastGood(target);   // 这次成功了 —— 它就是新的「已知可用」

                report.AppendLine(
                    $"✓ {target}@{handle.Version} 已装载"
                    + $"（工具 {handle.RegisteredTools.Count} 个：{string.Join("、", handle.RegisteredTools)}"
                    + $"；自测：{handle.SelfTestReport ?? "未定义"}）");
            }
            catch (Exception ex)
            {
                report.AppendLine($"✗ {target} 装载失败：{ex.Message}");

                if (lastGood is null)
                {
                    report.AppendLine("  （此前没有装载成功过的版本，无处可回滚；插件停在「未装载」状态）");
                    continue;
                }

                if (!store.TryRestore(target, lastGood))
                {
                    report.AppendLine($"  ⚠ 回滚失败（快照恢复不了：{lastGood}）—— 请人工检查插件仓库");
                    continue;
                }

                try
                {
                    var restored = await kernel.ReloadAsync(dir, ct).ConfigureAwait(false);
                    store.MarkLastGood(target);
                    report.AppendLine(
                        $"  ↩ 已回滚到上一可用版本并重新装载：{target}@{restored.Version}"
                        + $"（工具 {restored.RegisteredTools.Count} 个）");
                }
                catch (Exception rollbackError)
                {
                    report.AppendLine($"  ⚠ 回滚后仍装载失败：{rollbackError.Message}");
                }
            }
        }

        return ToolResult.Ok(report.ToString().TrimEnd());
    }
}

/// <summary>卸掉并删掉一个插件。</summary>
public sealed class PluginUninstallTool(PluginStore store, PluginHost kernel) : ITool, IToolWithSchema
{
    public string Name => "plugin_uninstall";

    public string Description =>
        "卸载并删除一个插件：撤销它注册的工具/钩子，并从插件仓库删掉它的目录。";

    public string ParametersJsonSchema => """
        {"type":"object","properties":{
          "id":{"type":"string","description":"要卸载的插件 id"}
        },"required":["id"]}
        """;

    public async ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        if (!invocation.Arguments.TryGetValue("id", out var id) || string.IsNullOrWhiteSpace(id))
        {
            return ToolResult.Fail("缺少参数 id");
        }

        if (!PluginStore.IsValidId(id))
        {
            return ToolResult.Fail($"插件 id 非法：{id}");
        }

        var unloaded = await kernel.UnloadAsync(id, ct).ConfigureAwait(false);

        bool removed;
        try
        {
            removed = store.Remove(id);
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"已卸载，但删除目录失败：{ex.Message}");
        }

        if (!unloaded && !removed)
        {
            return ToolResult.Fail($"没有这个插件：{id}");
        }

        return ToolResult.Ok(
            $"已卸载 {id}（撤销注册：{(unloaded ? "是" : "它本来没在跑")}；删除目录：{(removed ? "是" : "仓库里本来就没有")}）");
    }
}

/// <summary>看清现在装了什么。</summary>
public sealed class PluginListTool(PluginStore store, PluginHost kernel) : ITool, IToolWithSchema
{
    public string Name => "plugin_list";

    public string Description =>
        "列出当前装着的插件：id、版本、类型（脚本/程序集）、注册的工具、是否在跑、装载自测结果。";

    public string ParametersJsonSchema => """{"type":"object","properties":{}}""";

    public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        var running = kernel.Plugins.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var installed = store.ListInstalled();
        var lines = new List<string>();

        foreach (var id in installed)
        {
            lines.Add(running.TryGetValue(id, out var handle)
                ? $"● {Describe(id, handle)}"
                : $"○ {id}：装在仓库里，但当前未装载（plugin_reload 可装载它）");
        }

        // 不在仓库里的（随包分发的程序集插件）也要能看到
        foreach (var handle in kernel.Plugins.Where(p => !installed.Contains(p.Id, StringComparer.Ordinal)))
        {
            lines.Add($"● {Describe(handle.Id, handle)}（不在自写仓库，随包分发）");
        }

        if (lines.Count == 0)
        {
            return ValueTask.FromResult(ToolResult.Ok("当前没有任何插件。"));
        }

        return ValueTask.FromResult(ToolResult.Ok(string.Join('\n', lines)));
    }

    private string Describe(string id, PluginHandle handle)
    {
        var kind = handle.IsScript ? "脚本" : "程序集";
        var tools = handle.RegisteredTools.Count > 0
            ? string.Join("、", handle.RegisteredTools)
            : "（无）";
        var selfTest = handle.SelfTestReport is null ? "" : $"，自测：{handle.SelfTestReport}";

        return $"{id}@{handle.Version}（{kind}，工具：{tools}{selfTest}）";
    }
}
