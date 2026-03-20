namespace YetAnotherFATE.Data.Travel;

public readonly record struct TeleportRecommendation(
    bool ShouldTeleport,
    uint? AetheryteId,
    string Reason,
    float DirectDistance,
    float EstimatedRunDistance,
    float EstimatedTeleportDistance
);
