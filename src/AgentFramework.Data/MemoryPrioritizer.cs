using AgentFramework.Contracts;

namespace AgentFramework.Data;

/// <summary>
/// 把折叠后的记忆视图按档位策略排序、截窗 —— 投影前的最后一道「该让谁被看见」。
///
/// 为什么是静态函数而不是存储的职责：排序属于**装配决策**（模式档位），
/// 存储层只负责「忠实折叠出当前视图 + 检索打分」；
/// 同一份视图在闲聊模式按时间取、在工作模式按热度取 —— 决策归档位。
///
/// Temperature 策略的目标：救回「老而常用」——
/// 纯按时间取最近 N 条，一年前定下的项目约定会被最近一个月的流水账挤出常驻视野；
/// 按热度（命中次数）排序后，常用条目自动浮上来，冷条目自动降级为「仅可检索」（文件里仍在，不丢）。
/// </summary>
public static class MemoryPrioritizer
{
    public static IReadOnlyList<MemoryEntry> Order(
        IReadOnlyList<MemoryEntry> folded,
        MemoryPriorityStrategy strategy)
    {
        if (strategy != MemoryPriorityStrategy.Temperature
            || folded.Count <= 1)
        {
            return folded;
        }

        return [.. folded
            .OrderByDescending(e => e.IsImportant ? 1 : 0)        // 用户显式置顶的最先
            .ThenByDescending(e => e.Score)                        // 热度高的先
            .ThenByDescending(e => e.CreatedAt)];                  // 同分新者先（打破平局）
    }
}
