using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentFramework.Contracts;
using AgentFramework.Tools;

namespace AgentFramework.Host;

/// <summary>
/// 技能包校验（任务 2）。
///
/// <para>
/// 为什么单独抽一个类型：技能校验有<b>两个入口</b> —— 界面导入路由
/// （<c>POST /api/skills/import</c>）与 agent 手上的 <c>skill_validate</c> 工具。
/// 规则若各写一份，迟早出现「界面拦下了、agent 放过了」这种不一致。
/// 于是规则只此一处，两边共用。
/// </para>
/// </summary>
public static class SkillValidator
{
    /// <summary>技能名（即目录身份）白名单：与会话 id 同一规则（字母数字与 - _ ，≤64）。</summary>
    public static bool IsValidName(string? name)
        => !string.IsNullOrWhiteSpace(name)
           && name.Length <= 64
           && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>工具白名单里引用了未注册工具的那些（工坊包无法凭空造出新工具）。</summary>
    public static IReadOnlyList<string> UnknownTools(SkillDefinition skill, IEnumerable<string> registered)
    {
        var set = new HashSet<string>(registered, StringComparer.Ordinal);
        return skill.Tools.Where(t => !set.Contains(t)).ToList();
    }

    /// <summary>
    /// 与现有技能重名的那些。按 <b>name</b> 比较 —— 技能的身份是名字，
    /// 目录只是它的落地形式；builtin 优先、workshop 不覆盖，所以重名要显式拒绝而不是隐式覆盖。
    /// </summary>
    public static IReadOnlyList<SkillDefinition> Conflicts(SkillDefinition skill, IEnumerable<SkillDefinition> existing)
        => existing.Where(s => string.Equals(s.Name, skill.Name, StringComparison.Ordinal)).ToList();
}

/// <summary>工具入参读取的小工具（<see cref="ToolInvocation.Arguments"/> 的值统一为字符串）。</summary>
internal static class SkillToolArgs
{
    public static string? Get(ToolInvocation invocation, string key)
        => invocation.Arguments.TryGetValue(key, out var v) ? v : null;

    public static List<string> StringArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return doc.RootElement.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString() ?? string.Empty)
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>
/// 生成技能骨架：<c>id + description → &lt;workspace&gt;/skills/&lt;id&gt;/{skill.json, README.md}</c>。
///
/// <para>
/// 它取代的是「人手写 skill.json」这件事。技能刻意保持<b>纯声明式</b>
/// （工具白名单 + 提示词后缀，不含可执行内容），所以骨架器不需要沙箱就能安全地长出技能。
/// </para>
/// </summary>
public sealed class SkillScaffoldTool(ToolkitOptions toolkit, Action? skillsChanged = null) : ITool, IToolWithSchema
{
    public string Name => "skill_scaffold";

    public string Description =>
        "生成一个技能（skill）骨架：给 id / description（可选 name、prompt、tools），" +
        "写出 <workspace>/skills/<id>/{skill.json, README.md}。技能是**纯声明式**的（工具白名单 + 提示词后缀），不含可执行内容。" +
        "写完用 skill_validate 自检。";

    public string ParametersJsonSchema => """
        {"type":"object","properties":{
          "id":{"type":"string","description":"技能目录名（本地骨架落在 skills/<id>/）：字母数字或 - _"},
          "name":{"type":"string","description":"技能名；不填则用 id。须与 id 同规则（字母数字或 - _），导入时它会用作目录名"},
          "description":{"type":"string","description":"一句话说明这个技能干什么"},
          "prompt":{"type":"string","description":"技能激活时追加到提示词的说明（可空）"},
          "tools":{"type":"array","items":{"type":"string"},"description":"工具白名单（可空 = 不收窄）"}
        },"required":["id","description"]}
        """;

    public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        var id = SkillToolArgs.Get(invocation, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return ValueTask.FromResult(ToolResult.Fail("缺少参数 id"));
        }

        if (!SkillValidator.IsValidName(id))
        {
            return ValueTask.FromResult(ToolResult.Fail($"技能 id「{id}」含不安全字符（只允许字母数字 - _ ，≤64 字符）"));
        }

