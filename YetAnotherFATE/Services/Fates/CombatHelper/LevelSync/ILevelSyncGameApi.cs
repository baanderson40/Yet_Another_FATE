using YetAnotherFATE.Data.Fates;

namespace YetAnotherFATE.Services.Fates.CombatHelper.LevelSync;

public readonly record struct LevelSyncSnapshot(
    bool PlayerReady,
    bool NativeAvailable,
    uint TerritoryId,
    uint ClassJobId,
    int PlayerLevel,
    int EffectiveLevel,
    uint CurrentFateId,
    int CurrentFateStartTimeEpoch,
    byte CurrentFateLevel,
    byte CurrentFateMaxLevel,
    bool IsSyncedToCurrentFate);

public enum LevelSyncRequestResult
{
    Requested,
    AlreadyReady,
    WrongCurrentFate,
    NotReady,
    Unavailable,
    Failed,
}

public interface ILevelSyncGameApi
{
    LevelSyncSnapshot Read();
    LevelSyncRequestResult TryRequestSync(FateSelection selection);
}
