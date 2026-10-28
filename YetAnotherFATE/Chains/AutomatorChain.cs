using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using YetAnotherFATE.Config;
using YetAnotherFATE.Data.Automator;
using YetAnotherFATE.Data.Fates;
using YetAnotherFATE.Data.Retainers;
using YetAnotherFATE.Data.Travel;
using YetAnotherFATE.Modules.Automator.Handlers;
using YetAnotherFATE.Services.Combat.BossMod;
using YetAnotherFATE.Services.Combat.RotationSolver;
using YetAnotherFATE.Services.Combat.Wrath;
using YetAnotherFATE.Services.Fates.CombatHelper.Engagement;
using YetAnotherFATE.Services.Travel;
using YetAnotherFATE.Services.Fates;
using YetAnotherFATE.Services.Companions;
using YetAnotherFATE.Services.Retainers;

namespace YetAnotherFATE.Chains;

public sealed class AutomatorChain : IDisposable
{
    private readonly ITravelController travelController;
    private readonly IPluginLog log;
    private readonly ICondition condition;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly IFateFollowUpTracker followUpTracker;
    private readonly IFateCompletionTracker completionTracker;
    private readonly IBossModObstacleMap obstacleMap;
    private readonly FateObstacleMapAssurance mapAssurance;
    private readonly AutomaticTravelPolicy automaticTravel;
    private readonly AutoRetainerHandoffController? autoRetainerHandoff;
    private readonly IChocoboController? chocoboController;
    private readonly IdleAetheryteReturnController? idleAetheryteReturn;
    private readonly OceanFishingHandoffController? oceanFishingHandoff;
    private uint territory;
    private readonly ITextAdvanceClient textAdvance;
    private readonly IFateRewardTracker rewardTracker;
    private readonly Configuration configuration;
    private readonly BossModPresetManager bossModPresetManager;
    private readonly IRotationSolverBridge rotationSolverBridge;
    private readonly IWrathComboBridge wrathComboBridge;
    private readonly IFateCombatHandler combatHandler;
    private readonly IReadOnlyList<IAutomatorStateHandler> handlers;
    private readonly RecoveringStateHandler? recoveringHandler;
    private readonly RotatingZoneStateHandler? rotatingHandler;
    private readonly AutomatorContext context = new();
    private bool disposed;
    private int manualStopRequested;
    private bool missingHandlerLogged;
    private int observedTravelPhaseRaw = (int)TravelPhase.Idle;
    private string? observedTravelDetail;
    private string? lastResultReason;
    private DateTimeOffset nextResultLogAt = DateTimeOffset.MinValue;

    public AutomatorState State => context.State;
    public string? StateReason => context.LastStateReason;
    public TravelPhase TravelPhase => context.TravelPhase;
    public string? TravelPhaseDetail => context.TravelPhaseDetail;
    public FateSelection? CurrentSelection => context.CurrentSelection;
    public RecoveryPhase RecoveryPhase => recoveringHandler?.Phase ?? RecoveryPhase.Idle;
    public string ZoneRotationStatus => rotatingHandler?.Rotation.Status ?? "Unavailable";
    public AutoRetainerHandoffPhase AutoRetainerHandoffPhase => autoRetainerHandoff?.Phase ?? AutoRetainerHandoffPhase.Idle;
    public string AutoRetainerHandoffStatus => autoRetainerHandoff?.Status ?? "Unavailable";
    public AutoRetainerReadinessSnapshot AutoRetainerReadiness => autoRetainerHandoff?.LastReadiness ?? default;
    public string ChocoboStatus => chocoboController?.Status ?? "Unavailable";
    public string IdleAetheryteReturnStatus => idleAetheryteReturn?.Status ?? "Unavailable";
    public string CombatClearanceStatus => handlers.OfType<ClearingCombatStateHandler>().FirstOrDefault()?.ClearanceStatus ?? "Unavailable";
    public string OceanFishingStatus => oceanFishingHandoff?.Status ?? "Unavailable";
    public AutoRetainerHandoffPhase OceanFishingPhase => oceanFishingHandoff?.Phase ?? AutoRetainerHandoffPhase.Idle;

