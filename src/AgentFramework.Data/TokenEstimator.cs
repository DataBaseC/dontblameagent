using AgentFramework.Contracts;

namespace AgentFramework.Data;

/// <summary>
/// 粗略 token 估算器（零依赖）。
///
/// 为什么不接真分词器：那会给框架拖进一个模型相关的依赖（表大、还可能带原生产物），
/// 而我们用它只做**水位判断**（该不该压缩、压到多少）—— 估到八九不离十就够，
/// 精确分词换不来任何决策上的差别。
///
/// 经验规则（中英混排够用）：
///   - CJK 字符：约 1 字 ≈ 1 token
///   - 拉丁 / 数字 / 空白：约 4 字符 ≈ 1 token
/// </summary>
public static class TokenEstimator
{
    /// <summary>每条消息的固定协议开销（role、分隔符等）。</summary>
    private const int PerMessageOverhead = 4;

    public static int Estimate(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var cjk = 0;
        var other = 0;

        foreach (var ch in text)
        {
            if (IsCjk(ch))
            {
                cjk++;
            }
            else
            {
                other++;
            }
        }

        return cjk + ((other + 3) / 4);
    }

    /// <summary>
    /// 估算一次请求的消息总开销。
    ///
    /// <para>
    /// <b>工具调用也要数</b>：带 tool_calls 的 assistant 消息里，
    /// 每个调用的 name + arguments JSON 同样占上下文 ——
    /// 一轮 8 个调用、每个 200 字符参数，少算就是 400+ token。
    /// 预算 24k 的会话里水位会系统性偏低（以为 60%，实际 75%+），
    /// 而这套估算器唯一的用途**就是**水位判断，偏了等于白算。
    /// </para>
    /// </summary>
    public static int Estimate(IEnumerable<LlmMessage> messages)
        => messages.Sum(m => PerMessageOverhead
            + Estimate(m.Content)
            + (m.ToolCalls?.Sum(c => Estimate(c.ToolName) + Estimate(c.ArgumentsJson) + 8) ?? 0));

    private static bool IsCjk(char ch) => ch switch
    {
        >= '\u2e80' and <= '\u9fff' => true,  // 部首、假名、汉字
        >= '\uac00' and <= '\ud7af' => true,  // 谚文
        >= '\uf900' and <= '\ufaff' => true,  // 兼容汉字
        >= '\uff00' and <= '\uffef' => true,  // 全角标点
        _ => false,
    };
}
