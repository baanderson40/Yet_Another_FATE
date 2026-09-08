using System;
using YetAnotherFATE.Data.Fates;

namespace YetAnotherFATE.Services.Fates.CombatHelper.Targeter;

public interface IFateTargetTracker
{
    FateTargetSnapshot? CurrentTarget { get; }
    event EventHandler<FateTargetChangedEventArgs>? TargetChanged;

    FateTargetSnapshot? Refresh(FateSelection selection);
    void Clear(string reason);
}
