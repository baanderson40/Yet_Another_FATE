using System;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using YetAnotherFATE.Data.Fates;

namespace YetAnotherFATE.Services.Fates.CombatHelper.Targeter;

public static unsafe class FateTargetPolicy
{
    public const uint ForlornMaidenNameId = 6737;
    public const uint TheForlornNameId = 6738;

    public static int GetForlornPriority(uint nameId)
        => nameId switch
        {
            TheForlornNameId => 2,
            ForlornMaidenNameId => 1,
            _ => 0,
        };

    public static bool IsValidCombatTarget(IBattleNpc npc, ActiveFate fate)
        => GetExclusionReason(npc, fate) == null;

    public static bool IsValidTravelAnchor(IBattleNpc npc, ActiveFate fate)
    {
        if (!npc.IsValid() || npc.GameObjectId == 0 || npc.BaseId == 0 || npc.CurrentHp == 0 || npc.IsDead || !npc.IsTargetable)
            return false;
        if (string.Equals(npc.BattleNpcKind.ToString(), "Pet", StringComparison.OrdinalIgnoreCase))
            return false;
        if (IsFateNpcIdentity(npc.GameObjectId, npc.EntityId, fate.MotivationNpc, fate.ObjectiveNpc))
            return true;
        if (fate.FateId > ushort.MaxValue || npc.Address == nint.Zero)
            return false;

        var character = (Character*)npc.Address;
        return character->VirtualTable != null && character->FateId == fate.FateId;
    }

    public static string? GetExclusionReason(IBattleNpc npc, ActiveFate fate)
    {
        if (!npc.IsValid() || npc.GameObjectId == 0 || npc.BaseId == 0)
            return "invalid object";
        if (npc.CurrentHp == 0 || npc.IsDead)
            return "dead or zero HP";
        if (!npc.IsTargetable)
            return "untargetable";
        if (string.Equals(npc.BattleNpcKind.ToString(), "Pet", StringComparison.OrdinalIgnoreCase))
            return "pet";
        if (!IsCombatNpcKind(npc.BattleNpcKind.ToString()))
            return "non-combat BattleNpcKind";
        if (IsFateNpcIdentity(npc.GameObjectId, npc.EntityId, fate.MotivationNpc, fate.ObjectiveNpc))
            return "FATE motivation/objective NPC";
        if (IsKnownFateNpcNameplate(GetNameplateIcon(npc)))
            return "FATE NPC nameplate";
        if (fate.FateId > ushort.MaxValue || npc.Address == nint.Zero)
            return "invalid FATE/native address";

        var character = (Character*)npc.Address;
        if (character->VirtualTable == null || character->FateId != fate.FateId)
            return "different FATE";
        var targetCategory = ActionManager.ClassifyTarget(character);
        return IsEnemyTargetCategory(targetCategory) ? null : $"not enemy ({targetCategory})";
    }

    public static bool IsEnemyTargetCategory(ActionManager.TargetCategory category)
        => category == ActionManager.TargetCategory.Enemy;

    public static bool IsCombatNpcKind(string kind)
        => kind is "Combatant" or "BNpcPart";

    public static bool IsFateNpcIdentity(ulong gameObjectId, uint entityId, uint motivationNpc, uint objectiveNpc)
        => IsMatchingNpcIdentity(gameObjectId, entityId, motivationNpc) ||
           IsMatchingNpcIdentity(gameObjectId, entityId, objectiveNpc);

    private static bool IsMatchingNpcIdentity(ulong gameObjectId, uint entityId, uint npcId)
        => npcId is not 0 and not 0xE0000000 && (gameObjectId == npcId || entityId == npcId);

    public static bool IsKnownFateNpcNameplate(uint nameplateIconId)
        => nameplateIconId is 60093 or 60732;

    public static uint GetNameplateIcon(IBattleNpc npc)
    {
        if (npc.Address == nint.Zero)
            return 0;

        var gameObject = (GameObject*)npc.Address;
        return gameObject->NamePlateIconId;
    }
}
