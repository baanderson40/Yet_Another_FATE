namespace YetAnotherFATE.Data.Fates;

public enum FateRule : byte
{
    None = 0,
    Normal = 1,
    Collect = 2,
    Escort = 3,
    Defend = 4,
    Event = 5,
    Chase = 6,
    ConcertedWorks = 7,
    Fete = 8,
}

public enum FateSortCriteria
{
    // Persisted numeric values: never reorder or renumber existing criteria.
    HasBonusWithTwist = 0,
    HasBonus = 1,
    Progress = 2,
    TimeRemainingUrgent = 3,
    TimeRemaining = 4,
    Distance = 5,
    Level = 6,
    Name = 7,
}

public sealed class FateSortOrder
{
    public FateSortCriteria Criteria { get; set; }
    public bool Descending { get; set; }
}