    public AutomatorChain(
        ITravelController travelController,
        IPluginLog log,
        ICondition condition,
        IClientState clientState,
        IObjectTable objectTable,
        IFateFollowUpTracker followUpTracker,
        IFateCompletionTracker completionTracker,
        IBossModObstacleMap obstacleMap,
        ITextAdvanceClient textAdvance,
        IFateRewardTracker rewardTracker,
        Configuration configuration,
        IEnumerable<IAutomatorStateHandler> handlers,
        BossModPresetManager bossModPresetManager,
        IRotationSolverBridge rotationSolverBridge,
        IWrathComboBridge wrathComboBridge,
        IFateCombatHandler combatHandler,
        FateObstacleMapAssurance mapAssurance,
        AutomaticTravelPolicy automaticTravel,
        AutoRetainerHandoffController? autoRetainerHandoff = null,
        IChocoboController? chocoboController = null,
        IdleAetheryteReturnController? idleAetheryteReturn = null,
        OceanFishingHandoffController? oceanFishingHandoff = null)
    {
        this.travelController = travelController;
        this.log = log;
        this.condition = condition;
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.followUpTracker = followUpTracker;
        this.completionTracker = completionTracker;
        this.obstacleMap = obstacleMap;
        territory = clientState.TerritoryType;
        this.textAdvance = textAdvance;
        this.rewardTracker = rewardTracker;
        this.configuration = configuration;
        this.handlers = handlers.ToArray();
        recoveringHandler = this.handlers.OfType<RecoveringStateHandler>().FirstOrDefault();
        rotatingHandler = this.handlers.OfType<RotatingZoneStateHandler>().FirstOrDefault();
        this.bossModPresetManager = bossModPresetManager;
        this.rotationSolverBridge = rotationSolverBridge;
        this.wrathComboBridge = wrathComboBridge;
        this.combatHandler = combatHandler;
        this.mapAssurance = mapAssurance;
        this.automaticTravel = automaticTravel;
        this.autoRetainerHandoff = autoRetainerHandoff;
        this.chocoboController = chocoboController;
        this.idleAetheryteReturn = idleAetheryteReturn;
        this.oceanFishingHandoff = oceanFishingHandoff;

        rewardTracker.ManualStopRequested += OnManualStopRequested;
        travelController.PhaseChanged += OnTravelPhaseChanged;
    }

    public void RequestAutoRetainerPass() => autoRetainerHandoff?.RequestManualPass();
    public bool RetryAutoRetainerHandoff() => autoRetainerHandoff?.RetryFailedSession() == true;
    public bool ResumeAutoRetainerHandoff() => autoRetainerHandoff?.ResumeFailedSession() == true;
    public bool AbandonAutoRetainerHandoff() => autoRetainerHandoff?.AbandonFailedSession() == true;
    public bool ConfirmFeeshStoppedAndReturn() => oceanFishingHandoff?.ConfirmFeeshStoppedAndReturn() == true;
    public bool AbandonOceanFishingRecovery() => oceanFishingHandoff?.AbandonFailedSession() == true;

    public unsafe bool CanStartAutoRetainerHandoff()
    {
        if (context.State is not (AutomatorState.Disabled or AutomatorState.SelectingFate) ||
            context.CurrentSelection is not null ||
            travelController.CurrentJob is not null ||
            rewardTracker.PendingStop ||
            followUpTracker.IsWatching ||
            condition[ConditionFlag.InCombat] ||
            condition[ConditionFlag.Casting] ||
            condition[ConditionFlag.Occupied] ||
            condition[ConditionFlag.OccupiedInEvent] ||
            condition[ConditionFlag.OccupiedInQuestEvent] ||
            condition[ConditionFlag.BetweenAreas] ||
            condition[ConditionFlag.BetweenAreas51] ||
            objectTable.LocalPlayer is not { CurrentHp: > 0 })
            return false;

        var fateManager = FateManager.Instance();
        return fateManager == null || fateManager->CurrentFate == null;
    }

    public unsafe bool CanStartOceanFishingHandoff() => CanStartAutoRetainerHandoff();

    public bool CanResumeAfterAutoRetainerHandoff()
        => clientState.IsLoggedIn && objectTable.LocalPlayer is { CurrentHp: > 0 } &&
           !condition[ConditionFlag.BetweenAreas] && !condition[ConditionFlag.BetweenAreas51] &&
           !condition[ConditionFlag.InCombat] && !condition[ConditionFlag.Occupied] &&
           !condition[ConditionFlag.OccupiedInEvent] && !condition[ConditionFlag.OccupiedInQuestEvent];

