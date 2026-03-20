using System;
using YetAnotherFATE.Data.Fates;

namespace YetAnotherFATE.Data.Travel;

public enum TravelPhase
{
    Idle,
    Initializing,
    TeleportQueued,
    Teleporting,
    Mounting,
    Moving,
    Arriving,
    Arrived,
    Cancelled,
    Failed,
}

public enum TravelRequestKind
{
    Automatic,
    DebugForceRun,
}

public sealed record TravelJob(
    Guid JobId,
    FateSelection Selection,
    TravelRequestKind Kind,
    DateTimeOffset CreatedAt,
    float ArrivalRadius)
{
    public string Status { get; init; } = string.Empty;
}
