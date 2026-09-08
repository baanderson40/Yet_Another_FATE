using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using YetAnotherFATE.Data.Automator;
using YetAnotherFATE.Data.Fates;
using YetAnotherFATE.Data.Travel;
using YetAnotherFATE.Services.Fates;
using YetAnotherFATE.Services.Movement;
using YetAnotherFATE.Services.Travel;
using Dalamud.Plugin.Services;

namespace YetAnotherFATE.Modules.Automator.Handlers.TravellingToFate.Handlers;

public sealed class TravellingToFateStateHandler : IAutomatorStateHandler
{
    private static readonly TimeSpan TravelFailureCooldown = TimeSpan.FromSeconds(30);

    private readonly IFateRepository fateRepository;
    private readonly IFateSelector fateSelector;
    private readonly ITravelController travelController;
    private readonly INavmeshClient navmeshClient;
    private readonly IFateActivationService activationService;
    private readonly IPluginLog log;
    private DateTimeOffset nextStatusLogAt = DateTimeOffset.MinValue;
    private DateTimeOffset nextNavmeshWarningAt = DateTimeOffset.MinValue;
    private DateTimeOffset nextNoFatesLogAt = DateTimeOffset.MinValue;
    private readonly Dictionary<uint, DateTimeOffset> retryAfterByFateId = new();

    public TravellingToFateStateHandler(
        IFateRepository fateRepository,
        IFateSelector fateSelector,
        ITravelController travelController,
        INavmeshClient navmeshClient,
        IFateActivationService activationService,
        IPluginLog log)
    {
        this.fateRepository = fateRepository;
        this.fateSelector = fateSelector;
        this.travelController = travelController;
        this.navmeshClient = navmeshClient;
        this.activationService = activationService;
        this.log = log;
    }

    public bool CanHandle(AutomatorState state) => state is AutomatorState.SelectingFate or AutomatorState.Travelling;

    public AutomatorStateHandlerResult Tick(AutomatorContext context)
    {
        return context.State switch
        {
            AutomatorState.SelectingFate => HandleSelecting(context),
            AutomatorState.Travelling => HandleTravelling(context),
            _ => AutomatorStateHandlerResult.None(),
        };
    }

    private AutomatorStateHandlerResult HandleSelecting(AutomatorContext context)
    {
        var fates = fateRepository.EnumerateActiveFates();
        if (fates.Count == 0)
        {
            travelController.Cancel("No active fates", TravelRequestKind.Automatic);
            if (DateTimeOffset.UtcNow >= nextNoFatesLogAt)
            {
                nextNoFatesLogAt = DateTimeOffset.UtcNow.AddSeconds(10);
                return AutomatorStateHandlerResult.None("No active fates detected");
            }

            return AutomatorStateHandlerResult.None();
        }

        var eligibleFates = fates.Where(CanAttemptTravel).ToList();
        if (eligibleFates.Count == 0)
        {
            return AutomatorStateHandlerResult.None("All active fates are temporarily cooling down after travel failures");
        }

        var selection = fateSelector.SelectBest(eligibleFates);
        if (selection == null)
        {
            travelController.Cancel("Selector returned no candidate", TravelRequestKind.Automatic);
            return AutomatorStateHandlerResult.None("Selector returned no candidate");
        }

        if (!navmeshClient.IsAvailable)
        {
            EmitNavmeshWarning();
            travelController.Cancel("Navmesh unavailable", TravelRequestKind.Automatic);
            return AutomatorStateHandlerResult.None("Navmesh unavailable");
        }

        context.UpdateSelection(selection.Value, "Selection updated", log);
        travelController.RequestTravel(selection.Value);
        return AutomatorStateHandlerResult.Transition(AutomatorState.Travelling, $"Travelling to fate #{selection.Value.Fate.FateId}");
    }

