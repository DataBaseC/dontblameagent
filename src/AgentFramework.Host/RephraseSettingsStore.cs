using System.Text.Json;
using AgentFramework.Contracts;

namespace AgentFramework.Host;

/// <summary>
/// 转述设置的持久化（<c>agent-sessions/rephrase.json</c>）。
///
/// 刻意**不进会话 JSONL**：JSONL 记的是「这次对话发生了什么」，
/// 而这里存的是「用户偏好什么」—— 两者生命周期与归属都不同。
/// 会话可以删，偏好不该跟着消失。
/// </summary>
internal static class RephraseSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string PathFor(string sessionsDir) => Path.Combine(sessionsDir, "rephrase.json");

    /// <param name="fallback">
    /// 读不到文件时用什么。调用方可能已经预置了配置（比如验收测试注入），
    /// 这时不该被「文件不存在」覆盖成默认值。
    /// </param>
    public static RephraseOptions Load(string sessionsDir, RephraseOptions? fallback = null)
    {
        var effective = fallback ?? new RephraseOptions();

        try
        {
            var path = PathFor(sessionsDir);
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<RephraseOptions>(File.ReadAllText(path), SerializerOptions)
                    ?? effective;
            }
        }
        catch
        {
            // 配置文件坏掉不该让应用起不来 —— 退回调用方给的值即可
        }

        return effective;
    }

    public static void Save(string sessionsDir, RephraseOptions options)
    {
        try
        {
            Directory.CreateDirectory(sessionsDir);
            File.WriteAllText(
                PathFor(sessionsDir),
                JsonSerializer.Serialize(options, SerializerOptions));
        }
        catch
        {
            // 存不下也只影响下次启动，本次设置仍然生效
        }
    }
}
