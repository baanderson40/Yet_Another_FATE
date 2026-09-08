using System;
using System.Numerics;

namespace YetAnotherFATE.Data.Fates;

public readonly record struct FateTargetSnapshot(
    ulong ObjectId,
    uint BaseId,
    string Name,
    Vector3 Position,
    float DistanceFromPlayer,
    uint CurrentHp,
    uint MaxHp,
    double Score,
    bool IsDead,
    DateTimeOffset CapturedAt,
    nint GameObjectAddress);
