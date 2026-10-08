using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using YetAnotherFATE.Data.Fates;

namespace YetAnotherFATE.Services.Combat.Clearance;

public sealed unsafe class CombatClearanceTargeter : ICombatClearanceTargeter
{
    private const uint InvalidEntityId = 0xE0000000;
    private const int MaxHaters = 32;
    private readonly IObjectTable objects;
    private readonly IPluginLog log;
    private readonly HashSet<ulong> endedFateObjectIds = new();
    private readonly HashSet<uint> endedFateEntityIds = new();
    private nint ownedTarget;

    public CombatClearanceTargeter(IObjectTable objects, IPluginLog log)
    {
        this.objects = objects;
        this.log = log;
    }

    public void CaptureEndedFate(ActiveFate fate)
    {
        endedFateObjectIds.Clear();
        endedFateEntityIds.Clear();
        foreach (var npc in objects.OfType<IBattleNpc>())
        {
            if (ReadFateId(npc) != fate.FateId)
                continue;
            endedFateObjectIds.Add(npc.GameObjectId);
            if (npc.EntityId != InvalidEntityId)
                endedFateEntityIds.Add(npc.EntityId);
        }
        log.Debug("CombatClearance: remembered {Count} entities from ended FATE #{FateId}", endedFateObjectIds.Count, fate.FateId);
    }

    public ClearanceTargetSnapshot? SelectTarget(uint endedFateId)
    {
        var player = objects.LocalPlayer;
        if (player == null)
            return null;

        var uiState = UIState.Instance();
        if (uiState == null)
            return null;
        var enemyIds = new HashSet<uint>();
        var count = Math.Clamp(uiState->Hater.HaterCount, 0, MaxHaters);
        for (var i = 0; i < count; i++)
        {
            var hater = uiState->Hater.Haters[i];
            if (hater.EntityId != 0 && hater.EntityId != InvalidEntityId && hater.Enmity > 0)
                enemyIds.Add(hater.EntityId);
        }

        return objects.OfType<IBattleNpc>()
            .Where(npc => IsEligible(npc, enemyIds, endedFateId))
            .Select(npc => new ClearanceTargetSnapshot(
                npc.GameObjectId,
                npc.EntityId,
                ReadFateId(npc),
                npc.Name?.TextValue ?? "Unknown attacker",
                System.Numerics.Vector3.Distance(player.Position, npc.Position),
                npc.Address))
            .OrderBy(target => target.Distance)
            .ThenBy(target => target.EntityId)
            .Select(target => (ClearanceTargetSnapshot?)target)
            .FirstOrDefault();
    }

    public bool SetTarget(ClearanceTargetSnapshot target)
    {
        if (target.Address == nint.Zero)
            return false;
        var targetSystem = TargetSystem.Instance();
        if (targetSystem == null)
            return false;
        var current = targetSystem->GetHardTarget();
        if (current != null && current->GetGameObjectId() == target.ObjectId)
        {
            ownedTarget = (nint)current;
            return true;
        }
        var gameObject = (GameObject*)target.Address;
        if (gameObject == null)
            return false;
        if (!targetSystem->SetHardTarget(gameObject, true))
            return false;
        ownedTarget = target.Address;
        return true;
    }

    public void ClearOwnedTarget()
    {
        if (ownedTarget == nint.Zero)
            return;
        var targetSystem = TargetSystem.Instance();
        if (targetSystem == null)
            return;
        var current = targetSystem->GetHardTarget();
        if (current != null && (nint)current == ownedTarget)
            targetSystem->SetHardTarget(null, true);
        ownedTarget = nint.Zero;
    }

    public void CompleteEndedFate()
    {
        ClearOwnedTarget();
        endedFateObjectIds.Clear();
        endedFateEntityIds.Clear();
    }

    private bool IsEligible(IBattleNpc npc, HashSet<uint> enemyIds, uint endedFateId)
    {
        if (!npc.IsValid() || npc.GameObjectId == 0 || npc.EntityId == 0 || npc.Address == nint.Zero)
            return false;
        var character = (Character*)npc.Address;
        if (character->VirtualTable == null)
            return false;
        var category = ActionManager.ClassifyTarget(character);
        var rememberedEndedFateTarget = endedFateObjectIds.Contains(npc.GameObjectId) || endedFateEntityIds.Contains(npc.EntityId);
        return CombatClearanceTargetPolicy.IsEligible(
            ReadFateId(npc),
            npc.CurrentHp > 0 && !npc.IsDead && (npc.BattleNpcKind.ToString() is "Combatant" or "BNpcPart"),
            npc.IsTargetable,
            category == ActionManager.TargetCategory.Enemy,
            enemyIds.Contains(npc.EntityId),
            rememberedEndedFateTarget);
    }

    private static uint ReadFateId(IBattleNpc npc)
    {
        if (npc.Address == nint.Zero)
            return 0;
        var character = (Character*)npc.Address;
        return character->VirtualTable == null ? 0U : character->FateId;
    }
}
