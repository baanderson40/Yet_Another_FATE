using System.Numerics;
using YetAnotherFATE.Data.Zone;

namespace YetAnotherFATE.Data.Travel;

public sealed record AetheryteTeleportDiagnostic(
    AetheryteSummary Aetheryte,
    float DistanceToFate,
    float AdjustedDistance,
    float EstimatedRunTime,
    bool WithinCostLimit,
    bool Selected,
    string? ExclusionReason);
