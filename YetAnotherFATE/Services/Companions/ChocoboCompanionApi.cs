using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using YetAnotherFATE.Data.Companions;

namespace YetAnotherFATE.Services.Companions;

public sealed unsafe class ChocoboCompanionApi : IChocoboCompanionApi
{
    private const uint GreensItemId = 4868;
    private const uint DismountActionId = 23;
    private const uint InvalidEntityId = 0xE0000000;

    private readonly IClientState clientState;
    private readonly IPlayerState playerState;
    private readonly IObjectTable objectTable;
    private readonly ICondition condition;
    private readonly IPluginLog log;

    public ChocoboCompanionApi(
        IClientState clientState,
        IPlayerState playerState,
        IObjectTable objectTable,
        ICondition condition,
        IPluginLog log)
    {
        this.clientState = clientState;
        this.playerState = playerState;
        this.objectTable = objectTable;
        this.condition = condition;
        this.log = log;
    }

    public ChocoboSnapshot Read()
    {
        if (!IsPlayerReady())
            return default;

        try
        {
            var uiState = UIState.Instance();
            var inventory = InventoryManager.Instance();
            if (uiState == null || inventory == null)
                return default;

            var info = uiState->Buddy.CompanionInfo;
            var spawned = info.Companion != null && info.Companion->EntityId != InvalidEntityId;
            var stabled = PlayerState.Instance() != null && PlayerState.Instance()->IsPlayerStateFlagSet(PlayerStateFlag.IsBuddyInStable);
            return new ChocoboSnapshot(
                true,
                spawned,
                stabled,
                condition[ConditionFlag.Mounted],
                info.TimeLeft,
                info.ActiveCommand,
                inventory->GetInventoryItemCount(GreensItemId, false, false, false),
                info.Companion == null ? 0 : info.Companion->CurrentHealth,
                info.Companion == null ? 0 : info.Companion->MaxHealth);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "ChocoboCompanionApi: companion read failed");
            return default;
        }
    }

    public ChocoboActionResult TryUseGreens()
    {
        var snapshot = Read();
        if (!snapshot.PlayerReady)
            return ChocoboActionResult.NotReady;
        if (snapshot.IsStabled)
            return ChocoboActionResult.InStable;
        if (snapshot.IsMounted)
            return ChocoboActionResult.Mounted;
        if (snapshot.GreensCount <= 0)
            return ChocoboActionResult.NoGreens;
        if (IsBlocked())
            return ChocoboActionResult.Busy;

        try
        {
            var manager = ActionManager.Instance();
            if (manager == null || manager->GetActionStatus(ActionType.Item, GreensItemId) != 0)
                return ChocoboActionResult.Unavailable;
            return manager->UseAction(ActionType.Item, GreensItemId, extraParam: 0xFFFF)
                ? ChocoboActionResult.Issued
                : ChocoboActionResult.Rejected;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "ChocoboCompanionApi: greens action failed");
            return ChocoboActionResult.Rejected;
        }
    }

    public ChocoboActionResult TrySetStance(ChocoboStance stance)
    {
        var snapshot = Read();
        if (!snapshot.PlayerReady)
            return ChocoboActionResult.NotReady;
        if (!snapshot.IsActive)
            return ChocoboActionResult.Unavailable;
        if (snapshot.IsStabled)
            return ChocoboActionResult.InStable;
        if (snapshot.IsMounted || IsBlocked())
            return snapshot.IsMounted ? ChocoboActionResult.Mounted : ChocoboActionResult.Busy;
        if (snapshot.ActiveCommand == (byte)stance)
            return ChocoboActionResult.AlreadySatisfied;

        try
        {
            var manager = ActionManager.Instance();
            var actionId = (uint)stance;
            if (manager == null || manager->GetActionStatus(ActionType.BuddyAction, actionId) != 0)
                return ChocoboActionResult.Unavailable;
            return manager->UseAction(ActionType.BuddyAction, actionId)
                ? ChocoboActionResult.Issued
                : ChocoboActionResult.Rejected;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "ChocoboCompanionApi: stance action failed");
            return ChocoboActionResult.Rejected;
        }
    }

    public ChocoboActionResult TryDismount()
    {
        var snapshot = Read();
        if (!snapshot.PlayerReady)
            return ChocoboActionResult.NotReady;
        if (!snapshot.IsMounted)
            return ChocoboActionResult.AlreadySatisfied;
        if (IsBlocked(ignoreMounted: true))
            return ChocoboActionResult.Busy;

        try
        {
            var manager = ActionManager.Instance();
            if (manager == null || manager->GetActionStatus(ActionType.GeneralAction, DismountActionId) != 0)
                return ChocoboActionResult.Unavailable;
            return manager->UseAction(ActionType.GeneralAction, DismountActionId)
                ? ChocoboActionResult.Issued
                : ChocoboActionResult.Rejected;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "ChocoboCompanionApi: dismount action failed");
            return ChocoboActionResult.Rejected;
        }
    }

    private bool IsPlayerReady()
        => clientState.IsLoggedIn && playerState.IsLoaded && objectTable.LocalPlayer is { CurrentHp: > 0 };

    private bool IsBlocked(bool ignoreMounted = false)
        => condition[ConditionFlag.Unconscious] ||
           condition[ConditionFlag.BetweenAreas] ||
           condition[ConditionFlag.BetweenAreas51] ||
           condition[ConditionFlag.Occupied] ||
           condition[ConditionFlag.OccupiedInEvent] ||
           condition[ConditionFlag.OccupiedInQuestEvent] ||
           condition[ConditionFlag.OccupiedInCutSceneEvent] ||
           condition[ConditionFlag.WatchingCutscene] ||
           condition[ConditionFlag.WatchingCutscene78] ||
           condition[ConditionFlag.TradeOpen] ||
           condition[ConditionFlag.OccupiedSummoningBell] ||
           condition[ConditionFlag.MountOrOrnamentTransition] ||
           condition[ConditionFlag.Mounting] ||
           condition[ConditionFlag.Mounting71] ||
           condition[ConditionFlag.RidingPillion] ||
           condition[ConditionFlag.Jumping] ||
           condition[ConditionFlag.Jumping61] ||
           condition[ConditionFlag.Casting] ||
           condition[ConditionFlag.BoundByDuty] ||
           condition[ConditionFlag.InFlight] ||
           (!ignoreMounted && condition[ConditionFlag.Mounted]);
}
