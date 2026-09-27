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
}
