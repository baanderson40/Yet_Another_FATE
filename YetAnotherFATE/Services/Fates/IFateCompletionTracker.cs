using YetAnotherFATE.Data.Fates;

namespace YetAnotherFATE.Services.Fates;

public interface IFateCompletionTracker
{
    FateCompletionResult? LastResult { get; }
    void Observe(ActiveFate fate);
    FateCompletionResult RecordMissing(ActiveFate fate);
    FateCompletionResult RecordTerminal(ActiveFate fate);
    void Reset();
}
