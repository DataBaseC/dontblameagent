using System.Text.Json;
using AgentFramework.Contracts;

namespace AgentFramework.Host;

/// <summary>
/// 上下文治理设置（<see cref="ContextOptions"/> + <see cref="CheckpointOptions"/>）的持久化
/// （<c>agent-sessions/context.json</c>）。
///
/// <para>
/// 与转述设置同一纪律：<b>偏好不进会话 JSONL</b>。JSONL 记「这次对话发生了什么」，
/// 这里存「用户把水位调到了多少」—— 两者生命周期不同，会话删了偏好也不该跟着消失。
/// </para>
///
/// <para>
/// 两个水位（写盘 <see cref="CheckpointOptions.TriggerRatio"/> / 裁剪
/// <see cref="ContextOptions.CompressionTriggerRatio"/>）在同一个文件里，但各自独立存 ——
/// 它们是两个触发点，不合并成一个。
/// </para>
/// </summary>
internal static class ContextSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>落盘形态：把两份配置打成一个包。</summary>
    private sealed class Snapshot
    {
        public ContextOptions? Context { get; set; }

        public CheckpointOptions? Checkpoint { get; set; }
    }

    public static string PathFor(string sessionsDir) => Path.Combine(sessionsDir, "context.json");

    /// <summary>
    /// 读回设置。<paramref name="fallbackContext"/> / <paramref name="fallbackCheckpoint"/>
    /// 是调用方预置的值（默认值，或验收测试注入的）—— 读不到文件时原样返回，不覆盖成默认。
    /// </summary>
    public static (ContextOptions Context, CheckpointOptions Checkpoint) Load(
        string sessionsDir,
        ContextOptions? fallbackContext = null,
        CheckpointOptions? fallbackCheckpoint = null)
    {
        var context = fallbackContext ?? new ContextOptions();
        var checkpoint = fallbackCheckpoint ?? new CheckpointOptions();

        try
        {
            var path = PathFor(sessionsDir);
            if (!File.Exists(path))
            {
                return (context, checkpoint);
            }

            var snapshot = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path), SerializerOptions);
            return snapshot is null
                ? (context, checkpoint)
                : (snapshot.Context ?? context, snapshot.Checkpoint ?? checkpoint);
        }
        catch
        {
            // 配置文件坏掉不该让应用起不来 —— 退回调用方给的值即可。
            return (context, checkpoint);
        }
    }

    public static void Save(string sessionsDir, ContextOptions context, CheckpointOptions checkpoint)
    {
        try
        {
            Directory.CreateDirectory(sessionsDir);
            var snapshot = new Snapshot { Context = context, Checkpoint = checkpoint };
            File.WriteAllText(PathFor(sessionsDir), JsonSerializer.Serialize(snapshot, SerializerOptions));
        }
        catch
        {
            // 存不下也只影响下次启动，本次设置仍然生效。
        }
    }
}
