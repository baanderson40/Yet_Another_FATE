using System;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using YetAnotherFATE.Config;
using YetAnotherFATE.Data.Automator;
using YetAnotherFATE.Data.Combat;
using YetAnotherFATE.Data.Fates;
using YetAnotherFATE.Data.Travel;
using YetAnotherFATE.Services.Combat;
using YetAnotherFATE.Services.Combat.RotationSolver;
using YetAnotherFATE.Services.Combat.Wrath;
using YetAnotherFATE.Services.Combat.BossMod;
using YetAnotherFATE.Services.Combat.Clearance;
using YetAnotherFATE.Services.Fates;
using YetAnotherFATE.Services.Fates.CombatHelper.LevelSync;
using YetAnotherFATE.Services.Fates.CombatHelper.Engagement;
using YetAnotherFATE.Services.Fates.CombatHelper.Targeter;
using YetAnotherFATE.Services.Travel;
using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;

namespace YetAnotherFATE.Modules.Automator.Handlers.ParticipatingInFate.Handlers;

public sealed class ParticipatingInFateStateHandler : IAutomatorStateHandler
{
    private readonly IFateRepository fateRepository;
    private readonly ILevelSyncService levelSyncService;
    private readonly ICombatPluginRegistry combatPlugins;
    private readonly IRotationSolverBridge rotationSolverBridge;
    private readonly IWrathComboBridge wrathComboBridge;
    private readonly BossModPresetManager bossModPresetManager;
    private readonly Configuration configuration;
    private readonly ITravelController travelController;
    private readonly IPluginLog log;
    private readonly IFateCombatHandler combatHandler;
    private readonly IFateFollowUpTracker followUpTracker;
    private readonly ITextAdvanceClient textAdvance;
    private readonly IBossModObstacleMap obstacleMap;
    private readonly IFateCompletionTracker completionTracker;
    private readonly ICondition condition;
    private DateTimeOffset nextReminderAt = DateTimeOffset.MinValue;
    private DateTimeOffset nextTargetLogAt = DateTimeOffset.MinValue;
    private DateTimeOffset nextSyncLogAt = DateTimeOffset.MinValue;
    private DateTimeOffset nextRotationWarningAt = DateTimeOffset.MinValue;
    private readonly IFateRewardTracker rewardTracker;
    private readonly IGameGui gameGui;
    private readonly IObjectTable objectTable;
    private readonly AutomaticTravelPolicy automaticTravel;
    private readonly ICombatClearanceTargeter? clearanceTargeter;
    private DateTimeOffset participatingEnteredAt;
    private DateTimeOffset postEnteredAt;
    private DateTimeOffset collectionEndedAt;
    private int rewardsAtEntry;
    private ActiveFate lastObservedFate;
    private bool postCombatCleaned;
    private bool collectionInteractionOwned;
    private DateTimeOffset outsideFateSince = DateTimeOffset.MinValue;
    private readonly EscortLeapfrogTracker escortTracker = new();
    private DateTimeOffset nextEscortRetryAt = DateTimeOffset.MinValue;
    private DateTimeOffset nextEscortDiagnosticAt = DateTimeOffset.MinValue;
    private Vector3? lastEscortDestination;
    private static readonly TimeSpan OutsideFateGrace = TimeSpan.FromSeconds(3);
    private const float EscortAnchorParticipationDistance = 30f;

