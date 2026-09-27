using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using YetAnotherFATE.Config;
using YetAnotherFATE.Data.Automator;
using YetAnotherFATE.Data.Combat;
using YetAnotherFATE.Data.Travel;
using YetAnotherFATE.Services.Combat;
using YetAnotherFATE.Services.Combat.BossMod;
using YetAnotherFATE.Services.Combat.RotationSolver;
using YetAnotherFATE.Services.Combat.Wrath;
using YetAnotherFATE.Services.Fates;
using YetAnotherFATE.Services.Fates.CombatHelper.Engagement;
using YetAnotherFATE.Services.Travel;

namespace YetAnotherFATE.Modules.Automator.Handlers;

public sealed class ClearingCombatStateHandler : IAutomatorStateHandler
{
    private static readonly TimeSpan StableCombatClearance = TimeSpan.FromSeconds(1);

    private readonly ICombatPluginRegistry combatPlugins;
    private readonly IRotationSolverBridge rotationSolverBridge;
    private readonly IWrathComboBridge wrathComboBridge;
    private readonly BossModPresetManager bossModPresetManager;
    private readonly Configuration configuration;
    private readonly ITravelController travelController;
    private readonly IFateCombatHandler combatHandler;
    private readonly ITextAdvanceClient textAdvance;
    private readonly ICondition condition;
    private readonly IPluginLog log;
    private DateTimeOffset enteredAt;
    private DateTimeOffset combatClearSince;
    private DateTimeOffset nextProviderAttempt;

    public ClearingCombatStateHandler(
        ICombatPluginRegistry combatPlugins,
        IRotationSolverBridge rotationSolverBridge,
        IWrathComboBridge wrathComboBridge,
        BossModPresetManager bossModPresetManager,
        Configuration configuration,
        ITravelController travelController,
        IFateCombatHandler combatHandler,
        ITextAdvanceClient textAdvance,
        ICondition condition,
        IPluginLog log)
    {
        this.combatPlugins = combatPlugins;
        this.rotationSolverBridge = rotationSolverBridge;
        this.wrathComboBridge = wrathComboBridge;
        this.bossModPresetManager = bossModPresetManager;
        this.configuration = configuration;
        this.travelController = travelController;
        this.combatHandler = combatHandler;
        this.textAdvance = textAdvance;
        this.condition = condition;
        this.log = log;
    }

    public bool CanHandle(AutomatorState state) => state == AutomatorState.ClearingCombat;

    public void Reset()
    {
        enteredAt = DateTimeOffset.MinValue;
        combatClearSince = DateTimeOffset.MinValue;
        nextProviderAttempt = DateTimeOffset.MinValue;
    }

    public AutomatorStateHandlerResult Tick(AutomatorContext context)
    {
        var now = DateTimeOffset.UtcNow;
        if (enteredAt != context.LastStateChangedAt)
        {
            enteredAt = context.LastStateChangedAt;
            combatClearSince = DateTimeOffset.MinValue;
            nextProviderAttempt = DateTimeOffset.MinValue;
            travelController.Cancel("Combat clearance", TravelRequestKind.Automatic);
            textAdvance.Disable();
            combatHandler.Reset("Combat clearance started");
        }

        if (condition[ConditionFlag.InCombat])
        {
            combatClearSince = DateTimeOffset.MinValue;
            if (now >= nextProviderAttempt)
            {
                nextProviderAttempt = now.AddSeconds(2);
                ConfigureCombatProvider();
            }

            return AutomatorStateHandlerResult.Wait("Clearing combat before automation can continue");
        }

        combatClearSince = combatClearSince == DateTimeOffset.MinValue ? now : combatClearSince;
        if (now - combatClearSince < StableCombatClearance)
            return AutomatorStateHandlerResult.Wait("Waiting for combat to remain clear");

        StopCombatProvider();
        var nextState = context.CombatClearanceReturnState;
        if (nextState is AutomatorState.ClearingCombat or AutomatorState.Disabled)
            nextState = AutomatorState.SelectingFate;
        return AutomatorStateHandlerResult.Transition(nextState, "Combat cleared; resuming automation");
    }

    private void ConfigureCombatProvider()
    {
        var rotationProvider = combatPlugins.GetPreferredRotation(configuration.PreferredRotationProvider);
        var dodgeProvider = combatPlugins.GetPreferredDodge(configuration.PreferredDodgeProvider);
        if (rotationProvider == null)
        {
            bossModPresetManager.DeactivateActivePreset();
            rotationSolverBridge.NotifyFateEnded();
            wrathComboBridge.NotifyAutomationStopped();
            log.Warning("Automator: no rotation provider available while clearing combat");
            return;
        }

        var rotationIsBossMod = rotationProvider.Id is CombatPluginId.BossMod or CombatPluginId.BossModReborn;
        var dodgeIsBossMod = !rotationIsBossMod && dodgeProvider != null &&
            (dodgeProvider.Id is CombatPluginId.BossMod or CombatPluginId.BossModReborn);

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
            rotationSolverBridge.EngageNearestAuto();
        }
        else if (rotationProvider.Id == CombatPluginId.WrathCombo)
        {
            wrathComboBridge.ConfigureForFateAutomation();
        }
    }

    private void StopCombatProvider()
    {
        bossModPresetManager.DeactivateActivePreset();
        rotationSolverBridge.NotifyFateEnded();
        wrathComboBridge.NotifyAutomationStopped();
        combatHandler.Reset("Combat clearance complete");
        textAdvance.Disable();
    }
}
