namespace YetAnotherFATE.Services.Combat.Clearance;

public static class CombatClearanceTargetPolicy
{
    public static bool IsEligible(uint fateId, bool alive, bool targetable, bool hostile, bool onPlayerEnemyList, bool rememberedEndedFateTarget)
        => alive && targetable && hostile && onPlayerEnemyList && fateId == 0 && !rememberedEndedFateTarget;
}