    public ParticipatingInFateStateHandler(
        IFateRepository fateRepository,
        IFateCombatHandler combatHandler,
        ILevelSyncService levelSyncService,
        ICombatPluginRegistry combatPlugins,
        IRotationSolverBridge rotationSolverBridge,
        IWrathComboBridge wrathComboBridge,
        BossModPresetManager bossModPresetManager,
        Configuration configuration,
        ITravelController travelController,
        IFateFollowUpTracker followUpTracker,
        ITextAdvanceClient textAdvance,
        IBossModObstacleMap obstacleMap,
        IFateCompletionTracker completionTracker,
        ICondition condition,
        IFateRewardTracker rewardTracker,
        IGameGui gameGui,
        IObjectTable objectTable,
        AutomaticTravelPolicy automaticTravel,
        IPluginLog log,
        ICombatClearanceTargeter? clearanceTargeter = null)
    {
        this.fateRepository = fateRepository;
        this.combatHandler = combatHandler;
        this.levelSyncService = levelSyncService;
        this.combatPlugins = combatPlugins;
        this.rotationSolverBridge = rotationSolverBridge;
        this.wrathComboBridge = wrathComboBridge;
        this.bossModPresetManager = bossModPresetManager;
        this.configuration = configuration;
        this.travelController = travelController;
        this.followUpTracker = followUpTracker;
        this.textAdvance = textAdvance;
        this.obstacleMap = obstacleMap;
        this.completionTracker = completionTracker;
        this.condition = condition;
        this.rewardTracker = rewardTracker;
        this.gameGui = gameGui;
        this.objectTable = objectTable;
        this.automaticTravel = automaticTravel;
        this.log = log;
        this.clearanceTargeter = clearanceTargeter;
    }

    public bool CanHandle(AutomatorState state) => state is AutomatorState.Participating or AutomatorState.PostFate;

    public void Reset()
    {
        participatingEnteredAt = postEnteredAt = collectionEndedAt = default;
        lastObservedFate = default;
        collectionInteractionOwned = postCombatCleaned = false;
        outsideFateSince = DateTimeOffset.MinValue;
        ClearEscortPositioning("participation handler reset");
        levelSyncService.Reset();
    }

    public AutomatorStateHandlerResult Tick(AutomatorContext context)
    {
        return context.State switch
        {
            AutomatorState.Participating => HandleParticipating(context),
            AutomatorState.PostFate => HandlePostFate(context),
            _ => AutomatorStateHandlerResult.None(),
        };
    }

