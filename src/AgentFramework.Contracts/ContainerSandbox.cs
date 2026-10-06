namespace AgentFramework.Contracts;

/// <summary>
/// 容器沙箱的网络策略。
///
/// <para>
/// 为什么单独列出来而不是布尔：容器能做的网络隔离有好几档，
/// 「完全隔绝」与「走宿主网络」之间还有「走默认 NAT 网桥」——
/// 装依赖要出网、跑不可信脚本要断网，是两个真实存在、强度不同的诉求。
/// </para>
/// </summary>
public enum ContainerNetwork
{
    /// <summary><c>--network none</c>：完全无网络。<b>默认档</b> —— 容器最容易管的恰恰是网络。</summary>
    None = 0,

    /// <summary><c>--network bridge</c>：默认 NAT 网桥，可出网（<c>npm install</c> 之类靠它）。</summary>
    Bridge = 1,

    /// <summary><c>--network host</c>：直接共享宿主网络栈（隔离最弱，慎用）。</summary>
    Host = 2,
}

/// <summary>
/// 容器沙箱后端（Docker / Podman）的配置。
///
/// <para>
/// <b>为什么必须有镜像</b>：容器与 <c>bwrap</c> 不同 —— bwrap 借宿主的根文件系统，
/// 容器则要一个独立 rootfs。这个 rootfs 就是镜像，而「用哪个镜像」没有普适默认
/// （跑 .NET 要 sdk、跑 Node 要 node、跑 Python 要 python），只能由运维显式指定。
/// 所以容器档<b>不进 auto</b>：它是显式选用的档，没配镜像就不该悄悄替你挑一个。
/// </para>
/// </summary>
public sealed record ContainerSandboxOptions
{
    /// <summary>镜像名，如 <c>mcr.microsoft.com/dotnet/sdk:10.0</c> / <c>node:22</c>。空 = 未配置（档不可用）。</summary>
    public string? Image { get; init; }

    /// <summary>容器运行时：<c>docker</c> / <c>podman</c> / 可执行文件路径。空 = 自动探测（docker 优先）。</summary>
    public string? Runtime { get; init; }

    /// <summary>网络策略。<b>默认 None（禁网）</b>；装依赖等需要出网时设为 Bridge。</summary>
    public ContainerNetwork Network { get; init; } = ContainerNetwork.None;

    /// <summary>
    /// 根文件系统只读（配合 <c>--tmpfs /tmp</c> 给一块可写临时区）。
    /// 默认 true：命令只能写工作区与 /tmp，改不动镜像本身。
    /// </summary>
    public bool ReadOnlyRoot { get; init; } = true;

    /// <summary>容器内用来执行命令的 shell。<c>/bin/sh</c> 是绝大多数镜像的最低公共保证。</summary>
    public string ShellPath { get; init; } = "/bin/sh";

    /// <summary>镜像是否已配置齐全（空了就不该选中这一档）。</summary>
    public bool HasImage => !string.IsNullOrWhiteSpace(Image);

    /// <summary>把配置里的网络名解析成枚举；认不出来时回落 None 并给出说明。</summary>
    public static (ContainerNetwork Network, string? Note) ParseNetwork(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (ContainerNetwork.None, null);
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "none" => (ContainerNetwork.None, null),
            "bridge" => (ContainerNetwork.Bridge, null),
            "host" => (ContainerNetwork.Host, null),
            _ => (ContainerNetwork.None, $"网络策略「{value}」不认识，已按 none 处理（可选：none / bridge / host）"),
        };
    }

    /// <summary>网络策略的 docker 参数值。</summary>
    public string NetworkValue => Network switch
    {
        ContainerNetwork.Bridge => "bridge",
        ContainerNetwork.Host => "host",
        _ => "none",
    };
}
