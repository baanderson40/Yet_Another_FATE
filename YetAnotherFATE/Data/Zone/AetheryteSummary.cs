using System.Numerics;

namespace YetAnotherFATE.Data.Zone;

public readonly record struct AetheryteSummary(
    uint AetheryteId,
    uint TerritoryId,
    string Name,
    uint PlaceNameId,
    Vector3 Position,
    string PositionSource,
    bool HasPosition,
    bool IsPrimary,
    byte SubIndex,
    uint GilCost,
    bool IsFavourite
);
