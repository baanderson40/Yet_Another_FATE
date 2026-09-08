using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Dalamud.Plugin.Services;
using YetAnotherFATE.Config;
using YetAnotherFATE.Data.Automator;
using YetAnotherFATE.Data.Fates;
using YetAnotherFATE.Data.Travel;
using YetAnotherFATE.Modules.Automator.Handlers;
using YetAnotherFATE.Services.Combat.BossMod;
using YetAnotherFATE.Services.Combat.RotationSolver;
using YetAnotherFATE.Services.Combat.Wrath;
using YetAnotherFATE.Services.Fates.CombatHelper.Engagement;
using YetAnotherFATE.Services.Travel;

namespace YetAnotherFATE.Chains;

public sealed class AutomatorChain : IDisposable
{
    private readonly ITravelController travelController;
    private readonly IPluginLog log;
    private readonly Configuration configuration;
    private readonly BossModPresetManager bossModPresetManager;
    private readonly IRotationSolverBridge rotationSolverBridge;
    private readonly IWrathComboBridge wrathComboBridge;
    private readonly IFateCombatHandler combatHandler;
    private readonly IReadOnlyList<IAutomatorStateHandler> handlers;
    private readonly AutomatorContext context = new();
    private bool disposed;
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

    public AutomatorChain(
        ITravelController travelController,
        IPluginLog log,
        Configuration configuration,
        IEnumerable<IAutomatorStateHandler> handlers,
        BossModPresetManager bossModPresetManager,
        IRotationSolverBridge rotationSolverBridge,
        IWrathComboBridge wrathComboBridge,
        IFateCombatHandler combatHandler)
    {
        this.travelController = travelController;
        this.log = log;
        this.configuration = configuration;
        this.handlers = handlers.ToArray();
        this.bossModPresetManager = bossModPresetManager;
        this.rotationSolverBridge = rotationSolverBridge;
        this.wrathComboBridge = wrathComboBridge;
        this.combatHandler = combatHandler;

        travelController.PhaseChanged += OnTravelPhaseChanged;
    }

    public void Tick()
    {
        if (!configuration.AutomatorEnabled)
        {
            HandleDisabledAutomator();
            return;
        }

        var travelPhase = (TravelPhase)Volatile.Read(ref observedTravelPhaseRaw);
        var travelDetail = Volatile.Read(ref observedTravelDetail);
        context.SetTravelPhase(travelPhase, travelDetail);

        if (context.State == AutomatorState.Disabled)
        {
            context.TransitionTo(AutomatorState.SelectingFate, "Automator enabled", log);
        }

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
        }
        else if (!string.IsNullOrWhiteSpace(result.Reason) && ShouldLogResult(result.Reason))
        {
            log.Debug("Automator: {Reason}", result.Reason);
        }
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
        travelController.Cancel("Automator disabled", TravelRequestKind.Automatic);
        if (context.State != AutomatorState.Disabled)
        {
            context.Reset(log);
        }

        bossModPresetManager.ReleasePresets();
        rotationSolverBridge.NotifyFateEnded();
        wrathComboBridge.NotifyAutomationStopped();
        combatHandler.Reset("Automator disabled");
    }

    private void OnTravelPhaseChanged(object? sender, TravelPhaseChangedEventArgs args)
    {
        Volatile.Write(ref observedTravelPhaseRaw, (int)args.Phase);
        Volatile.Write(ref observedTravelDetail, args.Detail);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        travelController.PhaseChanged -= OnTravelPhaseChanged;
        bossModPresetManager.ReleasePresets();
        rotationSolverBridge.NotifyFateEnded();
        wrathComboBridge.NotifyAutomationStopped();
        combatHandler.Reset("Automator disposed");
        disposed = true;
    }
}
