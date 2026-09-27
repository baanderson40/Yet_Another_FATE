namespace YetAnotherFATE.Data.Retainers;

public enum AutoRetainerHandoffPhase
{
    Idle,
    PendingSafeBoundary,
    Starting,
    WaitingForAutoRetainer,
    ReturningOriginalCharacter,
    WaitingForOriginalCharacter,
    Resuming,
    Failed,
}
