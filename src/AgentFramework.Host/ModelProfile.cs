using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentFramework.Llm;

namespace AgentFramework.Host;

/// <summary>
/// 一个模型端点（provider）—— 连接信息 + 它下面有哪些模型。
///
/// 为什么要把「连接」和「模型」分成两层：同一个 baseUrl 下往往有多个模型可选
/// （DeepSeek 有 chat 和 reasoner，LM Studio 里可能加载了好几个），
/// 而换的通常只是模型、不是整条连接。分开之后「切换」变得很轻。
/// </summary>
public sealed class ProviderConfig
{
    /// <summary>稳定标识（用于引用，尽量别改）。</summary>
    public required string Id { get; set; }

    /// <summary>显示名（不填就用 Id）。</summary>
    public string? Name { get; set; }

    public required string BaseUrl { get; set; }

    /// <summary>明文密钥 —— 允许手写在配置单里，方便。</summary>
    public string? ApiKey { get; set; }

    /// <summary>DPAPI 加密后的密钥（界面保存时写这个，配置单里就见不到明文了）。</summary>
    public string? ApiKeyProtected { get; set; }

    /// <summary>这个端点下可选的模型。</summary>
    public List<ModelEntry> Models { get; set; } = [];

    /// <summary>
    /// 思考参数风格（none / openai / qwen，见 ReasoningStyles）。
    /// 决定思考强度以什么参数形态发给这个端点。
    /// </summary>
    public string ReasoningStyle { get; set; } = ReasoningStyles.None;

    /// <summary>该端点的默认思考强度（off/low/medium/high；空 = 端点自己的默认）。</summary>
    public string ReasoningEffort { get; set; } = "";

    /// <summary>本地端点不需要密钥。</summary>
    public bool IsLocalLike =>
        Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri)
        && (uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 取出这个端点实际可用的密钥：明文优先，其次解密。
    /// 解不开（比如换了机器、换了 Windows 用户）时返回 null —— 当作「没有密钥」，
    /// 让后续的连接测试去报「认证失败」，而不是在这里炸掉整个启动。
    /// </summary>
    public string? ResolveApiKey(ISecretProtector protector)
    {
        if (!string.IsNullOrWhiteSpace(ApiKey))
        {
            return ApiKey;
        }

        if (string.IsNullOrWhiteSpace(ApiKeyProtected))
        {
            return null;
        }

        try
        {
            return protector.Unprotect(ApiKeyProtected!);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>一个可选模型。</summary>
public sealed class ModelEntry
{
    public required string Id { get; set; }

    public string? DisplayName { get; set; }

    /// <summary>
    /// 是否提供思考（推理）通道 —— 界面据此决定要不要渲染思考块。
    /// 为 null 表示「没标注」，按启发式判断（见 <see cref="ModelCapabilities"/>）。
    /// </summary>
    public bool? SupportsReasoning { get; set; }

    /// <summary>
    /// 是否接受图像输入（视觉）。null = 没标注，按名字启发式猜。
    /// 界面据此决定要不要显示「贴图」入口。
    /// </summary>
    public bool? SupportsVision { get; set; }

    /// <summary>上下文窗口（token）；未知则不填。</summary>
    public int? ContextWindow { get; set; }

    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName!;
}

/// <summary>当前选中的是哪个端点的哪个模型。</summary>
public sealed class ActiveModelRef
{
    public required string ProviderId { get; set; }

    public required string ModelId { get; set; }
}

/// <summary>
/// 模型能力的**启发式**判断。
///
/// 为什么先靠启发式：`GET /v1/models` 只回一个 id 列表，**不告诉你这个模型有没有思考通道**。
/// 公开的模型目录（models.dev / LiteLLM 的元数据）能补上，但那要联网拉一份、还要定期更新 ——
/// 先用名字判断，命中率其实很高（reasoner / r1 / thinking / o1 这些命名已经成了惯例），
/// 而且**允许在界面上手动覆盖**，标错了也能改回来。
/// </summary>
public static class ModelCapabilities
{
    private static readonly string[] ReasoningHints =
    [
        "reasoner", "reasoning", "thinking", "think", "-r1", "r1-", "o1", "o3", "o4",
        "qwq", "deepseek-r", "magistral", "gpt-5", "gemini-2.5", "claude-3-7", "claude-4",
    ];

    private static readonly string[] VisionHints =
    [
        "vision", "vl", "-vl", "vl-", "qwen-vl", "qwen2-vl", "qwen2.5-vl", "internvl",
        "minicpm-v", "minicpm", "llava", "cogvlm", "glm-4v", "gpt-4o", "gpt-4.1", "gpt-5",
        "claude-3", "claude-4", "gemini", "pixtral", "molmo", "gemma-3",
    ];

    /// <summary>猜这个模型有没有思考通道。</summary>
    public static bool GuessSupportsReasoning(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return false;
        }

        var id = modelId.ToLowerInvariant();
        return ReasoningHints.Any(hint => id.Contains(hint, StringComparison.Ordinal));
    }

    /// <summary>猜这个模型能不能看图。可被 ModelEntry.SupportsVision 覆盖。</summary>
    public static bool GuessSupportsVision(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return false;
        }

        var id = modelId.ToLowerInvariant();
        return VisionHints.Any(hint => id.Contains(hint, StringComparison.Ordinal));
    }
}
