using System.Reflection;
using System.Runtime.Loader;
using AgentFramework.Contracts;

namespace AgentFramework.Kernel;

/// <summary>
/// 插件加载上下文（每个插件版本一个，collectible）。
///
/// 三条铁律（DESIGN.md 4.3）：
///   1. <b>契约程序集必须走 default ALC</b> —— Load 返回 null。
///      否则插件里的 IPlugin 与宿主的 IPlugin 是两个不同的 Type，
///      <c>(IPlugin)instance</c> 会在运行时炸。
///   2. <b>同名程序集可多版本共存</b> —— 每版独立 ALC，所以插件更新无需等旧版卸净。
///   3. <b>插件自有依赖从插件目录解析</b> —— 用 AssemblyDependencyResolver，
///      避免和宿主带的版本打架。
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private static readonly string ContractsAssemblyName =
        typeof(IPlugin).Assembly.GetName().Name!;

    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string mainAssemblyPath, string name)
        : base(name, isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // 铁律 1：契约程序集返回 null，交给 default ALC 共享加载
        if (string.Equals(assemblyName.Name, ContractsAssemblyName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // 铁律 3：插件自有依赖从插件目录解析；解析不到则回落 default（BCL 等）
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
