namespace YetAnotherFATE.Data.Automator;

public enum RecoveryPhase
{
    Idle,
    WaitingForRaise,
    AcceptingRaise,
    WaitingForRelease,
    ConfirmingRelease,
    Recovered,
    Failed,
    ReturningToZone,
}
