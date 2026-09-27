using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using UglyToad.PdfPig;

namespace AgentFramework.Plugins.WritingKit;

/// <summary>
/// 办公文档取文（写作扩展包的地基）。
///
/// <para>
/// 写作的素材常常是「一份 pdf / docx / xlsx」，而不是纯文本。模型看不到二进制，
/// 于是这里把它拆成可读文本：<b>pdf 用 PdfPig</b>；<b>docx / xlsx / pptx 零依赖自解</b>
/// （它们本质就是 zip + XML，取 <c>w:t</c> / <c>a:t</c> / 共享字符串即可，不必引重型库）。
/// </para>
///
/// <para>
/// <b>为什么不用 NPOI</b>：它能读旧二进制 doc/xls/ppt，但依赖会拖进 SkiaSharp 及一堆原生库 ——
/// 对一个「随插件目录加载」的 ALC 插件太重、也易踩平台坑。故本版只覆盖新格式，
/// 旧二进制格式给出「请另存为」的明确提示。
/// </para>
/// </summary>
internal static class DocumentText
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace X = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    /// <summary>纯文本家族：直接按文本读。</summary>
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".text", ".md", ".markdown", ".csv", ".tsv", ".json", ".xml", ".html", ".htm",
        ".log", ".yaml", ".yml", ".ini", ".cfg", ".conf", ".srt", ".tex",
    };

    /// <summary>需要解析的容器格式（zip / pdf）。</summary>
    private static readonly HashSet<string> ContainerExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".docx", ".xlsx", ".pptx",
    };

    /// <summary>旧二进制 Office：本版不支持。</summary>
    private static readonly HashSet<string> LegacyExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".xls", ".ppt",
    };

    public static string SupportedHint =>
        "支持 pdf、docx、xlsx、pptx，以及文本类（txt/md/csv/json/xml/html/yaml/log 等）。";

    /// <summary>是否是需要「解析」而非「按文本读」的格式（含不支持的旧格式，好给出提示）。</summary>
    public static bool NeedsExtraction(string path)
    {
        var ext = Path.GetExtension(path);
        return ContainerExtensions.Contains(ext) || LegacyExtensions.Contains(ext);
    }

    public static bool TryExtract(string fullPath, out string text, out string? error)
    {
        text = string.Empty;
        error = null;
        var ext = Path.GetExtension(fullPath).ToLowerInvariant();

        try
        {
            if (TextExtensions.Contains(ext))
            {
                var raw = File.ReadAllText(fullPath);
                text = ext is ".html" or ".htm" ? StripHtml(raw) : raw;
                return true;
            }

            text = ext switch
            {
                ".docx" => ExtractDocx(fullPath),
                ".pptx" => ExtractPptx(fullPath),
                ".xlsx" => ExtractXlsx(fullPath),
                ".pdf" => ExtractPdf(fullPath),
                _ => string.Empty,
            };

            if (text.Length > 0)
            {
                return true;
            }

            error = LegacyExtensions.Contains(ext)
                ? $"旧二进制格式 {ext} 暂不支持，请先另存为 {NewFormatFor(ext)} 再读。"
                : $"不支持的格式 {ext}。{SupportedHint}";
            return false;
        }
        catch (Exception ex)
        {
            error = $"解析 {ext} 失败：{ex.Message}";
            return false;
        }
    }

    // ── docx：word/document.xml 里取 w:t（按段分行）─────────────────────
    private static string ExtractDocx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("word/document.xml")
            ?? throw new InvalidOperationException("不是有效的 docx（缺 word/document.xml）");
        using var stream = entry.Open();
        var doc = XDocument.Load(stream);

        var sb = new StringBuilder();
        foreach (var para in doc.Descendants(W + "p"))
        {
            sb.AppendLine(string.Concat(para.Descendants(W + "t").Select(t => t.Value)));
        }

        return sb.ToString().TrimEnd('\n');
    }

    // ── pptx：ppt/slides/slideN.xml 里取 a:t（按页）─────────────────────
    private static string ExtractPptx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var slides = zip.Entries
            .Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase)
                        && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => SlideNumber(e.FullName))
            .ToList();

        var sb = new StringBuilder();
        var index = 1;
        foreach (var entry in slides)
        {
            using var stream = entry.Open();
            var doc = XDocument.Load(stream);
            var lines = doc.Descendants(A + "t").Select(t => t.Value).Where(s => s.Length > 0).ToList();
            if (lines.Count == 0)
            {
                continue;
            }

            sb.Append("── 第 ").Append(index).Append(" 页 ──\n");
            sb.AppendLine(string.Join("\n", lines));
            index++;
        }

        return sb.ToString().TrimEnd('\n');
    }

    private static int SlideNumber(string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        return int.TryParse(stem.Replace("slide", "", StringComparison.OrdinalIgnoreCase), out var n)
            ? n
            : int.MaxValue;
    }

    // ── xlsx：共享字符串表 + 各 sheet 单元格 ────────────────────────────
    private static string ExtractXlsx(string path)
    {
        using var zip = ZipFile.OpenRead(path);

        var shared = new List<string>();
        var sharedEntry = zip.GetEntry("xl/sharedStrings.xml");
        if (sharedEntry is not null)
        {
            using var s = sharedEntry.Open();
            foreach (var si in XDocument.Load(s).Descendants(X + "si"))
            {
                shared.Add(string.Concat(si.Descendants(X + "t").Select(t => t.Value)));
            }
        }

        var sheets = zip.Entries
            .Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase)
                        && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sb = new StringBuilder();
        var sheetIndex = 1;
        foreach (var entry in sheets)
        {
            using var s = entry.Open();
            var doc = XDocument.Load(s);
            sb.Append("── 工作表 ").Append(sheetIndex).Append(" ──\n");
            sheetIndex++;

            foreach (var row in doc.Descendants(X + "row"))
            {
                var cells = new List<string>();
                foreach (var cell in row.Elements(X + "c"))
                {
                    cells.Add(CellText(cell, shared));
                }

                sb.AppendLine(string.Join("\t", cells));
            }
        }

        return sb.ToString().TrimEnd('\n');
    }

    private static string CellText(XElement cell, List<string> shared)
    {
        var type = (string?)cell.Attribute("t");
        if (type == "s")
        {
            var v = cell.Element(X + "v")?.Value;
            return int.TryParse(v, out var idx) && idx >= 0 && idx < shared.Count ? shared[idx] : string.Empty;
        }

        if (type == "inlineStr")
        {
            return string.Concat(cell.Descendants(X + "t").Select(t => t.Value));
        }

        return cell.Element(X + "v")?.Value ?? string.Empty;
    }

    // ── pdf：PdfPig 逐页取文 ───────────────────────────────────────────
    private static string ExtractPdf(string path)
    {
        using var pdf = PdfDocument.Open(path);
        var sb = new StringBuilder();
        var pageNo = 0;
        foreach (var page in pdf.GetPages())
        {
            pageNo++;
            sb.Append("── 第 ").Append(pageNo).Append(" 页 ──\n");
            sb.AppendLine(page.Text);
        }

        return sb.ToString().TrimEnd('\n');
    }

    // ── html：剥标签取文 ───────────────────────────────────────────────
    private static readonly Regex ScriptOrStyle =
        new(@"(?is)<(script|style)\b[^>]*>.*?</\1\s*>", RegexOptions.Compiled);
    private static readonly Regex AnyTag = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"[ \t]{2,}", RegexOptions.Compiled);

    private static string StripHtml(string html)
    {
        var noScript = ScriptOrStyle.Replace(html, " ");
        var noTags = AnyTag.Replace(noScript, " ");
        var decoded = System.Net.WebUtility.HtmlDecode(noTags);
        return Spaces.Replace(decoded, " ").Trim();
    }

    private static string NewFormatFor(string ext) => ext switch
    {
        ".doc" => "docx",
        ".xls" => "xlsx",
        ".ppt" => "pptx",
        _ => "docx/xlsx/pptx",
    };
}
