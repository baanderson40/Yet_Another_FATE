namespace YetAnotherFATE.Data.Fates;

public enum FateCompletionKind
{
    Completed,
    Failed,
    Expired,
    Lost,
}

public readonly record struct FateCompletionResult(
    uint FateId,
    FateCompletionKind Kind,
    byte LastProgress,
    string Reason);
