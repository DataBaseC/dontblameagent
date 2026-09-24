namespace AgentFramework.Contracts;

/// <summary>
/// 输入转述（澄清模式）的配置。
///
/// 放在契约层而不是宿主层 —— 因为「把用户的话说清楚」是一个**能力**：
/// 将来插件也可以读它、甚至可以替换实现（换一个更懂你行业黑话的转述器）。
///
/// 注意这里**只有策略、没有提示词之外的硬编码**：
/// 学 dsh 的「参数极简、策略全在配置」—— 提示词本身也允许用户整体替换。
/// </summary>
public sealed class RephraseOptions
{
    /// <summary>总开关。关掉即零开销。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 发送前自动澄清。
    /// 默认**关闭** —— 主路径是输入框旁那颗手动按钮，
    /// 让「要不要多花一次模型调用」这个决定永远握在人手里。
    /// </summary>
    public bool AutoBeforeSend { get; set; }

    /// <summary>
    /// 走哪个端点。三种写法：
    /// <c>local</c> / <c>cloud</c>（环境变量老配置）· 端点 id（如 <c>deepseek</c>，用该端点默认模型）·
    /// <c>端点:模型</c>（如 <c>deepseek:deepseek-chat</c>）。转述是量大而智力要求低的活，默认给本地。
    /// </summary>
    public string Model { get; set; } = "local";

    /// <summary>系统提示词 —— 用户可整体替换。</summary>
    public string SystemPrompt { get; set; } = DefaultSystemPrompt;

    /// <summary>追加指令（拼在内置提示词之后）。想只微调、不想重写整段时用这个。</summary>
    public string ExtraInstructions { get; set; } = "";

    /// <summary>低温：澄清是压缩与消歧，不是创作。</summary>
    public double Temperature { get; set; } = 0.2;

    /// <summary>超时即回退原文 —— 转述不许把发消息卡住。</summary>
    public int TimeoutMs { get; set; } = 8000;

    /// <summary>低于这个长度不转述（"继续" / "好的" 这类没什么可澄清的）。</summary>
    public int MinChars { get; set; } = 8;

    /// <summary>超过这个长度不转述 —— 那多半是粘贴进来的资料，而不是一句需求。</summary>
    public int MaxInputChars { get; set; } = 2000;

    /// <summary>
    /// 内置提示词。三条铁律写在最前面，因为这是本模式唯一的真实风险：
    /// **转述模型自作聪明，替用户改了需求**。
    /// </summary>
    public const string DefaultSystemPrompt = """
        你是「需求澄清助手」。把用户的一句话改写成清晰、完整、可执行的任务描述，供另一个 AI 助手理解。

        铁律：
        1. 只澄清，不扩写：不得添加用户没说过的新需求、新约束、新事实。
        2. 专有名词、文件路径、命令、代码、数字、参数一律原样保留：不翻译、不"修正"、不改大小写。
        3. 保持用户的原语言与原意。输出只有改写后的任务描述本身 —— 不要前缀、不要引号、不要解释、不要标题。

        做法：
        - 消解指代：把"它 / 这个 / 刚才那个"替换成具体对象；若上下文不足以判断，保持原样。
        - 补全口语省略：把"搞一下"这类模糊说法明确成具体动作，但不替用户做决定、不加用户未提的选项。
        """;

    /// <summary>拼出最终系统提示词（内置 + 用户追加）。</summary>
    public string BuildSystemPrompt()
    {
        if (string.IsNullOrWhiteSpace(ExtraInstructions))
        {
            return SystemPrompt;
        }

        return SystemPrompt.TrimEnd() + "\n\n补充要求：\n" + ExtraInstructions.Trim();
    }

    public RephraseOptions Clone() => new()
    {
        Enabled = Enabled,
        AutoBeforeSend = AutoBeforeSend,
        Model = Model,
        SystemPrompt = SystemPrompt,
        ExtraInstructions = ExtraInstructions,
        Temperature = Temperature,
        TimeoutMs = TimeoutMs,
        MinChars = MinChars,
        MaxInputChars = MaxInputChars,
    };
}

/// <summary>跳过转述的原因（同时用于界面提示与事件留痕）。</summary>
public static class RephraseSkip
{
    public const string Disabled = "已关闭转述";
    public const string TooShort = "输入太短，没什么可澄清的";
    public const string TooLong = "输入过长，多半是资料而非需求";
    public const string NoModel = "没有可用的模型端点";
}

/// <summary>
/// 一次转述的结果。
///
/// 关键约定：<see cref="Text"/> **永远可以直接用** ——
/// 失败、跳过、关闭时它就是原文。调用方不必自己兜底，
/// 所以「转述挡住了发消息」这种事在结构上就不可能发生。
/// </summary>
public sealed record RephraseResult
{
    /// <summary>最终该采用的文本（成功=转述结果；其余=原文）。</summary>
    public required string Text { get; init; }

    /// <summary>用户原话。</summary>
    public string Original { get; init; } = "";

    /// <summary>是否真的转述成功了。</summary>
    public bool Rephrased { get; init; }

    /// <summary>实际使用的模型标识（成功时非空）。</summary>
    public string Model { get; init; } = "";

    public long ElapsedMs { get; init; }

    /// <summary>失败原因（成功与跳过时为 null）。</summary>
    public string? Error { get; init; }

    /// <summary>跳过原因（仅跳过时非空）。</summary>
    public string? SkipReason { get; init; }

    public bool Skipped => SkipReason is not null;

    /// <summary>跳过（不算失败）。</summary>
    public static RephraseResult Skip(string original, string reason)
        => new() { Text = original, Original = original, SkipReason = reason };

    /// <summary>失败 —— 仍然返回原文，绝不挡住发消息。</summary>
    public static RephraseResult Fail(string original, string error, long elapsedMs = 0)
        => new() { Text = original, Original = original, Error = error, ElapsedMs = elapsedMs };
}

/// <summary>
/// 输入转述器 —— 又一个 capability seam。
/// 默认实现走 <see cref="ILlmClient"/>；换成别的（规则式改写、别的模型）不影响宿主。
/// </summary>
public interface IUserInputRephraser
{
    Task<RephraseResult> RephraseAsync(string input, RephraseOptions options, CancellationToken ct = default);
}
