using System.Text.Json;
using AgentFramework.Agent;
using AgentFramework.Contracts;
using AgentFramework.Data;
using AgentFramework.Index;
using AgentFramework.Kernel;
using AgentFramework.Llm;
using AgentFramework.Tools;

namespace AgentFramework.Host;

public sealed partial class AgentHost
{

    // ── 工作模式 ───────────────────────────────────────────

    /// <summary>当前模式。</summary>
    public AgentMode Mode => _mode;

    /// <summary>当前会话钉住的模式 id（创建时选定；null = 跟随宿主默认）。</summary>
    public string? ModeId => _session.ModeId;

    /// <summary>当前会话实际生效的档位（解析自会话模式；未钉则宿主默认）。</summary>
    public ModeProfile ModeProfile => AgentModes.Resolve(ModeId ?? AgentModes.IdOf(_mode));

    /// <summary>
    /// 设置**新会话的默认模式**（HCI：模式在创建会话时选定后即钉住，
    /// 此方法只影响未钉模式的新会话 —— 老装配路径与测试的兼容入口）。
    /// </summary>
    public void SetMode(AgentMode mode) => _mode = mode;

    /// <summary>
    /// 切换**当前会话**的模式（任务 4）：in-session 切换、无需重启、不重装配 ——
    /// 只改「这一轮给模型看什么」。id 未注册（含内置五档与插件注册）则返回 false。
    /// </summary>
    public bool SetCurrentMode(string? modeId)
    {
        if (!AgentModes.TryResolve(modeId, out var profile))
        {
            return false;
        }

        _session.SetModeId(profile.CustomId ?? AgentModes.IdOf(profile.Mode));
        return true;
    }

    // ── 模型管理 ───────────────────────────────────────────

    /// <summary>
    /// 应用一份新的模型配置：落盘 + **立即生效**，不需要重启。
    ///
    /// 正在跑的那一轮不受影响（它抓的是旧的 client 引用），下一轮才是新的 ——
    /// 「先换引用、再让后续请求看见」这个顺序的价值就在于：不会把一轮对话劈成两半。
    /// </summary>
    public void ApplyModelSettings(ModelSettings settings, bool persist = true)
    {
        if (Models is null)
        {
            ModelSettings = settings;
            return;
        }

        if (persist)
        {
            Models.Save(settings);
        }

        ModelSettings = settings;

        if (_switchable is not null && Hosting.ModelModule.TryBuildActive(settings, Models.Protector, out var client))
        {
            _switchable.Switch(client);
            // 端点配好了 → 不再是离线演示
            UsingOfflineDemo = false;
        }

        // ★ 启动后首次配置端点时，转述器/摘要器还是 null（构造时没有端点）。
        //   这里检测并重建，否则「✨ 优化」和自动摘要永远不可用直到重启。
        if (_rephraser is null && settings.Providers.Count > 0)
        {
            _rephraser = new LlmUserInputRephraser(BuildRuntimeRephraseResolver(settings));
        }

        if (_contextSummarizer is null && settings.Providers.Count > 0
            && Hosting.ModelModule.TryBuildActive(settings, Models?.Protector ?? SecretProtectors.Default, out var summarizerClient))
        {
            _contextSummarizer = new LlmContextSummarizer(summarizerClient);
            SummarizationEnabled = true;
        }
    }

    /// <summary>
    /// 运行期构建转述/摘要解析器 —— <see cref="ApplyModelSettings"/> 时调用。
    /// 与 HostBuilder 的 ResolveRephraseTarget 逻辑等价，但用运行期 ModelSettings。
    /// </summary>
    private Func<string, ILlmClient?> BuildRuntimeRephraseResolver(ModelSettings settings)
    {
        var protector = Models?.Protector ?? SecretProtectors.Default;
        ILlmClient? resolver(string name)
        {
            // active 端点兜底
            ILlmClient? BuildActiveFallback()
            {
                if (Hosting.ModelModule.TryBuildActive(settings, protector, out var activeClient))
                    return activeClient;
                return null;
            }

            if (string.IsNullOrWhiteSpace(name))
                return BuildActiveFallback();

            // "local"/"cloud" 直连（老配置）
            // 不在运行期重建里支持 —— 多端点体系下走 provider 即可。

            // "端点:模型" 形态
            var parts = name.Split(':', 2);
            if (parts.Length == 2)
            {
                var provider = settings.FindProvider(parts[0].Trim());
                var modelId = parts[1].Trim();
                if (provider is not null && !string.IsNullOrWhiteSpace(provider.BaseUrl) && modelId.Length > 0)
                {
                    return new OpenAiCompatibleClient(provider.Id, new OpenAiCompatibleOptions
                    {
                        BaseUrl = provider.BaseUrl,
                        ApiKey = provider.ResolveApiKey(protector) ?? string.Empty,
                        DefaultModel = modelId,
                    });
                }
                return BuildActiveFallback();
            }

            // 只写端点 id
            var whole = settings.FindProvider(name.Trim());
            if (whole is not null && !string.IsNullOrWhiteSpace(whole.BaseUrl))
            {
                var fallbackModel = whole.Models.FirstOrDefault()?.Id;
                return new OpenAiCompatibleClient(whole.Id, new OpenAiCompatibleOptions
                {
                    BaseUrl = whole.BaseUrl,
                    ApiKey = whole.ResolveApiKey(protector) ?? string.Empty,
                    DefaultModel = string.IsNullOrWhiteSpace(fallbackModel) ? "auto" : fallbackModel!,
                });
            }

            return BuildActiveFallback();
        }
        return resolver;
    }

    /// <summary>在某个端点内换个模型（其余配置不动）。</summary>
    public void SwitchModel(string providerId, string modelId)
    {
        if (ModelSettings is null)
        {
            return;
        }

        ModelSettings.Active = new ActiveModelRef { ProviderId = providerId, ModelId = modelId };
        ApplyModelSettings(ModelSettings);
    }

    /// <summary>
    /// 命令沙箱的当前档位（名 / 一句话释义 / 回落说明）。
    ///
    /// <para>
    /// 为什么要在诊断面上露出来：<b>沙箱的强度必须可见</b>。
    /// 配置里写了 <c>job</c> 但机器上回落到了 <c>process</c>，用户却以为自己受内核配额保护 ——
    /// 那比干脆没有沙箱更危险。
    /// </para>
    /// </summary>
    public (string Name, string Description, string? Note) SandboxInfo
    {
        get
        {
            var registry = _state.Sandbox;
            if (registry is null)
            {
                return ("unknown", "沙箱注册表尚未装配", null);
            }

            var backend = registry.Resolve(_state.Options.Sandbox);
            return (backend.Name, backend.Describe(), registry.ResolveNote);
        }
    }
}
