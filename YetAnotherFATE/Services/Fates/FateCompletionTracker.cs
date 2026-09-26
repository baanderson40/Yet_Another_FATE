using YetAnotherFATE.Data.Fates;

namespace YetAnotherFATE.Services.Fates;

public sealed class FateCompletionTracker : IFateCompletionTracker
{
    private ActiveFate? lastObserved;

    public FateCompletionResult? LastResult { get; private set; }

    public void Observe(ActiveFate fate)
    {
        lastObserved = fate;
    }

    public FateCompletionResult RecordMissing(ActiveFate fallback)
    {
        var fate = lastObserved is { } observed && observed.FateId == fallback.FateId &&
                   observed.TerritoryId == fallback.TerritoryId && observed.StartTimeEpoch == fallback.StartTimeEpoch
            ? observed : fallback;
        var kind = fate.Progress >= 100
            ? FateCompletionKind.Completed
            : fate.TimeRemaining <= System.TimeSpan.Zero
                ? FateCompletionKind.Expired
                : FateCompletionKind.Lost;
        var result = new FateCompletionResult(fate.FateId, kind, fate.Progress, $"FATE disappeared from active table ({kind})");
        LastResult = result;
        lastObserved = null;
        return result;
    }

    public FateCompletionResult RecordTerminal(ActiveFate fate)
    {
        var kind = fate.State == Dalamud.Game.ClientState.Fates.FateState.Failed
            ? FateCompletionKind.Failed
            : fate.Progress >= 100
                ? FateCompletionKind.Completed
                : FateCompletionKind.Expired;
        var result = new FateCompletionResult(fate.FateId, kind, fate.Progress, $"FATE ended with state {fate.State} ({kind})");
        LastResult = result;
        lastObserved = null;
        return result;
    }

    public void Reset()
    {
        lastObserved = null;
        LastResult = null;
    }
}
