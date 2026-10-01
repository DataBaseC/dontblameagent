namespace AgentFramework.Contracts;

/// <summary>
/// 「合成内容」识别 —— 系统注入的**复述块**（摘要、任务卡、计划文件、记忆、小本本……），
/// 不是对话原文。约定：<b>首个方括号标注以「·非用户发言】」结尾</b>。
///
/// <h3>INV-C1（压缩产物再入禁止，CliffCompaction, arXiv:2609.26779）</h3>
/// <blockquote>摘要产物<b>永不</b>作为压缩输入主体。</blockquote>
/// 展开成三条可执行的纪律：
/// <list type="number">
///   <item>
///   上一次压缩的产物（<c>ContextCompactedEvent.Summary</c> / checkpoint 稿）
///   只以 <c>previousSummary</c> 身份进摘要器 —— 作**背景**供增量合并，
///   绝不混进待压缩的正文（transcript）。
///   </item>
///   <item>
///   已被摘要产物**覆盖**的原始轮次不再进压缩输入 —— 原料与产物同台等于
///   二次压缩：同一份信息被计两次，且每次往返都放大漂移（「压缩压缩产物」）。
///   </item>
///   <item>
///   合成复述块本身就是复述/压缩产物（或其载体），任何压缩入口都不得把它
///   当作对话正文再压一遍 —— 那是在压缩压缩产物的影子。
///   </item>
/// </list>
///
/// 为什么值得单独立法：违反它的每一步都「看起来无害」—— 摘要只是又一条文本，
/// 再压一次似乎还能更精炼；实测下来却是只减不增的漂移放大器，
/// 而且坏得无声无息（每轮都合法，直到任务悄悄跑偏）。
/// </summary>
public static class SyntheticContent
{
    /// <summary>合成标注的约定尾缀（如「【计划文件·非用户发言】」）。</summary>
    public const string MarkerSuffix = "·非用户发言】";

    /// <summary>
    /// 判定一段内容是否为合成复述块。
    /// 只认**首个方括号标注**（行首缩进忽略）—— 正文里偶然出现该字样不算。
    /// </summary>
    public static bool IsSynthetic(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var span = content.AsSpan().TrimStart();

        if (!span.StartsWith("【"))
        {
            return false;
        }

        var end = span.IndexOf('】');
        if (end < 0 || end > 60)
        {
            return false;
        }

        return span[..end].EndsWith("·非用户发言");
    }
}
