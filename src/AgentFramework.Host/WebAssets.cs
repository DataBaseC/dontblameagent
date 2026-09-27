using System.Reflection;

namespace AgentFramework.Host;

/// <summary>
/// 宿主内嵌的前端资源（安全审查 P1-3）。
///
/// <para>
/// 对话界面原本是 <see cref="WebUiPage"/> 里一个 3000+ 行的 C# 字符串常量 ——
/// 没有语法高亮、没有 lint、diff 不可读、无法拆分测试。抽成独立的 <c>Web/index.html</c>
/// 并以嵌入资源随程序集发布后，编辑器能力（高亮 / lint / diff）立刻回来，
/// 而运行时行为不变：仍是单文件、无外部依赖、随 exe 一起分发。
/// </para>
/// </summary>
internal static class WebAssets
{
    private static readonly Assembly Asm = typeof(WebAssets).Assembly;

    /// <summary>按文件名后缀取一份嵌入的文本资源（大小写不敏感）。</summary>
    public static string Load(string fileName)
    {
        var suffix = "." + fileName;
        var name = Array.Find(
                       Asm.GetManifestResourceNames(),
                       n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"找不到内嵌资源「{fileName}」。现有资源：{string.Join(", ", Asm.GetManifestResourceNames())}");

        using var stream = Asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"无法打开内嵌资源「{name}」。");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
