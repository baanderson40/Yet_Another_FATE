using System;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using YetAnotherFATE.Config;
using YetAnotherFATE.Data.Automator;
using YetAnotherFATE.Data.Travel;
using YetAnotherFATE.Services.Fates;
using YetAnotherFATE.Services.Travel;

namespace YetAnotherFATE.Modules.Automator.Handlers;

public sealed class RotatingZoneStateHandler(
    ZoneRotationController rotation, Configuration configuration, IClientState clientState,
    IPlayerState playerState, IObjectTable objectTable, ICondition condition,
    IFateRepository repository, IFateSelector selector, IFateFollowUpTracker followUps,
    IFateRewardTracker rewards, ITravelController travel, IGameGui gameGui, IPluginLog log,
    AutomaticTravelPolicy automaticTravel) : IAutomatorStateHandler
{
    public ZoneRotationController Rotation => rotation;
    public bool CanHandle(AutomatorState state) => state == AutomatorState.RotatingZone;
    public void Reset() => rotation.Reset();

    public AutomatorStateHandlerResult Tick(AutomatorContext context)
    {
        if (TryOwnSelection(context)) return AutomatorStateHandlerResult.Wait(rotation.Detail);
        return AutomatorStateHandlerResult.Transition(AutomatorState.SelectingFate, "Rotation yielded to local selection");
    }

    public bool TryOwnSelection(AutomatorContext context)
    {
        rotation.SynchronizeSettings();
        if (!configuration.ZoneRotationEnabled || !configuration.AutomatorEnabled || !clientState.IsLoggedIn)
        {
            if (!configuration.AutomatorEnabled || !clientState.IsLoggedIn) rotation.Reset();
            return false;
        }
        if (rewards.PendingStop) { rotation.Reset(); return false; }
        if (rotation.Phase == ZoneRotationPhase.Suspended)
            return true;
        var ready = clientState.IsLoggedIn && clientState.TerritoryType != 0 && playerState.IsLoaded &&
            objectTable.LocalPlayer is { CurrentHp: > 0 } &&
            !condition[ConditionFlag.BetweenAreas] && !condition[ConditionFlag.BetweenAreas51] && IsScreenReady();
        if (!ready) rotation.InvalidateReadiness();
        if (travel.CurrentJob is { } job)
        {
            if (job.Kind != TravelRequestKind.Automatic)
            {
                rotation.Suspend("Competing manual travel job");
                // Do not let normal selection create an automatic job beside manual travel.
                return true;
            }
            // A completed automatic FATE job may be awaiting asynchronous cleanup.
            // It must not permanently disarm rotation for the rest of the run.
            if (job.Kind != TravelRequestKind.Automatic || context.State == AutomatorState.RotatingZone)
                rotation.Suspend("Competing travel job");
            return false;
        }
        var manualTeleportCast = objectTable.LocalPlayer is { IsCasting: true, CastActionId: 5 or 6 } &&
            !(rotation.Phase == ZoneRotationPhase.Teleporting && rotation.TeleportState is TeleportState.Starting or TeleportState.Loading);
        if ((rotation.Destination == null && (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])) || manualTeleportCast)
        {
            rotation.Suspend("Manual teleport / zone travel");
            return false;
        }
        var fates = ready ? repository.EnumerateActiveFates().Where(f => f.TerritoryId == clientState.TerritoryType &&
            f.IsOnMap && f.State is Dalamud.Game.ClientState.Fates.FateState.Preparing or Dalamud.Game.ClientState.Fates.FateState.Running).ToArray() : [];
        var empty = fates.Length == 0 || configuration.ZoneRotationWhenNoEligibleFates && selector.SelectBest(fates) == null;
        var safe = !condition[ConditionFlag.InCombat] && !condition[ConditionFlag.Unconscious] &&
            !condition[ConditionFlag.Occupied] && !condition[ConditionFlag.OccupiedInEvent] &&
            !condition[ConditionFlag.OccupiedInQuestEvent] && !condition[ConditionFlag.Casting] && !followUps.IsWatching && automaticTravel.CanTeleport();
        var twist = objectTable.LocalPlayer?.StatusList?.Any(s => s.StatusId is 1288 or 1289) == true;
        if (!rotation.Tick(new(clientState.TerritoryType, ready, safe, twist, empty), DateTimeOffset.UtcNow)) return false;
        context.ClearSelection("Empty-zone rotation", log);
        if (context.State != AutomatorState.RotatingZone)
            context.TransitionTo(AutomatorState.RotatingZone, "Empty-zone rotation", log);
        return true;
    }

    private unsafe bool IsScreenReady()
    {
        foreach (var name in new[] { "NowLoading", "FadeMiddle", "FadeBack" })
        {
            var addon = gameGui.GetAddonByName(name, 0);
            if (addon.Address != IntPtr.Zero && ((AtkUnitBase*)addon.Address)->IsVisible) return false;
        }
        return true;
    }
}
