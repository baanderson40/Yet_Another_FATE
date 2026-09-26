using System;
using System.Linq;
using YetAnotherFATE.Config;
using YetAnotherFATE.Data.Automator;
using YetAnotherFATE.Data.Combat;
using YetAnotherFATE.Data.Fates;
using YetAnotherFATE.Data.Travel;
using YetAnotherFATE.Services.Combat;
using YetAnotherFATE.Services.Combat.RotationSolver;
using YetAnotherFATE.Services.Combat.Wrath;
using YetAnotherFATE.Services.Combat.BossMod;
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
    private DateTimeOffset participatingEnteredAt;
    private DateTimeOffset postEnteredAt;
    private DateTimeOffset collectionEndedAt;
    private int rewardsAtEntry;
    private ActiveFate lastObservedFate;
    private bool postCombatCleaned;
    private bool collectionInteractionOwned;
    private DateTimeOffset outsideFateSince = DateTimeOffset.MinValue;
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
        IPluginLog log)
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
    }

    public bool CanHandle(AutomatorState state) => state is AutomatorState.Participating or AutomatorState.PostFate;

    public void Reset()
    {
        participatingEnteredAt = postEnteredAt = collectionEndedAt = default;
        lastObservedFate = default;
        collectionInteractionOwned = postCombatCleaned = false;
        outsideFateSince = DateTimeOffset.MinValue;
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
            textAdvance.Disable();
            combatHandler.Reset("Player unconscious");
            return AutomatorStateHandlerResult.Transition(AutomatorState.Recovering, "Player unconscious; waiting for recovery");
        }

        var selection = context.CurrentSelection;
        if (selection == null)
        {
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
            collectionEndedAt = DateTimeOffset.UtcNow;
            automaticTravel.WaitForExpiry(lastObservedFate);
            completionTracker.RecordTerminal(lastObservedFate);
            if (configuration.WaitForFollowUpFates)
                followUpTracker.Begin(lastObservedFate);
            context.ClearSelection("Collection objective complete", log);
            return AutomatorStateHandlerResult.Transition(AutomatorState.PostFate, "Collection objective complete; awaiting rewards");
        }
        if (!levelSyncService.EnsureSynced(selection.Value))
        {
            EmitLevelSyncReminder(selection.Value);
            return AutomatorStateHandlerResult.Wait("Waiting for level sync");
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
            EmitWaitingReminder(selection.Value.Fate.FateId);
            return AutomatorStateHandlerResult.None("No fate targets available");
        }

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

        context.ClearSelection("Post-fate cleanup", log);
        return AutomatorStateHandlerResult.Transition(AutomatorState.SelectingFate, "Post-fate cleanup complete");
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
            "Automator: waiting for level sync before engaging fate #{FateId}",
            selection.Fate.FateId);
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
