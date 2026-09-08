using System;
using System.Collections.Generic;

namespace YetAnotherFATE.Data.Fates;

public sealed record FateSelectionDiagnostics(
    DateTimeOffset EvaluatedAt,
    IReadOnlyList<FateCandidateDiagnostic> Candidates,
    FateSelection? Selected);
