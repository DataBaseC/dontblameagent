using System.Text.RegularExpressions;

// ═══════════════════════════════════════════════════════════
//  密钥泄露回归扫描（安全审查 P0-1）
//
//  源码 / 配置文本里不得出现「疑似真实密钥」的字符串：命中即失败。
//  目的不是抓出所有密钥，而是给「手滑把 key 写进源码」装一道回归门禁 ——
//  提交前跑一次，CI 里跑一次。
// ═══════════════════════════════════════════════════════════

var passes = 0;
var failures = 0;

void Check(string name, bool ok, string? detail = null)
{
    var suffix = detail is null ? "" : $"  ({detail})";
    if (ok)
    {
        passes++;
        Console.WriteLine($"  [PASS] {name}{suffix}");
    }
    else
    {
        failures++;
        Console.WriteLine($"  [FAIL] {name}{suffix}");
    }
}

Console.WriteLine("═══ 密钥泄露回归扫描 ═══");

var root = FindRepoRoot(AppContext.BaseDirectory) ?? FindRepoRoot(Directory.GetCurrentDirectory());

if (root is null)
{
    Console.WriteLine("找不到仓库根（含 src/ 的目录）—— 请从仓库根运行本验证。");
    Console.WriteLine($"\n═══ 结果：{passes} 通过 / 1 失败 ═══");
    return 1;
}

Console.WriteLine($"仓库根：{root}");

// 逐条点名「长得就像真密钥」的形态。占位符（含中文 / <…> / xxxx）不会命中。
var patterns = new (string Name, Regex Rx)[]
{
    ("AMD Radeon（rc-…）", new Regex(@"rc-[0-9a-f]{40}\b", RegexOptions.Compiled)),
    ("Anthropic（sk-ant-…）", new Regex(@"sk-ant-[A-Za-z0-9_\-]{20,}", RegexOptions.Compiled)),
    ("OpenAI（sk-…）", new Regex(@"\bsk-[A-Za-z0-9_\-]{20,}", RegexOptions.Compiled)),
    ("GitHub（ghp_…）", new Regex(@"\bghp_[A-Za-z0-9]{30,}", RegexOptions.Compiled)),
};

var hits = new List<string>();

foreach (var file in EnumerateScanFiles(root))
{
    string text;
    try
    {
        text = File.ReadAllText(file);
    }
    catch
    {
        continue;
    }

    foreach (var (name, rx) in patterns)
    {
        var match = rx.Match(text);
        if (match.Success)
        {
            var rel = Path.GetRelativePath(root, file);
            hits.Add($"{rel} → {name}: {Mask(match.Value)}");
        }
    }
}

Check("源码 / 配置文本中无疑似真实密钥", hits.Count == 0,
    hits.Count == 0 ? "扫描通过" : $"命中 {hits.Count} 处");

foreach (var hit in hits)
{
    Console.WriteLine($"    · {hit}");
}

Console.WriteLine($"\n═══ 结果：{passes} 通过 / {failures} 失败 ═══");
return failures == 0 ? 0 : 1;

// ── 辅助 ────────────────────────────────────────────────────

static string Mask(string secret) =>
    secret.Length <= 12 ? secret : secret[..8] + "…" + secret[^4..];

static IEnumerable<string> EnumerateScanFiles(string dir)
{
    var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", "node_modules", ".sources", ".vs", ".vscode",
        "agent-workspace", "agent-sessions", "plugins", "profiles", "webview-data", "out",
    };

    var scanExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".json", ".jsonl", ".md", ".cmd", ".bat", ".sh", ".ps1",
        ".yml", ".yaml", ".props", ".targets", ".csproj", ".sln", ".example",
    };

    var stack = new Stack<string>();
    stack.Push(dir);

    while (stack.Count > 0)
    {
        var current = stack.Pop();

        string[] files;
        string[] subs;
        try
        {
            files = Directory.GetFiles(current);
            subs = Directory.GetDirectories(current);
        }
        catch
        {
            continue;
        }

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var ext = Path.GetExtension(file);
            // 越简单的命中面越好：只扫源码与配置文本，二进制一律跳过。
            if (scanExt.Contains(ext) || name.Equals(".gitignore", StringComparison.OrdinalIgnoreCase))
            {
                yield return file;
            }
        }

        foreach (var sub in subs)
        {
            if (!skip.Contains(Path.GetFileName(sub)))
            {
                stack.Push(sub);
            }
        }
    }
}

static string? FindRepoRoot(string start)
{
    var dir = new DirectoryInfo(start);
    while (dir is not null)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, "src")))
        {
            return dir.FullName;
        }

        dir = dir.Parent;
    }

    return null;
}
