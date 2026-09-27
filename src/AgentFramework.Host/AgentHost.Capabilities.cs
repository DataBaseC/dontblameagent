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

    /// <summary>
    /// 重新扫描技能（导入工坊条目后调用）：skills/ 与 workshop/ 两目录重算，
    /// 同名内置优先。已启用技能集合按名字保留 —— 重扫不丢开关状态。
    /// </summary>
    public void ReloadSkills()
    {
        _state.Skills = SkillLoader.ScanWorkspace(Options.WorkspaceRoot);

        // 开关集合里已不存在的技能名清掉（导入失败重扫 / 手动删目录后的残账）
        var known = _state.Skills.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        _state.EnabledSkills.RemoveWhere(name => !known.Contains(name));
    }

    /// <summary>启用/停用技能（回合边界生效：VisibleTools 每轮现取，下一轮就变）。</summary>
    public bool SetSkillEnabled(string name, bool enabled)
    {
        return enabled ? _state.EnabledSkills.Add(name) : _state.EnabledSkills.Remove(name);
    }

    /// <summary>
    /// 包是否处于暴露态 —— 与 <c>VisibleToolsCore</c> 同谓词
    /// （模式 AllowedToolsets / AllowedTools 空集 / DisabledToolsets）。
    ///
    /// 只看 DisabledToolsets 时，闲聊模式（AllowedTools = 空集）下包仍显示为开，
    /// 界面开关与实际暴露面就对不上了。
    /// </summary>
    private bool IsToolsetExposed(string toolsetId)
    {
        var profile = _state.CurrentProfile;

        // 与 VisibleToolsCore 的 AllowedTools 三态对齐：空集 = 一个工具都不发。
        if (profile.AllowedTools is { Count: 0 })
        {
            return false;
        }

        if (profile.AllowedToolsets is not null
            && !profile.AllowedToolsets.Contains(toolsetId, StringComparer.Ordinal))
        {
            return false;
        }

        return !_state.DisabledToolsets.Contains(toolsetId);
    }

    /// <summary>
    /// 开/关一个工具包。返回 <c>false</c> = 没改成（包不存在，或是不许关的保留包）。
    ///
    /// <para>
    /// 保留包（core / meta）拒绝关闭：关掉「读文件 + 写文件 + 列目录 + 问用户」会把 agent 关成残废；
    /// 关掉「工具包开关」这个工具，就再也没有办法开回来了。
    /// </para>
    /// </summary>
    public bool SetToolsetEnabled(string toolsetId, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(toolsetId))
        {
            return false;
        }

        var descriptors = _plugins.Toolsets;
        if (!descriptors.TryGetValue(toolsetId, out var descriptor))
        {
            return false;
        }

        if (!enabled && (descriptor.Protected || Contracts.BuiltinToolsets.Protected.Contains(toolsetId)))
        {
            return false;
        }

        return enabled
            ? _state.DisabledToolsets.Remove(toolsetId)
            : _state.DisabledToolsets.Add(toolsetId);
    }

    /// <summary>
    /// 向**指定会话**落一条事件（G1 子 Agent 编排用）：从会话表取它的 Sink，
    /// 不经过「当前会话」指针 —— 派发/完成留痕必须落在父会话自己的流里。
    /// </summary>
    public async ValueTask EmitToSessionAsync(string sessionId, SessionEvent sessionEvent, CancellationToken ct = default)
    {
        Hosting.SessionRuntime? runtime;
        lock (_sessions)
        {
            _sessions.TryGetValue(sessionId, out runtime);
        }

        if (runtime is null)
        {
            throw new InvalidOperationException($"会话不存在：{sessionId}");
        }

        await runtime.Sink.EmitAsync(sessionEvent, ct).ConfigureAwait(false);
    }
}
