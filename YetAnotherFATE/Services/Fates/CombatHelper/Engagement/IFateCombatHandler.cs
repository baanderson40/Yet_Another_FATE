using YetAnotherFATE.Data.Fates;

namespace YetAnotherFATE.Services.Fates.CombatHelper.Engagement;

public interface IFateCombatHandler
{
    FateTargetSnapshot? Execute(FateSelection selection);
    void Reset(string reason);
}
