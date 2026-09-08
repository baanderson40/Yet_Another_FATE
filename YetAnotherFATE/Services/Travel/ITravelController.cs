using System;
using YetAnotherFATE.Data.Fates;
using YetAnotherFATE.Data.Travel;

namespace YetAnotherFATE.Services.Travel;

public interface ITravelController
{
    TravelJob? CurrentJob { get; }
    TravelPhase Phase { get; }
    string? PhaseDetail { get; }
    event EventHandler<TravelPhaseChangedEventArgs>? PhaseChanged;

    void RequestTravel(FateSelection selection, TravelRequestKind kind = TravelRequestKind.Automatic, float? arrivalRadius = null);
    void Cancel(string reason, TravelRequestKind? onlyKind = null);
}