    private AutomatorStateHandlerResult HandleParticipating(AutomatorContext context)
    {
        if (participatingEnteredAt != context.LastStateChangedAt)
        {
            participatingEnteredAt = context.LastStateChangedAt;
            collectionEndedAt = DateTimeOffset.MinValue;
            collectionInteractionOwned = false;
            rewardsAtEntry = rewardTracker.CompletedCount;
            lastObservedFate = context.CurrentSelection?.Fate ?? default;
            outsideFateSince = DateTimeOffset.MinValue;
            textAdvance.Disable();
        }

        if (condition[ConditionFlag.Unconscious] || objectTable.LocalPlayer?.CurrentHp == 0)
        {
            ClearEscortPositioning("player unconscious");
            textAdvance.Disable();
            combatHandler.Reset("Player unconscious");
            return AutomatorStateHandlerResult.Transition(AutomatorState.Recovering, "Player unconscious; waiting for recovery");
        }

        var selection = context.CurrentSelection;
        if (selection == null)
        {
            ClearEscortPositioning("selection missing");
            levelSyncService.Reset();
            textAdvance.Disable();
            return AutomatorStateHandlerResult.Transition(AutomatorState.PostFate, "Selection missing in participating state");
        }

        if (objectTable.LocalPlayer == null || condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
        {
            textAdvance.Disable();
            return AutomatorStateHandlerResult.Wait("Waiting for player availability");
        }

        var active = fateRepository.EnumerateActiveFates();
        if (!active.Any(f => IsSameOccurrence(f, selection.Value.Fate)))
        {
            levelSyncService.Reset();
            context.EndedFateId = selection.Value.Fate.FateId;
            clearanceTargeter?.CaptureEndedFate(selection.Value.Fate);
            StopFateOffense("FATE reached terminal state");
            ClearEscortPositioning("FATE ended");
            var completion = fateRepository.TryGetTerminalFate(selection.Value.Fate.FateId, out var terminal)
                ? completionTracker.RecordTerminal(terminal)
                : completionTracker.RecordMissing(lastObservedFate);
            if (lastObservedFate.IsCollect && configuration.CollectionPolicy != FateCollectionPolicy.KillOnly &&
                (completion.Kind == FateCompletionKind.Completed || collectionInteractionOwned))
            {
                collectionEndedAt = DateTimeOffset.UtcNow;
            }
            if (lastObservedFate.IsCollect && completion.Kind == FateCompletionKind.Completed)
                automaticTravel.WaitForExpiry(lastObservedFate);
            if (configuration.WaitForFollowUpFates)
            {
                if (completion.Kind == FateCompletionKind.Completed)
                {
                    followUpTracker.Begin(lastObservedFate);
                }
            }
            context.ClearSelection(completion.Reason, log);
            return AutomatorStateHandlerResult.Transition(AutomatorState.PostFate, completion.Reason);
        }

        var observedFate = active.First(f => IsSameOccurrence(f, selection.Value.Fate));
        if (lastObservedFate.FateId != 0 && !IsSameOccurrence(lastObservedFate, observedFate))
            outsideFateSince = DateTimeOffset.MinValue;
        lastObservedFate = observedFate;
        if (lastObservedFate.FateRule != FateRule.Escort)
            ClearEscortPositioning("selected FATE is not an escort");
        completionTracker.Observe(lastObservedFate);
        if (!IsAtFate(lastObservedFate, objectTable.LocalPlayer.Position))
        {
            outsideFateSince = outsideFateSince == DateTimeOffset.MinValue ? DateTimeOffset.UtcNow : outsideFateSince;
            var outsideDuration = DateTimeOffset.UtcNow - outsideFateSince;
            if (outsideDuration < OutsideFateGrace)
                return AutomatorStateHandlerResult.Wait($"Player briefly outside selected FATE; retaining participation ({outsideDuration.TotalSeconds:F1}/{OutsideFateGrace.TotalSeconds:F1}s)");

            outsideFateSince = DateTimeOffset.MinValue;
            textAdvance.Disable();
            combatHandler.Reset("Player left selected FATE");
            var travelSelection = selection.Value with
            {
                Fate = lastObservedFate,
                Destination = lastObservedFate.Center,
                ShouldTeleport = false,
                PreferredAetheryteId = null,
                Teleport = default
            };
            context.UpdateSelection(travelSelection, "Player outside selected FATE", log);
            travelController.RequestTravel(travelSelection, TravelRequestKind.Automatic);
            return AutomatorStateHandlerResult.Transition(AutomatorState.Travelling, "Player outside selected FATE; routing back");
        }
        outsideFateSince = DateTimeOffset.MinValue;
        UpdateCollectionInteraction(lastObservedFate);
        if (lastObservedFate.IsCollect && lastObservedFate.Progress >= 100 &&
            !condition[ConditionFlag.InCombat] && !condition[ConditionFlag.OccupiedInQuestEvent])
        {
            context.EndedFateId = lastObservedFate.FateId;
            clearanceTargeter?.CaptureEndedFate(lastObservedFate);
            StopFateOffense("Collection objective completed");
            collectionEndedAt = DateTimeOffset.UtcNow;
            automaticTravel.WaitForExpiry(lastObservedFate);
            completionTracker.RecordTerminal(lastObservedFate);
            if (configuration.WaitForFollowUpFates)
                followUpTracker.Begin(lastObservedFate);
            context.ClearSelection("Collection objective complete", log);
            return AutomatorStateHandlerResult.Transition(AutomatorState.PostFate, "Collection objective complete; awaiting rewards");
        }
        var syncSelection = selection.Value with { Fate = lastObservedFate };
        if (!levelSyncService.EnsureSynced(syncSelection))
        {
            EmitLevelSyncReminder(syncSelection);
            return AutomatorStateHandlerResult.Wait(levelSyncService.Status);
        }

        var rotationProvider = combatPlugins.GetPreferredRotation(configuration.PreferredRotationProvider);
        if (rotationProvider == null)
        {
            EmitRotationWarning();
            bossModPresetManager.DeactivateActivePreset();
            rotationSolverBridge.NotifyFateEnded();
            wrathComboBridge.NotifyAutomationStopped();
            combatHandler.Reset("Rotation provider unavailable");
            return AutomatorStateHandlerResult.Wait("Rotation provider unavailable");
        }

        if (rotationProvider.Id != CombatPluginId.RotationSolver)
        {
            rotationSolverBridge.NotifyFateEnded();
        }

        if (rotationProvider.Id != CombatPluginId.WrathCombo)
        {
            wrathComboBridge.NotifyAutomationStopped();
        }

        var dodgeProvider = combatPlugins.GetPreferredDodge(configuration.PreferredDodgeProvider);
        var rotationIsBossMod = rotationProvider.Id == CombatPluginId.BossMod || rotationProvider.Id == CombatPluginId.BossModReborn;
        var dodgeIsBossMod = !rotationIsBossMod && dodgeProvider != null && (dodgeProvider.Id == CombatPluginId.BossMod || dodgeProvider.Id == CombatPluginId.BossModReborn);

        if (rotationIsBossMod)
        {
            bossModPresetManager.EnsurePresets();
            bossModPresetManager.ActivateRotationPreset();
            bossModPresetManager.ApplyFateTargetLimit();
        }
        else if (dodgeIsBossMod)
        {
            bossModPresetManager.EnsurePresets();
            bossModPresetManager.ActivatePassivePreset();
            bossModPresetManager.ApplyFateTargetLimit();
        }
        else
        {
            bossModPresetManager.DeactivateActivePreset();
        }

        if (rotationProvider.Id == CombatPluginId.RotationSolver)
        {
            rotationSolverBridge.EngageNearestAuto(selection.Value);
        }
        else if (rotationProvider.Id == CombatPluginId.WrathCombo)
        {
            if (!wrathComboBridge.ConfigureForFateAutomation())
            {
                EmitRotationWarning();
                combatHandler.Reset("Wrath configuration pending");
                return AutomatorStateHandlerResult.Wait("Wrath configuration pending");
            }
        }

        var snapshot = combatHandler.Execute(selection.Value);
        if (snapshot == null)
        {
            if (lastObservedFate.FateRule == FateRule.Escort)
                return TickEscortPositioning(selection.Value, lastObservedFate);
            EmitWaitingReminder(selection.Value.Fate.FateId);
            return AutomatorStateHandlerResult.None("No fate targets available");
        }

        SuspendEscortPositioning("combat target available");

        EmitTargetStatus(snapshot.Value);

        return AutomatorStateHandlerResult.None();
    }

    private AutomatorStateHandlerResult HandlePostFate(AutomatorContext context)
    {
        if (condition[ConditionFlag.Unconscious] || objectTable.LocalPlayer?.CurrentHp == 0)
        {
            textAdvance.Disable();
            return AutomatorStateHandlerResult.Transition(AutomatorState.Recovering, "Player died during post-FATE cleanup");
        }
        if (objectTable.LocalPlayer == null || condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
        {
            textAdvance.Disable();
            return AutomatorStateHandlerResult.Wait("Waiting for player availability before post-FATE cleanup");
        }

        if (postEnteredAt != context.LastStateChangedAt)
        {
            postEnteredAt = context.LastStateChangedAt;
            postCombatCleaned = false;
        }
        travelController.Cancel("Post-fate cleanup", TravelRequestKind.Automatic);
        UpdateCollectionInteraction(lastObservedFate, allowNewInteraction: false);
        if (condition[ConditionFlag.InCombat])
        {
            return AutomatorStateHandlerResult.Wait("Waiting for combat to end before post-FATE cleanup");
        }

        if (!postCombatCleaned)
        {
            obstacleMap.ClearTemporaryMap();
            bossModPresetManager.DeactivateActivePreset();
            rotationSolverBridge.NotifyFateEnded();
            wrathComboBridge.NotifyAutomationStopped();
            combatHandler.Reset("Post-fate cleanup");
            postCombatCleaned = true;
        }

        if (collectionEndedAt != DateTimeOffset.MinValue && configuration.CollectionPolicy != FateCollectionPolicy.KillOnly)
        {
            var elapsed = DateTimeOffset.UtcNow - collectionEndedAt;
            // FateReward PostSetup increments this tracker. A reward may precede disappearance.
            // Request/Talk closure is further lifecycle evidence; the timeout only covers lost UI events.
            var rewardObserved = rewardTracker.CompletedCount > rewardsAtEntry;
            var anyCollectionDialog = HasCollectionDialog();
            if ((anyCollectionDialog && elapsed < TimeSpan.FromSeconds(15)) ||
                (!rewardObserved && elapsed < TimeSpan.FromSeconds(3)))
                return AutomatorStateHandlerResult.Wait("Waiting for collect-FATE hand-in/reward lifecycle");
        }

        collectionEndedAt = DateTimeOffset.MinValue;
        collectionInteractionOwned = false;
        textAdvance.Disable();

        if (!rewardTracker.PendingStop && configuration.WaitForFollowUpFates && followUpTracker.IsWatching)
        {
            var followUp = fateRepository.EnumerateActiveFates().Any(followUpTracker.IsPreferred);
            if (!followUp)
            {
                return AutomatorStateHandlerResult.Wait("Waiting for follow-up FATE");
            }
        }

        clearanceTargeter?.CompleteEndedFate();
        context.EndedFateId = null;
        context.ClearSelection("Post-fate cleanup", log);
        return AutomatorStateHandlerResult.Transition(AutomatorState.SelectingFate, "Post-fate cleanup complete");
    }

    private void StopFateOffense(string reason)
    {
        var rotation = combatPlugins.GetPreferredRotation(configuration.PreferredRotationProvider);
        var dodge = combatPlugins.GetPreferredDodge(configuration.PreferredDodgeProvider);
        var bossModOwnsProvider = rotation?.Id is CombatPluginId.BossMod or CombatPluginId.BossModReborn ||
            dodge?.Id is CombatPluginId.BossMod or CombatPluginId.BossModReborn;
        if (bossModOwnsProvider)
        {
            bossModPresetManager.EnsurePresets();
            bossModPresetManager.ActivatePassivePreset();
            bossModPresetManager.ApplyCombatClearanceTargeting();
        }
        else
        {
            bossModPresetManager.DeactivateActivePreset();
        }
        rotationSolverBridge.NotifyFateEnded();
        wrathComboBridge.NotifyAutomationStopped();
        combatHandler.Reset(reason);
    }

    private unsafe void UpdateCollectionInteraction(ActiveFate fate, bool allowNewInteraction = true)
    {
        if (!fate.IsCollect || configuration.CollectionPolicy == FateCollectionPolicy.KillOnly ||
            !condition[ConditionFlag.OccupiedInQuestEvent] || (!allowNewInteraction && !collectionInteractionOwned))
        {
            collectionInteractionOwned = false;
            textAdvance.Disable();
            return;
        }

        // BossMod interacts without setting the hard target. Use the actual event scene's NPC.
        var events = EventFramework.Instance();
        var target = events == null ? null : objectTable.SearchById((ulong)events->SceneGameObjectId);
        var isCollector = target != null &&
            ((fate.HasObjectiveNpc && (target.EntityId == fate.ObjectiveNpc || target.GameObjectId == fate.ObjectiveNpc)) ||
             (fate.HasMotivationNpc && (target.EntityId == fate.MotivationNpc || target.GameObjectId == fate.MotivationNpc)));
        var owned = isCollector && !IsAddonReady("SelectString") && !IsAddonReady("SelectIconString") && !IsAddonReady("SelectYesno");
        collectionInteractionOwned = owned;
        if (owned)
            textAdvance.EnableForCollection();
        else
            textAdvance.Disable();
    }

    private bool HasCollectionDialog() => IsAddonReady("Request") || IsAddonReady("Talk") ||
                                           IsAddonReady("SelectString") || IsAddonReady("SelectIconString") ||
                                           IsAddonReady("SelectYesno") || condition[ConditionFlag.OccupiedInQuestEvent];

    private static bool IsSameOccurrence(ActiveFate current, ActiveFate selected)
        => current.FateId == selected.FateId && current.TerritoryId == selected.TerritoryId &&
           (current.StartTimeEpoch == 0 || selected.StartTimeEpoch == 0 || current.StartTimeEpoch == selected.StartTimeEpoch);

    private AutomatorStateHandlerResult TickEscortPositioning(FateSelection selection, ActiveFate fate)
    {
        if (condition[ConditionFlag.InCombat])
        {
            SuspendEscortPositioning("combat active");
            return AutomatorStateHandlerResult.None("Escort positioning suspended during combat");
        }

        var player = objectTable.LocalPlayer;
        if (player == null) return AutomatorStateHandlerResult.Wait("Waiting for player before escort positioning");

        var anchor = objectTable.Cast<IGameObject>()
            .OfType<IBattleNpc>()
            .Where(npc => FateTargetPolicy.IsValidTravelAnchor(npc, fate) &&
                          FateTargetPolicy.IsFateNpcIdentity(npc.GameObjectId, npc.EntityId, fate.MotivationNpc, fate.ObjectiveNpc))
            .OrderBy(npc => FlatDistance(npc.Position, player.Position))
            .FirstOrDefault();

        var anchorPosition = anchor?.Position ?? fate.Center;
        var anchorId = anchor?.GameObjectId ?? 0;
        var now = DateTimeOffset.UtcNow;
        var destination = escortTracker.Tick(fate, anchorId, anchorPosition, player.Position, now);
        if (now >= nextEscortDiagnosticAt)
        {
            var anchorSource = anchor == null ? "FATE center" : $"NPC object={anchor.GameObjectId} entity={anchor.EntityId}";
            log.Debug("Escort positioning: fate={FateId} anchor={AnchorSource} anchorPos=({AnchorX:F1},{AnchorY:F1},{AnchorZ:F1}) playerPos=({PlayerX:F1},{PlayerY:F1},{PlayerZ:F1}) heading=({HeadingX:F2},{HeadingZ:F2}) displacement={Displacement:F2}y playerOffset={PlayerOffset:F1}y state={State}",
                fate.FateId, anchorSource, anchorPosition.X, anchorPosition.Y, anchorPosition.Z,
                player.Position.X, player.Position.Y, player.Position.Z,
                escortTracker.Heading.X, escortTracker.Heading.Y, escortTracker.LastSampleDisplacement,
                escortTracker.PlayerHeadingOffset, escortTracker.Status);
            nextEscortDiagnosticAt = now.AddSeconds(5);
        }
        if (destination is not { } target)
        {
            if (travelController.CurrentJob?.Kind == TravelRequestKind.EscortPositioning)
                travelController.Cancel("Escort stopped or player caught up", TravelRequestKind.EscortPositioning);
            lastEscortDestination = null;
            return AutomatorStateHandlerResult.None($"Escort positioning: {escortTracker.Status}");
        }

        if (travelController.CurrentJob is { } current && current.Kind != TravelRequestKind.EscortPositioning)
        {
            SuspendEscortPositioning("higher-priority travel active");
            return AutomatorStateHandlerResult.None("Escort positioning yielded to higher-priority travel");
        }

        if (travelController.CurrentJob == null && travelController.Phase == TravelPhase.Failed &&
            DateTimeOffset.UtcNow < nextEscortRetryAt)
            return AutomatorStateHandlerResult.None("Escort positioning waiting for navigation retry backoff");

        var retryFailedLeg = travelController.CurrentJob == null && travelController.Phase == TravelPhase.Failed &&
                             DateTimeOffset.UtcNow >= nextEscortRetryAt;
        if (lastEscortDestination is not { } previous || FlatDistance(previous, target) >= 1f || retryFailedLeg)
        {
            var movementSelection = selection with
            {
                Fate = fate,
                Destination = target,
                ShouldTeleport = false,
                PreferredAetheryteId = null,
                Teleport = default
            };
            travelController.RequestTravel(movementSelection, TravelRequestKind.EscortPositioning, 2.5f);
            lastEscortDestination = target;
            nextEscortRetryAt = DateTimeOffset.UtcNow.AddSeconds(5);
            log.Debug("Automator: escort leapfrog leg started fate={FateId} anchor={AnchorId} heading=({HeadingX:F2},{HeadingZ:F2}) destination=({X:F1},{Y:F1},{Z:F1})",
                fate.FateId, anchorId, escortTracker.Heading.X, escortTracker.Heading.Y, target.X, target.Y, target.Z);
        }

        return AutomatorStateHandlerResult.None("Moving ahead of escort; destination held for this leg");
    }

    private void SuspendEscortPositioning(string reason)
    {
        if (travelController.CurrentJob?.Kind == TravelRequestKind.EscortPositioning)
            travelController.Cancel(reason, TravelRequestKind.EscortPositioning);
        escortTracker.SuspendLeg();
        lastEscortDestination = null;
    }

    private void ClearEscortPositioning(string reason)
    {
        SuspendEscortPositioning(reason);
        escortTracker.Reset();
        nextEscortRetryAt = DateTimeOffset.MinValue;
    }

    private unsafe bool IsAtFate(ActiveFate fate, System.Numerics.Vector3 position)
    {
        try
        {
            var manager = FateManager.Instance();
            if (manager != null && fate.FateId <= ushort.MaxValue && manager->GetCurrentFateId() == fate.FateId)
                return true;
        }
        catch
        {
            // Native addresses are unavailable in offline harnesses and during early loading.
        }
        var dx = position.X - fate.Center.X;
        var dz = position.Z - fate.Center.Z;
        if (MathF.Sqrt(dx * dx + dz * dz) <= fate.Radius + 2f)
            return true;

        return objectTable
            .Cast<Dalamud.Game.ClientState.Objects.Types.IGameObject>()
            .OfType<Dalamud.Game.ClientState.Objects.Types.IBattleNpc>()
            .Any(npc => FateTargetPolicy.IsValidTravelAnchor(npc, fate) &&
                        FlatDistance(npc.Position, position) <= EscortAnchorParticipationDistance);
    }

    private static float FlatDistance(System.Numerics.Vector3 left, System.Numerics.Vector3 right)
    {
        var dx = left.X - right.X;
        var dz = left.Z - right.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private unsafe bool IsAddonReady(string name)
    {
        var addon = (AtkUnitBase*)gameGui.GetAddonByName(name, 1).Address;
        return addon != null && addon->IsReady && addon->IsVisible;
    }

    private void EmitWaitingReminder(uint fateId)
    {
        if (DateTimeOffset.UtcNow < nextReminderAt)
        {
            return;
        }

        log.Debug("Automator: awaiting combat handler for fate #{FateId}", fateId);
        nextReminderAt = DateTimeOffset.UtcNow.AddSeconds(5);
    }

    private void EmitTargetStatus(FateTargetSnapshot snapshot)
    {
        if (DateTimeOffset.UtcNow < nextTargetLogAt)
        {
            return;
        }

        log.Debug(
            "Automator: target #{ObjectId} {Name} dist={Distance:F1}y hp={HpPercent:P0}",
            snapshot.ObjectId,
            snapshot.Name,
            snapshot.DistanceFromPlayer,
            snapshot.MaxHp > 0 ? (double)snapshot.CurrentHp / snapshot.MaxHp : 0);
        nextTargetLogAt = DateTimeOffset.UtcNow.AddSeconds(5);
    }

    private void EmitLevelSyncReminder(FateSelection selection)
    {
        if (DateTimeOffset.UtcNow < nextSyncLogAt)
        {
            return;
        }

        log.Debug(
            "Automator: waiting for FATE sync #{FateId}; playerLevel={PlayerLevel} fateLevel={FateLevel} maxLevel={FateMaxLevel} syncStatus={Status}",
            selection.Fate.FateId,
            objectTable.LocalPlayer == null ? 0 : objectTable.LocalPlayer.Level,
            selection.Fate.Level,
            selection.Fate.MaxLevel,
            levelSyncService.Status);
        nextSyncLogAt = DateTimeOffset.UtcNow.AddSeconds(3);
    }

    private void EmitRotationWarning()
    {
        if (DateTimeOffset.UtcNow < nextRotationWarningAt)
        {
            return;
        }

        var preferred = configuration.PreferredRotationProvider;
        if (preferred != CombatPluginId.None && combatPlugins.GetStatus(preferred) is { } configured)
        {
            var reason = !configured.Installed
                ? "not installed"
                : !configured.IsLoaded
                    ? "not loaded"
                    : !configured.SupportsRotation
                        ? "rotation unsupported"
                        : "unknown";
            log.Debug("Automator: rotation provider {Provider} unavailable ({Reason})", configured.DisplayName, reason);
        }
        else
        {
            log.Debug("Automator: no compatible rotation provider detected (BossMod, BossMod Reborn, Wrath Combo, Rotation Solver)");
        }

        nextRotationWarningAt = DateTimeOffset.UtcNow.AddSeconds(5);
    }
}