    private AutomatorStateHandlerResult HandleTravelling(AutomatorContext context)
    {
        var selection = context.CurrentSelection;
        if (selection == null)
        {
            travelController.Cancel("Selection missing", TravelRequestKind.Automatic);
            return AutomatorStateHandlerResult.Transition(AutomatorState.SelectingFate, "Selection missing");
        }

        var activeFates = fateRepository.EnumerateActiveFates();
        if (!activeFates.Any(f => f.FateId == selection.Value.Fate.FateId))
        {
            travelController.Cancel("Selected fate is no longer active", TravelRequestKind.Automatic);
            context.ClearSelection("Selected fate ended during travel", log);
            return AutomatorStateHandlerResult.Transition(AutomatorState.SelectingFate, "Selected fate ended during travel");
        }

        var currentFate = activeFates.First(f => f.FateId == selection.Value.Fate.FateId);
        var job = travelController.CurrentJob;
        if (currentFate.State == Dalamud.Game.ClientState.Fates.FateState.Running)
        {
            if (selection.Value.Fate.RequiresActivation)
            {
                var centerSelection = selection.Value with
                {
                    Fate = currentFate,
                    Destination = currentFate.Center,
                    ShouldTeleport = false,
                    PreferredAetheryteId = null
                };
                if (job == null || Vector3.Distance(job.Selection.Destination, currentFate.Center) >= 1f)
                {
                    travelController.RequestTravel(centerSelection);
                }

                context.UpdateSelection(centerSelection, "FATE started during travel; avoiding motivation NPC", log);
                selection = centerSelection;
            }
        }
        else if (currentFate.State == Dalamud.Game.ClientState.Fates.FateState.Preparing && currentFate.DistanceFromPlayer <= 100f &&
                 activationService.TryGetMotivationNpc(currentFate, out var motivationNpc) && motivationNpc != null)
        {
            if (job == null || Vector3.Distance(job.Selection.Destination, motivationNpc.Position) >= 1f)
            {
                var npcSelection = selection.Value with
                {
                    Fate = currentFate,
                    Destination = motivationNpc.Position,
                    ShouldTeleport = false,
                    PreferredAetheryteId = null
                };
                travelController.RequestTravel(npcSelection, TravelRequestKind.Automatic, 3f);
                context.UpdateSelection(npcSelection, "Retargeted travel to motivation NPC", log);
                selection = npcSelection;
                job = travelController.CurrentJob;
                log.Debug("Automator: retargeted FATE #{FateId} to motivation NPC {Name}", currentFate.FateId, motivationNpc.Name.TextValue);
            }
        }

        var phase = context.TravelPhase;

        if (job is { Selection: { } jobSelection } && jobSelection.Fate.FateId != selection.Value.Fate.FateId)
        {
            context.UpdateSelection(jobSelection, "Adopted active travel job selection", log);
            selection = jobSelection;
        }

        switch (phase)
        {
            case TravelPhase.Arrived:
                return currentFate.State == Dalamud.Game.ClientState.Fates.FateState.Preparing
                    ? AutomatorStateHandlerResult.Transition(AutomatorState.ActivatingFate, $"Arrived at fate #{selection.Value.Fate.FateId}; resolving motivation NPC")
                    : AutomatorStateHandlerResult.Transition(AutomatorState.Participating, $"Arrived at fate #{selection.Value.Fate.FateId}");
            case TravelPhase.Cancelled:
                context.ClearSelection("Travel cancelled", log);
                return AutomatorStateHandlerResult.Transition(AutomatorState.SelectingFate, "Travel cancelled");
            case TravelPhase.Failed:
                RegisterTravelFailure(selection.Value.Fate.FateId);
                context.ClearSelection("Travel failed", log);
                return AutomatorStateHandlerResult.Transition(AutomatorState.SelectingFate, $"Travel failed ({context.TravelPhaseDetail})");
        }

        if (job == null)
        {
            // Travel job completed without arrival info; reselect to stay in sync.
            context.ClearSelection("Travel job missing", log);
            return AutomatorStateHandlerResult.Transition(AutomatorState.SelectingFate, "Travel job missing");
        }

        EmitStatus(selection.Value, phase, job.Status);
        return AutomatorStateHandlerResult.None();
    }

    private void EmitStatus(FateSelection selection, TravelPhase phase, string? jobStatus)
    {
        if (DateTimeOffset.UtcNow < nextStatusLogAt)
        {
            return;
        }

        log.Debug(
            "Automator: travelling to fate #{FateId} ({Name}) phase={Phase} detail={Detail}",
            selection.Fate.FateId,
            selection.Fate.Name,
            phase,
            jobStatus ?? "n/a");
        nextStatusLogAt = DateTimeOffset.UtcNow.AddSeconds(5);
    }

    private void EmitNavmeshWarning()
    {
        if (DateTimeOffset.UtcNow < nextNavmeshWarningAt)
        {
            return;
        }

        log.Warning("Automator: navmesh unavailable (status={Status}). Install/enable vnavmesh to allow travel automation.", navmeshClient.Status);
        nextNavmeshWarningAt = DateTimeOffset.UtcNow.AddSeconds(10);
    }

    private bool CanAttemptTravel(ActiveFate fate)
    {
        if (!retryAfterByFateId.TryGetValue(fate.FateId, out var retryAfter))
        {
            return true;
        }

        if (DateTimeOffset.UtcNow < retryAfter)
        {
            return false;
        }

        retryAfterByFateId.Remove(fate.FateId);
        return true;
    }

    private void RegisterTravelFailure(uint fateId)
    {
        var retryAfter = DateTimeOffset.UtcNow + TravelFailureCooldown;
        retryAfterByFateId[fateId] = retryAfter;
        log.Warning("Automator: delaying another travel attempt to fate #{FateId} until {RetryAfter:O}", fateId, retryAfter);
    }
}
