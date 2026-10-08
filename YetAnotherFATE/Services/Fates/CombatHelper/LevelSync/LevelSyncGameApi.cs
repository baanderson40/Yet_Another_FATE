using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using YetAnotherFATE.Data.Fates;

namespace YetAnotherFATE.Services.Fates.CombatHelper.LevelSync;

public sealed unsafe class LevelSyncGameApi : ILevelSyncGameApi
{
    private readonly IClientState clientState;
    private readonly IPlayerState playerState;
    private readonly ICondition condition;
    private readonly IPluginLog log;

    public LevelSyncGameApi(IClientState clientState, IPlayerState playerState, ICondition condition, IPluginLog log)
    {
        this.clientState = clientState;
        this.playerState = playerState;
        this.condition = condition;
        this.log = log;
    }

    public LevelSyncSnapshot Read()
    {
        var playerReady = clientState.IsLoggedIn && playerState.IsLoaded && playerState.ClassJob.IsValid;
        if (!playerReady)
            return default;

        try
        {
            var manager = FateManager.Instance();
            if (manager == null)
                return new LevelSyncSnapshot(true, false, clientState.TerritoryType, (uint)playerState.ClassJob.Value.RowId,
                    playerState.Level, playerState.EffectiveLevel, 0, 0, 0, 0, false);

            var fate = manager->CurrentFate;
            if (fate == null)
                return new LevelSyncSnapshot(true, true, clientState.TerritoryType, (uint)playerState.ClassJob.Value.RowId,
                    playerState.Level, playerState.EffectiveLevel, 0, 0, 0, 0, false);

            var synced = manager->IsSyncedToFate(fate);
            return new LevelSyncSnapshot(
                true,
                true,
                clientState.TerritoryType,
                (uint)playerState.ClassJob.Value.RowId,
                playerState.Level,
                playerState.EffectiveLevel,
                fate->FateId,
                fate->StartTimeEpoch,
                fate->Level,
                fate->MaxLevel,
                synced);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "LevelSyncGameApi: native FATE sync read failed");
            return new LevelSyncSnapshot(true, false, clientState.TerritoryType, (uint)playerState.ClassJob.Value.RowId,
                playerState.Level, playerState.EffectiveLevel, 0, 0, 0, 0, false);
        }
    }

    public LevelSyncRequestResult TryRequestSync(FateSelection selection)
    {
        if (!clientState.IsLoggedIn || !playerState.IsLoaded || !playerState.ClassJob.IsValid)
            return LevelSyncRequestResult.NotReady;
        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51] ||
            condition[ConditionFlag.Casting] || condition[ConditionFlag.Occupied] ||
            condition[ConditionFlag.OccupiedInEvent] || condition[ConditionFlag.OccupiedInQuestEvent])
            return LevelSyncRequestResult.NotReady;

        try
        {
            if (clientState.TerritoryType != selection.Fate.TerritoryId)
                return LevelSyncRequestResult.WrongCurrentFate;
            var manager = FateManager.Instance();
            var current = manager == null ? null : manager->CurrentFate;
            if (manager == null || current == null)
                return LevelSyncRequestResult.Unavailable;
            if (!MatchesSelection(current, selection))
                return LevelSyncRequestResult.WrongCurrentFate;
            if (manager->IsSyncedToFate(current))
                return LevelSyncRequestResult.AlreadyReady;

            var maxLevel = current->MaxLevel > 0 ? current->MaxLevel : selection.Fate.MaxLevel;
            if (maxLevel <= 0 || playerState.Level <= maxLevel)
                return LevelSyncRequestResult.AlreadyReady;

            _ = manager->LevelSync();
            return LevelSyncRequestResult.Requested;
        }
        catch (Exception ex)
        {
            log.Error(ex, "LevelSyncGameApi: native FATE level sync request failed");
            return LevelSyncRequestResult.Failed;
        }
    }

    private static bool MatchesSelection(FateContext* current, FateSelection selection)
        => current != null && current->FateId == selection.Fate.FateId &&
           (current->StartTimeEpoch == 0 || selection.Fate.StartTimeEpoch == 0 || current->StartTimeEpoch == selection.Fate.StartTimeEpoch);
}
