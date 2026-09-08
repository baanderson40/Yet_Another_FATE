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
using YetAnotherFATE.Services.Travel;
using Dalamud.Plugin.Services;

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
    private DateTimeOffset nextReminderAt = DateTimeOffset.MinValue;
    private DateTimeOffset nextTargetLogAt = DateTimeOffset.MinValue;
    private DateTimeOffset nextSyncLogAt = DateTimeOffset.MinValue;
    private DateTimeOffset nextRotationWarningAt = DateTimeOffset.MinValue;

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
        this.log = log;
    }

    public bool CanHandle(AutomatorState state) => state is AutomatorState.Participating or AutomatorState.PostFate;

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
        var selection = context.CurrentSelection;
        if (selection == null)
        {
            combatHandler.Reset("Selection missing in state");
            return AutomatorStateHandlerResult.Transition(AutomatorState.SelectingFate, "Selection missing in participating state");
        }

        var active = fateRepository.EnumerateActiveFates();
        if (!active.Any(f => f.FateId == selection.Value.Fate.FateId))
        {
            combatHandler.Reset("Fate completed");
            context.ClearSelection("Fate completed", log);
            return AutomatorStateHandlerResult.Transition(AutomatorState.PostFate, $"Fate #{selection.Value.Fate.FateId} completed");
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
        }
        else if (dodgeIsBossMod)
        {
            bossModPresetManager.EnsurePresets();
            bossModPresetManager.ActivatePassivePreset();
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
            return AutomatorStateHandlerResult.None("No fate targets available");
        }

        EmitTargetStatus(snapshot.Value);

        EmitWaitingReminder(selection.Value.Fate.FateId);
        return AutomatorStateHandlerResult.None();
    }

    private AutomatorStateHandlerResult HandlePostFate(AutomatorContext context)
    {
        travelController.Cancel("Post-fate cleanup", TravelRequestKind.Automatic);
        bossModPresetManager.DeactivateActivePreset();
        rotationSolverBridge.NotifyFateEnded();
        wrathComboBridge.NotifyAutomationStopped();
        combatHandler.Reset("Post-fate cleanup");
        context.ClearSelection("Post-fate cleanup", log);
        return AutomatorStateHandlerResult.Transition(AutomatorState.SelectingFate, "Post-fate cleanup complete");
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
