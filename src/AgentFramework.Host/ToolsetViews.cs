namespace AgentFramework.Host;

/// <summary>
/// 工具包的一个视图。
///
/// <para>
/// 界面那一排开关、诊断面的 <c>toolsets</c> 段、agent 手上的 <c>toolsets</c> 工具，
/// 读的都是它 —— 一份数据三处用，才不会出现「界面说开着、实际没暴露」这种错位。
/// </para>
/// </summary>
public sealed record ToolsetView(
    string Id,
    string Name,
    string? Description,
    IReadOnlyList<string> Tools,
    bool Enabled,
    bool Protected,
    bool Eager,
    string Source);
