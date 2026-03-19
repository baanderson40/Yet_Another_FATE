namespace YetAnotherFATE.Data.Zone;

public readonly record struct ZoneInfo(
    uint TerritoryId,
    string Name,
    uint PlaceNameId,
    bool IsAetheryteUnlocked
);
