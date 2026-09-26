using System;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using YetAnotherFATE.Data.Automator;
using YetAnotherFATE.Data.Fates;
using YetAnotherFATE.Data.Travel;
using YetAnotherFATE.Services.Fates;
using YetAnotherFATE.Services.Travel;
using YetAnotherFATE.Services.Zone;
using YetAnotherFATE.Services.Combat.BossMod;
using YetAnotherFATE.Services.Combat.RotationSolver;
using YetAnotherFATE.Services.Combat.Wrath;
using YetAnotherFATE.Services.Fates.CombatHelper.Engagement;

namespace YetAnotherFATE.Modules.Automator.Handlers;

public sealed class RecoveringStateHandler : IAutomatorStateHandler
{
    private readonly ICondition condition;
    private readonly ITravelController travelController;
    private readonly IFateRepository fateRepository;
    private readonly IGameGui gameGui;
    private readonly IObjectTable objectTable;
    private readonly IPartyList partyList;
    private readonly IPluginLog log;
    private static readonly TimeSpan RaiseWaitTimeout = TimeSpan.FromMinutes(5);
    private static readonly uint[] RaiseStatusIds = [148u, 1140u];
    private DateTimeOffset recoveryStartedAt;
    private DateTimeOffset nextReviveAttemptAt;
    private DateTimeOffset recoveryDialogConfirmedAt;
    private int releaseAttempts;
    private DateTimeOffset enteredAt;
    private DateTimeOffset raiseSeenAt;
    private DateTimeOffset releaseStartedAt;
    private bool acceptingRaise;
    private readonly ITextAdvanceClient textAdvance;
    private readonly BossModPresetManager bossMod;
    private readonly IRotationSolverBridge rotationSolver;
    private readonly IWrathComboBridge wrath;
    private readonly IFateCombatHandler combat;
    private readonly IBossModObstacleMap obstacleMap;
    private readonly IClientState clientState;
    private readonly IPlayerState playerState;
    private readonly DeathReturnRoute returnRoute;
    private bool returningToZone;
    private DateTimeOffset? loadingStartedAt;

    public RecoveryPhase Phase { get; private set; } = RecoveryPhase.Idle;

    public bool ExpectsTerritory(uint territory, DeathRecoveryContext destination)
        => returnRoute.ExpectsTerritory(destination, territory);

    public void ObserveTransit(uint territory, bool loading)
        => returnRoute.ObserveTransit(territory, loading);

    public RecoveringStateHandler(
        ICondition condition,
        ITravelController travelController,
        IFateRepository fateRepository,
        IGameGui gameGui,
        IObjectTable objectTable,
        IPartyList partyList,
        ITextAdvanceClient textAdvance,
        BossModPresetManager bossMod,
        IRotationSolverBridge rotationSolver,
        IWrathComboBridge wrath,
        IFateCombatHandler combat,
        IBossModObstacleMap obstacleMap,
        IClientState clientState,
        IPlayerState playerState,
        IZoneService zoneService,
        ITeleportExecutor teleportExecutor,
        IPluginLog log,
        CityDeathReturnRoute? cityReturnRoute = null)
    {
        this.condition = condition;
        this.travelController = travelController;
        this.fateRepository = fateRepository;
        this.gameGui = gameGui;
        this.objectTable = objectTable;
        this.partyList = partyList;
        this.log = log;
        this.textAdvance = textAdvance;
        this.bossMod = bossMod;
        this.rotationSolver = rotationSolver;
        this.wrath = wrath;
        this.combat = combat;
        this.obstacleMap = obstacleMap;
        this.clientState = clientState;
        this.playerState = playerState;
        returnRoute = new DeathReturnRoute(zoneService, teleportExecutor, cityReturnRoute);
    }

    public bool CanHandle(AutomatorState state) => state == AutomatorState.Recovering;

    public void Reset()
    {
        enteredAt = default;
        returningToZone = false;
        loadingStartedAt = null;
        returnRoute.Reset();
        Phase = RecoveryPhase.Idle;
    }

