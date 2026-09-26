using System;
using System.Linq;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
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
    private static readonly TimeSpan DialogActionDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ActivationTimeout = TimeSpan.FromSeconds(45);

    private readonly IFateRepository fateRepository;
    private readonly IFateActivationService activationService;
    private readonly ITravelController travelController;
    private readonly IObjectTable objectTable;
    private readonly IGameGui gameGui;
    private readonly ITextAdvanceClient textAdvance;
    private readonly IPluginLog log;
    private readonly ICondition condition;
    private DateTimeOffset enteredAt;
    private DateTimeOffset interactionUntil;
    private ulong interactionNpcId;
    private uint interactionNpcEntityId;
    private bool interactionOwned;
    private bool textAdvanceUnavailableLogged;
    private DateTimeOffset startedAt = DateTimeOffset.MinValue;
    private DateTimeOffset nextInteractionAt = DateTimeOffset.MinValue;
    private DateTimeOffset nextDialogActionAt = DateTimeOffset.MinValue;
    private uint activeFateId;
    private bool npcTravelRequested;

    public ActivatingFateStateHandler(
        IFateRepository fateRepository,
        IFateActivationService activationService,
        ITravelController travelController,
        IObjectTable objectTable,
        IGameGui gameGui,
        ITextAdvanceClient textAdvance,
        ICondition condition,
        IPluginLog log)
    {
        this.fateRepository = fateRepository;
        this.activationService = activationService;
        this.travelController = travelController;
        this.objectTable = objectTable;
        this.gameGui = gameGui;
        this.textAdvance = textAdvance;
        this.condition = condition;
        this.log = log;
    }

    public bool CanHandle(AutomatorState state) => state == AutomatorState.ActivatingFate;

    public void Reset() => ResetInteraction();

    public AutomatorStateHandlerResult Tick(AutomatorContext context)
    {
        if (enteredAt != context.LastStateChangedAt)
        {
            enteredAt = context.LastStateChangedAt;
            activeFateId = 0;
            ResetInteraction();
        }

        if (condition[ConditionFlag.Unconscious] || objectTable.LocalPlayer?.CurrentHp == 0)
        {
            ResetInteraction();
            return AutomatorStateHandlerResult.Transition(AutomatorState.Recovering, "Player died during activation");
        }

        var player = objectTable.LocalPlayer;
        if (player == null || condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
        {
            ResetInteraction();
            return AutomatorStateHandlerResult.Wait("Waiting for player availability");
        }

        var selection = context.CurrentSelection;
        if (selection == null)
        {
            return EndActivation(context, "Activation selection missing");
        }

        if (activeFateId != selection.Value.Fate.FateId)
        {
            ResetInteraction();
            activeFateId = selection.Value.Fate.FateId;
            startedAt = DateTimeOffset.UtcNow;
            nextInteractionAt = DateTimeOffset.MinValue;
            npcTravelRequested = false;
        }

        var fate = fateRepository.EnumerateActiveFates().FirstOrDefault(f => f.FateId == activeFateId);
        if (fate.FateId == 0)
        {
            return EndActivation(context, "Preparing FATE disappeared");
        }

        if (fate.State == Dalamud.Game.ClientState.Fates.FateState.Running)
        {
            ResetInteraction();
            context.UpdateSelection(selection.Value with { Fate = fate }, "Activated FATE snapshot refreshed", log);
            if (!IsAtFate(fate, player.Position))
            {
                var travelSelection = selection.Value with
                {
                    Fate = fate,
                    Destination = fate.Center,
                    ShouldTeleport = false,
                    PreferredAetheryteId = null,
                    Teleport = default
                };
                context.UpdateSelection(travelSelection, "FATE started before arrival", log);
                travelController.RequestTravel(travelSelection, TravelRequestKind.Automatic);
                return AutomatorStateHandlerResult.Transition(AutomatorState.Travelling, "FATE started before arrival; routing to FATE");
            }

            travelController.Cancel("FATE activated", TravelRequestKind.Automatic);
            log.Debug("Automator: FATE #{FateId} is now running after activation", fate.FateId);
            return AutomatorStateHandlerResult.Transition(AutomatorState.Participating, $"FATE #{fate.FateId} activated");
        }

        if (DateTimeOffset.UtcNow - startedAt > ActivationTimeout)
        {
            return EndActivation(context, "FATE activation timed out");
        }

        if (travelController.Phase is TravelPhase.Cancelled or TravelPhase.Failed)
        {
            return EndActivation(context, $"Activation travel {travelController.Phase}");
        }

        var dialogOpen = HasDialog();
        if (interactionNpcId == 0 && dialogOpen)
            TryAdoptExistingInteraction(fate, player);

        if (interactionNpcId != 0)
        {
            if (DateTimeOffset.UtcNow >= interactionUntil)
            {
                if (!HasDialog())
                    ResetInteraction();
            }
            if (interactionNpcId != 0 && HasDialog())
            {
                interactionUntil = DateTimeOffset.UtcNow.AddSeconds(15);
                // SelectString entries are not universally ordered; never select entry zero blindly.
                if (IsAddonReady("SelectString") || IsAddonReady("SelectIconString"))
                {
                    textAdvance.Disable();
                    return AutomatorStateHandlerResult.Wait("Activation menu requires a verified choice");
                }
                if (IsAddonReady("SelectYesno"))
                {
                    textAdvance.Disable();
                    if (IsOwnedActivationDialog())
                    {
                        TryAdvanceActivationDialog();
                        return AutomatorStateHandlerResult.Wait("Confirming owned FATE activation prompt");
                    }

                    return AutomatorStateHandlerResult.Wait("Waiting for unrelated confirmation prompt to close");
                }
                if (IsOwnedActivationDialog())
                {
                    if (!textAdvance.EnableForActivation())
                    {
                        if (!textAdvanceUnavailableLogged)
                        {
                            textAdvanceUnavailableLogged = true;
                            log.Debug("Automator: TextAdvance activation control unavailable for FATE #{FateId}", fate.FateId);
                        }
                    }
                    else
                        textAdvanceUnavailableLogged = false;
                }
                else
                    textAdvance.Disable();
                return AutomatorStateHandlerResult.Wait("Waiting for owned activation dialogue");
            }
            else if (condition[ConditionFlag.OccupiedInQuestEvent] || DateTimeOffset.UtcNow < nextInteractionAt)
                return AutomatorStateHandlerResult.Wait("Waiting for activation interaction");
            else
                ResetInteraction();
        }

        if (HasDialog() || condition[ConditionFlag.OccupiedInQuestEvent])
        {
            textAdvance.Disable();
            return AutomatorStateHandlerResult.Wait("Waiting for existing dialogue to close");
        }

        if (!activationService.TryGetMotivationNpc(fate, out var npc) || npc == null)
        {
            if (DateTimeOffset.UtcNow - startedAt > NpcDiscoveryTimeout)
            {
                return EndActivation(context, $"Motivation NPC unavailable ({activationService.Status})");
            }

            return AutomatorStateHandlerResult.Wait(activationService.Status);
        }

        var distance = System.Numerics.Vector3.Distance(player.Position, npc.Position);
        if (distance > 2f)
        {
            if (travelController.CurrentJob == null && (!npcTravelRequested || travelController.Phase == TravelPhase.Arrived))
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

        npcTravelRequested = false;

        if (DateTimeOffset.UtcNow >= nextInteractionAt)
        {
            if (activationService.Interact(npc))
            {
                interactionNpcId = npc.GameObjectId;
                interactionNpcEntityId = npc.EntityId;
                interactionUntil = DateTimeOffset.UtcNow.AddSeconds(15);
                interactionOwned = true;
            }
            nextInteractionAt = DateTimeOffset.UtcNow + InteractionRetryDelay;
        }

        return AutomatorStateHandlerResult.Wait($"Waiting for FATE activation ({activationService.Status})");
    }

    private unsafe bool IsAddonReady(string name)
    {
        var addon = (AtkUnitBase*)gameGui.GetAddonByName(name, 1).Address;
        return addon != null && addon->IsReady && addon->IsVisible && addon->IsFullyLoaded();
    }

    private bool HasDialog() => IsAddonReady("Talk") || IsAddonReady("SelectYesno") ||
                                IsAddonReady("SelectString") || IsAddonReady("SelectIconString") || IsAddonReady("Request");

    private IGameObject? FindMotivationNpc(ActiveFate fate)
    {
        if (!fate.HasMotivationNpc)
            return null;

        return objectTable.SearchById(fate.MotivationNpc)
            ?? objectTable.SearchByEntityId(fate.MotivationNpc)
            ?? objectTable.FirstOrDefault(obj => obj.GameObjectId == fate.MotivationNpc || obj.EntityId == fate.MotivationNpc);
    }

    private unsafe bool TryAdoptExistingInteraction(ActiveFate fate, IGameObject player)
    {
        if (fate.State != Dalamud.Game.ClientState.Fates.FateState.Preparing ||
            IsAddonReady("SelectString") || IsAddonReady("SelectIconString"))
            return false;

        var npc = FindMotivationNpc(fate);
        if (npc == null || System.Numerics.Vector3.Distance(player.Position, npc.Position) > 6f)
            return false;

        var sceneMatched = IsInteractionScene(npc);
        var yesNoOpen = IsAddonReady("SelectYesno");
        var promptMatched = yesNoOpen && MatchesActivationPrompt(GetSelectYesnoPrompt(), fate.Objective);
        if ((!yesNoOpen && !IsAddonReady("Talk")) || (!sceneMatched && !promptMatched))
            return false;

        interactionNpcId = npc.GameObjectId;
        interactionNpcEntityId = npc.EntityId;
        interactionUntil = DateTimeOffset.UtcNow.AddSeconds(15);
        interactionOwned = true;
        log.Debug("Automator: adopted existing {Dialog} for FATE #{FateId}; sceneMatched={SceneMatched} promptMatched={PromptMatched} scene={SceneId} npcObject={NpcObjectId} npcEntity={NpcEntityId}",
            yesNoOpen ? "SelectYesno" : "Talk",
            fate.FateId,
            sceneMatched,
            promptMatched,
            GetInteractionSceneId(),
            npc.GameObjectId,
            npc.EntityId);
        return true;
    }

    private unsafe ulong GetInteractionSceneId()
    {
        var events = EventFramework.Instance();
        return events == null ? 0 : (ulong)events->SceneGameObjectId;
    }

    private bool IsInteractionScene(IGameObject npc)
        => MatchesInteractionScene(GetInteractionSceneId(), npc.GameObjectId, npc.EntityId);

    private bool IsOwnedActivationDialog()
    {
        return interactionOwned && (IsAddonReady("Talk") || IsAddonReady("SelectYesno"));
    }

    private unsafe string? GetSelectYesnoPrompt()
    {
        var addon = (AtkUnitBase*)gameGui.GetAddonByName("SelectYesno", 1).Address;
        if (addon == null || !addon->IsReady || !addon->IsVisible || addon->AtkValues == null || addon->AtkValuesCount == 0)
            return null;

        return addon->AtkValues[0].GetValueAsString();
    }

    private static bool MatchesActivationPrompt(string? prompt, string? objective)
    {
        var normalizedPrompt = NormalizeDialogText(prompt);
        var normalizedObjective = NormalizeDialogText(objective);
        if (normalizedPrompt.Length < 8 || normalizedObjective.Length < 8)
            return false;

        return normalizedPrompt.StartsWith(normalizedObjective, StringComparison.OrdinalIgnoreCase) ||
               normalizedObjective.StartsWith(normalizedPrompt, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDialogText(string? text)
        => string.Concat((text ?? string.Empty).Where(char.IsLetterOrDigit));

    private static bool MatchesInteractionScene(ulong sceneObjectId, ulong npcGameObjectId, uint npcEntityId)
        => sceneObjectId != 0 && (sceneObjectId == npcGameObjectId || sceneObjectId == npcEntityId);

    private void ResetInteraction()
    {
        activationService.ClearOwnedTarget();
        textAdvance.Disable();
        interactionNpcId = 0;
        interactionNpcEntityId = 0;
        interactionUntil = DateTimeOffset.MinValue;
        nextDialogActionAt = DateTimeOffset.MinValue;
        interactionOwned = false;
        textAdvanceUnavailableLogged = false;
    }

    private AutomatorStateHandlerResult EndActivation(AutomatorContext context, string reason)
    {
        ResetInteraction();
        travelController.Cancel(reason, TravelRequestKind.Automatic);
        context.ClearSelection(reason, log);
        return AutomatorStateHandlerResult.Transition(AutomatorState.SelectingFate, reason);
    }

    private static unsafe bool IsAtFate(ActiveFate fate, System.Numerics.Vector3 position)
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
        return MathF.Sqrt(dx * dx + dz * dz) <= fate.Radius + 2f;
    }

    private unsafe bool TryAdvanceActivationDialog()
    {
        if (interactionNpcId == 0 || DateTimeOffset.UtcNow < nextDialogActionAt || !IsOwnedActivationDialog())
            return false;
        var addon = (AtkUnitBase*)gameGui.GetAddonByName("SelectYesno", 1).Address;
        var stage = AtkStage.Instance();
        if (addon == null || !addon->IsReady || !addon->IsVisible || !addon->IsFullyLoaded() || stage == null)
            return false;

        var evt = new AtkEvent
        {
            Listener = &addon->AtkEventListener,
            Target = &stage->AtkEventTarget
        };
        var data = new AtkEventData();
        var prompt = GetSelectYesnoPrompt();
        addon->ReceiveEvent(AtkEventType.ButtonClick, 0, &evt, &data);
        nextDialogActionAt = DateTimeOffset.UtcNow.Add(DialogActionDelay);
        log.Debug("Automator: SelectYesno ButtonClick(0) sent; prompt={Prompt}", prompt ?? "<unavailable>");
        return true;
    }
}
