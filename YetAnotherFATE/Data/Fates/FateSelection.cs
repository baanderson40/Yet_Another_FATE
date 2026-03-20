using System.Numerics;
using YetAnotherFATE.Data.Travel;

namespace YetAnotherFATE.Data.Fates;

public readonly record struct FateSelection(
    ActiveFate Fate,
    Vector3 Destination,
    bool ShouldTeleport,
    uint? PreferredAetheryteId,
    double Score,
    string Reason,
    TeleportRecommendation Teleport
);
