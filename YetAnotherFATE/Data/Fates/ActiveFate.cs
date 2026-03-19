using System;
using System.Numerics;

namespace YetAnotherFATE.Data.Fates;

public readonly record struct ActiveFate(
    uint FateId,
    string Name,
    uint TerritoryId,
    Vector3 Center,
    float Radius,
    float DistanceFromPlayer,
    bool IsContinuation,
    TimeSpan TimeRemaining
);
