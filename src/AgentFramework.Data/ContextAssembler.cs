using AgentFramework.Contracts;

namespace AgentFramework.Data;

/// <summary>
/// 把「冻结段」与「动态段」装配成一次请求的 messages。
///
/// 存在的理由只有一个：**让前缀的稳定性可被执行与可被验证，而不是靠自觉。**
///
/// 原来把记忆块 <c>Insert(0, …)</c> 的写法，一旦某轮记忆变化，
/// 后面所有消息的位置都会整体位移 —— 前缀缓存整段失效。
/// 而 Anthropic 官方 prompt-caching 文档把这件事列为 "common mistake"，
/// 代价还特别隐蔽：cache write 按 1.25× 基准价计费，断点打不好**比不用缓存更贵**。
///
/// 所以规矩定死在这里：
///   · 只有**会话内不变**的内容才允许进冻结段（模式说明、记忆索引卡）
///   · 任何**每轮可能变**的内容（检索结果、任务卡、时间戳）一律走动态段
///   · 动态段永远排在冻结段之后 —— 它怎么变都不影响前面的字节
/// </summary>
public static class ContextAssembler
{
    /// <summary>
    /// 装配上下文。<paramref name="frozenBlock"/> 为 null/空白时退化为「全部动态」。
    /// </summary>
    public static AssembledContext Assemble(string? frozenBlock, IReadOnlyList<LlmMessage> dynamicMessages)
    {
        if (string.IsNullOrWhiteSpace(frozenBlock))
        {
            return AssembledContext.AllDynamic(dynamicMessages);
        }

        var messages = new List<LlmMessage>(dynamicMessages.Count + 1)
        {
            new() { Role = LlmRole.System, Content = frozenBlock },
        };

        messages.AddRange(dynamicMessages);
        return new AssembledContext(messages, FrozenCount: 1);
    }
}
