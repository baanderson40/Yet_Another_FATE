using YetAnotherFATE.Data.Fates;

namespace YetAnotherFATE.Services.Combat.RotationSolver;

public interface IRotationSolverBridge
{
    void EngageNearestAuto(FateSelection selection);
    void NotifyFateEnded();
}
