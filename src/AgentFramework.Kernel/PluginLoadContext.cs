using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using AgentFramework.Contracts;

// 验收面：Verify/VerifyPlugins 要直接观察 Load 的「共享 / 私有」裁决，
// 证明跨 ALC 后 ILogger 等契约传递类型没有被双载。只给这两个验收程序集。
[assembly: InternalsVisibleTo("AgentFramework.Verify")]
[assembly: InternalsVisibleTo("AgentFramework.VerifyPlugins")]

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

    /// <summary>
    /// 必须与宿主<b>共享</b>（Load 返回 null → 走 default ALC）的程序集名单。
    ///
    /// <para>
    /// 为什么不止 Contracts：契约的公开面把第三方类型直接露给了插件 ——
    /// <c>IPluginContext.Log</c> 是 <c>Microsoft.Extensions.Logging.ILogger</c>，
    /// <c>SessionEvents</c> 挂着 <c>System.Text.Json</c> 的多态标注。
    /// 这些是契约的<b>传递依赖</b>：一旦被插件 ALC 各自再载一份，
    /// 「同一个 ILogger / JsonPolymorphic」就裂成两个 Type ——
    /// 插件调 <c>ctx.Log.LogInformation</c> 会在 castclass 上当场炸，
    /// 而且炸得很冤：代码看起来毫无问题。
    /// </para>
    /// <para>
    /// 不整段 <c>System.*</c> 全共享：铁律 2 允许插件带私有版本的第三方库。
    /// 只点名真正出现在契约/跨边界签名上的那些 —— 共享面越小，版本共存越自由。
    /// </para>
    /// </summary>
    private static readonly HashSet<string> SharedAssemblyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ContractsAssemblyName,

        // 契约传递依赖：ILogger / ILoggerFactory 出现在 IPluginContext 公开面上
        "Microsoft.Extensions.Logging.Abstractions",
        "Microsoft.Extensions.Logging",

        // 契约传递依赖：SessionEvents 的 [JsonPolymorphic] / [JsonDerivedType] 来自这里
        "System.Text.Json",

        // BCL 门面 / 核心：双载要么类型破裂，要么把同一个类型变成两个 Type
        "System.Runtime",
        "System.Runtime.Extensions",
        "System.Private.CoreLib",
        "netstandard",
        "mscorlib",
        "System.Collections",
        "System.Linq",
        "System.Threading",
        "System.Threading.Tasks",
        "System.Runtime.InteropServices",
    };

    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string mainAssemblyPath, string name)
        : base(name, isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
    }

    /// <summary>
    /// 是否必须与宿主共享（走 default ALC）。
    /// 单独抽出来是为了可测：共享名单一旦被改小，验收必须红。
    /// </summary>
    internal static bool IsSharedAssembly(string? assemblyName)
    {
        if (string.IsNullOrEmpty(assemblyName))
        {
            return false;
        }

        if (SharedAssemblyNames.Contains(assemblyName))
        {
            return true;
        }

        // 日志家族整支共享：Contracts 把 ILogger 暴露在公开面上，
        // 任何一门日志相关程序集被双载都会撕裂类型同一性。
        return assemblyName.StartsWith("Microsoft.Extensions.Logging", StringComparison.OrdinalIgnoreCase);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // 铁律 1（扩展）：契约 + 其传递的 BCL/日志抽象一律返回 null，交给 default 共享加载。
        // 必须卡在 resolver 之前 —— deps.json 里若有本地副本，resolver 会把它找出来，
        // 那正是「插件自带依赖」发布形态下双载的入口。
        if (IsSharedAssembly(assemblyName.Name))
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
