using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentFramework.Host;

/// <summary>当前的模型配置（从配置单读出来，可被界面改写）。</summary>
public sealed class ModelSettings
{
    public List<ProviderConfig> Providers { get; init; } = [];

    public ActiveModelRef? Active { get; set; }

    public ProviderConfig? FindProvider(string? id)
        => id is null ? null : Providers.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));

    public ProviderConfig? ActiveProvider
        => Active is null ? Providers.FirstOrDefault() : FindProvider(Active.ProviderId) ?? Providers.FirstOrDefault();

    public ModelEntry? ActiveModel
    {
        get
        {
            var provider = ActiveProvider;
            if (provider is null)
            {
                return null;
            }

            return Active is null
                ? provider.Models.FirstOrDefault()
                : provider.Models.FirstOrDefault(m => string.Equals(m.Id, Active.ModelId, StringComparison.Ordinal))
                  ?? provider.Models.FirstOrDefault();
        }
    }

    /// <summary>当前真正生效的「端点 + 模型」。没有任何配置时为 null。</summary>
    public (ProviderConfig Provider, ModelEntry Model)? Current
        => ActiveProvider is { } provider && ActiveModel is { } model ? (provider, model) : null;
}

/// <summary>
/// 密钥保护。
///
/// 抽象出来是为了两件事：一是 Windows 上用 DPAPI（只有当前用户能解），
/// 二是**在没有该能力的平台上能诚实地降级** —— 而不是假装加密了。
/// </summary>
public interface ISecretProtector
{
    string Kind { get; }

    /// <summary>为 false 时，调用方应当把密钥按明文处理（别谎称加了密）。</summary>
    bool IsAvailable { get; }

    string Protect(string plaintext);

    string Unprotect(string protectedValue);
}

/// <summary>Windows DPAPI（CurrentUser）：同机器同用户才能解开。</summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AgentFramework.ModelKey.v1");

    public string Kind => "dpapi";

    public bool IsAvailable { get; } = OperatingSystem.IsWindows();

    public string Protect(string plaintext)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI 只在 Windows 上可用");
        }

        return Convert.ToBase64String(ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser));
    }

    public string Unprotect(string protectedValue)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI 只在 Windows 上可用");
        }

        return Encoding.UTF8.GetString(ProtectedData.Unprotect(
            Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser));
    }
}

public static class SecretProtectors
{
    public static ISecretProtector Default { get; } = new DpapiSecretProtector();
}