    public void PrepareForExternalHandoff(string reason)
    {
        travelController.Cancel(reason, TravelRequestKind.Automatic);
        textAdvance.Disable();
        followUpTracker.Clear();
        completionTracker.Reset();
        obstacleMap.ClearTemporaryMap();
        mapAssurance.Reset();
        rotatingHandler?.Rotation.Suspend(reason);
        bossModPresetManager.DeactivateActivePreset();
        rotationSolverBridge.NotifyFateEnded();
        wrathComboBridge.NotifyAutomationStopped();
        combatHandler.Reset(reason);
        chocoboController?.Reset();
        idleAetheryteReturn?.Reset();
        foreach (var handler in handlers)
            handler.Reset();
        context.Reset(log);
    }

    public void ResumeAfterAutoRetainerHandoff()
    {
        if (!configuration.AutomatorEnabled)
            return;
        territory = clientState.TerritoryType;
        context.Reset(log);
        context.TransitionTo(AutomatorState.SelectingFate, "AutoRetainer handoff complete", log);
    }

    public void ResumeAfterExternalHandoff(string reason)
    {
        if (!configuration.AutomatorEnabled)
            return;
        territory = clientState.TerritoryType;
        context.Reset(log);
        context.TransitionTo(AutomatorState.SelectingFate, reason, log);
    }

    public void Tick()
    {
        if (disposed)
        {
            return;
        }

        if (Interlocked.Exchange(ref manualStopRequested, 0) != 0)
        {
            HandleManualStop();
            return;
        }

        rewardTracker.SynchronizeSession();
        automaticTravel.Synchronize();
        rotatingHandler?.Rotation.SynchronizeSettings();
        if (autoRetainerHandoff?.HasSession == true)
        {
            autoRetainerHandoff.Tick(this);
            return;
        }
        if (oceanFishingHandoff?.ShouldDispatchChainedRetainer == true && autoRetainerHandoff?.Tick(this) == true)
            return;
        if (oceanFishingHandoff?.Tick(this, autoRetainerHandoff?.HasSession == true) == true)
            return;
        if (autoRetainerHandoff?.Tick(this) == true)
            return;
        if (objectTable.LocalPlayer == null || condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
            rotatingHandler?.Rotation.InvalidateReadiness();
        if (!configuration.AutomatorEnabled || !clientState.IsLoggedIn)
        {
            territory = clientState.TerritoryType;
            HandleDisabledAutomator();
            obstacleMap.ClearTemporaryMap();
            return;
        }
        if (territory != clientState.TerritoryType)
        {
            chocoboController?.Reset();
            idleAetheryteReturn?.Reset();
            var previousTerritory = territory;
            territory = clientState.TerritoryType;
            var automaticTravelOwnsTransition = travelController.CurrentJob is { Kind: TravelRequestKind.Automatic } ||
                context.TravelPhase is TravelPhase.TeleportQueued or TravelPhase.Teleporting;
            var startupTerritoryChange = previousTerritory == 0 && context.State == AutomatorState.Disabled;
            if (context.State == AutomatorState.Recovering)
                recoveringHandler?.ObserveTransit(clientState.TerritoryType,
                    condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51]);
            var recoveryOwnsTransition = context.State == AutomatorState.Recovering && context.DeathRecovery is { } recovery &&
                recoveringHandler?.ExpectsTerritory(clientState.TerritoryType, recovery) == true;
            if (!startupTerritoryChange && !recoveryOwnsTransition &&
                !automaticTravelOwnsTransition &&
                rotatingHandler?.Rotation.ExpectsArrival(territory) != true &&
                rotatingHandler?.Rotation.OwnsLoadingTransit(territory,
                    condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51]) != true)
            {
                HandleDisabledAutomator();
                rotatingHandler?.Rotation.Suspend("Unexpected territory change / manual travel");
            }
            obstacleMap.ClearTemporaryMap();
            mapAssurance.Reset();
        }

        var travelPhase = (TravelPhase)Volatile.Read(ref observedTravelPhaseRaw);
        var travelDetail = Volatile.Read(ref observedTravelDetail);
        context.SetTravelPhase(travelPhase, travelDetail);

        if (context.State == AutomatorState.Disabled)
        {
            context.TransitionTo(AutomatorState.SelectingFate, "Automator enabled", log);
        }

