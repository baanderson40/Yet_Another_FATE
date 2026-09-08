using System;
using YetAnotherFATE.Data.Travel;

namespace YetAnotherFATE.Data.Fates;

public sealed record FateCandidateDiagnostic(
    ActiveFate Fate,
    bool Eligible,
    string? ExclusionReason,
    SelectionScoreBreakdown? Score,
    TeleportAnalysis Teleport);
