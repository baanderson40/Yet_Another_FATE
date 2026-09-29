using YetAnotherFATE.Data.Companions;

namespace YetAnotherFATE.Services.Companions;

public readonly record struct ChocoboSnapshot(
    bool PlayerReady,
    bool IsSpawned,
    bool IsStabled,
    bool IsMounted,
    float TimeLeftSeconds,
    byte ActiveCommand,
    int GreensCount,
    uint CurrentHealth = 0,
    uint MaxHealth = 0)
{
    public bool IsActive => IsSpawned && TimeLeftSeconds > 0;
    public bool IsAlive => IsActive && (MaxHealth == 0 || CurrentHealth > 0);
}

public enum ChocoboActionResult
{
    Issued,
    AlreadySatisfied,
    NotReady,
    Busy,
    Mounted,
    InStable,
    NoGreens,
    Unavailable,
    Rejected,
}

public interface IChocoboCompanionApi
{
    ChocoboSnapshot Read();
    ChocoboActionResult TryUseGreens();
    ChocoboActionResult TrySetStance(ChocoboStance stance);
    ChocoboActionResult TryDismount();
}
