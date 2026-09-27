namespace YetAnotherFATE.Services.Fates;

public interface IFateRewardTracker
{
    int CompletedCount { get; }
    int? RunUntil { get; }
    int? Remaining { get; }
    bool PendingStop { get; }
    void SynchronizeSession();
    void Start();
    void Start(int count);
    void Stop();
    void RequestStopWhenSafe();
    void CompleteSafeStop();
}
