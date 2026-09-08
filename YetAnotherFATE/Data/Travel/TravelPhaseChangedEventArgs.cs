using System;

namespace YetAnotherFATE.Data.Travel;

public sealed class TravelPhaseChangedEventArgs : EventArgs
{
    public TravelPhaseChangedEventArgs(TravelPhase phase, string? detail, TravelJob? job)
    {
        Phase = phase;
        Detail = detail;
        Job = job;
    }

    public TravelPhase Phase { get; }
    public string? Detail { get; }
    public TravelJob? Job { get; }
}