    public AutomatorStateHandlerResult Tick(AutomatorContext context)
    {
        var now = DateTimeOffset.UtcNow;
        if (enteredAt != context.LastStateChangedAt)
        {
            enteredAt = context.LastStateChangedAt;
            recoveryStartedAt = now;
            nextReviveAttemptAt = recoveryDialogConfirmedAt = raiseSeenAt = releaseStartedAt = DateTimeOffset.MinValue;
            releaseAttempts = 0;
            acceptingRaise = false;
            returningToZone = false;
            loadingStartedAt = null;
            returnRoute.Reset();
            Phase = RecoveryPhase.WaitingForRaise;
            bossMod.DeactivateActivePreset();
            rotationSolver.NotifyFateEnded();
            wrath.NotifyAutomationStopped();
            combat.Reset("Player recovery");
            obstacleMap.ClearTemporaryMap();
        }
        textAdvance.Disable();
        travelController.Cancel("Player recovery", TravelRequestKind.Automatic);
        var player = objectTable.LocalPlayer;
        if (returningToZone)
        {
            if (player is { CurrentHp: 0 } && !condition[ConditionFlag.BetweenAreas] && !condition[ConditionFlag.BetweenAreas51])
            {
                returnRoute.Reset();
                Phase = RecoveryPhase.Failed;
                return AutomatorStateHandlerResult.Wait("Player died during death return; manual recovery required");
            }
            return ContinueReturn(context, now);
        }
        if (player == null || condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
        {
            loadingStartedAt ??= now;
            if (now - loadingStartedAt.Value >= TimeSpan.FromSeconds(60))
            {
                Phase = RecoveryPhase.Failed;
                return AutomatorStateHandlerResult.Wait("Recovery loading timed out; manual intervention required");
            }
            return AutomatorStateHandlerResult.Wait("Waiting for recovery loading to finish");
        }
        loadingStartedAt = null;

        if (condition[ConditionFlag.Unconscious] || player.CurrentHp == 0)
        {
            if (Phase == RecoveryPhase.Failed)
            {
                return AutomatorStateHandlerResult.Wait("Automatic recovery failed; manual intervention required");
            }

            // Once a callback is sent, preserve its deadline until it succeeds or expires.
            if (recoveryDialogConfirmedAt != DateTimeOffset.MinValue)
            {
                if (now - recoveryDialogConfirmedAt < TimeSpan.FromSeconds(acceptingRaise ? 5 : 15))
                    return AutomatorStateHandlerResult.Wait("Waiting for confirmed recovery");
                recoveryDialogConfirmedAt = DateTimeOffset.MinValue;
                if (!acceptingRaise && releaseAttempts >= 3)
                {
                    Phase = RecoveryPhase.Failed;
                    log.Error("Automator: automatic recovery failed after {Attempts} release attempts", releaseAttempts);
                    return AutomatorStateHandlerResult.Wait("Automatic recovery failed; manual intervention required");
                }
                nextReviveAttemptAt = now.AddSeconds(1);
            }

            var raiseAvailable = HasRaiseStatus();
            var raiseTimedOut = partyList.Count <= 1 || now - recoveryStartedAt >= RaiseWaitTimeout;
            if (raiseAvailable)
            {
                if (raiseSeenAt == DateTimeOffset.MinValue)
                    raiseSeenAt = now;
                Phase = RecoveryPhase.AcceptingRaise;
            }
            else
            {
                raiseSeenAt = DateTimeOffset.MinValue;
                Phase = raiseTimedOut ? RecoveryPhase.WaitingForRelease : RecoveryPhase.WaitingForRaise;
            }

            if (!raiseAvailable && raiseTimedOut && releaseStartedAt == DateTimeOffset.MinValue)
                releaseStartedAt = now;

            if ((raiseAvailable ? now - raiseSeenAt >= TimeSpan.FromSeconds(1) : raiseTimedOut) &&
                now >= nextReviveAttemptAt)
            {
                nextReviveAttemptAt = now.AddSeconds(1);
                if (TryConfirmReviveDialog(raiseAvailable))
                {
                    recoveryDialogConfirmedAt = now;
                    acceptingRaise = raiseAvailable;
                    if (!raiseAvailable)
                    {
                        Phase = RecoveryPhase.ConfirmingRelease;
                        releaseAttempts++;
                    }
                    log.Debug("Automator: confirmed {RecoveryType} dialog", raiseAvailable ? "raise" : "release");
                }
            }

            if (!raiseAvailable && recoveryDialogConfirmedAt == DateTimeOffset.MinValue &&
                releaseStartedAt != DateTimeOffset.MinValue && now - releaseStartedAt >= TimeSpan.FromSeconds(60))
            {
                Phase = RecoveryPhase.Failed;
                log.Warning("Automator: release dialog unavailable; manual recovery required");
            }

            return AutomatorStateHandlerResult.Wait("Waiting for player recovery");
        }

        nextReviveAttemptAt = DateTimeOffset.MinValue;
        recoveryStartedAt = DateTimeOffset.MinValue;
        recoveryDialogConfirmedAt = DateTimeOffset.MinValue;
        releaseAttempts = 0;
        Phase = RecoveryPhase.Recovered;
        returningToZone = true;
        return ContinueReturn(context, now);
    }

    private AutomatorStateHandlerResult ContinueReturn(AutomatorContext context, DateTimeOffset now)
    {
        if (Phase == RecoveryPhase.Failed)
        {
            returnRoute.Reset();
            return AutomatorStateHandlerResult.Wait("Automatic recovery failed; manual intervention required");
        }
        if (travelController.CurrentJob is { Kind: not TravelRequestKind.Automatic })
        {
            Phase = RecoveryPhase.Failed;
            returnRoute.Reset();
            return AutomatorStateHandlerResult.Wait("Death return cancelled by manual travel; restart automation to retry");
        }
        var recovery = context.DeathRecovery;
        if (recovery != null)
        {
            Phase = RecoveryPhase.ReturningToZone;
            var result = returnRoute.Tick(recovery, clientState.TerritoryType, IsWorldReady(), now);
            if (result == DeathReturnResult.Waiting)
                return AutomatorStateHandlerResult.Wait("Waiting for return to death territory");
            if (result == DeathReturnResult.Failed)
            {
                Phase = RecoveryPhase.Failed;
                log.Warning("Automator: {Reason}", returnRoute.Failure ?? "Death return failed");
                return AutomatorStateHandlerResult.Wait(returnRoute.Failure);
            }
        }
        else if (!IsWorldReady()) return AutomatorStateHandlerResult.Wait("Waiting for recovery loading to finish");

        Phase = RecoveryPhase.Recovered;
        var savedSelection = recovery != null ? recovery.Selection : context.CurrentSelection;
        context.DeathRecovery = null;
        if (savedSelection is { } selection && fateRepository.EnumerateActiveFates()
                .FirstOrDefault(f => f.FateId == selection.Fate.FateId && f.TerritoryId == selection.Fate.TerritoryId &&
                    f.TerritoryId == clientState.TerritoryType &&
                    (selection.Fate.StartTimeEpoch == 0 || f.StartTimeEpoch == selection.Fate.StartTimeEpoch) &&
                    f.Progress < 100 && f.State is Dalamud.Game.ClientState.Fates.FateState.Running or Dalamud.Game.ClientState.Fates.FateState.Preparing) is { FateId: > 0 } active)
        {
            selection = selection with { Fate = active, Destination = active.Center, ShouldTeleport = false,
                PreferredAetheryteId = null, Teleport = default };
            context.UpdateSelection(selection, "Refreshing recovery destination", log);
            context.ReturningAfterRecovery = true;
            travelController.RequestTravel(selection, TravelRequestKind.Automatic);
            return AutomatorStateHandlerResult.Transition(AutomatorState.Travelling, "Player recovered; returning to selected FATE");
        }

        context.ClearSelection("Player recovered; selected FATE unavailable", log);
        return AutomatorStateHandlerResult.Transition(AutomatorState.SelectingFate, "Player recovered");
    }

    private unsafe bool IsWorldReady()
    {
        if (!clientState.IsLoggedIn || !playerState.IsLoaded || objectTable.LocalPlayer is not { CurrentHp: > 0 } ||
            condition[ConditionFlag.Unconscious] || condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51]) return false;
        foreach (var name in new[] { "NowLoading", "FadeMiddle", "FadeBack" })
        {
            var addon = (AtkUnitBase*)gameGui.GetAddonByName(name, 0).Address;
            if (addon != null && addon->IsVisible) return false;
        }
        return true;
    }

    private unsafe bool TryConfirmReviveDialog(bool raise)
    {
        var addon = (AtkUnitBase*)gameGui.GetAddonByName("SelectYesno", 1).Address;
        var agent = AgentRevive.Instance();
        var module = RaptureAtkModule.Instance();
        if (addon == null || !addon->IsReady || !addon->IsVisible || agent == null || module == null)
        {
            return false;
        }

        // SelectYesno is shared with invitations and unrelated confirmations. Validate its callback owner.
        if (!module->AtkModule.AddonCallbackMapping.TryGetValue((uint)addon->Id, out var callback, false) ||
            (callback.EventInterface != (AtkModuleInterface.AtkEventInterface*)agent &&
             (agent->Revive == null || callback.EventInterface != (AtkModuleInterface.AtkEventInterface*)agent->Revive)))
            return false;
        var raiseDialog = agent->ResurrectionTimeLeft > 0 && agent->ResurrectingPlayerId is not (0 or 0xE0000000);
        if (raise != raiseDialog)
            return false;

        return addon->FireCallbackInt(0);
    }

    private bool HasRaiseStatus()
    {
        var player = objectTable.LocalPlayer;
        return player != null && player.StatusList.Any(status => RaiseStatusIds.Contains(status.StatusId));
    }
}
