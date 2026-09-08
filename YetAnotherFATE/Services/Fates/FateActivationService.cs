using System;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using YetAnotherFATE.Data.Fates;

namespace YetAnotherFATE.Services.Fates;

public sealed class FateActivationService : IFateActivationService
{
    private readonly IObjectTable objectTable;
    private readonly IPluginLog log;

    public FateActivationService(IObjectTable objectTable, IPluginLog log)
    {
        this.objectTable = objectTable;
        this.log = log;
    }

    public string Status { get; private set; } = "activation not started";

    public bool TryGetMotivationNpc(ActiveFate fate, out IGameObject? npc)
    {
        npc = null;
        if (fate.MotivationNpc == 0 || fate.MotivationNpc == 0xE0000000)
        {
            Status = "motivation NPC id unavailable";
            return false;
        }

        npc = objectTable.SearchById(fate.MotivationNpc)
            ?? objectTable.SearchByEntityId(fate.MotivationNpc)
            ?? objectTable.FirstOrDefault(obj => obj.GameObjectId == fate.MotivationNpc || obj.EntityId == fate.MotivationNpc);

        if (npc == null)
        {
            Status = $"motivation NPC {fate.MotivationNpc} not spawned";
            return false;
        }

        if (!npc.IsTargetable)
        {
            Status = $"motivation NPC {npc.Name.TextValue} is not targetable";
            return false;
        }

        Status = $"motivation NPC resolved: {npc.Name.TextValue} ({npc.ObjectKind}) id={npc.GameObjectId} entity={npc.EntityId}";
        return true;
    }

    public unsafe bool Interact(IGameObject npc)
    {
        if (!npc.IsTargetable || npc.Address == nint.Zero)
        {
            Status = "motivation NPC is not interactable";
            return false;
        }

        var targetSystem = TargetSystem.Instance();
        if (targetSystem == null)
        {
            Status = "target system unavailable";
            return false;
        }

        targetSystem->SetHardTarget((GameObject*)npc.Address, true);
        var result = targetSystem->InteractWithObject((GameObject*)npc.Address, false);
        Status = $"interaction requested for {npc.Name.TextValue}: result={result}";
        log.Debug("FateActivation: interaction requested npc={Name} gameObjectId={GameObjectId} entityId={EntityId} result={Result}",
            npc.Name.TextValue,
            npc.GameObjectId,
            npc.EntityId,
            result);
        return result != 0;
    }
}