        if ((condition[ConditionFlag.Unconscious] || objectTable.LocalPlayer?.CurrentHp == 0) && context.State != AutomatorState.Recovering)
        {
            context.DeathRecovery = new DeathRecoveryContext(clientState.TerritoryType,
                objectTable.LocalPlayer?.Position ?? context.CurrentSelection?.Fate.Center ?? default, context.CurrentSelection);
            followUpTracker.Clear();
            completionTracker.Reset();
            travelController.Cancel("Player unconscious", TravelRequestKind.Automatic);
            textAdvance.Disable();
            bossModPresetManager.DeactivateActivePreset();
            rotationSolverBridge.NotifyFateEnded();
            wrathComboBridge.NotifyAutomationStopped();
            combatHandler.Reset("Player unconscious");
            chocoboController?.Reset();
            obstacleMap.ClearTemporaryMap();
            mapAssurance.Reset();
            foreach (var stateHandler in handlers)
                stateHandler.Reset();
            context.TransitionTo(AutomatorState.Recovering, "Player unconscious; entering global recovery", log);
        }

        if (context.State is not (AutomatorState.Recovering or AutomatorState.RotatingZone) && (objectTable.LocalPlayer == null ||
            condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51]))
        {
            if ((condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51]) &&
                travelController.CurrentJob is not { Kind: TravelRequestKind.Automatic })
                rotatingHandler?.Rotation.Suspend("Manual zone travel");
            textAdvance.Disable();
            return;
        }

        // Provider restoration can fail transiently. Keep retrying (bridges apply
        // their own backoff) after a one-time state-exit cleanup has run.
        if (context.State is AutomatorState.SelectingFate or AutomatorState.Travelling or AutomatorState.Recovering or AutomatorState.RotatingZone)
        {
            bossModPresetManager.DeactivateActivePreset();
            rotationSolverBridge.NotifyFateEnded();
            wrathComboBridge.NotifyAutomationStopped();
            textAdvance.Disable();
        }

        if (rewardTracker.PendingStop &&
            context.State is not (AutomatorState.Participating or AutomatorState.PostFate or AutomatorState.Recovering))
        {
            rotatingHandler?.Reset();
            if (IsSafeToStop())
            {
                context.TransitionTo(AutomatorState.PostFate, "Run limit reached; finishing post-FATE cleanup", log);
            }
            else
            {
                travelController.Cancel("Run limit reached; waiting for safety", TravelRequestKind.Automatic);
                textAdvance.Disable();
                return;
            }
        }

        if (condition[ConditionFlag.InCombat] && !CanContinueFateTravelThroughCombat() && context.State is
            (AutomatorState.SelectingFate or AutomatorState.Travelling or AutomatorState.ActivatingFate or
             AutomatorState.PostFate or AutomatorState.RotatingZone))
        {
            context.CombatClearanceReturnState = context.State;
            travelController.Cancel("Combat started; clearing aggro", TravelRequestKind.Automatic);
            textAdvance.Disable();
            bossModPresetManager.DeactivateActivePreset();
            rotationSolverBridge.NotifyFateEnded();
            wrathComboBridge.NotifyAutomationStopped();
            combatHandler.Reset("Combat started outside FATE participation");
            context.TransitionTo(AutomatorState.ClearingCombat, "Combat started; clearing aggro before automation", log);
            return;
        }

        if (context.State is AutomatorState.Participating or AutomatorState.ClearingCombat)
            chocoboController?.TickDuringCombat();

        if (context.State == AutomatorState.SelectingFate && chocoboController?.Tick() == true)
            return;

        if (context.State == AutomatorState.SelectingFate && idleAetheryteReturn?.Tick() == true)
            return;

        if (context.State == AutomatorState.SelectingFate && rotatingHandler?.TryOwnSelection(context) == true)
            return;

        if (context.State == AutomatorState.Participating && context.CurrentSelection is { } engaged)
            mapAssurance.Tick(engaged.Fate, DateTimeOffset.UtcNow);

        var handler = handlers.FirstOrDefault(h => h.CanHandle(context.State));
        if (handler == null)
        {
            if (!missingHandlerLogged)
            {
                log.Warning("AutomatorChain: no handler registered for state {State}", context.State);
                missingHandlerLogged = true;
            }

            return;
        }

        missingHandlerLogged = false;
        var wasPostFate = context.State == AutomatorState.PostFate;
        var result = handler.Tick(context);
        if (result.IsWait)
        {
            if (!string.IsNullOrWhiteSpace(result.Reason) && ShouldLogResult(result.Reason))
            {
                log.Debug("Automator: {Reason}", result.Reason);
            }

            return;
        }

        if (result.NextState.HasValue)
        {
            context.TransitionTo(result.NextState.Value, result.Reason, log);
            if (wasPostFate && rewardTracker.PendingStop && context.State == AutomatorState.SelectingFate && IsSafeToStop())
            {
                // The handler has finished its cleanup, including collection reward grace.
                HandleDisabledAutomator();
                rewardTracker.CompleteSafeStop();
            }
        }
        else if (!string.IsNullOrWhiteSpace(result.Reason) && ShouldLogResult(result.Reason))
        {
            log.Debug("Automator: {Reason}", result.Reason);
        }
    }

    private unsafe bool IsSafeToStop()
    {
        if (condition[ConditionFlag.InCombat] || condition[ConditionFlag.Unconscious] ||
            objectTable.LocalPlayer is not { CurrentHp: > 0 } || condition[ConditionFlag.OccupiedInQuestEvent])
            return false;
        var fateManager = FateManager.Instance();
        var currentFate = fateManager == null ? null : fateManager->CurrentFate;
        return currentFate == null;
    }

    private bool ShouldLogResult(string reason)
    {
        var now = DateTimeOffset.UtcNow;
        if (!string.Equals(lastResultReason, reason, StringComparison.Ordinal) || now >= nextResultLogAt)
        {
            lastResultReason = reason;
            nextResultLogAt = now.AddSeconds(10);
            return true;
        }

        return false;
    }

    private void HandleDisabledAutomator()
    {
        mapAssurance.Reset();
        travelController.Cancel("Automator disabled", TravelRequestKind.Automatic);
        textAdvance.Disable();
        followUpTracker.Clear();
        completionTracker.Reset();
        foreach (var handler in handlers)
            handler.Reset();
        if (context.State != AutomatorState.Disabled)
        {
            context.Reset(log);
        }

        bossModPresetManager.ReleasePresets();
        rotationSolverBridge.NotifyFateEnded();
        wrathComboBridge.NotifyAutomationStopped();
        combatHandler.Reset("Automator disabled");
        chocoboController?.Reset();
        idleAetheryteReturn?.Reset();
    }

    private void HandleManualStop()
    {
        configuration.AutomatorEnabled = false;
        autoRetainerHandoff?.CancelForManualStop();
        oceanFishingHandoff?.CancelForManualStop();
        travelController.Cancel("Manual Stop");
        rotatingHandler?.Reset();
        HandleDisabledAutomator();
        obstacleMap.ClearTemporaryMap();
        log.Information("Manual Stop canceled YAF work and released owned blockers; unresolved provider operations remain in manual recovery.");
    }

    private void OnManualStopRequested()
        => Interlocked.Exchange(ref manualStopRequested, 1);

    private void OnTravelPhaseChanged(object? sender, TravelPhaseChangedEventArgs args)
    {
        Volatile.Write(ref observedTravelPhaseRaw, (int)args.Phase);
        Volatile.Write(ref observedTravelDetail, args.Detail);
    }

    private bool CanContinueFateTravelThroughCombat()
    {
        if (context.State != AutomatorState.Travelling || context.CurrentSelection is not { } selection)
            return false;

        var phase = context.TravelPhase;
        if (phase == TravelPhase.Arrived)
            return true;
        if (phase is not (TravelPhase.Teleporting or TravelPhase.Moving or TravelPhase.Arriving))
            return false;

        return travelController.CurrentJob is { Kind: TravelRequestKind.Automatic, Selection: { } jobSelection } &&
            IsSameFateOccurrence(jobSelection.Fate, selection.Fate);
    }

    private static bool IsSameFateOccurrence(ActiveFate left, ActiveFate right)
        => left.FateId == right.FateId && left.TerritoryId == right.TerritoryId &&
           (left.StartTimeEpoch == 0 || right.StartTimeEpoch == 0 || left.StartTimeEpoch == right.StartTimeEpoch);

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        HandleDisabledAutomator();
        obstacleMap.ClearTemporaryMap();
        rewardTracker.ManualStopRequested -= OnManualStopRequested;
        travelController.PhaseChanged -= OnTravelPhaseChanged;
        disposed = true;
    }
}