        var description = SkillToolArgs.Get(invocation, "description");
        if (string.IsNullOrWhiteSpace(description))
        {
            return ValueTask.FromResult(ToolResult.Fail("缺少参数 description"));
        }

        var name = SkillToolArgs.Get(invocation, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            name = id;
        }

        // name 与 id 同一白名单 —— 它在导入时会当目录名用。
        // 从前 scaffold 只校 id、validate 却卡 name，必然产出「自检不过」的包。
        if (!SkillValidator.IsValidName(name))
        {
            return ValueTask.FromResult(ToolResult.Fail(
                $"技能名「{name}」不合法（只允许字母数字 - _ ，≤64 字符）。" +
                "它在导入时会用作目录名；要显示空格请改用连字符（如 Smoke-Test-Skill），或省略 name 直接用 id。"));
        }

        var tools = SkillToolArgs.StringArray(SkillToolArgs.Get(invocation, "tools"));

        var manifest = new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["promptSuffix"] = SkillToolArgs.Get(invocation, "prompt"),
        };

        var toolsArray = new JsonArray();
        foreach (var t in tools)
        {
            toolsArray.Add(t);
        }

        manifest["tools"] = toolsArray;

        // 写路径一律走既有边界解析（RealPath 穿透 + 同路径落盘）—— 禁止另起一套路径解析。
        var rel = Path.Combine(SkillLoader.BuiltinDirName, id!);
        if (!WorkspacePath.TryResolve(toolkit, rel, forWrite: true, out var dir, out var error))
        {
            return ValueTask.FromResult(ToolResult.Fail(error ?? "路径解析失败"));
        }

        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                Path.Combine(dir, "skill.json"),
                manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(dir, "README.md"),
                BuildReadme(name!, description!, tools),
                new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult(ToolResult.Fail($"写入技能目录失败：{ex.Message}"));
        }

        skillsChanged?.Invoke();

        return ValueTask.FromResult(ToolResult.Ok(
            $"已生成技能骨架：{dir}\n  - skill.json\n  - README.md\n" +
            $"下一步：用 skill_validate（id={id}）自检。"));
    }

    private static string BuildReadme(string name, string description, IReadOnlyList<string> tools)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {name}").AppendLine();
        sb.AppendLine(description).AppendLine();
        sb.AppendLine("## 这是一个技能（skill）").AppendLine();
        sb.AppendLine("技能是**纯声明式**包：一份 `skill.json`（工具白名单 + 提示词后缀），不含任何可执行内容。");
        sb.AppendLine("启用它只会改变「这一轮给模型看什么」，不会重装配、不重启。").AppendLine();
        sb.AppendLine("## 工具白名单").AppendLine();
        sb.AppendLine(tools.Count == 0
            ? "（空）—— 不收窄工具面，仅追加提示词。"
            : string.Join("\n", tools.Select(t => $"- `{t}`")));
        sb.AppendLine();
        sb.AppendLine("## 激活后的提示词后缀").AppendLine();
        sb.AppendLine("见 `skill.json` 的 `promptSuffix`。").AppendLine();
        sb.AppendLine("> 由 `skill_scaffold` 生成。改完用 `skill_validate` 复检。");
        return sb.ToString();
    }
}

