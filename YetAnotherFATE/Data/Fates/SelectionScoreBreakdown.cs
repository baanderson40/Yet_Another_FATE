namespace YetAnotherFATE.Data.Fates;

public readonly record struct SelectionScoreBreakdown(
    double BaseScore,
    double PriorityAdjustment,
    double DistancePenalty,
    double TimeAdjustment,
    double ActivationPenalty,
    int LevelDifference,
    double LevelPenalty,
    double TravelAdjustment,
    double ContinuationBonus,
    double FinalScore);
