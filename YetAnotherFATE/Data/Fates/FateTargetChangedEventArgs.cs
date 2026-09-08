using System;

namespace YetAnotherFATE.Data.Fates;

public sealed class FateTargetChangedEventArgs : EventArgs
{
    public FateTargetChangedEventArgs(FateTargetSnapshot? previous, FateTargetSnapshot? current, string reason)
    {
        Previous = previous;
        Current = current;
        Reason = reason;
    }

    public FateTargetSnapshot? Previous { get; }
    public FateTargetSnapshot? Current { get; }
    public string Reason { get; }
}
