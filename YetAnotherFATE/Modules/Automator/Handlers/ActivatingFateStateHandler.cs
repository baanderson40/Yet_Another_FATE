using System;
using System.Linq;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Plugin.Services;
using YetAnotherFATE.Data.Automator;
using YetAnotherFATE.Data.Fates;
using YetAnotherFATE.Data.Travel;
using YetAnotherFATE.Services.Fates;
using YetAnotherFATE.Services.Travel;

namespace YetAnotherFATE.Modules.Automator.Handlers;

public sealed class ActivatingFateStateHandler : IAutomatorStateHandler
{
    private static readonly TimeSpan NpcDiscoveryTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan InteractionRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ActivationTimeout = TimeSpan.FromSeconds(45);

    private readonly IFateRepository fateRepository;
    private readonly IFateActivationService activationService;
    private readonly ITravelController travelController;
    private readonly IObjectTable objectTable;
    private readonly IPluginLog log;
    private DateTimeOffset startedAt = DateTimeOffset.MinValue;
    private DateTimeOffset nextInteractionAt = DateTimeOffset.MinValue;
    private uint activeFateId;
    private bool npcTravelRequested;

    public ActivatingFateStateHandler(
        IFateRepository fateRepository,
        IFateActivationService activationService,
        ITravelController travelController,
        IObjectTable objectTable,
        IPluginLog log)
    {
        this.fateRepository = fateRepository;
        this.activationService = activationService;
        this.travelController = travelController;
        this.objectTable = objectTable;
        this.log = log;
    }

    public bool CanHandle(AutomatorState state) => state == AutomatorState.ActivatingFate;

    public AutomatorStateHandlerResult Tick(AutomatorContext context)
    {
        var selection = context.CurrentSelection;
        if (selection == null)
        {
            return AutomatorStateHandlerResult.Transition(AutomatorState.SelectingFate, "Activation selection missing");
        }

        if (activeFateId != selection.Value.Fate.FateId)
        {
            activeFateId = selection.Value.Fate.FateId;
            startedAt = DateTimeOffset.UtcNow;
            nextInteractionAt = DateTimeOffset.MinValue;
            npcTravelRequested = false;
        }

        var fate = fateRepository.EnumerateActiveFates().FirstOrDefault(f => f.FateId == activeFateId);
        if (fate.FateId == 0)
        {
            travelController.Cancel("Preparing FATE disappeared", TravelRequestKind.Automatic);
            return AutomatorStateHandlerResult.Transition(AutomatorState.SelectingFate, "Preparing FATE disappeared");
        }

        if (fate.State == FateState.Running)
        {
            log.Debug("Automator: FATE #{FateId} is now running after activation", fate.FateId);
            return AutomatorStateHandlerResult.Transition(AutomatorState.Participating, $"FATE #{fate.FateId} activated");
        }

        if (DateTimeOffset.UtcNow - startedAt > ActivationTimeout)
        {
            travelController.Cancel("FATE activation timed out", TravelRequestKind.Automatic);
            return AutomatorStateHandlerResult.Transition(AutomatorState.SelectingFate, "FATE activation timed out");
        }

        if (travelController.Phase is TravelPhase.Cancelled or TravelPhase.Failed)
        {
            return AutomatorStateHandlerResult.Transition(AutomatorState.SelectingFate, $"Activation travel {travelController.Phase}");
        }

        if (!activationService.TryGetMotivationNpc(fate, out var npc) || npc == null)
        {
            if (DateTimeOffset.UtcNow - startedAt > NpcDiscoveryTimeout)
            {
                travelController.Cancel("Motivation NPC not found", TravelRequestKind.Automatic);
                return AutomatorStateHandlerResult.Transition(AutomatorState.SelectingFate, $"Motivation NPC unavailable ({activationService.Status})");
            }

            return AutomatorStateHandlerResult.Wait(activationService.Status);
        }

        var player = objectTable.LocalPlayer;
        var distance = player == null ? float.MaxValue : System.Numerics.Vector3.Distance(player.Position, npc.Position);
        if (distance > 2f)
        {
            if (!npcTravelRequested && travelController.CurrentJob == null)
            {
                var npcSelection = selection.Value with
                {
                    Fate = fate,
                    Destination = npc.Position,
                    ShouldTeleport = false,
                    PreferredAetheryteId = null
                };
                npcTravelRequested = true;
                travelController.RequestTravel(npcSelection, TravelRequestKind.Automatic, 2f);
                log.Debug("Automator: travelling to motivation NPC for FATE #{FateId}, distance={Distance:F1}y", fate.FateId, distance);
            }

            return AutomatorStateHandlerResult.Wait("Travelling to motivation NPC");
        }

        npcTravelRequested = true;
        if (DateTimeOffset.UtcNow >= nextInteractionAt)
        {
            activationService.Interact(npc);
            nextInteractionAt = DateTimeOffset.UtcNow + InteractionRetryDelay;
        }

        return AutomatorStateHandlerResult.Wait($"Waiting for FATE activation ({activationService.Status})");
    }
}
