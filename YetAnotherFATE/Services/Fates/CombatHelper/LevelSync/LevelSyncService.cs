using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using YetAnotherFATE.Data.Fates;

namespace YetAnotherFATE.Services.Fates.CombatHelper.LevelSync;

public sealed class LevelSyncService : ILevelSyncService
{
    private static readonly TimeSpan AttemptCooldown = TimeSpan.FromSeconds(5);

    private const int LevelGapThreshold = 5;

    private readonly IPlayerState playerState;
    private readonly ICondition condition;
    private readonly IPluginLog log;
    private DateTimeOffset nextAttemptAt = DateTimeOffset.MinValue;

    public LevelSyncService(
        IPlayerState playerState,
        ICondition condition,
        IPluginLog log)
    {
        this.playerState = playerState;
        this.condition = condition;
        this.log = log;
    }

    public bool EnsureSynced(FateSelection selection)
    {
        if (!NeedsSync(selection))
        {
            return true;
        }

        if (!CanAttemptSync())
        {
            return false;
        }

        TrySendLevelSync(selection);
        return false;
    }

    private bool NeedsSync(FateSelection selection)
    {
        if (!playerState.IsLoaded)
        {
            return false;
        }

        if (playerState.IsLevelSynced)
        {
            return false;
        }

        var playerLevel = playerState.Level;
        var fateLevel = selection.Fate.Level;
        return playerLevel >= fateLevel + LevelGapThreshold;
    }

    private bool CanAttemptSync()
    {
        if (DateTimeOffset.UtcNow < nextAttemptAt)
        {
            return false;
        }

        if (condition[ConditionFlag.BetweenAreas])
        {
            return false;
        }

        if (condition[ConditionFlag.InCombat])
        {
            return false;
        }

        return true;
    }

    private void TrySendLevelSync(FateSelection selection)
    {
        try
        {
            unsafe
            {
                var fateManager = FateManager.Instance();
                if (fateManager == null)
                {
                    log.Debug("LevelSyncService: FateManager unavailable; cannot level sync right now.");
                    return;
                }

                fateManager->LevelSync();
            }

            log.Debug(
                "LevelSyncService: issued level sync for fate #{FateId} (player level {PlayerLevel} > fate level {FateLevel})",
                selection.Fate.FateId,
                playerState.Level,
                selection.Fate.Level);
        }
        catch (Exception ex)
        {
            log.Error(ex, "LevelSyncService: LevelSync call failed");
        }
        finally
        {
            nextAttemptAt = DateTimeOffset.UtcNow + AttemptCooldown;
        }
    }
}
