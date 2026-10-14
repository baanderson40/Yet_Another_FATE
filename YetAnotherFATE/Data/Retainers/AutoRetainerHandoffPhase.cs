namespace YetAnotherFATE.Data.Retainers;

public enum AutoRetainerHandoffPhase
{
    Idle,
    PendingSafeBoundary,
    Starting,
    WaitingForAutoRetainer,
    ReturningOriginalCharacter,
    WaitingForOriginalCharacter,
    ReturningOriginalTerritory,
    Resuming,
    Failed,
    PreparingSafeLocation = 9,
    SwitchingFishingCharacter = 10,
    WaitingForFishingCharacter = 11,
    StartingOceanVoyage = 12,
    WaitingForOceanVoyage = 13,
    WaitingForWahToolsStop = 14,
    ChainingAutoRetainer = 15,
}
