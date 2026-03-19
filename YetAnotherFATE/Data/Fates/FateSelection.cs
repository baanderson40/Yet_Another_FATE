using System.Numerics;

namespace YetAnotherFATE.Data.Fates;

public readonly record struct FateSelection(
    ActiveFate Fate,
    Vector3 Destination,
    bool ShouldTeleport,
    uint? PreferredAetheryteId
);
