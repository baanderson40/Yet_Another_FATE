using System;
using System.Numerics;

namespace YetAnotherFATE.Data.Retainers;

[Serializable]
public sealed class AutoRetainerHandoffSession
{
    public AutoRetainerHandoffPhase Phase { get; set; }
    public ulong OriginalContentId { get; set; }
    public uint OriginalTerritoryId { get; set; }
    public Vector3 OriginalPosition { get; set; }
    public string OriginalCharacterName { get; set; } = string.Empty;
    public string OriginalWorldName { get; set; } = string.Empty;
    public bool WasAutomatorEnabled { get; set; }
    public AutoRetainerMode Mode { get; set; } = AutoRetainerMode.Retainers;
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset PhaseStartedAtUtc { get; set; }
    public bool StartedByYaf { get; set; }
    public bool RequestIssued { get; set; }
    public bool ObservedAutoRetainerActive { get; set; }
    public bool CompletionEventObserved { get; set; }
    public string? LastError { get; set; }
}
