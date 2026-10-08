using YetAnotherFATE.Data.Fates;

namespace YetAnotherFATE.Services.Combat.Clearance;

public interface ICombatClearanceTargeter
{
    void CaptureEndedFate(ActiveFate fate);
    ClearanceTargetSnapshot? SelectTarget(uint endedFateId);
    bool SetTarget(ClearanceTargetSnapshot target);
    void ClearOwnedTarget();
    void CompleteEndedFate();
}

public readonly record struct ClearanceTargetSnapshot(ulong ObjectId, uint EntityId, uint FateId, string Name, float Distance, nint Address);
