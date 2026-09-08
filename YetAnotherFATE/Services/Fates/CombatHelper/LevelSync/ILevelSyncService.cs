using YetAnotherFATE.Data.Fates;

namespace YetAnotherFATE.Services.Fates.CombatHelper.LevelSync;

public interface ILevelSyncService
{
    /// <summary>
    /// Ensures the player is level synced for the provided fate selection.
    /// Returns true when the player is either already synced or does not need syncing.
    /// </summary>
    bool EnsureSynced(FateSelection selection);
}
