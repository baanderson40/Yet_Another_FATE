using YetAnotherFATE.Data.Fates;

namespace YetAnotherFATE.Services.Fates;

public interface IFateFollowUpTracker
{
    void Begin(ActiveFate completedFate);
    bool IsPreferred(ActiveFate fate);
    bool IsWatching { get; }
    string Status { get; }
    void Clear() { }
}