/// <summary>
/// 校验技能包：技能名合法性 / 工具白名单是否引用未注册工具 / 是否与内置或工坊技能重名。
/// 校验规则与界面导入路由<b>共用同一套</b>（见 <see cref="SkillValidator"/>）。
/// </summary>
public sealed class SkillValidateTool(
    ToolkitOptions toolkit,
    Func<IReadOnlyCollection<string>> registeredTools,
    Func<IReadOnlyList<SkillDefinition>> existingSkills) : ITool, IToolWithSchema
{
    public string Name => "skill_validate";

    public string Description =>
        "校验一个技能包是否可用：技能名合法性、工具白名单是否引用了未注册工具、是否与内置/工坊技能重名。" +
        "给 id（工作区 skills/ 或 workshop/ 下的目录名）或 path（技能目录）。故意造坏包时能指出具体字段错误。";

    public string ParametersJsonSchema => """
        {"type":"object","properties":{
          "id":{"type":"string","description":"技能目录名（在 <workspace>/skills 或 workshop 下找）"},
          "path":{"type":"string","description":"技能目录路径（给了就优先用它）"}
        }}
        """;

    public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        var path = SkillToolArgs.Get(invocation, "path");
        var id = SkillToolArgs.Get(invocation, "id");

        string? dir = null;
        if (!string.IsNullOrWhiteSpace(path))
        {
            dir = path;
        }
        else if (!string.IsNullOrWhiteSpace(id))
        {
            foreach (var sub in new[] { SkillLoader.BuiltinDirName, SkillLoader.WorkshopDirName })
            {
                if (WorkspacePath.TryResolve(toolkit, Path.Combine(sub, id!), forWrite: false, out var full, out _)
                    && Directory.Exists(full))
                {
                    dir = full;
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(dir))
        {
            return ValueTask.FromResult(ToolResult.Fail("没找到技能：给 id（skills/ 或 workshop/ 下的目录名）或 path"));
        }

        var manifestPath = Path.Combine(dir, "skill.json");
        if (!File.Exists(manifestPath))
        {
            return ValueTask.FromResult(ToolResult.Fail($"技能目录缺少 skill.json：{dir}"));
        }

        var skill = SkillLoader.TryReadManifest(manifestPath);
        if (skill is null)
        {
            return ValueTask.FromResult(ToolResult.Fail("skill.json 解析失败或缺少 name（须为合法 JSON 且含 name）"));
        }

        var errors = new List<string>();

        if (!SkillValidator.IsValidName(skill.Name))
        {
            errors.Add($"name「{skill.Name}」不合法：只允许字母数字与 - _ ，≤64 字符（导入时它会用作目录名，与 scaffold 的 id 同规则）");
        }

        var unknown = SkillValidator.UnknownTools(skill, registeredTools());
        if (unknown.Count > 0)
        {
            errors.Add($"工具白名单引用了未注册工具：{string.Join("、", unknown)}");
        }

        var selfId = Path.GetFileName(dir);
        var conflicts = SkillValidator.Conflicts(skill, existingSkills())
            .Where(s => !string.Equals(s.Id, selfId, StringComparison.Ordinal)) // 排除它自己
            .ToList();
        if (conflicts.Count > 0)
        {
            errors.Add($"与现有技能重名：{string.Join("、", conflicts.Select(c => $"{c.Name}（{c.Source}）"))}");
        }

        if (errors.Count == 0)
        {
            var tools = skill.Tools.Count == 0 ? "（不收窄）" : string.Join(", ", skill.Tools);
            return ValueTask.FromResult(ToolResult.Ok(
                $"技能「{skill.Name}」校验通过。\n  - 工具白名单：{tools}\n  - 提示词后缀：{(string.IsNullOrWhiteSpace(skill.PromptSuffix) ? "（无）" : "有")}"));
        }

        return ValueTask.FromResult(ToolResult.Fail("校验未通过：\n  - " + string.Join("\n  - ", errors)));
    }
}

/// <summary>
/// 从一段材料提炼技能草稿：产出 id/description/promptSuffix + 推荐工具白名单。
/// <b>不落盘</b> —— 返回草稿 JSON，确认后用 <c>skill_scaffold</c> 写入。
///
/// <para>
/// 规则化的第一版：不调用模型（工具不该自己偷偷花一次模型调用）。
/// 工具白名单取自「材料里**显式提到**的已注册工具名」，其余交给用户/模型在草稿上补。
/// </para>
/// </summary>
public sealed class SkillExtractTool(Func<IReadOnlyCollection<string>> registeredTools) : ITool, IToolWithSchema
{
    public string Name => "skill_extract";

    public string Description =>
        "从一段材料（对话片段 / 文档要点 / 步骤清单）提炼一个技能草稿：" +
        "产出 id + description + promptSuffix + 推荐工具白名单。不落盘 —— 返回草稿 JSON，确认后用 skill_scaffold 写入。";

    public string ParametersJsonSchema => """
        {"type":"object","properties":{
          "material":{"type":"string","description":"要提炼的材料"},
          "name":{"type":"string","description":"可选：期望的技能名"}
        },"required":["material"]}
        """;

    public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        var material = SkillToolArgs.Get(invocation, "material");
        if (string.IsNullOrWhiteSpace(material))
        {
            return ValueTask.FromResult(ToolResult.Fail("缺少参数 material"));
        }

        var trimmed = material.Trim();
        var id = SkillToolArgs.Get(invocation, "name");
        if (string.IsNullOrWhiteSpace(id))
        {
            id = Slugify(FirstLine(trimmed));
        }

        var mentioned = registeredTools()
            .Where(t => trimmed.Contains(t, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        var draft = new JsonObject
        {
            ["id"] = id,
            ["name"] = id,
            ["description"] = Summarize(trimmed, 80),
            ["promptSuffix"] = "当处理与下列材料相关的任务时，遵循其中确立的步骤与要求：\n" + Summarize(trimmed, 600),
        };

        var toolsArray = new JsonArray();
        foreach (var t in mentioned)
        {
            toolsArray.Add(t);
        }

        draft["tools"] = toolsArray;

        return ValueTask.FromResult(ToolResult.Ok(
            "技能草稿（未落盘，确认后用 skill_scaffold 写入）：\n"
            + draft.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
    }

    private static string FirstLine(string s)
    {
        var i = s.IndexOfAny(['\n', '\r']);
        return (i >= 0 ? s[..i] : s).Trim();
    }

    private static string Summarize(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static string Slugify(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
            else if (c is ' ' or '-' or '_' or '/')
            {
                if (sb.Length > 0 && sb[^1] != '-')
                {
                    sb.Append('-');
                }
            }

            if (sb.Length >= 40)
            {
                break;
            }
        }

        var result = sb.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(result) ? "extracted-skill" : result;
    }
}

/// <summary>
/// 工具目录查询：按包 / 关键词列出已注册工具与参数摘要。
/// 数据源与界面开关、<c>toolsets</c> 工具同一份（<see cref="ToolsetView"/>），不会出现「界面说开着、这里说没有」。
/// </summary>
public sealed class ToolCatalogTool(
    Func<IReadOnlyList<ToolsetView>> views,
    IReadOnlyList<ITool> officialTools) : ITool, IToolWithSchema
{
    public string Name => "tool_catalog";

    public string Description =>
        "按工具包或关键词列出已注册工具及其参数摘要（含延迟包索引）。" +
        "用于回答「有哪些工具可用 / 某个包里有啥 / 某个工具怎么调」。";

    public string ParametersJsonSchema => """
        {"type":"object","properties":{
          "toolset":{"type":"string","description":"只看这个包"},
          "keyword":{"type":"string","description":"按工具名过滤"},
          "schema":{"type":"boolean","description":"是否附参数摘要（默认否）"}
        }}
        """;

    public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        var toolset = SkillToolArgs.Get(invocation, "toolset");
        var keyword = SkillToolArgs.Get(invocation, "keyword");
        var wantSchema = string.Equals(SkillToolArgs.Get(invocation, "schema"), "true", StringComparison.OrdinalIgnoreCase);

        var schemaMap = wantSchema
            ? officialTools
                .Where(t => t is IToolWithSchema)
                .GroupBy(t => t.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => ToolSchemas.For(g.First()), StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);

        var report = new StringBuilder();
        var total = 0;

        foreach (var v in views())
        {
            if (!string.IsNullOrWhiteSpace(toolset) && !string.Equals(v.Id, toolset, StringComparison.Ordinal))
            {
                continue;
            }

            var names = v.Tools
                .Where(t => string.IsNullOrWhiteSpace(keyword) || t.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (names.Count == 0)
            {
                continue;
            }

            total += names.Count;
            report.Append($"[{(v.Enabled ? "开" : "关")}] {v.Id}（{v.Name}）");
            if (!v.Eager)
            {
                report.Append(" · 延迟包");
            }

            if (!string.IsNullOrWhiteSpace(v.Description))
            {
                report.Append("  —— ").Append(v.Description);
            }

            report.Append('\n');

            foreach (var t in names)
            {
                report.Append("   - ").Append(t);
                if (schemaMap.TryGetValue(t, out var schema))
                {
                    var summary = SummarizeSchema(schema);
                    if (summary.Length > 0)
                    {
                        report.Append("  ").Append(summary);
                    }
                }

                report.Append('\n');
            }
        }

        return ValueTask.FromResult(total == 0
            ? ToolResult.Ok("没有匹配的工具（试试不带 toolset/keyword，或先 use_toolset 打开相关包）。")
            : ToolResult.Ok(report.ToString()));
    }

    private static string SummarizeSchema(string schemaJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(schemaJson);
            if (doc.RootElement.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
            {
                var keys = props.EnumerateObject().Select(p => p.Name).ToList();
                return keys.Count == 0 ? string.Empty : $"参数：{string.Join(", ", keys)}";
            }
        }
        catch (JsonException)
        {
            // schema 不是合法 JSON —— 摘要跳过即可，不影响工具目录本身。
        }

        return string.Empty;
    }
}

/// <summary>
/// <b>结构化输出工具</b>的官方样例（任务 2 要的「扩展工具类型」）。
/// 把 CSV 文本解析成 JSON 数组，并声明输出结构（<see cref="IToolWithStructuredOutput"/>）——
/// 与 <see cref="IToolWithSchema"/> 对称：那个描述输入，这个描述输出。
/// </summary>
public sealed class CsvToJsonTool : ITool, IToolWithSchema, IToolWithStructuredOutput
{
    public string Name => "csv_to_json";

    public string Description =>
        "把 CSV 文本解析成 JSON 数组（结构化输出工具类型的官方样例）。" +
        "首行默认当表头，之后每行转成一个对象；带引号的字段（可含逗号/换行/双引号）按 RFC 4180 处理。";

    public string ParametersJsonSchema => """
        {"type":"object","properties":{
          "csv":{"type":"string","description":"CSV 文本"},
          "delimiter":{"type":"string","description":"分隔符，默认逗号"},
          "header":{"type":"boolean","description":"首行是否为表头（默认 true）"}
        },"required":["csv"]}
        """;

    public string OutputJsonSchema => """{"type":"array","items":{"type":"object"}}""";

    public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        var csv = SkillToolArgs.Get(invocation, "csv");
        if (string.IsNullOrWhiteSpace(csv))
        {
            return ValueTask.FromResult(ToolResult.Fail("缺少参数 csv"));
        }

        var delimiter = SkillToolArgs.Get(invocation, "delimiter");
        var sep = string.IsNullOrEmpty(delimiter) ? ',' : delimiter[0];
        var hasHeader = !string.Equals(SkillToolArgs.Get(invocation, "header"), "false", StringComparison.OrdinalIgnoreCase);

        var rows = ParseCsv(csv, sep).Where(r => !(r.Count == 1 && string.IsNullOrWhiteSpace(r[0]))).ToList();
        if (rows.Count == 0)
        {
            return ValueTask.FromResult(ToolResult.Fail("CSV 没有有效行"));
        }

        var headers = hasHeader
            ? rows[0].Select((h, i) => string.IsNullOrWhiteSpace(h) ? $"col{i + 1}" : h.Trim()).ToList()
            : Enumerable.Range(1, rows[0].Count).Select(i => $"col{i}").ToList();

        var dataRows = hasHeader ? rows.Skip(1) : rows;
        var array = new JsonArray();

        foreach (var row in dataRows)
        {
            var obj = new JsonObject();
            for (var i = 0; i < headers.Count; i++)
            {
                obj[headers[i]] = i < row.Count ? row[i] : string.Empty;
            }

            array.Add(obj);
        }

        return ValueTask.FromResult(ToolResult.Ok(
            array.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
    }

    private static List<List<string>> ParseCsv(string text, char sep)
    {
        var rows = new List<List<string>>();
        var field = new StringBuilder();
        var row = new List<string>();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == sep)
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c == '\n')
            {
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row);
                row = [];
            }
            else if (c != '\r')
            {
                field.Append(c);
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }
}

/// <summary>
/// 把「工具包」直接转成技能草稿：<c>toolset → &lt;workspace&gt;/skills/&lt;id&gt;/skill.json</c>。
///
/// <para>
/// 与 <see cref="SkillScaffoldTool"/> 的分工：scaffold 是「从零手写白名单」；
/// 本工具是「已有工具（尤其整包）→ 一键收成技能」。
/// 典型用法：把 <c>devkit</c> 的一整套编辑工具收成「改代码」技能，只在这一轮放出来 ——
/// 工具面收窄，模型负担随之下降，而收放动作本身不重装配、不重启。
/// </para>
///
/// <para>
/// 转化产物仍是纯声明包：只写 skill.json，不复制、不包装任何可执行内容。
/// 想再挑工具或补做法说明，转化完接着用 scaffold / validate。
/// </para>
/// </summary>
public sealed class SkillFromToolsetTool(
    ToolkitOptions toolkit,
    Func<IReadOnlyList<ToolsetView>> toolsets,
    Action? skillsChanged = null) : ITool, IToolWithSchema
{
    public string Name => "skill_from_toolset";

    public string Description =>
        "把某个工具包（toolset）里的全部工具一键转成一个技能草稿，写出 <workspace>/skills/<id>/skill.json。" +
        "适合「把一套现成工具收成可复用技能」；工具包 id 可用 toolsets 或 tool_catalog 查。" +
        "转化后可用 skill_validate 自检。";

    public string ParametersJsonSchema => """
        {"type":"object","properties":{
          "toolset":{"type":"string","description":"要转化的工具包 id"},
          "id":{"type":"string","description":"技能目录名；不填则用 toolset"},
          "name":{"type":"string","description":"技能显示名；不填则用 id"},
          "description":{"type":"string","description":"一句话说明；不填自动生成"},
          "prompt":{"type":"string","description":"提示词后缀（怎么做）；不填给一段占位说明"}
        },"required":["toolset"]}
        """;

    public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        var toolsetId = SkillToolArgs.Get(invocation, "toolset");
        if (string.IsNullOrWhiteSpace(toolsetId))
        {
            return ValueTask.FromResult(ToolResult.Fail("缺少参数 toolset"));
        }

        var view = toolsets().FirstOrDefault(t => string.Equals(t.Id, toolsetId, StringComparison.Ordinal));
        if (view is null)
        {
            return ValueTask.FromResult(ToolResult.Fail($"没有找到工具包「{toolsetId}」。可用 toolsets 看有哪些包。"));
        }

        if (view.Tools.Count == 0)
        {
            return ValueTask.FromResult(ToolResult.Fail($"工具包「{toolsetId}」当前没有任何工具，无法转化。"));
        }

        var id = SkillToolArgs.Get(invocation, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            id = toolsetId;
        }

        if (!SkillValidator.IsValidName(id))
        {
            return ValueTask.FromResult(ToolResult.Fail($"技能 id「{id}」含不安全字符（只允许字母数字 - _ ，≤64 字符）"));
        }

        var name = SkillToolArgs.Get(invocation, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            name = id;
        }

        var description = SkillToolArgs.Get(invocation, "description");
        if (string.IsNullOrWhiteSpace(description))
        {
            description = $"由工具包「{view.Name}」（{toolsetId}）转化：启用后工具面收窄到这 {view.Tools.Count} 个。";
        }

        var prompt = SkillToolArgs.Get(invocation, "prompt");
        if (string.IsNullOrWhiteSpace(prompt))
        {
            prompt = $"本技能启用时，工具面收窄到「{view.Name}」包：{string.Join("、", view.Tools)}。按需调用，不要越界。";
        }

        var manifest = new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["promptSuffix"] = prompt,
        };

        var toolsArray = new JsonArray();
        foreach (var t in view.Tools)
        {
            toolsArray.Add(t);
        }

        manifest["tools"] = toolsArray;

        // 写路径一律走既有边界解析（RealPath 穿透 + 同路径落盘）—— 禁止另起一套路径解析。
        var rel = Path.Combine(SkillLoader.BuiltinDirName, id!);
        if (!WorkspacePath.TryResolve(toolkit, rel, forWrite: true, out var dir, out var error))
        {
            return ValueTask.FromResult(ToolResult.Fail(error ?? "路径解析失败"));
        }

        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                Path.Combine(dir, "skill.json"),
                manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult(ToolResult.Fail($"写入技能目录失败：{ex.Message}"));
        }

        skillsChanged?.Invoke();

        return ValueTask.FromResult(ToolResult.Ok(
            $"已把工具包「{view.Name}」转化为技能：{dir}\n" +
            $"  - 工具白名单 {view.Tools.Count} 个：{string.Join(", ", view.Tools)}\n" +
            $"下一步：用 skill_validate（id={id}）自检，或在「技能」面板里启用它。"));
    }
}
