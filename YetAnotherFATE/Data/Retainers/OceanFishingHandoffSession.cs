using System;
using System.Numerics;

namespace YetAnotherFATE.Data.Retainers;

[Serializable]
public sealed class OceanFishingHandoffSession
{
    public AutoRetainerHandoffPhase Phase { get; set; }
    public DateTime WindowStartUtc { get; set; }
    public ulong OriginalContentId { get; set; }
    public uint OriginalTerritoryId { get; set; }
    public Vector3 OriginalPosition { get; set; }
    public string OriginalCharacterName { get; set; } = string.Empty;
    public string OriginalWorldName { get; set; } = string.Empty;
    public ulong FishingContentId { get; set; }
    public string FishingCharacterName { get; set; } = string.Empty;
    public string FishingWorldName { get; set; } = string.Empty;
    public bool WasAutomatorEnabled { get; set; }
    public bool LaunchRequested { get; set; }
    public bool LaunchAccepted { get; set; }
    public bool LaunchAmbiguous { get; set; }
    public bool DutyStarted { get; set; }
    public bool DutyCompleted { get; set; }
    public bool OceanFishingLegSkipped { get; set; }
    public bool OceanTerritoryEntered { get; set; }
    public bool OceanTerritoryExited { get; set; }
    public bool StopRequested { get; set; }
    public DateTime? StopRequestedAtUtc { get; set; }
    public DateTime? StopAcceptedAtUtc { get; set; }
    public long? StopStatusGeneration { get; set; }
    public bool StopAccepted { get; set; }
    public bool WindowConsumed { get; set; }
    public bool YesAlreadyLeaseRequired { get; set; }
    public bool YesAlreadyLeaseAcquired { get; set; }
    public bool RetainerLegRequested { get; set; }
    public bool RetainerLegObserved { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset PhaseStartedAtUtc { get; set; }
    public string? LastError { get; set; }
}
