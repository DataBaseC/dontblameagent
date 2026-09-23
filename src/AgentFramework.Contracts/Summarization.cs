namespace AgentFramework.Contracts;

/// <summary>
/// 长文本摘要 seam。
///
/// 设计意图（DESIGN.md 4.1）：让「吃 token」的活交给**本地小模型** ——
/// 网页正文在本地压缩成摘要，只有摘要回灌云端上下文。
/// 于是 <b>token 账单与网页体积脱钩，且原文永不离开本机</b>。
/// </summary>
public interface IResultSummarizer
{
    string Name { get; }

    /// <summary>
    /// 把长文本压成摘要。
    /// <b>允许失败</b> —— 调用方必须能在失败时退回原文，
    /// 摘要只是省钱手段，不该成为功能可用性的单点。
    /// </summary>
    ValueTask<string> SummarizeAsync(string content, string? sourceUrl, CancellationToken ct = default);
}
