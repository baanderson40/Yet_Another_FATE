using YetAnotherFATE.Data.Fates;
using YetAnotherFATE.Data.Travel;

namespace YetAnotherFATE.Services.Travel;

public interface ITravelController
{
    TravelJob? CurrentJob { get; }
    TravelPhase Phase { get; }
    string? PhaseDetail { get; }

    void RequestTravel(FateSelection selection, TravelRequestKind kind = TravelRequestKind.Automatic);
    void Cancel(string reason, TravelRequestKind? onlyKind = null);
}
