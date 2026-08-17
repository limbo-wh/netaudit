namespace NetAudit.Core.GameBoost;

/// <summary>Какие твики применять при включении разгона.</summary>
public sealed record GameBoostOptions(
    bool HighPerfPowerPlan,
    bool MuteNotifications,
    bool DisableVisualEffects,
    bool StopBackgroundServices,
    bool BoostGamePriority,
    IReadOnlyList<string> ProcessesToClose);

/// <summary>Что реально произошло при включении — для строки статуса и лога.</summary>
public sealed class GameBoostReport
{
    public List<string> Applied { get; } = [];
    public List<string> Skipped { get; } = [];
    public List<string> Closed  { get; } = [];
}
