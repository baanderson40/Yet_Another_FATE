using System;
using YetAnotherFATE.Data.Fates;
using YetAnotherFATE.Data.Travel;
using Dalamud.Plugin.Services;

namespace YetAnotherFATE.Data.Automator;

public sealed class AutomatorContext
{
    public AutomatorState State { get; private set; } = AutomatorState.Disabled;
    public FateSelection? CurrentSelection { get; private set; }
    public bool ReturningAfterRecovery { get; set; }
    public DeathRecoveryContext? DeathRecovery { get; set; }
    public DateTimeOffset LastStateChangedAt { get; private set; } = DateTimeOffset.MinValue;
    public string? LastStateReason { get; private set; }
    public DateTimeOffset? SelectionUpdatedAt { get; private set; }
    public string? SelectionReason { get; private set; }
    public TravelPhase TravelPhase { get; private set; } = TravelPhase.Idle;
    public string? TravelPhaseDetail { get; private set; }

    public void TransitionTo(AutomatorState nextState, string? reason, IPluginLog log)
    {
        if (State == nextState)
        {
            if (!string.IsNullOrWhiteSpace(reason))
            {
                log.Debug("Automator: state={State} (note: {Reason})", nextState, reason);
            }

            return;
        }

        State = nextState;
        LastStateReason = reason;
        LastStateChangedAt = DateTimeOffset.UtcNow;
        log.Debug("Automator: state => {State} ({Reason})", nextState, reason ?? "no reason");
    }

    public void SetTravelPhase(TravelPhase phase, string? detail)
    {
        TravelPhase = phase;
        TravelPhaseDetail = detail;
    }

    public void UpdateSelection(FateSelection selection, string reason, IPluginLog log)
    {
        CurrentSelection = selection;
        SelectionUpdatedAt = DateTimeOffset.UtcNow;
        SelectionReason = reason;
        log.Debug(
            "Automator: selection => #{FateId} {FateName} ({Reason})",
            selection.Fate.FateId,
            selection.Fate.Name,
            reason);
    }

    public void ClearSelection(string reason, IPluginLog log)
    {
        if (CurrentSelection == null)
        {
            return;
        }

        log.Debug("Automator: clearing selection ({Reason})", reason);
        CurrentSelection = null;
        ReturningAfterRecovery = false;
        SelectionUpdatedAt = DateTimeOffset.UtcNow;
        SelectionReason = reason;
    }

    public void Reset(IPluginLog log)
    {
        DeathRecovery = null;
        CurrentSelection = null;
        ReturningAfterRecovery = false;
        SelectionUpdatedAt = null;
        SelectionReason = null;
        State = AutomatorState.Disabled;
        LastStateChangedAt = DateTimeOffset.UtcNow;
        LastStateReason = "reset";
        TravelPhase = TravelPhase.Idle;
        TravelPhaseDetail = null;
        log.Debug("Automator: context reset");
    }
}