/// <summary>
/// 模型配置的读写 —— 落在同一个 <c>agent.json</c> 上。
///
/// 两个刻意的设计：
///   1. <b>老配置单自动迁移</b>：只写了 cloud / local 的老文件，读出来会被当成两个 provider，
///      不需要主人手工改写。配置格式演进不该让旧文件报废。
///   2. <b>只改自己那几个字段</b>：写回时读原 JSON、只替换 providers / active，
///      别的字段（工作区、端口、注释外的内容）原样保留 —— 界面改模型不该抹掉别的东西。
/// </summary>
public sealed class ModelSettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>
    /// 读配置单的选项。
    /// <b>必须容忍注释和尾逗号</b> —— 配置单是人手写的，示例里到处都是说明注释。
    /// 不容忍的后果不是「读不了」，而是保存时把整份文件当成坏的、只写回自己那两个键，
    /// 等于把主人的配置**静默抹掉**。
    /// </summary>
    private static readonly JsonDocumentOptions ConfigJsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly ISecretProtector _protector;

    public ModelSettingsStore(string path, ISecretProtector? protector = null)
    {
        Path = path;
        _protector = protector ?? SecretProtectors.Default;
    }

    public string Path { get; }

    public ISecretProtector Protector => _protector;

    public ModelSettings Load()
    {
        var config = AgentConfig.TryLoad(Path) ?? new AgentConfig();
        var providers = ResolveProviders(config);

        var active = config.Active;
        if (active is null || providers.All(p => !string.Equals(p.Id, active.ProviderId, StringComparison.Ordinal)))
        {
            active = providers.Count > 0
                ? new ActiveModelRef
                {
                    ProviderId = providers[0].Id,
                    ModelId = providers[0].Models.FirstOrDefault()?.Id ?? string.Empty,
                }
                : null;
        }

        return new ModelSettings { Providers = providers, Active = active };
    }

    /// <summary>
    /// 写回配置单（原子写：先写临时文件再替换）。
    ///
    /// 三条自我约束：
    ///   1. <b>只动自己那两个键</b>（providers / active），别的字段原样留着 ——
    ///      界面改模型不该顺手抹掉工作区、端口这些设置；
    ///   2. 写过 providers 就把老的 cloud / local 摘掉 —— 同一件事不能有两个真相；
    ///   3. <b>改之前先留一份 .bak</b>：配置单是人手写的，界面操作不该是不可逆的。
    /// </summary>
    public void Save(ModelSettings settings)
    {
        var node = ReadRootObject();
        BackupOnce();

        node["providers"] = JsonSerializer.SerializeToNode(settings.Providers, Json);

        // 老格式（cloud / local）已经由 providers 表达了，留两份迟早打架
        node.Remove("cloud");
        node.Remove("local");

        if (settings.Active is null)
        {
            node.Remove("active");
        }
        else
        {
            node["active"] = JsonSerializer.SerializeToNode(settings.Active, Json);
        }

        WriteAtomic(node);
    }

    /// <summary>第一次改动前备份一份原始配置单，且只备份一次（保住最早那份）。</summary>
    private void BackupOnce()
    {
        try
        {
            if (!File.Exists(Path))
            {
                return;
            }

            var backup = Path + ".bak";
            if (!File.Exists(backup))
            {
                File.Copy(Path, backup);
            }
        }
        catch (IOException)
        {
            // 备份失败不该拦住保存本身
        }
    }

    /// <summary>把一个端点的密钥按当前平台能力落盘：能加密就加密，不能就如实存明文。</summary>
    public void ApplySecret(ProviderConfig provider, string? plaintext)
    {
        if (string.IsNullOrWhiteSpace(plaintext))
        {
            provider.ApiKey = null;
            provider.ApiKeyProtected = null;
            return;
        }

        if (_protector.IsAvailable)
        {
            provider.ApiKeyProtected = _protector.Protect(plaintext);
            provider.ApiKey = null;
        }
        else
        {
            // 没有加密能力的平台（比如在 Linux 上跑）就存明文 —— 但至少别谎称加了密
            provider.ApiKey = plaintext;
            provider.ApiKeyProtected = null;
        }
    }

    /// <summary>老配置单兼容：只写了 cloud / local 时，把它们看成两个 provider。</summary>
    private static List<ProviderConfig> ResolveProviders(AgentConfig config)
    {
        if (config.Providers is { Count: > 0 })
        {
            return config.Providers;
        }

        var providers = new List<ProviderConfig>();

        if (IsConfigured(config.Cloud))
        {
            providers.Add(new ProviderConfig
            {
                Id = "cloud",
                Name = "云端",
                BaseUrl = config.Cloud!.BaseUrl!,
                ApiKey = config.Cloud.ApiKey,
                Models = [new ModelEntry { Id = config.Cloud.Model! }],
            });
        }

        if (IsConfigured(config.Local))
        {
            providers.Add(new ProviderConfig
            {
                Id = "local",
                Name = "本地",
                BaseUrl = config.Local!.BaseUrl!,
                Models = [new ModelEntry { Id = config.Local.Model! }],
            });
        }

        return providers;

        static bool IsConfigured(EndpointConfig? endpoint)
            => !string.IsNullOrWhiteSpace(endpoint?.BaseUrl) && !string.IsNullOrWhiteSpace(endpoint.Model);
    }

    private JsonObject ReadRootObject()
    {
        if (!File.Exists(Path))
        {
            return [];
        }

        try
        {
            return JsonNode.Parse(
                File.ReadAllText(Path),
                nodeOptions: null,
                documentOptions: ConfigJsonOptions) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            // 真坏了才以空对象为底重建
            return [];
        }
    }

    private void WriteAtomic(JsonObject node)
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var temporary = Path + ".tmp";

        File.WriteAllText(temporary, json);
        File.Move(temporary, Path, overwrite: true);
    }
}
