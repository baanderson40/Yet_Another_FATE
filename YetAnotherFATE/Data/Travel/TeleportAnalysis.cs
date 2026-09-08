using System.Collections.Generic;
using YetAnotherFATE.Data.Zone;

namespace YetAnotherFATE.Data.Travel;

public sealed record TeleportAnalysis(
    ZoneInfo? Zone,
    TeleportRecommendation Recommendation,
    float RunSpeed,
    float TeleportPenalty,
    float PostTeleportBuffer,
    float Advantage,
    float AdvantageThreshold,
    string? FailureReason,
    IReadOnlyList<AetheryteTeleportDiagnostic> Aetherytes);
